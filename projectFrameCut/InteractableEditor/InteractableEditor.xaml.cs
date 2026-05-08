using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.Asset;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
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
        #region types

        private enum ResizeHandle
        {
            TopLeft,
            TopRight,
            BottomLeft,
            BottomRight
        }

        public enum ReferenceLineOrientation
        {
            Horizontal,
            Vertical
        }

        public struct ReferenceLine
        {
            public string Id;
            public double Position;
            public ReferenceLineOrientation Orientation;
            public Color Color;
            public double Thickness;
        }

        #endregion

        #region fields

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
        private bool _isPlacingReferenceLine;
        private ReferenceLineOrientation? _pendingReferenceLineOrientation;
        private int _referenceLineCounter;
        private Color _defaultReferenceLineColor = Color.FromRgba(0, 255, 255, 128);
        private double _defaultReferenceLineThickness = 1.0;
        private Stopwatch _panTimer = new();
        private long _lastPanUpdateTicks = 0;
        private int _activeTextEntryIndex = -1;
        private Rect _textEntryStartRect;
        private double _textEntryStartOriginX;
        private double _textEntryStartOriginY;
        private double _textEntryStartOffsetX;
        private double _textEntryStartOffsetY;
        private float _textEntryStartFontSize;
        private float? _textEntryStartWrappingWidth;
        private float? _textEntryStartStrokeWidth;

        private Rect? _panPreviewRect;
        private List<TextClipEntry>? _panPreviewTextEntries;

        private const double HandleSize = 15;
        private const double MinSize = 10;
        private const double SnapThresholdDisplayPx = 10.0;
        private const float MinTextFontSize = 1f;

        private readonly Dictionary<string, ClipOverlayState> _clipStates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, object> _previewSourceClips = new(StringComparer.Ordinal);
        private ClipOverlayState? _activeState;
        private Func<Task>? _previewRefreshCallback;
        private Func<string, Task>? _overlayClipTappedCallback;
        private Func<Task>? _blankAreaTappedCallback;
        private Func<Task>? _referenceLinesChangedCallback;
        private bool _suppressReferenceLinesChangedNotify;
        private readonly Dictionary<string, ReferenceLine> _referenceLines = new(StringComparer.Ordinal);
        private readonly Dictionary<string, BoxView> _referenceLineVisuals = new(StringComparer.Ordinal);
        private Action? _manageReferenceLinesRequestedCallback;
        private Action<Color>? _defaultColorPickerRequestedCallback;
        private long _lastPreviewRefreshTick;
        private int _isPreviewRefreshRunning;
        private int _hasPendingPreviewRefresh;
        private int _isCommitUpdateRunning;
        private int _hasPendingCommitUpdate;
        private long _lastOverlayTapTick;

        private CancellationTokenSource? _commitUpdateDebounceCts;
        private readonly object _commitUpdateDebounceLock = new();
        private bool _autoHideBottomControls;
        private CancellationTokenSource? _hideBottomControlsCts;

        private const int PreviewRefreshThrottleMs = 180;
        private const int CommitUpdateDebounceMs = 220;
        private const int OverlayTapBlankSuppressMs = 180;

        #endregion

        #region properties

        public ConcurrentDictionary<string, ClipElementUI> Clips { get; private set; } = new();

        // 用于直接显示DraftPage中的所有clips
        private IReadOnlyDictionary<string, ClipElementUI>? _allClips;
        private ConcurrentDictionary<string, AssetItem>? _assets;
        private uint _currentFrame;
        private double _framePerPixel = 1d;
        private double _tracksZoomOffset = 1d;
        private float _secondPerFrameRatio = 1f;

        public bool ShowRenderRectOverlay { get; set; } = true;
        public bool EnableSnapping { get; set { if (field == value) return; field = value; OnPropertyChanged(); } } = true;
        public bool LockLayout { get; set { if (field == value) return; field = value; OnPropertyChanged(); } } = false;
        public bool AllowClipOutOfBounds { get; set { if (field == value) return; field = value; LogDiagnostic($"AllowClipOutOfBounds now is {field}"); OnPropertyChanged(); OnPropertyChanged(nameof(DisallowClipOutOfBounds)); } } = false;
        public bool DisallowClipOutOfBounds
        {
            get => !AllowClipOutOfBounds;
            set => AllowClipOutOfBounds = !value;
        }
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

        public bool ShowReferenceLines
        {
            get;
            set
            {
                if (field == value)
                {
                    return;
                }

                field = value;
                OnPropertyChanged();
                UpdateVisuals();
            }
        } = true;

        public Color DefaultReferenceLineColor
        {
            get => _defaultReferenceLineColor;
            set
            {
                if (AreColorsClose(_defaultReferenceLineColor, value))
                    return;
                _defaultReferenceLineColor = value;
                DefaultColorSwatchBorder.BackgroundColor = value;
            }
        }

        public double DefaultReferenceLineThickness
        {
            get => _defaultReferenceLineThickness;
            set
            {
                var clamped = Math.Clamp(value, 0.5, 10.0);
                if (Math.Abs(_defaultReferenceLineThickness - clamped) < 0.001)
                    return;
                _defaultReferenceLineThickness = clamped;
                ThicknessEntry.Text = clamped.ToString("F1");
            }
        }

        public bool ShowDetailReferenceLineControl { get; set; } = false;

        private static bool AreColorsClose(Color a, Color b) =>
            Math.Abs(a.Red - b.Red) < 0.004 &&
            Math.Abs(a.Green - b.Green) < 0.004 &&
            Math.Abs(a.Blue - b.Blue) < 0.004 &&
            Math.Abs(a.Alpha - b.Alpha) < 0.004;

        public ContentView RealtimePreviewHost => LivePreviewerHost;
        public Image StaticPreviewOverlayImage => PreviewOverlayImage;
        public bool IsPlacingReferenceLine => _isPlacingReferenceLine && _pendingReferenceLineOrientation != null;

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

        #endregion

        #region ClipOverlayState

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
                        FontSize = 10,
                        LineBreakMode = LineBreakMode.NoWrap,
                        HorizontalOptions = LayoutOptions.Center,
                        VerticalOptions = LayoutOptions.Center,
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

            public ClipOverlayState(InteractableEditor owner, string clipId, string? displayName = null)
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
                    FontSize = 10,
                    LineBreakMode = LineBreakMode.NoWrap,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
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

                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    ToolTipProperties.SetText(Root, displayName);
                    ToolTipProperties.SetText(ClipVisual, displayName);
                    ToolTipProperties.SetText(PreviewHost, displayName);
                }

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

        #endregion

        #region init/config
        public InteractableEditor()
        {
            BindingContext = this;
            InitializeComponent();

            var canvasTap = new TapGestureRecognizer();
            canvasTap.Tapped += OnEditorCanvasTapped;
            EditorCanvas.GestureRecognizers.Add(canvasTap);

            var hoverPointer = new PointerGestureRecognizer();
            hoverPointer.PointerEntered += OnBottomControlsHostEntered;
            hoverPointer.PointerExited += OnBottomControlsHostExited;
            BottomControlsHost.GestureRecognizers.Add(hoverPointer);
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

        public void ConfigureReferenceLinesChanged(Func<Task>? callback)
        {
            _referenceLinesChangedCallback = callback;
        }

        public void ConfigureManageReferenceLinesRequested(Action? callback)
        {
            _manageReferenceLinesRequestedCallback = callback;
        }

        public void ConfigureDefaultColorPickerRequested(Action<Color>? callback)
        {
            _defaultColorPickerRequestedCallback = callback;
        }

        protected override void OnSizeAllocated(double width, double height)
        {
            base.OnSizeAllocated(width, height);
            UpdateCanvasSize(width, height, true);
        }

        public void Init(Func<Task> updateCallback, double videoWidth, double videoHeight)
        {
            _updateCallback = updateCallback;
            _videoWidth = videoWidth;
            _videoHeight = videoHeight;
        }

        public void UpdateCanvasSize(double width, double height, bool ignorePositionProvider = false)
        {
            _canvasWidth = width;
            _canvasHeight = height;
            UpdateVisuals(ignorePositionProvider);
        }

        public void UpdateVideoResolution(double width, double height, bool ignorePositionProvider = false)
        {
            _videoWidth = width;
            _videoHeight = height;
            UpdateVisuals(ignorePositionProvider);
        }

        #endregion

        #region Clip State Management

        private ClipOverlayState GetOrCreateClipState(ClipElementUI clip)
            => GetOrCreateClipState(clip.Id, clip.DisplayName);

        private ClipOverlayState GetOrCreateClipState(string clipId, string? displayName = null)
        {
            if (_clipStates.TryGetValue(clipId, out var state))
            {
                return state;
            }

            state = new ClipOverlayState(this, clipId, displayName);
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
            if (_isPlacingReferenceLine)
            {
                var placementTap = e.GetPosition(EditorCanvas);
                if (placementTap is null)
                {
                    return;
                }

                var renderRect = GetRenderRect();
                var scale = renderRect.Width / _videoWidth;
                if (scale <= 0)
                {
                    return;
                }

                var id = $"ref-{Interlocked.Increment(ref _referenceLineCounter)}";
                if (_pendingReferenceLineOrientation == ReferenceLineOrientation.Horizontal)
                {
                    var videoY = Math.Clamp((placementTap.Value.Y - renderRect.Y) / scale, 0, _videoHeight);
                    AddReferenceLine(id, videoY, ReferenceLineOrientation.Horizontal);
                }
                else
                {
                    var videoX = Math.Clamp((placementTap.Value.X - renderRect.X) / scale, 0, _videoWidth);
                    AddReferenceLine(id, videoX, ReferenceLineOrientation.Vertical);
                }

                _isPlacingReferenceLine = false;
                return;
            }

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

        #endregion

        #region Reference Lines

        public void AddReferenceLine(string id, double position, ReferenceLineOrientation orientation, Color? color = null, double thickness = -1.0)
        {
            _referenceLines[id] = new ReferenceLine
            {
                Id = id,
                Position = position,
                Orientation = orientation,
                Color = color ?? _defaultReferenceLineColor,
                Thickness = thickness > 0 ? Math.Max(0.5, thickness) : _defaultReferenceLineThickness
            };
            UpdateVisuals();
            NotifyReferenceLinesChanged();
        }

        public void RemoveReferenceLine(string id)
        {
            if (_referenceLines.Remove(id))
            {
                if (_referenceLineVisuals.TryGetValue(id, out var visual))
                {
                    ReferenceLinesHost.Children.Remove(visual);
                    _referenceLineVisuals.Remove(id);
                }

                UpdateVisuals();
                NotifyReferenceLinesChanged();
            }
        }

        public void ClearReferenceLines()
        {
            if (_referenceLines.Count == 0)
            {
                return;
            }

            _referenceLines.Clear();
            foreach (var kvp in _referenceLineVisuals)
            {
                ReferenceLinesHost.Children.Remove(kvp.Value);
            }

            _referenceLineVisuals.Clear();
            UpdateVisuals();
            NotifyReferenceLinesChanged();
        }

        public void UpdateReferenceLineColor(string id, Color color)
        {
            if (!_referenceLines.TryGetValue(id, out var line))
                return;
            _referenceLines[id] = new ReferenceLine
            {
                Id = line.Id,
                Position = line.Position,
                Orientation = line.Orientation,
                Color = color,
                Thickness = line.Thickness
            };
            UpdateVisuals();
            NotifyReferenceLinesChanged();
        }

        public void UpdateReferenceLineThickness(string id, double thickness)
        {
            if (!_referenceLines.TryGetValue(id, out var line))
                return;
            _referenceLines[id] = new ReferenceLine
            {
                Id = line.Id,
                Position = line.Position,
                Orientation = line.Orientation,
                Color = line.Color,
                Thickness = Math.Clamp(thickness, 0.5, 10.0)
            };
            UpdateVisuals();
            NotifyReferenceLinesChanged();
        }

        public IReadOnlyDictionary<string, ReferenceLine> ReferenceLines => _referenceLines;

        public string GetReferenceLinesJson()
        {
            var data = _referenceLines.Values.Select(rl => new ReferenceLineData
            {
                Id = rl.Id,
                Position = rl.Position,
                Orientation = rl.Orientation,
                ColorHex = rl.Color.ToArgbHex(),
                Thickness = rl.Thickness
            }).ToList();
            return JsonSerializer.Serialize(data);
        }

        public void RestoreReferenceLinesFromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return;

            List<ReferenceLineData>? data;
            try
            {
                data = JsonSerializer.Deserialize<List<ReferenceLineData>>(json);
            }
            catch
            {
                return;
            }

            if (data is null || data.Count == 0)
                return;

            _referenceLines.Clear();
            foreach (var kvp in _referenceLineVisuals)
            {
                ReferenceLinesHost.Children.Remove(kvp.Value);
            }
            _referenceLineVisuals.Clear();

            _suppressReferenceLinesChangedNotify = true;
            try
            {
                foreach (var item in data)
                {
                    AddReferenceLine(item.Id, item.Position, item.Orientation,
                        Color.FromArgb(item.ColorHex), item.Thickness);
                }
            }
            finally
            {
                _suppressReferenceLinesChangedNotify = false;
            }
        }

        private sealed class ReferenceLineData
        {
            public string Id { get; set; } = string.Empty;
            public double Position { get; set; }
            public ReferenceLineOrientation Orientation { get; set; }
            public string ColorHex { get; set; } = "#00FFFFFF";
            public double Thickness { get; set; } = 1.0;
        }

        private void NotifyReferenceLinesChanged()
        {
            if (_suppressReferenceLinesChangedNotify)
                return;

            var callback = _referenceLinesChangedCallback;
            if (callback is null)
                return;

            _ = InvokeReferenceLinesChangedAsync(callback);
        }

        private static async Task InvokeReferenceLinesChangedAsync(Func<Task> callback)
        {
            try
            {
                await callback();
            }
            catch (Exception ex)
            {
                LogDiagnostic($"Reference lines changed callback failed: {ex.Message}");
            }
        }

        public void AddThirdsGuides()
        {
            for (var i = 1; i < 3; i++)
            {
                AddReferenceLine($"third-h-{i}", _videoHeight * i / 3.0, ReferenceLineOrientation.Horizontal);
                AddReferenceLine($"third-v-{i}", _videoWidth * i / 3.0, ReferenceLineOrientation.Vertical);
            }
        }

        public void AddCenterGuides()
        {
            AddReferenceLine("center-h", _videoHeight / 2.0, ReferenceLineOrientation.Horizontal);
            AddReferenceLine("center-v", _videoWidth / 2.0, ReferenceLineOrientation.Vertical);
        }

        public void AddGoldenRatioGuides()
        {
            var phi = 1.6180339887498948482;
            var hThird = _videoHeight / phi;
            var wThird = _videoWidth / phi;
            AddReferenceLine("golden-h-1", hThird, ReferenceLineOrientation.Horizontal, Color.FromRgba(255, 215, 0, 160));
            AddReferenceLine("golden-h-2", _videoHeight - hThird, ReferenceLineOrientation.Horizontal, Color.FromRgba(255, 215, 0, 160));
            AddReferenceLine("golden-v-1", wThird, ReferenceLineOrientation.Vertical, Color.FromRgba(255, 215, 0, 160));
            AddReferenceLine("golden-v-2", _videoWidth - wThird, ReferenceLineOrientation.Vertical, Color.FromRgba(255, 215, 0, 160));
        }

        private void UpdateReferenceLines(Rect renderRect, double scale)
        {
            var shouldShow = ShowReferenceLines && _referenceLines.Count > 0;
            ReferenceLinesHost.IsVisible = shouldShow;

            if (!shouldShow)
            {
                return;
            }

            // Remove visuals for lines that no longer exist
            var staleIds = _referenceLineVisuals.Keys.Except(_referenceLines.Keys).ToList();
            foreach (var id in staleIds)
            {
                if (_referenceLineVisuals.Remove(id, out var staleVisual))
                {
                    ReferenceLinesHost.Children.Remove(staleVisual);
                }
            }

            foreach (var refLine in _referenceLines.Values)
            {
                if (!_referenceLineVisuals.TryGetValue(refLine.Id, out var lineVisual))
                {
                    lineVisual = new BoxView
                    {
                        InputTransparent = true,
                        Opacity = 0.9,
                    };
                    _referenceLineVisuals[refLine.Id] = lineVisual;
                    ReferenceLinesHost.Children.Add(lineVisual);
                }

                lineVisual.Color = refLine.Color;
                lineVisual.IsVisible = true;

                if (refLine.Orientation == ReferenceLineOrientation.Horizontal)
                {
                    var y = renderRect.Y + refLine.Position * scale;
                    AbsoluteLayout.SetLayoutBounds(lineVisual, new Rect(
                        renderRect.X, y, renderRect.Width, Math.Max(1, refLine.Thickness)));
                }
                else
                {
                    var x = renderRect.X + refLine.Position * scale;
                    AbsoluteLayout.SetLayoutBounds(lineVisual, new Rect(
                        x, renderRect.Y, Math.Max(1, refLine.Thickness), renderRect.Height));
                }
            }
        }

        #endregion

        #region clip & asset binding

        public void SetClip(projectFrameCut.DraftStuff.ClipElementUI? clip, AssetItem? asset)
        {
            CancelPendingCommitUpdate();

            _currentClip = clip;
            _currentAsset = asset;
            _isClipPanInProgress = false;
            _isHandleResizeInProgress = false;
            _panPreviewRect = null;
            _panPreviewTextEntries = null;
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
                var baseW = _videoWidth;
                var baseH = _videoHeight;
                ComputeFittedRectFromAsset(_currentAsset, clip, _videoWidth, _videoHeight, ref baseW, ref baseH);
                _baseRect = new Rect(0, 0, baseW, baseH);
            }

            SetActiveState(GetOrCreateClipState(clip));
            UpdateVisuals();

            // 确保手势识别器在新的容器环境中正确工作。
            // 但如果正在交互中（拖拽/缩放），跳过刷新以避免销毁正在活跃的 GestureRecognizer，
            // 否则会导致拖拽/缩放被中断，选区卡住无法响应。
            if (!IsInteractiveManipulationInProgress)
            {
                RefreshGestureRecognizers();
            }
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

        public void SetAssets(ConcurrentDictionary<string, AssetItem>? assets)
        {
            _assets = assets;
        }

        #endregion

        #region update

        private void ComputeFittedRectFromAsset(
            AssetItem? asset,
            ClipElementUI? clip,
            double videoWidth,
            double videoHeight,
            ref double w,
            ref double h)
        {
            w = clip.TargetWidth;
            h = clip.TargetHeight;
            if (clip is null)
                return;

            if (!ClipInfoBuilder.TryGetSourceAspectRatio(clip, [AssetDatabase.Assets, _assets ?? []], out var assetAspect)) return;
            var projectAspect = videoWidth / videoHeight;

            if (assetAspect > projectAspect)
            {
                w = videoWidth;
                h = videoWidth / assetAspect;
            }
            else
            {
                h = videoHeight;
                w = videoHeight * assetAspect;
            }
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

        private void UpdateVisuals(bool ignorePositionProvider = false)
        {
            if (_videoWidth <= 0 || _videoHeight <= 0 || _canvasWidth <= 0 || _canvasHeight <= 0)
                return;

            Rect renderRect = GetRenderRect();
            double scale = renderRect.Width / _videoWidth;

            UpdateRenderRectOverlay(renderRect);
            UpdateReferenceLines(renderRect, scale);
            UpdateBottomControlsVisibility(renderRect);

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
                UpdateVisualsForMultipleClips(renderRect, scale, ignorePositionProvider);
                return;
            }

            foreach (var entry in _clipStates)
            {
                var clipId = entry.Key;
                var state = entry.Value;

                UpdateClipStateZIndex(state, clipId);

                if (!TryResolveClipRect(clipId, ignorePositionProvider, out var x, out var y, out var w, out var h, out var clipType, out var isCurrentClip))
                {
                    state.Hide();
                    continue;
                }

                // Clamp to keep UI stable.
                w = Math.Clamp(w, MinSize, _videoWidth);
                h = Math.Clamp(h, MinSize, _videoHeight);
                if (!AllowClipOutOfBounds)
                {
                    x = Math.Clamp(x, 0, _videoWidth - w);
                    y = Math.Clamp(y, 0, _videoHeight - h);
                }

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

        private bool TryResolveClipRect(string clipId, bool ignorePosotionProvider, out double x, out double y, out double w, out double h, out ClipMode clipType, out bool isCurrentClip)
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
                GetCurrentRect(ignorePosotionProvider, out x, out y, out w, out h);
                return true;
            }

            if (!_previewSourceClips.TryGetValue(clipId, out var sourceClip))
            {
                return false;
            }

            return TryResolveSourceClipRect(sourceClip, ignorePosotionProvider, ref x, ref y, ref w, ref h, out clipType);
        }

        private bool TryResolveSourceClipRect(object sourceClip, bool ignorePositionProvider, ref double x, ref double y, ref double w, ref double h, out ClipMode clipType)
        {
            clipType = ClipMode.AudioClip;
            IEnumerable<IEffect>? effects = null;
            IClip? iclipSource = null;

            if (sourceClip is ClipElementUI uiClip)
            {
                clipType = uiClip.ClipType;

                if (uiClip.ClipType == ClipMode.TextClip && TryResolveTextClipRect(uiClip, out var textX, out var textY, out var textW, out var textH))
                {
                    x = textX;
                    y = textY;
                    w = textW;
                    h = textH;
                    return true;
                }

                x = uiClip.TargetX;
                y = uiClip.TargetY;
                if (uiClip.TargetWidth > 0)
                {
                    w = uiClip.TargetWidth;
                }

                if (uiClip.TargetHeight > 0)
                {
                    h = uiClip.TargetHeight;
                }

                if (uiClip.ClipType == ClipMode.SolidColorClip)
                {
                    if (uiClip.TargetWidth > 0)
                    {
                        w = uiClip.TargetWidth;
                    }
                    else
                    {
                        w = ReadSolidColorSize(uiClip.ExtraData, SolidColorOutputWidthKey, (int)Math.Round(w));
                    }

                    if (uiClip.TargetHeight > 0)
                    {
                        h = uiClip.TargetHeight;
                    }
                    else
                    {
                        h = ReadSolidColorSize(uiClip.ExtraData, SolidColorOutputHeightKey, (int)Math.Round(h));
                    }
                }

                // 当 TargetWidth 和 TargetHeight 均未设置时，根据资产原始比例计算适配尺寸
                if (uiClip.TargetWidth <= 0 && uiClip.TargetHeight <= 0)
                {
                    AssetItem? clipAsset = null;
                    _assets?.TryGetValue(uiClip.Id, out clipAsset);
                    ComputeFittedRectFromAsset(clipAsset, uiClip, _videoWidth, _videoHeight, ref w, ref h);
                }

                effects = uiClip.Effects?.Count > 0 ? uiClip.Effects.Values : null;
            }
            else if (sourceClip is IClip iclip)
            {
                clipType = iclip.ClipType;

                x = iclip.TargetX;
                y = iclip.TargetY;
                if (iclip.TargetWidth > 0)
                {
                    w = iclip.TargetWidth;
                }

                if (iclip.TargetHeight > 0)
                {
                    h = iclip.TargetHeight;
                }

                effects = iclip.EffectsInstances?.Length > 0 ? iclip.EffectsInstances : null;
                iclipSource = iclip;
            }
            else
            {
                return false;
            }

            if (effects is not null && !ignorePositionProvider)
            {
                ApplyPositionProvidersToRect(
                    effects,
                    iclipSource,
                    _currentFrame,
                    (int)Math.Round(_videoWidth),
                    (int)Math.Round(_videoHeight),
                    ref x, ref y, ref w, ref h);
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

        private void UpdateVisualsForMultipleClips(Rect renderRect, double scale, bool ignorePositionProvider)
        {
            //LogDiagnostic($"Updating visuals for {_allClips.Count} clips, scale: {scale}");
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

                    // 当 TargetWidth 和 TargetHeight 均未设置时，根据资产原始比例计算适配尺寸
                    if (clip.TargetWidth <= 0 && clip.TargetHeight <= 0)
                    {
                        AssetItem? clipAsset = null;
                        _assets?.TryGetValue(clip.Id, out clipAsset);
                        ComputeFittedRectFromAsset(clipAsset, clip, _videoWidth, _videoHeight, ref w, ref h);
                    }

                    if (clip.Effects?.Count > 0 && !ignorePositionProvider)
                    {
                        ApplyPositionProvidersToRect(
                            clip.Effects.Values,
                            clipSource: null,
                            _currentFrame,
                            (int)Math.Round(_videoWidth),
                            (int)Math.Round(_videoHeight),
                            ref x, ref y, ref w, ref h);
                    }
                }

                // Clamp to keep UI stable.
                w = Math.Clamp(w, MinSize, _videoWidth);
                h = Math.Clamp(h, MinSize, _videoHeight);
                if (!AllowClipOutOfBounds)
                {
                    x = Math.Clamp(x, 0, _videoWidth - w);
                    y = Math.Clamp(y, 0, _videoHeight - h);
                }

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

            Dispatcher.Dispatch(() =>
            {
                foreach (var root in orderedRoots)
                {
                    try
                    {
                        ClipStatesHost.Children.Remove(root);
                        ClipStatesHost.Children.Add(root);
                    }
                    catch (Exception ex)
                    {
                        Log(ex, "reorder the clips overlay state", this);
                    }
                }
            });
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

        #endregion

        #region gesture handlers

        long _panEventTriggerCounter = 0;
        double _stateOrigX = 0, _stateOrigY = 0;
        double _stateOrigScaleX = 1, _stateOrigScaleY = 1, _stateOrigThickness = 1;

        private void OnClipPanUpdated(ClipOverlayState state, PanUpdatedEventArgs e)
        {
            if (LockLayout) return;
            LogDiagnostic($"[Pan] OnClipPanUpdated fired, id:{e.GestureId}, StatusType:{e.StatusType}, last update:{_panTimer.ElapsedTicks - _lastPanUpdateTicks}");
            if (_currentClip == null || !ReferenceEquals(state, _activeState) || _isTextClip) return;
            _panEventTriggerCounter++;
            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    if (_isClipPanInProgress)
                    {
                        // Platform fired a redundant Started during an ongoing gesture
                        // (typically caused by layout changes in UpdateVisuals).
                        // Ignore it to avoid resetting the pan origin.
                        return;
                    }

                    _panEventTriggerCounter = 0;
                    _panTimer.Restart();
                    _isClipPanInProgress = true;
                    _lastPanUpdateTicks = 0; // process first Running event immediately
                    _stateOrigX = _activeState.Root.TranslationX;
                    _stateOrigY = _activeState.Root.TranslationY;
                    GetCurrentRect(true, out _startX, out _startY, out _startW, out _startH);
                    LogDiagnostic($"[Pan] Started: Pos=({_startX:F1}, {_startY:F1}), Size=({_startW:F1}, {_startH:F1})");
                    break;

                case GestureStatus.Running:
                    {
                        _activeState.Root.TranslationX = _stateOrigX + e.TotalX;
                        _activeState.Root.TranslationY = _stateOrigY + e.TotalY;
                        if (_panTimer.ElapsedTicks - _lastPanUpdateTicks < 200) return;

                        Rect renderRect = GetRenderRect();
                        double scale = Math.Max(renderRect.Width, 0.001) / _videoWidth;
                        if (renderRect.Width <= 0 || renderRect.Height <= 0 || scale <= 0.001) break;
                        double deltaX = e.TotalX / scale;
                        double deltaY = e.TotalY / scale;

                        double newVisualX = _startX + deltaX;
                        double newVisualY = _startY + deltaY;
                        double snapThresholdVideo = ComputeSnapThresholdVideo(scale);
                        var unsnapped = new Rect(newVisualX, newVisualY, _startW, _startH);
                        _panPreviewRect = ApplyClipSnapping(unsnapped, snapThresholdVideo, handle: null);

                        // Snap visual to the snapped preview rect for magnetic feel
                        _activeState.Root.TranslationX = _stateOrigX + (_panPreviewRect.Value.X - _startX) * scale;
                        _activeState.Root.TranslationY = _stateOrigY + (_panPreviewRect.Value.Y - _startY) * scale;

                        LogDiagnostic($"[Pan] Updated: triggered {_panEventTriggerCounter} times, Pos=({_panPreviewRect.Value.X:F1}, {_panPreviewRect.Value.Y:F1}), Delta=({deltaX:F1}, {deltaY:F1}) , elapsed:{_panTimer.Elapsed}, last update: {_panTimer.ElapsedTicks - _lastPanUpdateTicks}");
                        _lastPanUpdateTicks = _panTimer.ElapsedTicks;
                        break;
                    }

                case GestureStatus.Completed:
                case GestureStatus.Canceled:
                    _isClipPanInProgress = false;
                    if (_panPreviewRect.HasValue)
                    {
                        var r = _panPreviewRect.Value;
                        UpdateClipEffects(r.X, r.Y, r.Width, r.Height);
                        UpdateVisuals(true);
                    }
                    // Reset translation after committing the drag position to TargetX/Y
                    _activeState.Root.TranslationX = 0;
                    _activeState.Root.TranslationY = 0;
                    UpdateVisuals(true);
                    GetCurrentRect(true, out var finalX, out var finalY, out _, out _);
                    LogDiagnostic($"[Pan] Completed: triggered {_panEventTriggerCounter} times, FinalPos=({finalX:F1}, {finalY:F1}), elapsed:{_panTimer.Elapsed}");
                    RequestCommitUpdate();
                    RequestInteractivePreviewRefreshIfMissing(state);
                    break;
            }
        }

        private void OnTextEntryPanUpdated(ClipOverlayState state, int entryIndex, PanUpdatedEventArgs e)
        {
            if (LockLayout) return;
            if (_currentClip == null || !_isTextClip || !ReferenceEquals(state, _activeState)) return;

            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    if (_isClipPanInProgress)
                    {
                        // Redundant Started during an ongoing gesture; ignore.
                        return;
                    }

                    if (!TryPrepareTextEntryManipulation(entryIndex))
                    {
                        return;
                    }

                    _panTimer.Restart();
                    _isClipPanInProgress = true;
                    _lastPanUpdateTicks = 0; // process first Running event immediately
                    _stateOrigX = _activeState.Root.TranslationX;
                    _stateOrigY = _activeState.Root.TranslationY;
                    _isHandleResizeInProgress = false;
                    LogDiagnostic($"[TextEntryPan] Started: entry={entryIndex}, Rect=({_textEntryStartRect.X:F1}, {_textEntryStartRect.Y:F1}, {_textEntryStartRect.Width:F1}, {_textEntryStartRect.Height:F1})");
                    break;

                case GestureStatus.Running:
                    if (!_isClipPanInProgress || _activeTextEntryIndex != entryIndex)
                    {
                        break;
                    }

                    _activeState.Root.TranslationX = _stateOrigX + e.TotalX;
                    _activeState.Root.TranslationY = _stateOrigY + e.TotalY;
                    if (_panTimer.ElapsedTicks - _lastPanUpdateTicks < 200) return;

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
                    var snapThresholdVideo = ComputeSnapThresholdVideo(scale);
                    if (!TryUpdateTextEntryPositionFromPan(entryIndex, deltaX, deltaY, snapThresholdVideo))
                    {
                        break;
                    }

                    // Snap visual to the snapped text entry position for magnetic feel
                    if (_panPreviewTextEntries is not null && entryIndex < _panPreviewTextEntries.Count)
                    {
                        var sEntry = _panPreviewTextEntries[entryIndex];
                        _activeState.Root.TranslationX = _stateOrigX + (sEntry.x - _textEntryStartOriginX) * scale;
                        _activeState.Root.TranslationY = _stateOrigY + (sEntry.y - _textEntryStartOriginY) * scale;
                    }

                    break;

                case GestureStatus.Completed:
                case GestureStatus.Canceled:
                    _isClipPanInProgress = false;
                    _activeState.Root.TranslationX = 0;
                    _activeState.Root.TranslationY = 0;
                    if (_panPreviewTextEntries is not null && _currentClip is not null)
                    {
                        UpdateVisuals(true);
                        _currentClip.ExtraData!["TextEntries"] = _panPreviewTextEntries;
                        _panPreviewTextEntries = null;
                    }
                    RequestCommitUpdate();
                    RequestInteractivePreviewRefreshIfMissing(state);
                    break;
            }
        }

        private void OnTextEntryResizePanUpdated(ClipOverlayState state, int entryIndex, ResizeHandle handle, PanUpdatedEventArgs e)
        {
            if (LockLayout) return;
            if (_currentClip == null || !_isTextClip || !ReferenceEquals(state, _activeState)) return;

            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    if (_isHandleResizeInProgress)
                    {
                        // Redundant Started during an ongoing gesture; ignore.
                        return;
                    }

                    if (!TryPrepareTextEntryManipulation(entryIndex))
                    {
                        return;
                    }

                    _isClipPanInProgress = false;
                    _isHandleResizeInProgress = true;
                    LogDiagnostic($"[TextEntryResize] Started: entry={entryIndex}, Rect=({_textEntryStartRect.X:F1}, {_textEntryStartRect.Y:F1}, {_textEntryStartRect.Width:F1}, {_textEntryStartRect.Height:F1})");
                    state.SetPreviewView(null);
                    UpdateVisuals(true);
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
                    var snapThresholdVideo = ComputeSnapThresholdVideo(scale);

                    if (!TryScaleTextEntryFromResize(entryIndex, handle, deltaX, deltaY, snapThresholdVideo))
                    {
                        break;
                    }

                    UpdateVisuals(true);
                    break;

                case GestureStatus.Completed:
                case GestureStatus.Canceled:
                    _isHandleResizeInProgress = false;
                    if (_panPreviewTextEntries is not null && _currentClip is not null)
                    {
                        _currentClip.ExtraData!["TextEntries"] = _panPreviewTextEntries;
                        _panPreviewTextEntries = null;
                    }
                    state.SetPreviewView(null);
                    UpdateVisuals(true);
                    RequestInteractivePreviewRefresh();
                    RequestCommitUpdate();
                    break;
            }
        }

        private bool TryPrepareTextEntryManipulation(int entryIndex)
        {
            if (!TryGetCurrentTextEntry(entryIndex, out var entries, out var entry))
            {
                return false;
            }

            _panPreviewTextEntries = entries.Select(e => e).ToList();

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

        private bool TryUpdateTextEntryPositionFromPan(int entryIndex, double deltaX, double deltaY, double snapThresholdVideo)
        {
            if (_currentClip == null || _panPreviewTextEntries is null || entryIndex < 0 || entryIndex >= _panPreviewTextEntries.Count)
            {
                return false;
            }

            var entry = _panPreviewTextEntries[entryIndex];

            var rectW = Math.Clamp(_textEntryStartRect.Width, MinSize, _videoWidth);
            var rectH = Math.Clamp(_textEntryStartRect.Height, MinSize, _videoHeight);
            var minOriginX = -_textEntryStartOffsetX;
            var minOriginY = -_textEntryStartOffsetY;
            var maxOriginX = _videoWidth - rectW - _textEntryStartOffsetX;
            var maxOriginY = _videoHeight - rectH - _textEntryStartOffsetY;

            var nextOriginX = Math.Clamp(_textEntryStartOriginX + deltaX, minOriginX, maxOriginX);
            var nextOriginY = Math.Clamp(_textEntryStartOriginY + deltaY, minOriginY, maxOriginY);

            if (snapThresholdVideo > 0)
            {
                double rectX = nextOriginX + _textEntryStartOffsetX;
                double rectY = nextOriginY + _textEntryStartOffsetY;
                var snapped = ApplyClipSnapping(new Rect(rectX, rectY, rectW, rectH), snapThresholdVideo, handle: null);
                nextOriginX = snapped.X - _textEntryStartOffsetX;
                nextOriginY = snapped.Y - _textEntryStartOffsetY;
            }

            _panPreviewTextEntries[entryIndex] = entry with
            {
                x = (int)Math.Round(nextOriginX, MidpointRounding.AwayFromZero),
                y = (int)Math.Round(nextOriginY, MidpointRounding.AwayFromZero)
            };

            return true;
        }

        private bool TryScaleTextEntryFromResize(int entryIndex, ResizeHandle handle, double deltaX, double deltaY, double snapThresholdVideo)
        {
            if (_currentClip == null || _panPreviewTextEntries is null || entryIndex < 0 || entryIndex >= _panPreviewTextEntries.Count)
            {
                return false;
            }

            var entry = _panPreviewTextEntries[entryIndex];

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

            if (snapThresholdVideo > 0)
            {
                var snapped = ApplyClipSnapping(new Rect(nextX, nextY, nextW, nextH), snapThresholdVideo, handle);
                nextX = snapped.X;
                nextY = snapped.Y;
                nextW = snapped.Width;
                nextH = snapped.Height;
            }

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

            _panPreviewTextEntries[entryIndex] = entry with
            {
                x = (int)Math.Round(nextOriginX, MidpointRounding.AwayFromZero),
                y = (int)Math.Round(nextOriginY, MidpointRounding.AwayFromZero),
                fontSize = fontSize,
                wrappingWidth = wrappingWidth,
                strokeWidth = strokeWidth
            };

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
            if (LockLayout) return;
            LogDiagnostic($"[Pan] OnResizePanUpdated fired, last update:{_panTimer.ElapsedTicks - _lastPanUpdateTicks}");
            if (_currentClip == null || !ReferenceEquals(state, _activeState)) return;
            if (_isTextClip) return;  // Can't resize a TextClip
            bool allowFreeScale = IsAllowFreeScaleResizeEnabled(_currentClip);

            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    if (_isHandleResizeInProgress)
                    {
                        // Redundant Started during an ongoing gesture; ignore.
                        return;
                    }

                    _panTimer.Restart();
                    _lastPanUpdateTicks = 0;
                    _isHandleResizeInProgress = true;
                    GetCurrentRect(true, out _startX, out _startY, out _startW, out _startH);
                    _stateOrigX = _activeState.Root.TranslationX;
                    _stateOrigY = _activeState.Root.TranslationY;
                    _stateOrigScaleX = _activeState.Root.ScaleX;
                    _stateOrigScaleY = _activeState.Root.ScaleY;
                    _stateOrigThickness = _activeState.ClipVisual.StrokeThickness;
                    _activeState.SizeLabel.ScaleX = 1;
                    _activeState.SizeLabel.ScaleY = 1;
                    LogDiagnostic($"[Resize] Started: Pos=({_startX:F1}, {_startY:F1}), Size=({_startW:F1}x{_startH:F1})");
                    state.SetPreviewView(null);
                    break;

                case GestureStatus.Running:
                    _isHandleResizeInProgress = true;

                    Rect renderRect = GetRenderRect();
                    if (renderRect.Width <= 0 || renderRect.Height <= 0) break;

                    double scale = Math.Max(renderRect.Width, 0.001) / _videoWidth;
                    if (scale <= 0.001) break;

                    double dx = e.TotalX / scale;
                    double dy = e.TotalY / scale;

                    double newX = _startX, newY = _startY, newW = _startW, newH = _startH;

                    if (handle == ResizeHandle.TopLeft)
                    {
                        newW = Math.Max(MinSize, _startW - dx);
                        newH = Math.Max(MinSize, _startH - dy);
                        newX = _startX + (_startW - newW);
                        newY = _startY + (_startH - newH);
                    }
                    else if (handle == ResizeHandle.TopRight)
                    {
                        newW = Math.Max(MinSize, _startW + dx);
                        newH = Math.Max(MinSize, _startH - dy);
                        newY = _startY + (_startH - newH);
                    }
                    else if (handle == ResizeHandle.BottomLeft)
                    {
                        newW = Math.Max(MinSize, _startW - dx);
                        newH = Math.Max(MinSize, _startH + dy);
                        newX = _startX + (_startW - newW);
                    }
                    else if (handle == ResizeHandle.BottomRight)
                    {
                        newW = Math.Max(MinSize, _startW + dx);
                        newH = Math.Max(MinSize, _startH + dy);
                    }

                    if (!allowFreeScale)
                    {
                        ApplyAspectLockedResize(handle, ref newX, ref newY, ref newW, ref newH);
                    }

                    double snapThresholdVideo = ComputeSnapThresholdVideo(scale);
                    var snapped = ApplyClipSnapping(new Rect(newX, newY, newW, newH), snapThresholdVideo, handle);
                    newX = snapped.X;
                    newY = snapped.Y;
                    newW = snapped.Width;
                    newH = snapped.Height;
                    _panPreviewRect = snapped;



                    // Visual-only transform on Root (Scale + Translation), matching the clip‑pan
                    // pattern where the UI is updated via lightweight transforms during drag
                    // and the real effect is applied only on completion.
                    double sx = newW / Math.Max(_startW, 0.001);
                    double sy = newH / Math.Max(_startH, 0.001);
                    _activeState.Root.ScaleX = _stateOrigScaleX * sx;
                    _activeState.Root.ScaleY = _stateOrigScaleY * sy;
                    _activeState.Root.TranslationX = _stateOrigX + scale * ((newX - _startX) + (newW - _startW) / 2);
                    _activeState.Root.TranslationY = _stateOrigY + scale * ((newY - _startY) + (newH - _startH) / 2);
                    _activeState.HandleBL.ScaleX = 1 / (_stateOrigScaleX * sx);
                    _activeState.HandleBL.ScaleY = 1 / (_stateOrigScaleY * sy);
                    _activeState.HandleBR.ScaleX = 1 / (_stateOrigScaleX * sx);
                    _activeState.HandleBR.ScaleY = 1 / (_stateOrigScaleY * sy);
                    _activeState.HandleTL.ScaleX = 1 / (_stateOrigScaleX * sx);
                    _activeState.HandleTL.ScaleY = 1 / (_stateOrigScaleY * sy);
                    _activeState.HandleTR.ScaleX = 1 / (_stateOrigScaleX * sx);
                    _activeState.HandleTR.ScaleY = 1 / (_stateOrigScaleY * sy);
                    _activeState.ClipVisual.StrokeThickness = _stateOrigThickness * (1 / (_stateOrigScaleY * sy));
                    _activeState.SizeLabel.Text = $"{Math.Round(newW)} x {Math.Round(newH)}";
                    _activeState.SizeLabel.ScaleX = 1 / (_stateOrigScaleX * sx);
                    _activeState.SizeLabel.ScaleY = 1 / (_stateOrigScaleY * sy);
                    _activeState.SizeLabel.IsVisible = true;
                    _lastPanUpdateTicks = _panTimer.ElapsedTicks;
                    break;

                case GestureStatus.Completed:
                case GestureStatus.Canceled:
                    _isHandleResizeInProgress = false;

                    // Reset transforms applied during drag BEFORE UpdateVisuals so that
                    // SetLayoutBounds works with identity scale/translation.  If transforms
                    // are reset after UpdateVisuals, the stale Scale values distort the
                    // layout and produce a visible "snap back" when Scale is finally reset.
                    _activeState.Root.ScaleX = _stateOrigScaleX;
                    _activeState.Root.ScaleY = _stateOrigScaleY;
                    _activeState.Root.TranslationX = _stateOrigX;
                    _activeState.Root.TranslationY = _stateOrigY;
                    _activeState.HandleBL.ScaleX = 1;
                    _activeState.HandleBL.ScaleY = 1;
                    _activeState.HandleBR.ScaleX = 1;
                    _activeState.HandleBR.ScaleY = 1;
                    _activeState.HandleTL.ScaleX = 1;
                    _activeState.HandleTL.ScaleY = 1;
                    _activeState.HandleTR.ScaleX = 1;
                    _activeState.HandleTR.ScaleY = 1;
                    _activeState.SizeLabel.ScaleX = 1;
                    _activeState.SizeLabel.ScaleY = 1;
                    _activeState.ClipVisual.StrokeThickness = _stateOrigThickness;
                    _activeState.SizeLabel.IsVisible = false;

                    if (_panPreviewRect.HasValue)
                    {
                        var r = _panPreviewRect.Value;
                        UpdateClipEffects(r.X, r.Y, r.Width, r.Height);
                        _panPreviewRect = null;
                    }
                    GetCurrentRect(true, out var finalX, out var finalY, out var finalW, out var finalH);
                    LogDiagnostic($"[Resize] Completed: Pos=({finalX:F1}, {finalY:F1}), Size=({finalW:F1}x{finalH:F1}), elapsed:{_panTimer.Elapsed}");
                    state.SetPreviewView(null);
                    UpdateVisuals(true);
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
            if (state.HasPreviewView || IsInteractiveManipulationInProgress)
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

        #endregion

        #region snapping

        private List<double> GetHorizontalSnapTargets()
        {
            var targets = new List<double>(3 + _referenceLines.Count);
            targets.Add(0);
            targets.Add(_videoWidth);
            targets.Add(_videoWidth / 2.0);
            foreach (var rl in _referenceLines.Values)
            {
                if (rl.Orientation == ReferenceLineOrientation.Vertical)
                    targets.Add(rl.Position);
            }

            return targets;
        }

        private List<double> GetVerticalSnapTargets()
        {
            var targets = new List<double>(3 + _referenceLines.Count);
            targets.Add(0);
            targets.Add(_videoHeight);
            targets.Add(_videoHeight / 2.0);
            foreach (var rl in _referenceLines.Values)
            {
                if (rl.Orientation == ReferenceLineOrientation.Horizontal)
                    targets.Add(rl.Position);
            }

            return targets;
        }

        private void AddOtherClipEdgesToSnapTargets(List<double> hTargets, List<double> vTargets)
        {
            if (_currentClip is null || _allClips is null)
                return;

            var currentId = _currentClip.Id;
            foreach (var clip in _allClips.Values)
            {
                if (string.Equals(clip.Id, currentId, StringComparison.Ordinal))
                    continue;
                if (!IsClipVisibleInCurrentFrame(clip))
                    continue;

                var cx = clip.TargetX;
                var cy = clip.TargetY;
                var cw = clip.TargetWidth > 0 ? clip.TargetWidth : _videoWidth;
                var ch = clip.TargetHeight > 0 ? clip.TargetHeight : _videoHeight;

                hTargets.Add(cx);
                hTargets.Add(cx + cw);
                hTargets.Add(cx + cw / 2);

                vTargets.Add(cy);
                vTargets.Add(cy + ch);
                vTargets.Add(cy + ch / 2);
            }
        }

        private double ComputeSnapThresholdVideo(double scale)
        {
            return EnableSnapping && scale > 0.001
                ? SnapThresholdDisplayPx / scale
                : 0;
        }

        private Rect ApplyClipSnapping(Rect rect, double snapThresholdVideo, ResizeHandle? handle)
        {
            if (snapThresholdVideo <= 0)
                return rect;

            double x = rect.X, y = rect.Y, w = rect.Width, h = rect.Height;

            bool snapLeft, snapRight, snapCenterX;
            bool snapTop, snapBottom, snapCenterY;

            if (handle == null)
            {
                snapLeft = snapRight = snapCenterX = true;
                snapTop = snapBottom = snapCenterY = true;
            }
            else
            {
                snapCenterX = false;
                snapCenterY = false;
                switch (handle.Value)
                {
                    case ResizeHandle.TopLeft:
                        snapLeft = true; snapRight = false;
                        snapTop = true; snapBottom = false;
                        break;
                    case ResizeHandle.TopRight:
                        snapLeft = false; snapRight = true;
                        snapTop = true; snapBottom = false;
                        break;
                    case ResizeHandle.BottomLeft:
                        snapLeft = true; snapRight = false;
                        snapTop = false; snapBottom = true;
                        break;
                    case ResizeHandle.BottomRight:
                    default:
                        snapLeft = false; snapRight = true;
                        snapTop = false; snapBottom = true;
                        break;
                }
            }

            var hTargets = GetHorizontalSnapTargets();
            var vTargets = GetVerticalSnapTargets();
            AddOtherClipEdgesToSnapTargets(hTargets, vTargets);

            if (snapLeft || snapRight || snapCenterX)
            {
                double bestDist = snapThresholdVideo;
                double bestAdjustX = 0;
                double? bestW = null;

                foreach (var target in hTargets)
                {
                    if (snapLeft)
                    {
                        double dist = Math.Abs(x - target);
                        if (dist < bestDist) { bestDist = dist; bestAdjustX = target - x; bestW = null; }
                    }

                    if (snapRight)
                    {
                        double right = x + w;
                        double dist = Math.Abs(right - target);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            if (handle == null)
                            {
                                bestAdjustX = target - w - x;
                                bestW = null;
                            }
                            else
                            {
                                bestAdjustX = 0;
                                bestW = Math.Max(MinSize, target - x);
                            }
                        }
                    }

                    if (snapCenterX)
                    {
                        double cx = x + w / 2.0;
                        double dist = Math.Abs(cx - target);
                        if (dist < bestDist) { bestDist = dist; bestAdjustX = target - w / 2.0 - x; bestW = null; }
                    }
                }

                if (bestDist < snapThresholdVideo)
                {
                    LogDiagnostic($"Snap x triggered! bestAdjustX:{bestAdjustX}, bestW:{bestW ?? -1}");
                    x += bestAdjustX;
                    if (bestW.HasValue)
                        w = bestW.Value;
                }
            }

            if (snapTop || snapBottom || snapCenterY)
            {
                double bestDist = snapThresholdVideo;
                double bestAdjustY = 0;
                double? bestH = null;

                foreach (var target in vTargets)
                {
                    if (snapTop)
                    {
                        double dist = Math.Abs(y - target);
                        if (dist < bestDist) { bestDist = dist; bestAdjustY = target - y; bestH = null; }
                    }

                    if (snapBottom)
                    {
                        double bottom = y + h;
                        double dist = Math.Abs(bottom - target);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            if (handle == null)
                            {
                                bestAdjustY = target - h - y;
                                bestH = null;
                            }
                            else
                            {
                                bestAdjustY = 0;
                                bestH = Math.Max(MinSize, target - y);
                            }
                        }
                    }

                    if (snapCenterY)
                    {
                        double cy = y + h / 2.0;
                        double dist = Math.Abs(cy - target);
                        if (dist < bestDist) { bestDist = dist; bestAdjustY = target - h / 2.0 - y; bestH = null; }
                    }
                }

                if (bestDist < snapThresholdVideo)
                {
                    LogDiagnostic($"Snap y triggered! bestAdjustY:{bestAdjustY}, bestH:{bestH ?? -1}");
                    y += bestAdjustY;
                    if (bestH.HasValue)
                        h = bestH.Value;
                }
            }

            if (!AllowClipOutOfBounds)
            {
                w = Math.Min(w, _videoWidth);
                h = Math.Min(h, _videoHeight);
                x = Math.Clamp(x, 0, _videoWidth - w);
                y = Math.Clamp(y, 0, _videoHeight - h);
            }

            return new Rect(x, y, w, h);
        }

        #endregion

        #region update

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

        #endregion

        #region helpers

        private static void ApplyPositionProvidersToRect(IEnumerable<IEffect>? effects, IClip? clipSource, uint frameIndex, int targetWidth, int targetHeight, ref double x, ref double y, ref double w, ref double h)
        {
            if (effects is null)
            {
                return;
            }

            foreach (var effect in effects.OrderBy(e => ((IEffect)e).Index))
            {
                ClipPositionTuple pos;
                if (effect is IContinuousClipPositionProvider cp)
                {
                    pos = cp.GetPosition(clipSource!, frameIndex, targetWidth, targetHeight);
                }
                else if (effect is IClipPositionProvider p)
                {
                    pos = p.GetPosition(clipSource!, targetWidth, targetHeight);
                }
                else
                {
                    continue;
                }

                if (pos.IsDelta)
                {
                    x += pos.TargetX;
                    y += pos.TargetY;
                    w += pos.TargetWidth;
                    h += pos.TargetHeight;
                }
                else
                {
                    x = pos.TargetX;
                    y = pos.TargetY;
                    if (pos.TargetWidth > 0)
                    {
                        w = pos.TargetWidth;
                    }

                    if (pos.TargetHeight > 0)
                    {
                        h = pos.TargetHeight;
                    }
                }
            }
        }

        private void GetCurrentRect(bool ignorePositionProvider, out double x, out double y, out double w, out double h)
        {
            if (_panPreviewRect.HasValue && ignorePositionProvider)
            {
                var r = _panPreviewRect.Value;
                x = r.X;
                y = r.Y;
                w = r.Width;
                h = r.Height;
                return;
            }

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

            if (_currentClip.Effects?.Count > 0 && !ignorePositionProvider)
            {
                ApplyPositionProvidersToRect(
                    _currentClip.Effects.Values,
                    clipSource: null,
                    _currentFrame,
                    (int)Math.Round(_videoWidth),
                    (int)Math.Round(_videoHeight),
                    ref x, ref y, ref w, ref h);
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

            if (ReferenceEquals(clip, _currentClip) && _panPreviewTextEntries is not null)
            {
                entries = _panPreviewTextEntries;
                return true;
            }

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
            if (!AllowClipOutOfBounds)
            {
                x = Math.Clamp(x, 0, _videoWidth - w);
                y = Math.Clamp(y, 0, _videoHeight - h);
            }

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

        #endregion

        #region ui event handlers

        public void AddAReferenceLine(ReferenceLineOrientation? orientation)
        {
            if (orientation is null)
            {
                _isPlacingReferenceLine = false;
                return;
            }
            _pendingReferenceLineOrientation = orientation ?? ReferenceLineOrientation.Horizontal;
            _isPlacingReferenceLine = true;
        }

        private async void RefreshButton_Clicked(object sender, EventArgs e)
        {
            UpdateCanvasSize(Width, Height, true);

            if (StaticPreviewOverlayImage.IsVisible)
            {
                await (_updateCallback?.Invoke() ?? Task.CompletedTask);
            }
            else
            {
                UpdateVisuals(false);
            }
        }

        private void OnDefaultColorSwatchTapped(object? sender, EventArgs e)
        {
            _defaultColorPickerRequestedCallback?.Invoke(_defaultReferenceLineColor);
        }

        private void OnManageReferenceLinesTapped(object? sender, EventArgs e)
        {
            _manageReferenceLinesRequestedCallback?.Invoke();
        }

        private void OnThicknessEntryCompleted(object? sender, EventArgs e)
        {
            if (double.TryParse(ThicknessEntry.Text, out var t))
                DefaultReferenceLineThickness = t;
            else
                ThicknessEntry.Text = DefaultReferenceLineThickness.ToString("F1");
        }

        private void OnThicknessEntryUnfocused(object? sender, FocusEventArgs e)
        {
            if (double.TryParse(ThicknessEntry.Text, out var t))
                DefaultReferenceLineThickness = t;
            else
                ThicknessEntry.Text = DefaultReferenceLineThickness.ToString("F1");
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

        private void ManageRefLineButton_Clicked(object sender, EventArgs e)
        {
            _manageReferenceLinesRequestedCallback?.Invoke();
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

        private void UpdateBottomControlsVisibility(Rect renderRect)
        {
            var bottomGap = _canvasHeight - (renderRect.Y + renderRect.Height);
            _autoHideBottomControls = bottomGap < 35d;

            if (!_autoHideBottomControls)
            {
                CancelHideBottomControlsDebounce();
                LayoutOptionsBar.IsVisible = true;
                RefreshButton.IsVisible = true;
                ManageRefLineButton.IsVisible = true;
            }
            else
            {
                // 默认隐藏，鼠标进入 BottomControlsHost 区域时才显示
                LayoutOptionsBar.IsVisible = false;
                RefreshButton.IsVisible = false;
                ManageRefLineButton.IsVisible = false;

            }
        }

        private void OnBottomControlsHostEntered(object? sender, PointerEventArgs e)
        {
            if (!_autoHideBottomControls)
            {
                return;
            }

            CancelHideBottomControlsDebounce();
            LayoutOptionsBar.IsVisible = true;
            RefreshButton.IsVisible = true;
            ManageRefLineButton.IsVisible = true;
        }

        private async void OnBottomControlsHostExited(object? sender, PointerEventArgs e)
        {
            if (!_autoHideBottomControls)
            {
                return;
            }

            CancelHideBottomControlsDebounce();
            var cts = new CancellationTokenSource();
            _hideBottomControlsCts = cts;

            try
            {
                await Task.Delay(250, cts.Token);
                LayoutOptionsBar.IsVisible = false;
                RefreshButton.IsVisible = false;
                ManageRefLineButton.IsVisible = false;
            }
            catch (OperationCanceledException)
            {
                // 鼠标在延迟期间重新进入，取消隐藏
            }
        }

        private void CancelHideBottomControlsDebounce()
        {
            var cts = Interlocked.Exchange(ref _hideBottomControlsCts, null);
            if (cts is not null)
            {
                cts.Cancel();
                cts.Dispose();
            }
        }

        /// <summary>
        /// 强制更新手势识别器以避免与父容器的手势冲突
        /// </summary>
        public void RefreshGestureRecognizers()
        {
            _activeState?.RefreshGestureRecognizers();
        }

        #endregion
    }
}