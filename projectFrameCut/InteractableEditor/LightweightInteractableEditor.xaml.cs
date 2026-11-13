using projectFrameCut.ApplicationAPIBase.Interaction;
using projectFrameCut.Controls;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.InteractableEditor;

public partial class LightweightInteractableEditor : ContentView, IInteractableEditor
{
    public static readonly BindableProperty AllowClipOutOfBoundsProperty = BindableProperty.Create(
        nameof(AllowClipOutOfBounds), typeof(bool), typeof(LightweightInteractableEditor), false,
        propertyChanged: static (bindable, _, _) => ((LightweightInteractableEditor)bindable).LayoutPreviews());

    public static readonly BindableProperty UseCheckerboardBackgroundProperty = BindableProperty.Create(
        nameof(UseCheckerboardBackground),
        typeof(bool),
        typeof(LightweightInteractableEditor),
        false,
        propertyChanged: static (bindable, _, _) =>
            ((LightweightInteractableEditor)bindable).CheckerboardBackgroundView.Invalidate());

    private readonly Dictionary<Guid, PreviewState> _states = new();
    private readonly Dictionary<Guid, IInteractableElement> _elements = new();
    private double _canvasWidth;
    private double _canvasHeight;
    private double _videoWidth = 1920;
    private double _videoHeight = 1080;
    private uint _currentFrame;
    private int _generation;
    private uint _pendingFrame;
    private Dictionary<Guid, Rect>? _pendingFrameLayouts;

    public LightweightInteractableEditor()
    {
        InitializeComponent();
        CheckerboardBackgroundView.Drawable = new CheckerboardDrawable();
        CanvasBackground.ZIndex = int.MinValue;
        SizeChanged += (_, _) =>
        {
            if (Width > 0 && Height > 0)
            {
                SetCanvasSize(Width, Height);
            }
        };
    }

    public bool UseCheckerboardBackground
    {
        get => (bool)GetValue(UseCheckerboardBackgroundProperty);
        set => SetValue(UseCheckerboardBackgroundProperty, value);
    }

    public bool AllowClipOutOfBounds
    {
        get => (bool)GetValue(AllowClipOutOfBoundsProperty);
        set => SetValue(AllowClipOutOfBoundsProperty, value);
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
        _pendingFrameLayouts = null;
        LayoutPreviews();
    }

    public void SetCurrentFrame(uint frame)
    {
        _pendingFrameLayouts = null;
        if (_currentFrame == frame) return;
        _currentFrame = frame;
        LayoutPreviews();
    }

    internal void PrepareFrameLayout(uint frame, IReadOnlyList<IClip>? clips, bool reusePrepared = false)
    {
        if (reusePrepared && _pendingFrame == frame && _pendingFrameLayouts is not null) return;
        var layouts = new Dictionary<Guid, Rect>();
        if (clips is not null)
        {
            foreach (var clip in clips)
            {
                if (clip.ClipType is ClipMode.AudioClip or ClipMode.MarkingClip || !clip.ContainsFrame(frame)) continue;
                layouts[clip.Id] = ResolveClipRect(ClipArgumentBinding.InitializeFrame(clip, frame), frame);
            }
        }
        _pendingFrame = frame;
        _pendingFrameLayouts = layouts;
    }

    public async Task<bool> ApplyPreparedPreviewsAsync(IReadOnlyList<PreparedPreview> previews)
    {
#if DIAGHUB_ENABLE_TRACE_SYSTEM
        using var dispatchMark = new UserMarkRange("Preview.DispatchApply", $"previews={previews.Count}, host=lightweight");
#endif
        if (Dispatcher.IsDispatchRequired)
        {
            return await Dispatcher.DispatchAsync(() => ApplyPreviews(previews));
        }

        return ApplyPreviews(previews);
    }

    public void ApplyPreparedPreviews(IReadOnlyList<PreparedPreview> previews) => ApplyPreviews(previews);

    internal bool ApplyPreparedPreviews(IReadOnlyList<PreparedPreview> previews, uint frame)
        => ApplyPreviews(previews, frame);

