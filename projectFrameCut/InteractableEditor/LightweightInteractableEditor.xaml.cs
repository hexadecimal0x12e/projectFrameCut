using projectFrameCut.ApplicationAPIBase.Interaction;
using projectFrameCut.Controls;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.InteractableEditor;

public partial class LightweightInteractableEditor : ContentView, IInteractableEditor
{
    private readonly Dictionary<Guid, PreviewState> _states = new();
    private readonly Dictionary<Guid, IInteractableElement> _elements = new();
    private double _canvasWidth;
    private double _canvasHeight;
    private double _videoWidth = 1920;
    private double _videoHeight = 1080;
    private uint _currentFrame;
    private int _generation;

    public LightweightInteractableEditor()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            if (Width > 0 && Height > 0)
            {
                SetCanvasSize(Width, Height);
            }
        };
    }

    public void SetInteractiveElements(IReadOnlyCollection<IInteractableElement> elements)
    {
        _elements.Clear();
        foreach (var element in elements)
        {
            _elements[element.Id] = element;
        }
    }

    public void SetSelectedElement(Guid? elementId)
    {
        foreach (var element in _elements.Values)
        {
            element.IsSelected = element.Id == elementId;
        }
    }

    public void SetCanvasSize(double width, double height)
    {
        if (width <= 0 || height <= 0 || (_canvasWidth == width && _canvasHeight == height)) return;
        _canvasWidth = width;
        _canvasHeight = height;
        LayoutPreviews();
    }

    public void SetVideoSize(double width, double height)
    {
        if (width <= 0 || height <= 0 || (_videoWidth == width && _videoHeight == height)) return;
        _videoWidth = width;
        _videoHeight = height;
        LayoutPreviews();
    }

    public void SetCurrentFrame(uint frame)
    {
        if (_currentFrame == frame) return;
        _currentFrame = frame;
        LayoutPreviews();
    }

    public async Task<bool> ApplyPreparedPreviewsAsync(IReadOnlyList<PreparedPreview> previews)
    {
        if (Dispatcher.IsDispatchRequired)
        {
            return await Dispatcher.DispatchAsync(() => ApplyPreviews(previews));
        }

        return ApplyPreviews(previews);
    }

    public void ApplyPreparedPreviews(IReadOnlyList<PreparedPreview> previews) => ApplyPreviews(previews);

    private bool ApplyPreviews(IReadOnlyList<PreparedPreview> previews)
    {
        _generation++;
        var hasPreview = false;

        foreach (var preview in previews)
        {
            if (preview.ClipId == Guid.Empty || preview.Source?.ClipType is ClipMode.AudioClip or ClipMode.MarkingClip) continue;

            if (!_states.TryGetValue(preview.ClipId, out var state))
            {
                state = new PreviewState(preview.ClipId);
                _states.Add(preview.ClipId, state);
                PreviewCanvas.Children.Add(state.Host);
            }

            state.Generation = _generation;
            state.Source = preview.Source;
            state.IsCanvasPreview = preview.IsCanvasPreview;
            if (preview.Source is not null)
            {
                var z = (long)preview.Source.LayerIndex * 10000L + Math.Min(9999u, preview.Source.SubLayerIndex);
                state.Host.ZIndex = z >= int.MaxValue ? int.MinValue : -(int)z;
            }
            if (preview.View is not null)
            {
                state.SetView(preview.View);
            }

            state.Host.IsVisible = state.HasView;
            LayoutPreview(state);
            hasPreview |= state.HasView;
        }

        foreach (var state in _states.Values)
        {
            if (state.Generation == _generation) continue;
            state.Clear();
        }

        return hasPreview;
    }

    private void LayoutPreviews()
    {
        foreach (var state in _states.Values)
        {
            if (state.HasView) LayoutPreview(state);
        }
    }

    private void LayoutPreview(PreviewState state)
    {
        if (_canvasWidth <= 0 || _canvasHeight <= 0 || _videoWidth <= 0 || _videoHeight <= 0) return;

        var renderRect = GetRenderRect();
        if (state.IsCanvasPreview)
        {
            state.SetBounds(renderRect);
            return;
        }

        double x = 0;
        double y = 0;
        double w = _videoWidth;
        double h = _videoHeight;

        if (state.Source is not null)
        {
            x = state.Source.TargetX;
            y = state.Source.TargetY;
            if (state.Source.TargetWidth > 0) w = state.Source.TargetWidth;
            if (state.Source.TargetHeight > 0) h = state.Source.TargetHeight;
            ApplyPositionProviders(state.Source, ref x, ref y, ref w, ref h);
        }
        else if (_elements.TryGetValue(state.Id, out var element))
        {
            var rect = element.LogicalRect;
            x = rect.X;
            y = rect.Y;
            w = rect.Width;
            h = rect.Height;
        }

        var scale = renderRect.Width / _videoWidth;
        state.SetBounds(new Rect(
            renderRect.X + x * scale,
            renderRect.Y + y * scale,
            Math.Max(0, w * scale),
            Math.Max(0, h * scale)));
    }

    private void ApplyPositionProviders(IClip clip, ref double x, ref double y, ref double w, ref double h)
    {
        if (clip.EffectsInstances is null) return;

        foreach (var effect in clip.EffectsInstances.OrderBy(e => e.Index))
        {
            ClipPositionTuple position;
            if (effect is IContinuousClipPositionProvider continuous)
            {
                position = continuous.GetPosition(clip, _currentFrame, (int)_videoWidth, (int)_videoHeight);
            }
            else if (effect is IClipPositionProvider provider)
            {
                position = provider.GetPosition(clip, (int)_videoWidth, (int)_videoHeight);
            }
            else
            {
                continue;
            }

            if (position.IsDelta)
            {
                x += position.TargetX;
                y += position.TargetY;
                w += position.TargetWidth;
                h += position.TargetHeight;
            }
            else
            {
                x = position.TargetX;
                y = position.TargetY;
                if (position.TargetWidth > 0) w = position.TargetWidth;
                if (position.TargetHeight > 0) h = position.TargetHeight;
            }
        }
    }

    private Rect GetRenderRect()
    {
        var scale = Math.Min(_canvasWidth / _videoWidth, _canvasHeight / _videoHeight);
        var width = _videoWidth * scale;
        var height = _videoHeight * scale;
        return new Rect((_canvasWidth - width) / 2, (_canvasHeight - height) / 2, width, height);
    }

    public void AddReferenceLine(ReferenceLineOrientation? orientation) { }
    public void RemoveReferenceLine(string id) { }
    public void ClearReferenceLines() { }
    public string SerializeReferenceLines() => "[]";
    public void RestoreReferenceLines(string? json) { }
    public IInteractableEditor ConfigurePreviewRefresh(Func<Task>? callback) => this;
    public IInteractableEditor ConfigureOverviewImageSource(Func<Task<ImageSource?>>? provider) => this;
    public IInteractableEditor ConfigureInfoIndicator(bool isVisible, string? message) => this;
    public IInteractableEditor ConfigureElementClicked(InteractiveElementClickedHandler? callback) => this;
    public IInteractableEditor ConfigureBlankAreaClicked(Func<Task>? callback) => this;
    public IInteractableEditor ConfigureElementChanged(InteractiveElementChangedHandler? callback) => this;
    public IInteractableEditor ConfigureCustomHandles(CustomHandleProvider? provider, CustomHandleDragHandler? dragHandler) => this;

    private sealed class PreviewState(Guid id)
    {
        private Rect _bounds = new(double.NaN, double.NaN, double.NaN, double.NaN);
        private Grid? _imageBuffers;
        private Image? _imageA;
        private Image? _imageB;
        private Image? _activeImage;

        public Guid Id { get; } = id;
        public ContentView Host { get; } = new()
        {
            InputTransparent = true,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill
        };
        public IClip? Source { get; set; }
        public bool IsCanvasPreview { get; set; }
        public int Generation { get; set; }
        public bool HasView => Host.Content is not null;

        public void SetView(View incoming)
        {
            if (incoming is Image image && image.Source is not null)
            {
                SetImage(image);
                return;
            }

            _imageBuffers = null;
            _imageA = null;
            _imageB = null;
            _activeImage = null;
            if (Host.Content is HdrPreviewView currentHdr && incoming is HdrPreviewView incomingHdr)
            {
                currentHdr.Frame = incomingHdr.Frame;
                return;
            }

            if (!ReferenceEquals(Host.Content, incoming)) Host.Content = incoming;
        }

        private void SetImage(Image incoming)
        {
            if (_imageBuffers is null)
            {
                _imageA = CreateImage();
                _imageB = CreateImage();
                _imageBuffers = new Grid { InputTransparent = true };
                _imageBuffers.Children.Add(_imageA);
                _imageBuffers.Children.Add(_imageB);
                _activeImage = _imageA;
                Host.Content = _imageBuffers;
            }

            var next = ReferenceEquals(_activeImage, _imageA) ? _imageB! : _imageA!;
            next.Aspect = incoming.Aspect;
            next.Source = incoming.Source;
            next.IsVisible = true;
            _activeImage!.IsVisible = false;
            _activeImage = next;
        }

        private static Image CreateImage() => new()
        {
            Aspect = Aspect.Fill,
            InputTransparent = true,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            IsVisible = false
        };

        public void SetBounds(Rect bounds)
        {
            if (_bounds == bounds) return;
            _bounds = bounds;
            AbsoluteLayout.SetLayoutBounds(Host, bounds);
        }

        public void Clear()
        {
            Host.IsVisible = false;
            Source = null;
        }
    }
}
