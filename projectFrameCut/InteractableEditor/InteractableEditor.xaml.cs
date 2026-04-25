using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Shared;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace projectFrameCut.InteractableEditor
{
    public partial class InteractableEditor : ContentView
    {
        private enum ResizeHandle
        {
            TopLeft,
            TopRight,
            BottomLeft,
            BottomRight
        }

        private projectFrameCut.DraftStuff.ClipElementUI? _currentClip;
        private AssetItem? _currentAsset;
        private Func<Task>? _updateCallback;

        private const string SolidColorOutputWidthKey = "SolidColorOutputWidth";
        private const string SolidColorOutputHeightKey = "SolidColorOutputHeight";
        private const string SolidColorUseFixedOutputSizeKey = "SolidColorUseFixedOutputSize";
        private const string AllowFreeScaleResizeKey = "AllowFreeScaleResize";

        private double _canvasWidth = 800;
        private double _canvasHeight = 240;
        private double _videoWidth = 1920;
        private double _videoHeight = 1080;

        private double _startX, _startY, _startW, _startH;
        private Rect _baseRect;
        private bool _isTextClip = false;
        private bool _isClipPanInProgress;
        private bool _isHandleResizeInProgress;
        private int _activeTextEntryIndex = -1;
        private Rect _textEntryStartRect;
        private double _textEntryStartOriginX;
        private double _textEntryStartOriginY;
        private double _textEntryStartOffsetX;
        private double _textEntryStartOffsetY;
        private float _textEntryStartFontSize;
        private float? _textEntryStartWrappingWidth;
        private float? _textEntryStartStrokeWidth;

        private const double HandleSize = 15;
        private const double MinSize = 10;
        private const float MinTextFontSize = 1f;

        private readonly Dictionary<string, ClipOverlayState> _clipStates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, object> _previewSourceClips = new(StringComparer.Ordinal);
        private ClipOverlayState? _activeState;
        private Func<Task>? _previewRefreshCallback;
        private Func<string, Task>? _overlayClipTappedCallback;
        private Func<Task>? _blankAreaTappedCallback;
        private long _lastPreviewRefreshTick;
        private int _isPreviewRefreshRunning;
        private int _hasPendingPreviewRefresh;
        private int _isCommitUpdateRunning;
        private int _hasPendingCommitUpdate;
        private long _lastOverlayTapTick;

        private CancellationTokenSource? _commitUpdateDebounceCts;
        private readonly object _commitUpdateDebounceLock = new();

        private const int PreviewRefreshThrottleMs = 180;
        private const int CommitUpdateDebounceMs = 220;
        private const int OverlayTapBlankSuppressMs = 180;

        public ConcurrentDictionary<string, ClipElementUI> Clips { get; private set; } = new();

        // 用于直接显示DraftPage中的所有clips
        private IReadOnlyDictionary<string, ClipElementUI>? _allClips;
        private uint _currentFrame;
        private double _framePerPixel = 1d;
        private double _tracksZoomOffset = 1d;
        private float _secondPerFrameRatio = 1f;

        public bool ShowRenderRectOverlay { get; set; } = true;
        public bool ShowClipPreviewOverlays
        {
            get;
            set
            {
                if (field == value)
                {
                    return;
                }

                field = value;
                foreach (var state in _clipStates.Values)
                {
                    state.RefreshPreviewVisibility();
                }
            }
        } = true;

        public bool ShowPreviewDebugOverlay
        {
            get;
            set
            {
                if (field == value)
                {
                    return;
                }

                field = value;
                UpdateVisuals();
            }
        } = false;

        public ContentView RealtimePreviewHost => LivePreviewerHost;
        public Image StaticPreviewOverlayImage => PreviewOverlayImage;

        public void SetRealtimePreviewContent(View? content)
        {
            if (!ReferenceEquals(LivePreviewerHost.Content, content))
            {
                LivePreviewerHost.Content = content;
            }
        }

        public void SetStaticPreviewVisible(bool isVisible)
        {
            PreviewOverlayImage.IsVisible = isVisible;
        }

        public InteractableEditor()
        {
            InitializeComponent();

            var canvasTap = new TapGestureRecognizer();
            canvasTap.Tapped += OnEditorCanvasTapped;
            EditorCanvas.GestureRecognizers.Add(canvasTap);
        }

        private sealed class ClipOverlayState
        {
            private sealed class TextEntryOverlayState
            {
                private readonly InteractableEditor _owner;
                private readonly ClipOverlayState _clipState;

                public TextEntryOverlayState(InteractableEditor owner, ClipOverlayState clipState, int entryIndex)
                {
                    _owner = owner;
                    _clipState = clipState;
                    EntryIndex = entryIndex;

                    Root = new AbsoluteLayout
                    {
                        InputTransparent = false,
                        CascadeInputTransparent = false,
                        IsVisible = false,
                        BackgroundColor = Colors.Transparent,
                        ZIndex = 4
                    };

                    Visual = new Border
                    {
                        Stroke = Colors.Lime,
                        StrokeThickness = 1.5,
                        BackgroundColor = Colors.Transparent,
                        InputTransparent = false,
                        ZIndex = 4
                    };

                    HandleTL = CreateHandle();
                    HandleTR = CreateHandle();
                    HandleBL = CreateHandle();
                    HandleBR = CreateHandle();

                    SizeLabel = new Label
                    {
                        IsVisible = false,
                        Opacity = 0.95,
                        InputTransparent = true,
                        TextColor = Colors.White,
                        BackgroundColor = Color.FromArgb("#88000000"),
                        Padding = new Thickness(4, 2),
                        FontSize = 10,
                        LineBreakMode = LineBreakMode.NoWrap,
                        HorizontalOptions = LayoutOptions.Start,
                        VerticalOptions = LayoutOptions.Start,
                        ZIndex = 5
                    };

                    Pan = new PanGestureRecognizer();
                    Pan.PanUpdated += (_, e) => _owner.OnTextEntryPanUpdated(_clipState, EntryIndex, e);

                    TlPan = new PanGestureRecognizer();
                    TlPan.PanUpdated += (_, e) => _owner.OnTextEntryResizePanUpdated(_clipState, EntryIndex, ResizeHandle.TopLeft, e);

                    TrPan = new PanGestureRecognizer();
                    TrPan.PanUpdated += (_, e) => _owner.OnTextEntryResizePanUpdated(_clipState, EntryIndex, ResizeHandle.TopRight, e);

                    BlPan = new PanGestureRecognizer();
                    BlPan.PanUpdated += (_, e) => _owner.OnTextEntryResizePanUpdated(_clipState, EntryIndex, ResizeHandle.BottomLeft, e);

                    BrPan = new PanGestureRecognizer();
                    BrPan.PanUpdated += (_, e) => _owner.OnTextEntryResizePanUpdated(_clipState, EntryIndex, ResizeHandle.BottomRight, e);

                    var rootTap = new TapGestureRecognizer();
                    rootTap.Tapped += (_, _) => _owner.OnTextEntryOverlayTapped(_clipState, EntryIndex);
                    Root.GestureRecognizers.Add(rootTap);

                    Visual.GestureRecognizers.Add(Pan);
                    HandleTL.GestureRecognizers.Add(TlPan);
                    HandleTR.GestureRecognizers.Add(TrPan);
                    HandleBL.GestureRecognizers.Add(BlPan);
                    HandleBR.GestureRecognizers.Add(BrPan);

                    Root.Children.Add(Visual);
                    Root.Children.Add(HandleTL);
                    Root.Children.Add(HandleTR);
                    Root.Children.Add(HandleBL);
                    Root.Children.Add(HandleBR);
                    Root.Children.Add(SizeLabel);
                }

                public int EntryIndex { get; set; }
                public AbsoluteLayout Root { get; }
                public Border Visual { get; }
                public BoxView HandleTL { get; }
                public BoxView HandleTR { get; }
                public BoxView HandleBL { get; }
                public BoxView HandleBR { get; }
                public Label SizeLabel { get; }
                public PanGestureRecognizer Pan { get; }
                public PanGestureRecognizer TlPan { get; }
                public PanGestureRecognizer TrPan { get; }
                public PanGestureRecognizer BlPan { get; }
                public PanGestureRecognizer BrPan { get; }

                public void RefreshGestureRecognizers()
                {
                    Visual.GestureRecognizers.Clear();
                    HandleTL.GestureRecognizers.Clear();
                    HandleTR.GestureRecognizers.Clear();
                    HandleBL.GestureRecognizers.Clear();
                    HandleBR.GestureRecognizers.Clear();

                    Visual.GestureRecognizers.Add(Pan);
                    HandleTL.GestureRecognizers.Add(TlPan);
                    HandleTR.GestureRecognizers.Add(TrPan);
                    HandleBL.GestureRecognizers.Add(BlPan);
                    HandleBR.GestureRecognizers.Add(BrPan);
                }

                public void UpdateLayout(double x, double y, double w, double h, bool showHandles, bool showSizeLabel, string? sizeText)
                {
                    Root.IsVisible = true;
                    AbsoluteLayout.SetLayoutBounds(Root, new Rect(x, y, w, h));
                    AbsoluteLayout.SetLayoutBounds(Visual, new Rect(0, 0, w, h));
                    Visual.IsVisible = true;

                    var handleSize = HandleSize;
                    AbsoluteLayout.SetLayoutBounds(HandleTL, new Rect(-handleSize / 2, -handleSize / 2, handleSize, handleSize));
                    AbsoluteLayout.SetLayoutBounds(HandleTR, new Rect(w - handleSize / 2, -handleSize / 2, handleSize, handleSize));
                    AbsoluteLayout.SetLayoutBounds(HandleBL, new Rect(-handleSize / 2, h - handleSize / 2, handleSize, handleSize));
                    AbsoluteLayout.SetLayoutBounds(HandleBR, new Rect(w - handleSize / 2, h - handleSize / 2, handleSize, handleSize));

                    HandleTL.IsVisible = showHandles;
                    HandleTR.IsVisible = showHandles;
                    HandleBL.IsVisible = showHandles;
                    HandleBR.IsVisible = showHandles;

                    if (showSizeLabel)
                    {
                        SizeLabel.Text = sizeText ?? string.Empty;
                        SizeLabel.IsVisible = !string.IsNullOrWhiteSpace(SizeLabel.Text);
                        AbsoluteLayout.SetLayoutBounds(SizeLabel, new Rect(2, 2, AbsoluteLayout.AutoSize, AbsoluteLayout.AutoSize));
                    }
                    else
                    {
                        SizeLabel.Text = string.Empty;
                        SizeLabel.IsVisible = false;
                    }
                }

                public void Hide()
                {
                    _ = MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        Root.IsVisible = false;
                        SizeLabel.IsVisible = false;
                    });
                }
            }

            private readonly InteractableEditor _owner;
            private readonly List<TextEntryOverlayState> _textEntryStates = new();
            public string ClipId { get; init; }

            public ClipOverlayState(InteractableEditor owner, string clipId)
            {
                _owner = owner;
                ClipId = clipId;
                Root = new AbsoluteLayout
                {
                    InputTransparent = true,
                    CascadeInputTransparent = false,
                    IsVisible = false,
                    ZIndex = 101,
                    BackgroundColor = Colors.Transparent
                };

                ClipVisual = new Border
                {
                    Stroke = Colors.Yellow,
                    StrokeThickness = 2,
                    BackgroundColor = Colors.Transparent,
                    InputTransparent = false
                };

                PreviewHost = new ContentView
                {
                    InputTransparent = true,
                    BackgroundColor = Colors.Transparent,
                    IsVisible = false,
                    ZIndex = 1,
                    HorizontalOptions = LayoutOptions.Start,
                    VerticalOptions = LayoutOptions.Start,
                    AnchorX = 0,
                    AnchorY = 0
                };

                HandleTL = CreateHandle();
                HandleTR = CreateHandle();
                HandleBL = CreateHandle();
                HandleBR = CreateHandle();

                SizeLabel = new Label
                {
                    IsVisible = false,
                    Opacity = 0.95,
                    InputTransparent = true,
                    TextColor = Colors.White,
                    BackgroundColor = Color.FromArgb("#88000000"),
                    Padding = new Thickness(6, 2),
                    FontSize = 10,
                    LineBreakMode = LineBreakMode.NoWrap,
                    HorizontalOptions = LayoutOptions.Start,
                    VerticalOptions = LayoutOptions.Start,
                    ZIndex = 3
                };

                DebugLabel = new Label
                {
                    IsVisible = false,
                    Opacity = 0.95,
                    InputTransparent = true,
                    TextColor = Colors.Lime,
                    BackgroundColor = Color.FromArgb("#66000000"),
                    Padding = new Thickness(4, 2),
                    FontSize = 8,
                    LineBreakMode = LineBreakMode.WordWrap,
                    MaxLines = 4,
                    HorizontalOptions = LayoutOptions.Start,
                    VerticalOptions = LayoutOptions.Start,
                    ZIndex = 6
                };

                ClipPan = new PanGestureRecognizer();
                ClipPan.PanUpdated += (_, e) => _owner.OnClipPanUpdated(this, e);

                TlPan = new PanGestureRecognizer();
                TlPan.PanUpdated += (_, e) => _owner.OnResizePanUpdated(this, ResizeHandle.TopLeft, e);

                TrPan = new PanGestureRecognizer();
                TrPan.PanUpdated += (_, e) => _owner.OnResizePanUpdated(this, ResizeHandle.TopRight, e);

                BlPan = new PanGestureRecognizer();
                BlPan.PanUpdated += (_, e) => _owner.OnResizePanUpdated(this, ResizeHandle.BottomLeft, e);

                BrPan = new PanGestureRecognizer();
                BrPan.PanUpdated += (_, e) => _owner.OnResizePanUpdated(this, ResizeHandle.BottomRight, e);

                var rootTap = new TapGestureRecognizer();
                rootTap.Tapped += (_, _) => _owner.OnClipOverlayTapped(this);
                Root.GestureRecognizers.Add(rootTap);

                ClipVisual.GestureRecognizers.Add(ClipPan);
                HandleTL.GestureRecognizers.Add(TlPan);
                HandleTR.GestureRecognizers.Add(TrPan);
                HandleBL.GestureRecognizers.Add(BlPan);
                HandleBR.GestureRecognizers.Add(BrPan);

                Root.Children.Add(ClipVisual);
                Root.Children.Add(PreviewHost);
                Root.Children.Add(HandleTL);
                Root.Children.Add(HandleTR);
                Root.Children.Add(HandleBL);
                Root.Children.Add(HandleBR);
                Root.Children.Add(SizeLabel);
                Root.Children.Add(DebugLabel);

            }

            public AbsoluteLayout Root { get; }
            public Border ClipVisual { get; }
            public ContentView PreviewHost { get; }
            public BoxView HandleTL { get; }
            public BoxView HandleTR { get; }
            public BoxView HandleBL { get; }
            public BoxView HandleBR { get; }
            public Label SizeLabel { get; }
            public Label DebugLabel { get; }
            public PanGestureRecognizer ClipPan { get; }
            public PanGestureRecognizer TlPan { get; }
            public PanGestureRecognizer TrPan { get; }
            public PanGestureRecognizer BlPan { get; }
            public PanGestureRecognizer BrPan { get; }

            private TextEntryOverlayState GetOrCreateTextEntryState(int entryIndex)
            {
                while (_textEntryStates.Count <= entryIndex)
                {
                    var state = new TextEntryOverlayState(_owner, this, _textEntryStates.Count);
                    _textEntryStates.Add(state);
                    Root.Children.Add(state.Root);
                }

                var existing = _textEntryStates[entryIndex];
                existing.EntryIndex = entryIndex;
                return existing;
            }

            private static BoxView CreateHandle()
            {
                return new BoxView
                {
                    Color = Colors.Cyan,
                    WidthRequest = HandleSize,
                    HeightRequest = HandleSize,
                    InputTransparent = false,
                    ZIndex = int.MaxValue
                };
            }

            public void RefreshGestureRecognizers()
            {
                ClipVisual.GestureRecognizers.Clear();
                HandleTL.GestureRecognizers.Clear();
                HandleTR.GestureRecognizers.Clear();
                HandleBL.GestureRecognizers.Clear();
                HandleBR.GestureRecognizers.Clear();

                ClipVisual.GestureRecognizers.Add(ClipPan);
                HandleTL.GestureRecognizers.Add(TlPan);
                HandleTR.GestureRecognizers.Add(TrPan);
                HandleBL.GestureRecognizers.Add(BlPan);
                HandleBR.GestureRecognizers.Add(BrPan);

                foreach (var textEntryState in _textEntryStates)
                {
                    textEntryState.RefreshGestureRecognizers();
                }
            }

            private bool HasVisibleTextEntryOverlay()
            {
                foreach (var textEntryState in _textEntryStates)
                {
                    if (textEntryState.Root.IsVisible)
                    {
                        return true;
                    }
                }

                return false;
            }

            private static bool CanApplyVisualProperty(VisualElement element)
            {
                var handler = element.Handler;
                return handler is null || handler.PlatformView is not null;
            }

            private void UpdateRootInputTransparency()
            {
                if (!CanApplyVisualProperty(Root))
                {
                    return;
                }

                Root.InputTransparent = !_owner.ShouldAllowOverlayTapSelection(this)
                    || (!ClipVisual.IsVisible && !PreviewHost.IsVisible && !HasVisibleTextEntryOverlay());
            }

            public void UpdateTextEntryLayout(int entryIndex, double x, double y, double w, double h, bool showHandles, bool showSizeLabel, string? sizeText)
            {
                var textEntryState = GetOrCreateTextEntryState(entryIndex);
                textEntryState.UpdateLayout(x, y, w, h, showHandles, showSizeLabel, sizeText);
                UpdateRootInputTransparency();
            }

            public void HideTextEntryOverlaysBeyond(int visibleCount)
            {
                if (visibleCount < 0)
                {
                    visibleCount = 0;
                }

                for (var i = visibleCount; i < _textEntryStates.Count; i++)
                {
                    _textEntryStates[i].Hide();
                }

                UpdateRootInputTransparency();
            }

            public void HideTextEntryOverlays()
            {
                HideTextEntryOverlaysBeyond(0);
            }

            public void UpdateLayout(double displayX, double displayY, double displayW, double displayH, double logicalW, double logicalH, bool showHandles, bool showSizeLabel, string? sizeText, bool showClipVisual)
            {
                Root.IsVisible = true;
                AbsoluteLayout.SetLayoutBounds(Root, new Rect(displayX, displayY, displayW, displayH));
                AbsoluteLayout.SetLayoutBounds(ClipVisual, new Rect(0, 0, displayW, displayH));
                ClipVisual.IsVisible = showClipVisual;
                UpdatePreviewHostLayout(displayW, displayH, logicalW, logicalH);
                UpdateRootInputTransparency();

                double handleSize = HandleSize;
                AbsoluteLayout.SetLayoutBounds(HandleTL, new Rect(-handleSize / 2, -handleSize / 2, handleSize, handleSize));
                AbsoluteLayout.SetLayoutBounds(HandleTR, new Rect(displayW - handleSize / 2, -handleSize / 2, handleSize, handleSize));
                AbsoluteLayout.SetLayoutBounds(HandleBL, new Rect(-handleSize / 2, displayH - handleSize / 2, handleSize, handleSize));
                AbsoluteLayout.SetLayoutBounds(HandleBR, new Rect(displayW - handleSize / 2, displayH - handleSize / 2, handleSize, handleSize));

                HandleTL.IsVisible = showHandles;
                HandleTR.IsVisible = showHandles;
                HandleBL.IsVisible = showHandles;
                HandleBR.IsVisible = showHandles;

                if (showSizeLabel)
                {
                    SizeLabel.Text = sizeText ?? string.Empty;
                    SizeLabel.IsVisible = !string.IsNullOrWhiteSpace(SizeLabel.Text);
                    AbsoluteLayout.SetLayoutBounds(SizeLabel, new Rect(4, 4, AbsoluteLayout.AutoSize, AbsoluteLayout.AutoSize));
                }
                else
                {
                    SizeLabel.Text = string.Empty;
                    SizeLabel.IsVisible = false;
                }
            }

            public void Hide()
            {
                Root.IsVisible = false;
                PreviewHost.IsVisible = false;
                PreviewHost.Content = null;
                SizeLabel.IsVisible = false;
                DebugLabel.IsVisible = false;
                HideTextEntryOverlays();
            }

            public void UpdateDebugInfo(bool isVisible, string? text, double displayW, double displayH)
            {
                if (!isVisible || string.IsNullOrWhiteSpace(text))
                {
                    DebugLabel.Text = string.Empty;
                    DebugLabel.IsVisible = false;
                    return;
                }

                DebugLabel.Text = text;
                DebugLabel.IsVisible = true;
                var y = Math.Max(4d, displayH - 42d);
                AbsoluteLayout.SetLayoutBounds(DebugLabel, new Rect(4, y, Math.Max(8d, displayW - 8d), AbsoluteLayout.AutoSize));
            }

            public bool HasPreviewView => PreviewHost.Content is not null;

            public void SetPreviewView(View? view, bool keepExistingWhenNull = false)
            {
                if (view is null && keepExistingWhenNull)
                {
                    UpdatePreviewHostVisibility();
                    return;
                }

                if (!ReferenceEquals(PreviewHost.Content, view))
                {
                    PreviewHost.Content = view;
                }

                UpdatePreviewHostVisibility();
            }

            public void RefreshPreviewVisibility()
            {
                UpdatePreviewHostVisibility();
            }

            private void UpdatePreviewHostLayout(double displayW, double displayH, double logicalW, double logicalH)
            {
                if (logicalW <= 0 || logicalH <= 0 || displayW <= 0 || displayH <= 0)
                {
                    UpdatePreviewHostVisibility();
                    PreviewHost.Scale = 1d;
                    AbsoluteLayout.SetLayoutBounds(PreviewHost, new Rect(0, 0, 1, 1));
                    return;
                }

                PreviewHost.WidthRequest = logicalW;
                PreviewHost.HeightRequest = logicalH;
                PreviewHost.Scale = Math.Clamp(displayW / logicalW, 0.0001d, 1000d);
                AbsoluteLayout.SetLayoutBounds(PreviewHost, new Rect(0, 0, logicalW, logicalH));
                UpdatePreviewHostVisibility();
            }

            private void UpdatePreviewHostVisibility()
            {
                if (CanApplyVisualProperty(PreviewHost))
                {
                    PreviewHost.IsVisible = _owner.ShouldShowPreviewHost(this) && PreviewHost.Content is not null;
                }

                UpdateRootInputTransparency();
            }
        }

        public void ConfigurePreviewRefresh(Func<Task>? refreshCallback)
        {
            _previewRefreshCallback = refreshCallback;
        }

        public void ConfigureOverlayClipTap(Func<string, Task>? tapCallback)
        {
            _overlayClipTappedCallback = tapCallback;
            foreach (var state in _clipStates.Values)
            {
                state.RefreshPreviewVisibility();
            }
            UpdateVisuals();
        }

        public void ConfigureBlankAreaTap(Func<Task>? tapCallback)
        {
            _blankAreaTappedCallback = tapCallback;
        }

        protected override void OnSizeAllocated(double width, double height)
        {
            base.OnSizeAllocated(width, height);
            UpdateCanvasSize(width, height);
        }

        public void Init(Func<Task> updateCallback, double videoWidth, double videoHeight)
        {
            _updateCallback = updateCallback;
            _videoWidth = videoWidth;
            _videoHeight = videoHeight;
        }

        public void UpdateCanvasSize(double width, double height)
        {
            _canvasWidth = width;
            _canvasHeight = height;
            UpdateVisuals();
        }

        public void UpdateVideoResolution(double width, double height)
        {
            _videoWidth = width;
            _videoHeight = height;
            UpdateVisuals();
        }

        private ClipOverlayState GetOrCreateClipState(ClipElementUI clip)
            => GetOrCreateClipState(clip.Id);

        private ClipOverlayState GetOrCreateClipState(string clipId)
        {
            if (_clipStates.TryGetValue(clipId, out var state))
            {
                return state;
            }

            state = new ClipOverlayState(this, clipId);
            try
            {
                Dispatcher.Dispatch(() =>
                {
                    ClipStatesHost.Children.Add(state.Root);
                    _clipStates[clipId] = state;
                });
                return state;
            }
            catch (Exception e1)
            {
                Log(e1, "add the clip overlay state for clip " + clipId, this);
                try
                {
                    ClipStatesHost.Children.Remove(state.Root);
                }
                catch (Exception e2)
                {
                    Log(e2, "Exception fallback:remove the clip overlay state for clip " + clipId, this);
                }

                try
                {
                    state.Root.Handler?.DisconnectHandler();
                }
                catch (Exception e3)
                {
                    Log(e3, "Exception fallback:disconnect the handler for clip " + clipId, this);
                }

                throw;
            }
        }

        private bool ShouldAllowOverlayTapSelection(ClipOverlayState state)
        {
            if (_overlayClipTappedCallback is null)
            {
                return false;
            }

            return !_isHandleResizeInProgress
                && !string.IsNullOrWhiteSpace(state.ClipId);
        }

        private bool ShouldShowPreviewHost(ClipOverlayState state)
        {
            if (!ShowClipPreviewOverlays)
            {
                return false;
            }

            return !ShouldSuppressPreviewForResize(state.ClipId);
        }

        private void OnClipOverlayTapped(ClipOverlayState state)
        {
            if (!ShouldAllowOverlayTapSelection(state))
            {
                return;
            }

            var callback = _overlayClipTappedCallback;
            if (callback is null)
            {
                return;
            }

            Interlocked.Exchange(ref _lastOverlayTapTick, Environment.TickCount64);

            _ = InvokeOverlayClipTappedAsync(callback, state.ClipId);
        }

        private void OnTextEntryOverlayTapped(ClipOverlayState state, int entryIndex)
        {
            _activeTextEntryIndex = entryIndex;
            OnClipOverlayTapped(state);
        }

        private void OnEditorCanvasTapped(object? sender, TappedEventArgs e)
        {
            var callback = _blankAreaTappedCallback;
            if (callback is null)
            {
                return;
            }

            var nowTick = Environment.TickCount64;
            var overlayTapTick = Interlocked.Read(ref _lastOverlayTapTick);
            if (nowTick - overlayTapTick <= OverlayTapBlankSuppressMs)
            {
                return;
            }

            var tapPoint = e.GetPosition(EditorCanvas);
            if (tapPoint is null)
            {
                return;
            }

            if (IsPointInsideAnyVisibleClipState(tapPoint.Value))
            {
                return;
            }

            _activeTextEntryIndex = -1;
            _ = InvokeBlankAreaTappedAsync(callback);
        }

        private bool IsPointInsideAnyVisibleClipState(Point tapPoint)
        {
            var hostOffsetX = ClipStatesHost.X;
            var hostOffsetY = ClipStatesHost.Y;

            foreach (var state in _clipStates.Values)
            {
                if (!state.Root.IsVisible)
                {
                    continue;
                }

                var bounds = AbsoluteLayout.GetLayoutBounds(state.Root);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    continue;
                }

                var left = hostOffsetX + bounds.X;
                var top = hostOffsetY + bounds.Y;
                var right = left + bounds.Width;
                var bottom = top + bounds.Height;

                if (tapPoint.X >= left && tapPoint.X <= right && tapPoint.Y >= top && tapPoint.Y <= bottom)
                {
                    return true;
                }
            }

            return false;
        }

        private async Task InvokeOverlayClipTappedAsync(Func<string, Task> callback, string clipId)
        {
            try
            {
                await callback(clipId);
            }
            catch (Exception ex)
            {
                LogDiagnostic($"Overlay clip tap callback failed: {ex.Message}");
            }
        }

        private async Task InvokeBlankAreaTappedAsync(Func<Task> callback)
        {
            try
            {
                await callback();
            }
            catch (Exception ex)
            {
                LogDiagnostic($"Blank area tap callback failed: {ex.Message}");
            }
        }

        private void SetActiveState(ClipOverlayState? state)
        {
            _ = MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (ReferenceEquals(_activeState, state))
                {
                    if (_activeState is not null)
                    {
                        _activeState.Root.IsVisible = true;
                    }

                    return;
                }

                var previous = _activeState;
                if (previous is not null)
                {
                    previous.HandleTL.IsVisible = false;
                    previous.HandleTR.IsVisible = false;
                    previous.HandleBL.IsVisible = false;
                    previous.HandleBR.IsVisible = false;
                    previous.SizeLabel.IsVisible = false;
                    previous.HideTextEntryOverlays();
                }

                _activeState = state;

                if (_activeState is not null)
                {
                    _activeState.Root.IsVisible = true;
                }
            });

        }

        private void ClearClipStates()
        {
            foreach (var state in _clipStates.Values)
            {
                state.Hide();
            }

            ClipStatesHost.Children.Clear();
            _clipStates.Clear();
            _previewSourceClips.Clear();
            _activeState = null;
        }

        public void SetClip(projectFrameCut.DraftStuff.ClipElementUI? clip, AssetItem? asset)
        {
            CancelPendingCommitUpdate();

            _currentClip = clip;
            _currentAsset = asset;
            _isClipPanInProgress = false;
            _isHandleResizeInProgress = false;
            _activeTextEntryIndex = -1;
            if (clip == null)
            {
                SetActiveState(null);
                //this.IsVisible = false;
                //this.InputTransparent = true;
                //RenderRectVisual.IsVisible = false;
                Interlocked.Exchange(ref _hasPendingPreviewRefresh, 0);
                return;
            }
            this.IsVisible = true;
            this.InputTransparent = false;

            _isTextClip = clip.ClipType == ClipMode.TextClip;
            if (_isTextClip)
            {
                if (TryResolveTextClipRect(clip, out var textX, out var textY, out var textW, out var textH))
                {
                    _baseRect = new Rect(textX, textY, textW, textH);
                }
                else
                {
                    _baseRect = new Rect(0, 0, _videoWidth, _videoHeight);
                }
            }
            else
            {
                _baseRect = new Rect(0, 0, _videoWidth, _videoHeight);
            }

            SetActiveState(GetOrCreateClipState(clip));
            UpdateVisuals();

            // 确保手势识别器在新的容器环境中正确工作
            RefreshGestureRecognizers();
        }

        /// <summary>
        /// 从DraftPage设置所有clips数据并设置回调以显示当前帧的所有clips
        /// </summary>
        public void SetClipsFromDraftPage(
            IReadOnlyDictionary<string, ClipElementUI> allClips,
            double framePerPixel,
            double tracksZoomOffset = 1d,
            float secondPerFrameRatio = 1f)
        {
            Clips = allClips as ConcurrentDictionary<string, ClipElementUI>
                ?? new ConcurrentDictionary<string, ClipElementUI>(allClips);
            _allClips = allClips;
            _framePerPixel = framePerPixel;
            _tracksZoomOffset = tracksZoomOffset;
            _secondPerFrameRatio = secondPerFrameRatio;
            UpdateVisuals();
        }

        public Task UpdateClips(ConcurrentDictionary<string, ClipElementUI> clips)
        {
            Clips = clips;
            _allClips = clips;
            UpdateVisuals();
            return Task.CompletedTask;
        }

        /// <summary>
        /// 更新当前帧号，用于显示该帧的所有clips
        /// </summary>
        public void UpdateCurrentFrame(uint frameNumber)
        {
            _currentFrame = frameNumber;
            UpdateVisuals();
        }

        public void SetCurrentFrame(uint currentFrame)
        {
            UpdateCurrentFrame(currentFrame);
        }

        private void RequestInteractivePreviewRefresh()
        {
            if (_currentClip is null || _previewRefreshCallback is null)
            {
                return;
            }

            Interlocked.Exchange(ref _hasPendingPreviewRefresh, 1);

            if (Interlocked.CompareExchange(ref _isPreviewRefreshRunning, 1, 0) == 0)
            {
                _ = RefreshInteractivePreviewCoreAsync();
            }
        }

        private async Task RefreshInteractivePreviewCoreAsync()
        {
            try
            {
                while (Interlocked.Exchange(ref _hasPendingPreviewRefresh, 0) == 1)
                {
                    var now = Environment.TickCount64;
                    var last = Interlocked.Read(ref _lastPreviewRefreshTick);
                    var waitMs = PreviewRefreshThrottleMs - (int)(now - last);
                    if (waitMs > 0)
                    {
                        await Task.Delay(waitMs);
                    }

                    Interlocked.Exchange(ref _lastPreviewRefreshTick, Environment.TickCount64);

                    var callback = _previewRefreshCallback;
                    if (callback is null || _currentClip is null)
                    {
                        break;
                    }

                    await callback();
                }
            }
            catch (Exception ex)
            {
                LogDiagnostic($"Dynamic preview refresh failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _isPreviewRefreshRunning, 0);

                // Handle race where a new request arrives between loop exit and flag reset.
                if (Volatile.Read(ref _hasPendingPreviewRefresh) == 1
                    && Interlocked.CompareExchange(ref _isPreviewRefreshRunning, 1, 0) == 0)
                {
                    _ = RefreshInteractivePreviewCoreAsync();
                }
            }
        }

        private Rect GetRenderRect()
        {
            if (_canvasHeight == 0 || _videoHeight == 0) return new Rect(0, 0, _canvasWidth, _canvasHeight);

            double ratioCanvas = _canvasWidth / _canvasHeight;
            double ratioVideo = _videoWidth / _videoHeight;

            double drawW, drawH, offX, offY;

            if (ratioVideo > ratioCanvas)
            {
                drawW = _canvasWidth;
                drawH = drawW / ratioVideo;
                offX = 0;
                offY = (_canvasHeight - drawH) / 2;
            }
            else
            {
                drawH = _canvasHeight;
                drawW = drawH * ratioVideo;
                offY = 0;
                offX = (_canvasWidth - drawW) / 2;
            }

            return new Rect(offX, offY, drawW, drawH);
        }

        private void UpdateVisuals()
        {
            if (_videoWidth <= 0 || _videoHeight <= 0 || _canvasWidth <= 0 || _canvasHeight <= 0)
                return;

            Rect renderRect = GetRenderRect();
            double scale = renderRect.Width / _videoWidth;

            UpdateRenderRectOverlay(renderRect);

            if (_currentClip is not null)
            {
                var activeState = _activeState ?? GetOrCreateClipState(_currentClip);
                SetActiveState(activeState);
            }
            else
            {
                SetActiveState(null);
            }

            // 当使用DraftPage中的所有clips时，先处理多clips模式
            if (_allClips is not null)
            {
                UpdateVisualsForMultipleClips(renderRect, scale);
                return;
            }

            foreach (var entry in _clipStates)
            {
                var clipId = entry.Key;
                var state = entry.Value;

                UpdateClipStateZIndex(state, clipId);

                if (!TryResolveClipRect(clipId, out var x, out var y, out var w, out var h, out var clipType, out var isCurrentClip))
                {
                    state.Hide();
                    continue;
                }

                // Clamp to keep UI stable.
                w = Math.Clamp(w, MinSize, _videoWidth);
                h = Math.Clamp(h, MinSize, _videoHeight);
                x = Math.Clamp(x, 0, _videoWidth - w);
                y = Math.Clamp(y, 0, _videoHeight - h);

                double displayX = renderRect.X + x * scale;
                double displayY = renderRect.Y + y * scale;
                double displayW = w * scale;
                double displayH = h * scale;

                var isCurrentTextClip = isCurrentClip && clipType == ClipMode.TextClip;
                var showHandles = isCurrentClip && !isCurrentTextClip;
                var showSizeLabel = isCurrentClip && _isHandleResizeInProgress;

                state.UpdateLayout(
                    displayX,
                    displayY,
                    displayW,
                    displayH,
                    w,
                    h,
                    showHandles,
                    showSizeLabel,
                    showSizeLabel ? $"{Math.Round(w)} x {Math.Round(h)}" : null,
                    isCurrentClip && !isCurrentTextClip);

                UpdateTextEntryOverlays(
                    state,
                    isCurrentClip ? _currentClip : null,
                    isCurrentClip,
                    clipX: x,
                    clipY: y,
                    scale: scale);

                UpdatePreviewDebugOverlay(state, clipId, w, h, displayW, displayH);
            }

            ReorderClipStateRootsByZIndex();
        }

        private void UpdateTextEntryOverlays(ClipOverlayState state, ClipElementUI? clip, bool isCurrentClip, double clipX, double clipY, double scale)
        {
            if (!isCurrentClip
                || clip is null
                || clip.ClipType != ClipMode.TextClip
                || !TryGetTextEntries(clip, out var entries))
            {
                state.HideTextEntryOverlays();
                return;
            }

            if (_activeTextEntryIndex >= entries.Count)
            {
                _activeTextEntryIndex = -1;
            }

            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (!TryMeasureTextEntryRect(entry, out var entryX, out var entryY, out var entryW, out var entryH))
                {
                    entryX = entry.x;
                    entryY = entry.y;
                    entryW = MinSize;
                    entryH = MinSize;
                }

                entryW = Math.Clamp(entryW, MinSize, _videoWidth);
                entryH = Math.Clamp(entryH, MinSize, _videoHeight);
                entryX = Math.Clamp(entryX, 0, _videoWidth - entryW);
                entryY = Math.Clamp(entryY, 0, _videoHeight - entryH);

                var localX = (entryX - clipX) * scale;
                var localY = (entryY - clipY) * scale;
                var localW = Math.Max(1d, entryW * scale);
                var localH = Math.Max(1d, entryH * scale);

                var showSizeLabel = _isHandleResizeInProgress && _activeTextEntryIndex == i;
                state.UpdateTextEntryLayout(
                    i,
                    localX,
                    localY,
                    localW,
                    localH,
                    showHandles: true,
                    showSizeLabel: showSizeLabel,
                    sizeText: showSizeLabel ? $"{Math.Round(entryW)} x {Math.Round(entryH)}" : null);
            }

            state.HideTextEntryOverlaysBeyond(entries.Count);
        }

        private void UpdatePreviewDebugOverlay(ClipOverlayState state, string clipId, double logicalW, double logicalH, double displayW, double displayH)
        {
            if (!ShowPreviewDebugOverlay)
            {
                state.UpdateDebugInfo(false, null, displayW, displayH);
                return;
            }

            var content = state.PreviewHost.Content;
            var contentType = content?.GetType().Name ?? "null";
            var contentDebugTag = content?.AutomationId;
            var shortClipId = clipId.Length > 8 ? clipId[..8] : clipId;
            var info = $"dbg:{shortClipId} view:{state.HasPreviewView}/{state.PreviewHost.IsVisible} show:{ShouldShowPreviewHost(state)} sup:{ShouldSuppressPreviewForResize(clipId)}"
                + Environment.NewLine
                + $"L:{Math.Round(logicalW)}x{Math.Round(logicalH)} D:{Math.Round(displayW)}x{Math.Round(displayH)} T:{contentType}";

            if (!string.IsNullOrWhiteSpace(contentDebugTag))
            {
                info += Environment.NewLine + contentDebugTag;
            }

            state.UpdateDebugInfo(true, info, displayW, displayH);
        }

        public async Task<bool> ApplyPreparedPreviewsAsync(IReadOnlyList<DynamicPreview.PreparedPreview> preparedPreviews)
        {
            if (Dispatcher.IsDispatchRequired)
            {
                return await Dispatcher.DispatchAsync(() => ApplyPreparedPreviews(preparedPreviews));
            }

            return ApplyPreparedPreviews(preparedPreviews);
        }

        private bool ApplyPreparedPreviews(IReadOnlyList<DynamicPreview.PreparedPreview> preparedPreviews)
        {
            if (preparedPreviews.Count == 0)
            {
                if (IsInteractiveManipulationInProgress)
                {
                    var hasPreviewView = false;
                    foreach (var state in _clipStates.Values)
                    {
                        var keepExisting = ShouldKeepExistingPreviewFrame(state.ClipId);
                        state.SetPreviewView(null, keepExistingWhenNull: keepExisting);
                        hasPreviewView |= state.HasPreviewView;
                    }

                    UpdateVisuals();
                    return hasPreviewView;
                }
                else
                {
                    foreach (var state in _clipStates.Values)
                    {
                        state.SetPreviewView(null);
                    }

                    _previewSourceClips.Clear();

                    UpdateVisuals();
                    return false;
                }
            }

            var knownStates = new HashSet<string>(StringComparer.Ordinal);
            var hasVisiblePreview = false;

            foreach (var prepared in preparedPreviews)
            {
                if (string.IsNullOrWhiteSpace(prepared.ClipId))
                {
                    continue;
                }

                if (prepared.Source is not null)
                {
                    _previewSourceClips[prepared.ClipId] = prepared.Source;
                }

                var state = GetOrCreateClipState(prepared.ClipId);
                var suppressPreviewForResize = ShouldSuppressPreviewForResize(prepared.ClipId);

                knownStates.Add(prepared.ClipId);
                if (suppressPreviewForResize)
                {
                    state.SetPreviewView(null);
                    continue;
                }

                if (prepared.View is not null)
                {
                    var shouldDeferViewSwap = ShouldKeepExistingPreviewFrame(prepared.ClipId)
                        && state.HasPreviewView;
                    if (shouldDeferViewSwap)
                    {
                        // Keep current frame during drag/resize to avoid visible blink from replacing image views.
                        state.SetPreviewView(null, keepExistingWhenNull: true);
                        hasVisiblePreview |= state.HasPreviewView;
                    }
                    else
                    {
                        state.SetPreviewView(prepared.View);
                        hasVisiblePreview = true;
                    }
                }
                else
                {
                    // Keep the last good preview frame to avoid visible blink on transient null results.
                    state.SetPreviewView(null, keepExistingWhenNull: ShouldKeepExistingPreviewFrame(prepared.ClipId));
                    hasVisiblePreview |= state.HasPreviewView;
                }
            }

            foreach (var entry in _clipStates)
            {
                if (!knownStates.Contains(entry.Key))
                {
                    var keepExisting = ShouldKeepExistingPreviewFrame(entry.Key);
                    entry.Value.SetPreviewView(null, keepExistingWhenNull: keepExisting);
                    if (!keepExisting)
                    {
                        _previewSourceClips.Remove(entry.Key);
                    }
                }
            }

            UpdateVisuals();
            return hasVisiblePreview;
        }

        private bool TryResolveClipRect(string clipId, out double x, out double y, out double w, out double h, out ClipMode clipType, out bool isCurrentClip)
        {
            x = 0;
            y = 0;
            w = _videoWidth;
            h = _videoHeight;
            clipType = ClipMode.AudioClip;
            isCurrentClip = _currentClip is not null && string.Equals(_currentClip.Id, clipId, StringComparison.Ordinal);

            if (isCurrentClip)
            {
                clipType = _currentClip!.ClipType;
                GetCurrentRect(out x, out y, out w, out h);
                return true;
            }

            if (!_previewSourceClips.TryGetValue(clipId, out var sourceClip))
            {
                return false;
            }

            return TryResolveSourceClipRect(sourceClip, ref x, ref y, ref w, ref h, out clipType);
        }

        private bool TryResolveSourceClipRect(object sourceClip, ref double x, ref double y, ref double w, ref double h, out ClipMode clipType)
        {
            clipType = ClipMode.AudioClip;

            if (sourceClip is not ClipElementUI clip)
            {
                return false;
            }

            clipType = clip.ClipType;

            if (clip.ClipType == ClipMode.TextClip && TryResolveTextClipRect(clip, out var textX, out var textY, out var textW, out var textH))
            {
                x = textX;
                y = textY;
                w = textW;
                h = textH;
                return true;
            }

            x = clip.TargetX;
            y = clip.TargetY;
            if (clip.TargetWidth > 0)
            {
                w = clip.TargetWidth;
            }

            if (clip.TargetHeight > 0)
            {
                h = clip.TargetHeight;
            }

            if (clip.ClipType == ClipMode.SolidColorClip)
            {
                if (clip.TargetWidth > 0)
                {
                    w = clip.TargetWidth;
                }
                else if (clip is ClipElementUI solidClip)
                {
                    w = ReadSolidColorSize(solidClip.ExtraData, SolidColorOutputWidthKey, (int)Math.Round(w));
                }

                if (clip.TargetHeight > 0)
                {
                    h = clip.TargetHeight;
                }
                else if (clip is ClipElementUI solidClip)
                {
                    h = ReadSolidColorSize(solidClip.ExtraData, SolidColorOutputHeightKey, (int)Math.Round(h));
                }
            }

            return true;
        }

        private void UpdateRenderRectOverlay(Rect renderRect)
        {
            var visible = ShowRenderRectOverlay
                && renderRect.Width > 0
                && renderRect.Height > 0;

            if (!visible)
            {
                RenderRectVisual.IsVisible = false;
                return;
            }

            AbsoluteLayout.SetLayoutBounds(RenderRectVisual, new Rect(renderRect.X, renderRect.Y, renderRect.Width, renderRect.Height));
            RenderRectVisual.IsVisible = true;
        }

        private void UpdateVisualsForMultipleClips(Rect renderRect, double scale)
        {
            if (_allClips is null || _allClips.Count == 0)
            {
                ClearClipStates();
                return;
            }

            var activeClipIds = new HashSet<string>(StringComparer.Ordinal);

            // 遍历所有clips，筛选出在当前帧范围内的clips
            foreach (var clipEntry in _allClips)
            {
                var clip = clipEntry.Value;
                if (!IsClipVisibleInCurrentFrame(clip))
                {
                    continue;
                }

                activeClipIds.Add(clip.Id);
                var state = GetOrCreateClipState(clip.Id);
                UpdateClipStateZIndex(state, clip.Id);

                // 计算clip的位置和大小
                double x;
                double y;
                double w;
                double h;

                if (clip.ClipType == ClipMode.TextClip && TryResolveTextClipRect(clip, out var textX, out var textY, out var textW, out var textH))
                {
                    x = textX;
                    y = textY;
                    w = textW;
                    h = textH;
                }
                else
                {
                    x = clip.TargetX;
                    y = clip.TargetY;
                    w = clip.TargetWidth > 0 ? clip.TargetWidth : _videoWidth;
                    h = clip.TargetHeight > 0 ? clip.TargetHeight : _videoHeight;

                }

                // Clamp to keep UI stable.
                w = Math.Clamp(w, MinSize, _videoWidth);
                h = Math.Clamp(h, MinSize, _videoHeight);
                x = Math.Clamp(x, 0, _videoWidth - w);
                y = Math.Clamp(y, 0, _videoHeight - h);

                double displayX = renderRect.X + x * scale;
                double displayY = renderRect.Y + y * scale;
                double displayW = w * scale;
                double displayH = h * scale;

                bool isCurrentClip = _currentClip is not null
                    && string.Equals(_currentClip.Id, clip.Id, StringComparison.Ordinal);
                bool isCurrentTextClip = isCurrentClip && clip.ClipType == ClipMode.TextClip;
                bool showHandles = isCurrentClip && !isCurrentTextClip;
                bool showSizeLabel = isCurrentClip && _isHandleResizeInProgress;

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    state.UpdateLayout(
                        displayX,
                        displayY,
                        displayW,
                        displayH,
                        w,
                        h,
                        showHandles: showHandles,
                        showSizeLabel: showSizeLabel,
                        sizeText: showSizeLabel ? $"{Math.Round(w)} x {Math.Round(h)}" : null,
                        showClipVisual: isCurrentClip && !isCurrentTextClip);

                    UpdateTextEntryOverlays(
                        state,
                        clip,
                        isCurrentClip,
                        clipX: x,
                        clipY: y,
                        scale: scale);

                    UpdatePreviewDebugOverlay(state, clip.Id, w, h, displayW, displayH);
                });
            }

            // 隐藏不再活跃的clips
            var inactiveIds = _clipStates.Keys.Except(activeClipIds).ToList();
            MainThread.BeginInvokeOnMainThread(() =>
            {
                foreach (var inactiveId in inactiveIds)
                {
                    if (_clipStates.TryGetValue(inactiveId, out var state))
                    {
                        state.Hide();
                    }
                }

            });


            ReorderClipStateRootsByZIndex();
        }

        private void UpdateClipStateZIndex(ClipOverlayState state, string clipId)
        {
            if (_allClips is not null && _allClips.TryGetValue(clipId, out var allClip))
            {
                state.Root.ZIndex = ResolveClipOverlayZIndex(allClip);
                return;
            }

            if (_currentClip is not null && string.Equals(_currentClip.Id, clipId, StringComparison.Ordinal))
            {
                state.Root.ZIndex = ResolveClipOverlayZIndex(_currentClip);
                return;
            }

            if (_previewSourceClips.TryGetValue(clipId, out var sourceClip)
                && sourceClip is ClipElementUI previewClip)
            {
                state.Root.ZIndex = ResolveClipOverlayZIndex(previewClip);
                return;
            }

            state.Root.ZIndex = int.MinValue;
        }

        private static int ResolveClipOverlayZIndex(ClipElementUI clip)
        {
            // Keep overlay order aligned with timeline track order (lower track index on top).
            var layer = Math.Max(0, clip.origTrack ?? 0);
            var subLayer = Math.Max(0, clip.SubLayerIndex);
            var composite = (long)layer * 10000L + Math.Min(9999, subLayer);
            var zIndex = composite >= int.MaxValue ? int.MinValue : -((int)composite);
            //LogDiagnostic($"For clip {clip.Id}/{clip.DisplayName}, track:{layer}, sub:{subLayer}, ZIndex is {zIndex}");
            return zIndex;
        }

        private void ReorderClipStateRootsByZIndex()
        {
            if (_clipStates.Count <= 1 || ClipStatesHost.Children.Count <= 1)
            {
                return;
            }

            var orderedRoots = _clipStates.Values
                .Select(state => state.Root)
                .Where(root => ClipStatesHost.Children.Contains(root))
                .OrderBy(root => root.ZIndex)
                .ToList();

            if (orderedRoots.Count <= 1)
            {
                return;
            }

            var currentRoots = ClipStatesHost.Children.ToList();
            if (currentRoots.Count == orderedRoots.Count
                && !currentRoots.Where((root, index) => !ReferenceEquals(root, orderedRoots[index])).Any())
            {
                return;
            }

            foreach (var root in orderedRoots)
            {
                try
                {
                    Dispatcher.Dispatch(() =>
                    {
                        ClipStatesHost.Children.Remove(root);
                        ClipStatesHost.Children.Add(root);
                    });
                }
                catch (Exception ex)
                {
                    Log(ex, "reorder the clips overlay state", this);

                }
            }
        }

        private bool IsClipVisibleInCurrentFrame(ClipElementUI clip)
        {
            if (clip.IsExtraDataOptionIsTrue("ExtendToWholeDraft"))
            {
                return true;
            }

            // 计算clip的开始和结束帧
            // startFrame = PixelToFrame(translationX) / clip.SecondPerFrameRatio
            var clipPixelX = clip.Clip?.TranslationX ?? clip.layoutX;
            var clipPixelWidth = clip.Clip is null
                ? clip.origLength
                : ((clip.Clip.WidthRequest > 0)
                    ? clip.Clip.WidthRequest
                    : (clip.Clip.Width > 0 ? clip.Clip.Width : clip.origLength));
            if (clipPixelWidth <= 0)
            {
                clipPixelWidth = 1;
            }

            // PixelToFrame的计算方法：px * framePerPixel * tracksZoomOffset
            double framePerPixelValue = _framePerPixel * _tracksZoomOffset;
            if (framePerPixelValue <= 0) framePerPixelValue = 1d;

            var clipSecondPerFrameRatio = clip.SecondPerFrameRatio;
            if (clipSecondPerFrameRatio <= 0)
            {
                clipSecondPerFrameRatio = _secondPerFrameRatio > 0 ? _secondPerFrameRatio : 1f;
            }

            uint clipStartFrame = (uint)(clipPixelX * framePerPixelValue / clipSecondPerFrameRatio);
            uint clipDurationFrames = (uint)(clipPixelWidth * framePerPixelValue / clipSecondPerFrameRatio);
            if (clipDurationFrames == 0)
            {
                clipDurationFrames = 1;
            }
            uint clipEndFrame = clipStartFrame + clipDurationFrames;

            // 检查当前帧是否在clip的范围内
            return _currentFrame >= clipStartFrame && _currentFrame < clipEndFrame;
        }

        private void OnClipPanUpdated(ClipOverlayState state, PanUpdatedEventArgs e)
        {
            if (_currentClip == null || !ReferenceEquals(state, _activeState) || _isTextClip) return;

            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    _isClipPanInProgress = true;
                    GetCurrentRect(out _startX, out _startY, out _startW, out _startH);
                    LogDiagnostic($"[Pan] Started: Pos=({_startX:F1}, {_startY:F1}), Size=({_startW:F1}, {_startH:F1})");
                    break;

                case GestureStatus.Running:
                    // Get the render rectangle (video viewport on canvas)
                    Rect renderRect = GetRenderRect();
                    if (renderRect.Width <= 0 || renderRect.Height <= 0) break;

                    // Scale factor from screen to video coordinates
                    double scale = Math.Max(renderRect.Width, 0.001) / _videoWidth;
                    if (scale <= 0.001) break;

                    // Convert gesture pan amount to video coordinates
                    double deltaX = e.TotalX / scale;
                    double deltaY = e.TotalY / scale;

                    // Calculate new position in video space
                    double newVisualX = _startX + deltaX;
                    double newVisualY = _startY + deltaY;

                    UpdateClipEffects(newVisualX, newVisualY, _startW, _startH);

                    UpdateVisuals();
                    RequestInteractivePreviewRefreshIfMissing(state);
                    break;

                case GestureStatus.Completed:
                case GestureStatus.Canceled:
                    _isClipPanInProgress = false;
                    GetCurrentRect(out var finalX, out var finalY, out _, out _);
                    LogDiagnostic($"[Pan] Completed: FinalPos=({finalX:F1}, {finalY:F1})");
                    RequestCommitUpdate();
                    break;
            }
        }

        private void OnTextEntryPanUpdated(ClipOverlayState state, int entryIndex, PanUpdatedEventArgs e)
        {
            if (_currentClip == null || !_isTextClip || !ReferenceEquals(state, _activeState)) return;

            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    if (!TryPrepareTextEntryManipulation(entryIndex))
                    {
                        return;
                    }

                    _isClipPanInProgress = true;
                    _isHandleResizeInProgress = false;
                    LogDiagnostic($"[TextEntryPan] Started: entry={entryIndex}, Rect=({_textEntryStartRect.X:F1}, {_textEntryStartRect.Y:F1}, {_textEntryStartRect.Width:F1}, {_textEntryStartRect.Height:F1})");
                    break;

                case GestureStatus.Running:
                    if (!_isClipPanInProgress || _activeTextEntryIndex != entryIndex)
                    {
                        break;
                    }

                    var renderRect = GetRenderRect();
                    if (renderRect.Width <= 0 || renderRect.Height <= 0)
                    {
                        break;
                    }

                    var scale = Math.Max(renderRect.Width, 0.001) / _videoWidth;
                    if (scale <= 0.001)
                    {
                        break;
                    }

                    var deltaX = e.TotalX / scale;
                    var deltaY = e.TotalY / scale;
                    if (!TryUpdateTextEntryPositionFromPan(entryIndex, deltaX, deltaY))
                    {
                        break;
                    }

                    UpdateVisuals();
                    RequestInteractivePreviewRefreshIfMissing(state);
                    break;

                case GestureStatus.Completed:
                case GestureStatus.Canceled:
                    _isClipPanInProgress = false;
                    RequestCommitUpdate();
                    break;
            }
        }

        private void OnTextEntryResizePanUpdated(ClipOverlayState state, int entryIndex, ResizeHandle handle, PanUpdatedEventArgs e)
        {
            if (_currentClip == null || !_isTextClip || !ReferenceEquals(state, _activeState)) return;

            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    if (!TryPrepareTextEntryManipulation(entryIndex))
                    {
                        return;
                    }

                    _isClipPanInProgress = false;
                    _isHandleResizeInProgress = true;
                    LogDiagnostic($"[TextEntryResize] Started: entry={entryIndex}, Rect=({_textEntryStartRect.X:F1}, {_textEntryStartRect.Y:F1}, {_textEntryStartRect.Width:F1}, {_textEntryStartRect.Height:F1})");
                    state.SetPreviewView(null);
                    UpdateVisuals();
                    break;

                case GestureStatus.Running:
                    if (!_isHandleResizeInProgress || _activeTextEntryIndex != entryIndex)
                    {
                        break;
                    }

                    var renderRect = GetRenderRect();
                    if (renderRect.Width <= 0 || renderRect.Height <= 0)
                    {
                        break;
                    }

                    var scale = Math.Max(renderRect.Width, 0.001) / _videoWidth;
                    if (scale <= 0.001)
                    {
                        break;
                    }

                    var deltaX = e.TotalX / scale;
                    var deltaY = e.TotalY / scale;

                    if (!TryScaleTextEntryFromResize(entryIndex, handle, deltaX, deltaY))
                    {
                        break;
                    }

                    UpdateVisuals();
                    break;

                case GestureStatus.Completed:
                case GestureStatus.Canceled:
                    _isHandleResizeInProgress = false;
                    state.SetPreviewView(null);
                    UpdateVisuals();
                    RequestInteractivePreviewRefresh();
                    RequestCommitUpdate();
                    break;
            }
        }

        private bool TryPrepareTextEntryManipulation(int entryIndex)
        {
            if (!TryGetCurrentTextEntry(entryIndex, out _, out var entry))
            {
                return false;
            }

            if (!TryMeasureTextEntryRect(entry, out var x, out var y, out var w, out var h))
            {
                x = entry.x;
                y = entry.y;
                w = MinSize;
                h = MinSize;
            }

            _activeTextEntryIndex = entryIndex;
            _textEntryStartRect = new Rect(
                x,
                y,
                Math.Clamp(w, MinSize, _videoWidth),
                Math.Clamp(h, MinSize, _videoHeight));
            _textEntryStartOriginX = entry.x;
            _textEntryStartOriginY = entry.y;
            _textEntryStartOffsetX = _textEntryStartRect.X - _textEntryStartOriginX;
            _textEntryStartOffsetY = _textEntryStartRect.Y - _textEntryStartOriginY;
            _textEntryStartFontSize = Math.Max(MinTextFontSize, entry.fontSize);
            _textEntryStartWrappingWidth = entry.wrappingWidth;
            _textEntryStartStrokeWidth = entry.strokeWidth;
            return true;
        }

        private bool TryUpdateTextEntryPositionFromPan(int entryIndex, double deltaX, double deltaY)
        {
            if (_currentClip == null || !TryGetCurrentTextEntry(entryIndex, out var entries, out var entry))
            {
                return false;
            }

            var rectW = Math.Clamp(_textEntryStartRect.Width, MinSize, _videoWidth);
            var rectH = Math.Clamp(_textEntryStartRect.Height, MinSize, _videoHeight);
            var minOriginX = -_textEntryStartOffsetX;
            var minOriginY = -_textEntryStartOffsetY;
            var maxOriginX = _videoWidth - rectW - _textEntryStartOffsetX;
            var maxOriginY = _videoHeight - rectH - _textEntryStartOffsetY;

            var nextOriginX = Math.Clamp(_textEntryStartOriginX + deltaX, minOriginX, maxOriginX);
            var nextOriginY = Math.Clamp(_textEntryStartOriginY + deltaY, minOriginY, maxOriginY);

            entries[entryIndex] = entry with
            {
                x = (int)Math.Round(nextOriginX, MidpointRounding.AwayFromZero),
                y = (int)Math.Round(nextOriginY, MidpointRounding.AwayFromZero)
            };

            _currentClip.ExtraData["TextEntries"] = entries;
            return true;
        }

        private bool TryScaleTextEntryFromResize(int entryIndex, ResizeHandle handle, double deltaX, double deltaY)
        {
            if (_currentClip == null || !TryGetCurrentTextEntry(entryIndex, out var entries, out var entry))
            {
                return false;
            }

            var startX = _textEntryStartRect.X;
            var startY = _textEntryStartRect.Y;
            var startW = _textEntryStartRect.Width;
            var startH = _textEntryStartRect.Height;
            if (startW <= 0 || startH <= 0)
            {
                return false;
            }

            var nextX = startX;
            var nextY = startY;
            var nextW = startW;
            var nextH = startH;

            switch (handle)
            {
                case ResizeHandle.TopLeft:
                    nextW = Math.Max(MinSize, startW - deltaX);
                    nextH = Math.Max(MinSize, startH - deltaY);
                    nextX = startX + (startW - nextW);
                    nextY = startY + (startH - nextH);
                    break;
                case ResizeHandle.TopRight:
                    nextW = Math.Max(MinSize, startW + deltaX);
                    nextH = Math.Max(MinSize, startH - deltaY);
                    nextY = startY + (startH - nextH);
                    break;
                case ResizeHandle.BottomLeft:
                    nextW = Math.Max(MinSize, startW - deltaX);
                    nextH = Math.Max(MinSize, startH + deltaY);
                    nextX = startX + (startW - nextW);
                    break;
                case ResizeHandle.BottomRight:
                    nextW = Math.Max(MinSize, startW + deltaX);
                    nextH = Math.Max(MinSize, startH + deltaY);
                    break;
            }

            var aspect = startW / Math.Max(startH, 0.0001);
            ApplyAspectLockedResize(handle, startX, startY, startW, startH, aspect, ref nextX, ref nextY, ref nextW, ref nextH);

            nextW = Math.Clamp(nextW, MinSize, _videoWidth);
            nextH = Math.Clamp(nextH, MinSize, _videoHeight);
            nextX = Math.Clamp(nextX, 0, _videoWidth - nextW);
            nextY = Math.Clamp(nextY, 0, _videoHeight - nextH);

            var scale = nextW / Math.Max(startW, 0.0001);
            var fontSize = Math.Max(MinTextFontSize, _textEntryStartFontSize * (float)scale);

            float? wrappingWidth = _textEntryStartWrappingWidth;

            float? strokeWidth = _textEntryStartStrokeWidth;

            var nextOffsetX = _textEntryStartOffsetX;
            var nextOffsetY = _textEntryStartOffsetY;
            var nextOriginX = nextX - nextOffsetX;
            var nextOriginY = nextY - nextOffsetY;

            nextOriginX = Math.Clamp(nextOriginX, -nextOffsetX, _videoWidth - nextW - nextOffsetX);
            nextOriginY = Math.Clamp(nextOriginY, -nextOffsetY, _videoHeight - nextH - nextOffsetY);

            entries[entryIndex] = entry with
            {
                x = (int)Math.Round(nextOriginX, MidpointRounding.AwayFromZero),
                y = (int)Math.Round(nextOriginY, MidpointRounding.AwayFromZero),
                fontSize = fontSize,
                wrappingWidth = wrappingWidth,
                strokeWidth = strokeWidth
            };

            _currentClip.ExtraData["TextEntries"] = entries;
            return true;
        }

        private bool TryGetCurrentTextEntry(int entryIndex, out List<TextClipEntry> entries, out TextClipEntry entry)
        {
            entries = null!;
            entry = null!;

            if (_currentClip == null || !_isTextClip)
            {
                return false;
            }

            if (!TryGetTextEntries(_currentClip, out entries))
            {
                return false;
            }

            if (entryIndex < 0 || entryIndex >= entries.Count)
            {
                return false;
            }

            entry = entries[entryIndex];
            return true;
        }

        private void OnResizePanUpdated(ClipOverlayState state, ResizeHandle handle, PanUpdatedEventArgs e)
        {
            if (_currentClip == null || !ReferenceEquals(state, _activeState)) return;

            if (_isTextClip) return;  // Can't resize a TextClip

            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    _isHandleResizeInProgress = true;
                    GetCurrentRect(out _startX, out _startY, out _startW, out _startH);
                    LogDiagnostic($"[Resize] Started: Pos=({_startX:F1}, {_startY:F1}), Size=({_startW:F1}x{_startH:F1})");
                    state.SetPreviewView(null);
                    UpdateVisuals();
                    break;

                case GestureStatus.Running:
                    _isHandleResizeInProgress = true;
                    // Get the render rectangle (video viewport on canvas)
                    Rect renderRect = GetRenderRect();
                    if (renderRect.Width <= 0 || renderRect.Height <= 0) break;

                    // Scale factor from screen to video coordinates
                    double scale = Math.Max(renderRect.Width, 0.001) / _videoWidth;
                    if (scale <= 0.001) break;

                    // Convert gesture delta to video coordinates
                    double dx = e.TotalX / scale;
                    double dy = e.TotalY / scale;

                    double newX = _startX, newY = _startY, newW = _startW, newH = _startH;
                    bool allowFreeScale = IsAllowFreeScaleResizeEnabled(_currentClip);

                    if (handle == ResizeHandle.TopLeft)
                    {
                        // Top-Left: resize from top-left corner
                        newW = Math.Max(MinSize, _startW - dx);
                        newH = Math.Max(MinSize, _startH - dy);
                        newX = _startX + (_startW - newW);
                        newY = _startY + (_startH - newH);
                    }
                    else if (handle == ResizeHandle.TopRight)
                    {
                        // Top-Right: resize from top-right corner
                        newW = Math.Max(MinSize, _startW + dx);
                        newH = Math.Max(MinSize, _startH - dy);
                        newY = _startY + (_startH - newH);
                    }
                    else if (handle == ResizeHandle.BottomLeft)
                    {
                        // Bottom-Left: resize from bottom-left corner
                        newW = Math.Max(MinSize, _startW - dx);
                        newH = Math.Max(MinSize, _startH + dy);
                        newX = _startX + (_startW - newW);
                    }
                    else if (handle == ResizeHandle.BottomRight)
                    {
                        // Bottom-Right: resize from bottom-right corner
                        newW = Math.Max(MinSize, _startW + dx);
                        newH = Math.Max(MinSize, _startH + dy);
                    }

                    if (!allowFreeScale)
                    {
                        ApplyAspectLockedResize(handle, ref newX, ref newY, ref newW, ref newH);
                    }

                    UpdateClipEffects(newX, newY, newW, newH);
                    UpdateVisuals();
                    break;

                case GestureStatus.Completed:
                case GestureStatus.Canceled:
                    _isHandleResizeInProgress = false;
                    GetCurrentRect(out var finalX, out var finalY, out var finalW, out var finalH);
                    LogDiagnostic($"[Resize] Completed: Pos=({finalX:F1}, {finalY:F1}), Size=({finalW:F1}x{finalH:F1})");
                    state.SetPreviewView(null);
                    UpdateVisuals();
                    RequestInteractivePreviewRefresh();
                    RequestCommitUpdate();
                    break;
            }
        }

        private bool IsInteractiveManipulationInProgress
            => _isClipPanInProgress || _isHandleResizeInProgress;

        private bool ShouldKeepExistingPreviewFrame(string clipId)
            => _isClipPanInProgress
                && !_isHandleResizeInProgress
                && _currentClip is not null
                && string.Equals(_currentClip.Id, clipId, StringComparison.Ordinal);

        private bool ShouldSuppressPreviewForResize(string clipId)
            => _isHandleResizeInProgress
                && _currentClip is not null
                && string.Equals(_currentClip.Id, clipId, StringComparison.Ordinal);

        private void RequestInteractivePreviewRefreshIfMissing(ClipOverlayState state)
        {
            if (state.HasPreviewView)
            {
                return;
            }

            RequestInteractivePreviewRefresh();
        }

        private void ApplyAspectLockedResize(ResizeHandle handle, ref double x, ref double y, ref double w, ref double h)
        {
            if (_startW <= 0 || _startH <= 0)
            {
                return;
            }

            ApplyAspectLockedResize(
                handle,
                _startX,
                _startY,
                _startW,
                _startH,
                ResolveLockedResizeAspectRatio(),
                ref x,
                ref y,
                ref w,
                ref h);
        }

        private static void ApplyAspectLockedResize(
            ResizeHandle handle,
            double startX,
            double startY,
            double startW,
            double startH,
            double aspect,
            ref double x,
            ref double y,
            ref double w,
            ref double h)
        {
            if (startW <= 0 || startH <= 0)
            {
                return;
            }

            var relW = Math.Abs((w / Math.Max(startW, 0.0001)) - 1d);
            var relH = Math.Abs((h / Math.Max(startH, 0.0001)) - 1d);

            if (relW >= relH)
            {
                h = Math.Max(MinSize, w / Math.Max(0.0001, aspect));
            }
            else
            {
                w = Math.Max(MinSize, h * Math.Max(0.0001, aspect));
            }

            switch (handle)
            {
                case ResizeHandle.TopLeft:
                    x = startX + (startW - w);
                    y = startY + (startH - h);
                    break;
                case ResizeHandle.TopRight:
                    y = startY + (startH - h);
                    break;
                case ResizeHandle.BottomLeft:
                    x = startX + (startW - w);
                    break;
                case ResizeHandle.BottomRight:
                default:
                    break;
            }
        }

        private double ResolveLockedResizeAspectRatio()
        {
            if (_currentClip != null
                && _currentClip.ClipType is ClipMode.VideoClip or ClipMode.PhotoClip
                && _currentAsset != null
                && _currentAsset.Width > 0
                && _currentAsset.Height > 0)
            {
                return (double)_currentAsset.Width / _currentAsset.Height;
            }

            return _startW / Math.Max(_startH, 0.0001);
        }

        private void RequestCommitUpdate()
        {
            var callback = _updateCallback;
            if (callback is null)
            {
                return;
            }

            CancellationTokenSource currentCts;
            lock (_commitUpdateDebounceLock)
            {
                _commitUpdateDebounceCts?.Cancel();
                _commitUpdateDebounceCts?.Dispose();
                _commitUpdateDebounceCts = new CancellationTokenSource();
                currentCts = _commitUpdateDebounceCts;
            }

            _ = DispatchCommitUpdateAfterDelayAsync(currentCts);
        }

        private async Task DispatchCommitUpdateAfterDelayAsync(CancellationTokenSource debounceCts)
        {
            try
            {
                await Task.Delay(CommitUpdateDebounceMs, debounceCts.Token);
                QueueCommitUpdateExecution();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log(ex, $"Commit update dispatch");
            }
        }

        private void QueueCommitUpdateExecution()
        {
            Interlocked.Exchange(ref _hasPendingCommitUpdate, 1);

            if (Interlocked.CompareExchange(ref _isCommitUpdateRunning, 1, 0) == 0)
            {
                _ = ExecuteCommitUpdateLoopAsync();
            }
        }

        private async Task ExecuteCommitUpdateLoopAsync()
        {
            try
            {
                while (Interlocked.Exchange(ref _hasPendingCommitUpdate, 0) == 1)
                {
                    var callback = _updateCallback;
                    if (callback is null)
                    {
                        break;
                    }

                    await InvokeUpdateCallbackAsync(callback);
                }
            }
            catch (Exception ex)
            {
                LogDiagnostic($"Commit update execution failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _isCommitUpdateRunning, 0);

                // Handle race where a request arrives between loop exit and running flag reset.
                if (Volatile.Read(ref _hasPendingCommitUpdate) == 1
                    && Interlocked.CompareExchange(ref _isCommitUpdateRunning, 1, 0) == 0)
                {
                    _ = ExecuteCommitUpdateLoopAsync();
                }
            }
        }

        private async Task InvokeUpdateCallbackAsync(Func<Task> callback)
        {
            if (!Dispatcher.IsDispatchRequired)
            {
                await callback();
                return;
            }

            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.Dispatch(async () =>
            {
                try
                {
                    await callback();
                    tcs.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            await tcs.Task;
        }

        private void CancelPendingCommitUpdate()
        {
            lock (_commitUpdateDebounceLock)
            {
                _commitUpdateDebounceCts?.Cancel();
                _commitUpdateDebounceCts?.Dispose();
                _commitUpdateDebounceCts = null;
            }

            Interlocked.Exchange(ref _hasPendingCommitUpdate, 0);
        }

        private void GetCurrentRect(out double x, out double y, out double w, out double h)
        {
            x = 0;
            y = 0;
            w = _baseRect.Width > 0 ? _baseRect.Width : _videoWidth;
            h = _baseRect.Height > 0 ? _baseRect.Height : _videoHeight;

            if (_currentClip == null)
                return;

            if (_isTextClip)
            {
                if (TryResolveTextClipRect(_currentClip, out var textX, out var textY, out var textW, out var textH))
                {
                    x = textX;
                    y = textY;
                    w = textW;
                    h = textH;
                    _baseRect = new Rect(textX, textY, textW, textH);
                    return;
                }

                if (TryGetTextEntry(out var entry) && entry != null)
                {
                    x = entry.x;
                    y = entry.y;
                }
                return;
            }

            x = _currentClip.TargetX;
            y = _currentClip.TargetY;
            if (_currentClip.TargetWidth > 0)
            {
                w = _currentClip.TargetWidth;
            }

            if (_currentClip.TargetHeight > 0)
            {
                h = _currentClip.TargetHeight;
            }

            if (_currentClip.ClipType == ClipMode.SolidColorClip)
            {
                if (_currentClip.TargetWidth > 0)
                {
                    w = _currentClip.TargetWidth;
                }
                else
                {
                    w = ReadSolidColorSize(_currentClip.ExtraData, SolidColorOutputWidthKey, (int)Math.Round(w));
                }

                if (_currentClip.TargetHeight > 0)
                {
                    h = _currentClip.TargetHeight;
                }
                else
                {
                    h = ReadSolidColorSize(_currentClip.ExtraData, SolidColorOutputHeightKey, (int)Math.Round(h));
                }
            }
        }

        private bool TryGetTextEntry(out TextClipEntry? entry)
        {
            entry = null;
            if (_currentClip == null) return false;
            if (!TryGetTextEntries(_currentClip, out var entries)) return false;
            entry = entries[0];
            return true;
        }

        private bool TryGetTextEntries(ClipElementUI clip, out List<TextClipEntry> entries)
        {
            entries = null!;
            if (clip.ExtraData == null || !clip.ExtraData.TryGetValue("TextEntries", out var entriesObj))
            {
                return false;
            }

            if (entriesObj is List<TextClipEntry> list)
            {
                if (list.Count == 0)
                {
                    return false;
                }

                entries = list;
                return true;
            }

            if (entriesObj is JsonElement je)
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<List<TextClipEntry>>(je);
                    if (parsed is { Count: > 0 })
                    {
                        clip.ExtraData["TextEntries"] = parsed;
                        entries = parsed;
                        return true;
                    }
                }
                catch
                {
                    return false;
                }
            }

            if (entriesObj is string json && !string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<List<TextClipEntry>>(json);
                    if (parsed is { Count: > 0 })
                    {
                        clip.ExtraData["TextEntries"] = parsed;
                        entries = parsed;
                        return true;
                    }
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private bool TryResolveTextClipRect(ClipElementUI clip, out double x, out double y, out double w, out double h)
        {
            x = 0;
            y = 0;
            w = 0;
            h = 0;

            if (!TryGetTextEntries(clip, out var entries))
            {
                return false;
            }

            var hasBounds = false;
            double minX = 0;
            double minY = 0;
            double maxX = 0;
            double maxY = 0;

            foreach (var entry in entries)
            {
                if (!TryMeasureTextEntryRect(entry, out var entryX, out var entryY, out var entryW, out var entryH))
                {
                    continue;
                }

                var left = entryX;
                var top = entryY;
                var right = entryX + entryW;
                var bottom = entryY + entryH;

                if (!hasBounds)
                {
                    minX = left;
                    minY = top;
                    maxX = right;
                    maxY = bottom;
                    hasBounds = true;
                }
                else
                {
                    minX = Math.Min(minX, left);
                    minY = Math.Min(minY, top);
                    maxX = Math.Max(maxX, right);
                    maxY = Math.Max(maxY, bottom);
                }
            }

            if (!hasBounds)
            {
                return false;
            }

            x = minX;
            y = minY;
            w = Math.Max(MinSize, maxX - minX);
            h = Math.Max(MinSize, maxY - minY);
            return true;
        }

        private bool TryMeasureTextEntryRect(TextClipEntry entry, out double x, out double y, out double w, out double h)
        {
            x = entry.x;
            y = entry.y;
            w = MinSize;
            h = MinSize;

            var rawText = entry.text ?? string.Empty;
            var textForMeasure = string.IsNullOrEmpty(rawText) ? " " : rawText;
            var dpi = entry.dpi ?? 72d;
            var fontSize = Math.Max(1d, entry.fontSize * (dpi / 72.0));
            var strokeExtra = Math.Max(0d, entry.strokeWidth ?? 0f) * 2d;

            if (entry.UseVerticalLayout)
            {
                var glyphCount = rawText.Count(c => c != '\n' && c != '\r');
                if (glyphCount <= 0)
                {
                    glyphCount = 1;
                }

                var lineAdvance = fontSize * Math.Max(0.1d, entry.lineSpacing);
                w = Math.Max(MinSize, fontSize + strokeExtra);
                h = Math.Max(MinSize, glyphCount * lineAdvance + strokeExtra);
            }
            else
            {
                var previousText = MeasurementLabel.Text;
                var previousFontFamily = MeasurementLabel.FontFamily;
                var previousFontSize = MeasurementLabel.FontSize;
                var previousFontAttributes = MeasurementLabel.FontAttributes;
                var previousWidthRequest = MeasurementLabel.WidthRequest;
                var previousLineBreakMode = MeasurementLabel.LineBreakMode;

                try
                {
                    MeasurementLabel.Text = textForMeasure;
                    MeasurementLabel.FontSize = fontSize;
                    MeasurementLabel.FontFamily = string.IsNullOrWhiteSpace(entry.fontFamily) ? null : entry.fontFamily;
                    MeasurementLabel.FontAttributes = entry.fontStyle switch
                    {
                        SixLabors.Fonts.FontStyle.Bold => FontAttributes.Bold,
                        SixLabors.Fonts.FontStyle.Italic => FontAttributes.Italic,
                        SixLabors.Fonts.FontStyle.BoldItalic => FontAttributes.Bold | FontAttributes.Italic,
                        _ => FontAttributes.None
                    };

                    var wrappingWidth = entry.wrappingWidth.HasValue && entry.wrappingWidth.Value > 0
                        ? entry.wrappingWidth.Value
                        : 0f;

                    if (wrappingWidth > 0)
                    {
                        MeasurementLabel.WidthRequest = wrappingWidth;
                        MeasurementLabel.LineBreakMode = LineBreakMode.WordWrap;
                        var wrappedSize = MeasurementLabel.Measure(wrappingWidth, double.PositiveInfinity);
                        w = wrappedSize.Width;
                        h = wrappedSize.Height;
                    }
                    else
                    {
                        MeasurementLabel.WidthRequest = -1;
                        MeasurementLabel.LineBreakMode = LineBreakMode.NoWrap;
                        var size = MeasurementLabel.Measure(double.PositiveInfinity, double.PositiveInfinity);
                        w = size.Width;
                        h = size.Height;
                    }
                }
                catch
                {
                    var fallbackWidth = Math.Max(1, textForMeasure.Length) * fontSize * 0.6d;
                    var fallbackHeight = fontSize * 1.2d;
                    w = fallbackWidth;
                    h = fallbackHeight;
                }
                finally
                {
                    MeasurementLabel.Text = previousText;
                    MeasurementLabel.FontFamily = previousFontFamily;
                    MeasurementLabel.FontSize = previousFontSize;
                    MeasurementLabel.FontAttributes = previousFontAttributes;
                    MeasurementLabel.WidthRequest = previousWidthRequest;
                    MeasurementLabel.LineBreakMode = previousLineBreakMode;
                }

                w = Math.Max(MinSize, w + strokeExtra);
                h = Math.Max(MinSize, h + strokeExtra);
            }

            switch (entry.horizontalAlignment)
            {
                case SixLabors.Fonts.HorizontalAlignment.Center:
                    x -= w / 2d;
                    break;
                case SixLabors.Fonts.HorizontalAlignment.Right:
                    x -= w;
                    break;
            }

            switch (entry.verticalAlignment)
            {
                case SixLabors.Fonts.VerticalAlignment.Center:
                    y -= h / 2d;
                    break;
                case SixLabors.Fonts.VerticalAlignment.Bottom:
                    y -= h;
                    break;
            }

            if (Math.Abs(entry.rotation) > 0.0001f)
            {
                var radians = entry.rotation * Math.PI / 180d;
                var cos = Math.Cos(radians);
                var sin = Math.Sin(radians);

                static (double rx, double ry) Rotate(double px, double py, double cosV, double sinV)
                    => (px * cosV - py * sinV, px * sinV + py * cosV);

                var p0 = Rotate(0, 0, cos, sin);
                var p1 = Rotate(w, 0, cos, sin);
                var p2 = Rotate(0, h, cos, sin);
                var p3 = Rotate(w, h, cos, sin);

                var minRx = Math.Min(Math.Min(p0.rx, p1.rx), Math.Min(p2.rx, p3.rx));
                var minRy = Math.Min(Math.Min(p0.ry, p1.ry), Math.Min(p2.ry, p3.ry));
                var maxRx = Math.Max(Math.Max(p0.rx, p1.rx), Math.Max(p2.rx, p3.rx));
                var maxRy = Math.Max(Math.Max(p0.ry, p1.ry), Math.Max(p2.ry, p3.ry));

                x = entry.x + minRx;
                y = entry.y + minRy;
                w = Math.Max(MinSize, maxRx - minRx);
                h = Math.Max(MinSize, maxRy - minRy);
                return true;
            }

            return true;
        }

        private void UpdateClipEffects(double x, double y, double w, double h)
        {
            if (_currentClip == null) return;

            // Clamp in video coordinate space.
            w = Math.Clamp(w, MinSize, _videoWidth);
            h = Math.Clamp(h, MinSize, _videoHeight);
            x = Math.Clamp(x, 0, _videoWidth - w);
            y = Math.Clamp(y, 0, _videoHeight - h);

            _currentClip.TargetX = (int)Math.Round(x, MidpointRounding.AwayFromZero);
            _currentClip.TargetY = (int)Math.Round(y, MidpointRounding.AwayFromZero);
            _currentClip.TargetWidth = Math.Max(1, (int)Math.Round(w, MidpointRounding.AwayFromZero));
            _currentClip.TargetHeight = Math.Max(1, (int)Math.Round(h, MidpointRounding.AwayFromZero));

            if (_currentClip.ClipType == ClipMode.SolidColorClip)
            {
                UpdateSolidColorOutputSize(w, h);
            }
        }

        private void UpdateSolidColorOutputSize(double w, double h)
        {
            if (_currentClip == null) return;

            _currentClip.ExtraData ??= new Dictionary<string, object>();
            _currentClip.ExtraData[SolidColorOutputWidthKey] = Math.Max(1, (int)Math.Round(w, MidpointRounding.AwayFromZero));
            _currentClip.ExtraData[SolidColorOutputHeightKey] = Math.Max(1, (int)Math.Round(h, MidpointRounding.AwayFromZero));
            _currentClip.ExtraData[SolidColorUseFixedOutputSizeKey] = true;
        }

        private static bool IsAllowFreeScaleResizeEnabled(ClipElementUI clip)
        {
            if (clip.ClipType == ClipMode.SolidColorClip) return true;

            if (ReadBoolExtraData(clip.ExtraData, AllowFreeScaleResizeKey, out var allowFreeScale))
            {
                return allowFreeScale;
            }

            return false;
        }

        private static bool ReadBoolExtraData(Dictionary<string, object>? data, string key, out bool value)
        {
            value = false;
            if (data == null || !data.TryGetValue(key, out var raw) || raw is null)
            {
                return false;
            }

            if (raw is bool b)
            {
                value = b;
                return true;
            }

            if (raw is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.True)
                {
                    value = true;
                    return true;
                }

                if (je.ValueKind == JsonValueKind.False)
                {
                    value = false;
                    return true;
                }

                if (je.ValueKind == JsonValueKind.String && bool.TryParse(je.GetString(), out var parsedFromJe))
                {
                    value = parsedFromJe;
                    return true;
                }
            }

            if (bool.TryParse(raw.ToString(), out var parsed))
            {
                value = parsed;
                return true;
            }

            return false;
        }

        private static int ReadSolidColorSize(Dictionary<string, object>? data, string key, int fallback)
        {
            if (data != null && data.TryGetValue(key, out var raw) && raw is not null)
            {
                if (raw is int i) return Math.Max(1, i);
                if (raw is long l) return Math.Max(1, (int)Math.Min(int.MaxValue, l));
                if (raw is JsonElement je)
                {
                    if (je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var jn)) return Math.Max(1, jn);
                    if (je.ValueKind == JsonValueKind.String && int.TryParse(je.GetString(), out var js)) return Math.Max(1, js);
                }

                if (int.TryParse(raw.ToString(), out var parsed)) return Math.Max(1, parsed);
            }

            return Math.Max(1, fallback);
        }

        /// <summary>
        /// 强制更新手势识别器以避免与父容器的手势冲突
        /// </summary>
        public void RefreshGestureRecognizers()
        {
            _activeState?.RefreshGestureRecognizers();
        }
    }
}