    private bool ApplyPreviews(IReadOnlyList<PreparedPreview> previews, uint? frame = null)
    {
#if DIAGHUB_ENABLE_TRACE_SYSTEM
        using var applyMark = new UserMarkRange("Preview.Apply", $"previews={previews.Count}, host=lightweight");
#endif
        _generation++;
        var hasPreview = false;
        var views = new View?[previews.Count];
        var canvasIndex = -1;
        for (var i = 0; i < previews.Count; i++)
        {
            views[i] = PreviewErrorView.GetView(previews[i]);
            if (canvasIndex < 0 && previews[i].IsCanvasPreview) canvasIndex = i;
        }
        var layouts = frame.HasValue && _pendingFrame == frame.Value ? _pendingFrameLayouts : null;
        if (frame.HasValue)
        {
            _pendingFrameLayouts = null;
            _currentFrame = frame.Value;
        }

        if (canvasIndex >= 0)
        {
            foreach (var existing in _states.Values) existing.Clear();
            if (!_states.TryGetValue(Guid.Empty, out var state))
            {
                state = new PreviewState(Guid.Empty) { IsCanvasPreview = true };
                _states[Guid.Empty] = state;
                PreviewCanvas.Children.Add(state.Host);
            }
            if (views[canvasIndex] is { } canvasView) state.SetView(canvasView);
            state.Host.IsVisible = state.HasView;
            LayoutPreview(state);
            return state.HasView;
        }

        for (var i = 0; i < previews.Count; i++)
        {
            var preview = previews[i];
            if (preview.ClipId == Guid.Empty || preview.Source?.ClipType is ClipMode.AudioClip or ClipMode.MarkingClip) continue;

            if (!_states.TryGetValue(preview.ClipId, out var state))
            {
                state = new PreviewState(preview.ClipId);
                _states.Add(preview.ClipId, state);
                PreviewCanvas.Children.Add(state.Host);
            }

            state.Generation = _generation;
            state.Source = preview.Source;
            state.IsCanvasPreview = preview.IsCanvasPreview || preview.IsPositionedClipPreview;
            if (preview.Source is not null)
            {
                state.Host.ZIndex = ClipLayerOrder.GetPreviewZIndex(preview.Source.LayerIndex, preview.Source.SubLayerIndex);
            }
            if (views[i] is { } previewView)
            {
                state.SetView(previewView);
            }

            state.Host.IsVisible = state.HasView;
            LayoutPreview(state, layouts is not null && layouts.TryGetValue(preview.ClipId, out var rect) ? rect : null);
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
        if (_canvasWidth > 0 && _canvasHeight > 0 && _videoWidth > 0 && _videoHeight > 0)
        {
            var rect = GetRenderRect();
            AbsoluteLayout.SetLayoutBounds(CanvasBackground, rect);
            PreviewCanvas.ClipBounds = AllowClipOutOfBounds ? null : rect;
        }

        foreach (var state in _states.Values)
        {
            if (state.HasView) LayoutPreview(state);
        }
    }

    private void LayoutPreview(PreviewState state, Rect? preparedRect = null)
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
            var rect = preparedRect ?? ResolveClipRect(state.Source, _currentFrame);
            (x, y, w, h) = (rect.X, rect.Y, rect.Width, rect.Height);
        }
        else if (_elements.TryGetValue(state.Id, out var element))
        {
            var rect = element.LogicalRect;
            x = rect.X;
            y = rect.Y;
            w = rect.Width;
            h = rect.Height;
            if (element is ClipElementUIInteractableAdapter adapter)
                (x, y, w, h) = VideoClipRotation.GetBounds(adapter.Source.Rotation, x, y, w, h);
        }

        var scale = renderRect.Width / _videoWidth;
        state.SetBounds(new Rect(
            renderRect.X + x * scale,
            renderRect.Y + y * scale,
            Math.Max(0, w * scale),
            Math.Max(0, h * scale)));
    }

    private Rect ResolveClipRect(IClip clip, uint frame)
    {
        double x = clip.TargetX;
        double y = clip.TargetY;
        double w = clip.TargetWidth > 0 ? clip.TargetWidth : _videoWidth;
        double h = clip.TargetHeight > 0 ? clip.TargetHeight : _videoHeight;
        float rotation = clip.Rotation;
        ApplyPositionProviders(clip, frame, ref x, ref y, ref w, ref h, ref rotation);
        (x, y, w, h) = VideoClipRotation.GetBounds(rotation, x, y, w, h);
        return new Rect(x, y, w, h);
    }

    private void ApplyPositionProviders(IClip clip, uint frame, ref double x, ref double y, ref double w, ref double h, ref float rotation)
    {
        if (clip.EffectsInstances is null) return;

        foreach (var effect in clip.EffectsInstances.Where(e => e.Enabled).OrderBy(e => e.Index))
        {
            ClipPositionTuple position;
            if (effect is IContinuousClipPositionProvider continuous)
            {
                position = continuous.GetPosition(clip, frame, (int)_videoWidth, (int)_videoHeight);
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
                rotation += position.Rotation;
            }
            else
            {
                x = position.TargetX;
                y = position.TargetY;
                if (position.TargetWidth > 0) w = position.TargetWidth;
                if (position.TargetHeight > 0) h = position.TargetHeight;
                rotation = position.Rotation;
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

    private sealed class CheckerboardDrawable : IDrawable
    {
        private const float CellSize = 12;

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.FillColor = Color.FromArgb("#E0E0E0");
            canvas.FillRectangle(dirtyRect);
            canvas.FillColor = Color.FromArgb("#B8B8B8");

            for (float y = 0; y < dirtyRect.Bottom; y += CellSize)
            {
                for (float x = 0; x < dirtyRect.Right; x += CellSize)
                {
                    if (((int)(x / CellSize) + (int)(y / CellSize)) % 2 == 0)
                    {
                        canvas.FillRectangle(x, y, CellSize, CellSize);
                    }
                }
            }
        }
    }

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

        private static Image CreateImage() => new PreviewImage()
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
