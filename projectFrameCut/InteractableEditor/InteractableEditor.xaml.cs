using projectFrameCut.ApplicationPluginBase.Text;
using projectFrameCut.Asset;
using projectFrameCut.DraftStuff;
using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Shared;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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
        private const string TextClipStyleAllowFreeResizeKey = "TextClipStyleAllowFreeResize";
        private const string TextStyleParametersKey = "TextStyleProvider_Parameters";
        private const string TextStyleProviderFromKey = "TextStyleProvider_FromPlugin";
        private const string TextStyleProviderTypeKey = "TextStyleProvider_TypeName";
        private const string TextStyleProviderParametersKey = "TextStyleProvider_Parameters";

        private const string DisableMoveKey = "InteractableEditor_DisableMove";
        private const string DisableHorizontalResizeKey = "InteractableEditor_DisableHorizontalResize";
        private const string DisableVerticalResizeKey = "InteractableEditor_DisableVerticalResize";

        private double _canvasWidth = 800;
        private double _canvasHeight = 240;
        private double _videoWidth = 1920;
        private double _videoHeight = 1080;

        private double _startX, _startY, _startW, _startH;
        private Rect _baseRect;
        private bool _isClipPanInProgress;
        private bool _isHandleResizeInProgress;
        private bool _isPlacingReferenceLine;
        private ReferenceLineOrientation? _pendingReferenceLineOrientation;
        private int _referenceLineCounter;
        private Color _defaultReferenceLineColor = Color.FromRgba(0, 255, 255, 128);
        private double _defaultReferenceLineThickness = 1.0;
        private Stopwatch _panTimer = new();
        private long _lastPanUpdateTicks = 0;

        private Rect? _panPreviewRect;

        private const double HandleSize = 15;
        private const double MinSize = 10;
        private const double SnapThresholdDisplayPx = 10.0;

        private readonly Dictionary<string, ClipOverlayState> _clipStates = new(StringComparer.Ordinal);
        private readonly object _clipStatesLock = new();
        private readonly Dictionary<string, object> _previewSourceClips = new(StringComparer.Ordinal);
        private ClipOverlayState? _activeState;
        private Func<Task>? _previewRefreshCallback;
        private Func<string, Task>? _overlayClipTappedCallback;
        private Func<string, Task>? _overlayClipDoubleTappedCallback;
        private Func<Task>? _blankAreaTappedCallback;
        private Func<Task>? _referenceLinesChangedCallback;
        private Action<string, uint, ClipPositionTuple>? _keyframeCandidateCapturedCallback;
        private Func<ClipElementUI, IClip?>? _getClipInstanceCallback;
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
        public bool EnableKeyframeRecording { get; set { if (field == value) return; field = value; OnPropertyChanged(); } } = false;
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

        private static Brush GetClipOverlayStroke(ClipElementUI? clip)
            => clip?.Clip.Stroke ?? Colors.Yellow;

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
            private readonly InteractableEditor _owner;
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

                var rootDoubleTap = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
                rootDoubleTap.Tapped += (_, _) => _owner.OnClipOverlayDoubleTapped(this);
                Root.GestureRecognizers.Add(rootDoubleTap);

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
                    || (!ClipVisual.IsVisible && !PreviewHost.IsVisible);
            }

            public void UpdateLayout(double displayX, double displayY, double displayW, double displayH, double logicalW, double logicalH, bool showHandles, bool showSizeLabel, string? sizeText, bool showClipVisual, Brush? clipStroke = null)
            {
                Root.IsVisible = true;
                AbsoluteLayout.SetLayoutBounds(Root, new Rect(displayX, displayY, displayW, displayH));
                AbsoluteLayout.SetLayoutBounds(ClipVisual, new Rect(0, 0, displayW, displayH));
                ClipVisual.IsVisible = showClipVisual;
                if (showClipVisual)
                {
                    ClipVisual.Stroke = clipStroke ?? Colors.Yellow;
                }
                UpdatePreviewHostLayout(displayW, displayH, logicalW, logicalH);
                UpdateRootInputTransparency();

                double handleSize = HandleSize;
                AbsoluteLayout.SetLayoutBounds(HandleTL, new Rect(-handleSize / 2, -handleSize / 2, handleSize, handleSize));
                AbsoluteLayout.SetLayoutBounds(HandleTR, new Rect(displayW - handleSize / 2, -handleSize / 2, handleSize, handleSize));
                AbsoluteLayout.SetLayoutBounds(HandleBL, new Rect(-handleSize / 2, displayH - handleSize / 2, handleSize, handleSize));
                AbsoluteLayout.SetLayoutBounds(HandleBR, new Rect(displayW - handleSize / 2, displayH - handleSize / 2, handleSize, handleSize));

                bool resizeHandleVisible = showHandles && (_owner.Clips[ClipId].IsHorizontalResizable || _owner.Clips[ClipId].IsVerticalResizable);
                HandleTL.IsVisible = resizeHandleVisible;
                HandleTR.IsVisible = resizeHandleVisible;
                HandleBL.IsVisible = resizeHandleVisible;
                HandleBR.IsVisible = resizeHandleVisible;

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

                if (view is not null
                    && PreviewHost.Content is View existingPreview
                    && !ReferenceEquals(existingPreview, view)
                    && TryUpdatePreviewTreeInPlace(existingPreview, view))
                {
                    UpdatePreviewHostVisibility();
                    return;
                }

                if (!ReferenceEquals(PreviewHost.Content, view))
                {
                    _ = MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        PreviewHost.Content = view;
                    });
                }

                UpdatePreviewHostVisibility();
            }

            private static bool TryUpdatePreviewTreeInPlace(View existing, View incoming)
            {
                if (ReferenceEquals(existing, incoming))
                {
                    return true;
                }

                if (existing.GetType() != incoming.GetType())
                {
                    return false;
                }

                ApplySharedViewState(existing, incoming);

                switch (existing)
                {
                    case Image existingImage when incoming is Image incomingImage:
                        existingImage.Aspect = incomingImage.Aspect;
                        existingImage.Source = incomingImage.Source;
                        return true;
                    case ContentView existingContent when incoming is ContentView incomingContent:
                        if (incomingContent.Content is null)
                        {
                            existingContent.Content = null;
                            return true;
                        }

                        if (existingContent.Content is not View existingContentChild
                            || incomingContent.Content is not View incomingContentChild)
                        {
                            return false;
                        }

                        return TryUpdatePreviewTreeInPlace(existingContentChild, incomingContentChild);
                    case Grid existingGrid when incoming is Grid incomingGrid:
                        if (existingGrid.Children.Count != incomingGrid.Children.Count)
                        {
                            return false;
                        }

                        for (var i = 0; i < existingGrid.Children.Count; i++)
                        {
                            if (existingGrid.Children[i] is not View existingGridChild
                                || incomingGrid.Children[i] is not View incomingGridChild
                                || !TryUpdatePreviewTreeInPlace(existingGridChild, incomingGridChild))
                            {
                                return false;
                            }
                        }

                        return true;
                    default:
                        return false;
                }
            }

            private static void ApplySharedViewState(View target, View source)
            {
                target.WidthRequest = source.WidthRequest;
                target.HeightRequest = source.HeightRequest;
                target.MinimumWidthRequest = source.MinimumWidthRequest;
                target.MinimumHeightRequest = source.MinimumHeightRequest;
                target.MaximumWidthRequest = source.MaximumWidthRequest;
                target.MaximumHeightRequest = source.MaximumHeightRequest;
                target.HorizontalOptions = source.HorizontalOptions;
                target.VerticalOptions = source.VerticalOptions;
                target.Margin = source.Margin;
                target.InputTransparent = source.InputTransparent;
                target.AnchorX = source.AnchorX;
                target.AnchorY = source.AnchorY;
                target.Scale = source.Scale;
                target.ScaleX = source.ScaleX;
                target.ScaleY = source.ScaleY;
                target.TranslationX = source.TranslationX;
                target.TranslationY = source.TranslationY;
                target.Rotation = source.Rotation;
                target.RotationX = source.RotationX;
                target.RotationY = source.RotationY;
                target.Opacity = source.Opacity;
                target.IsVisible = source.IsVisible;
                target.ZIndex = source.ZIndex;
                if (string.IsNullOrWhiteSpace(target.AutomationId)) target.AutomationId = source.AutomationId;
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

        public void ConfigureOverlayClipDoubleTap(Func<string, Task>? doubleTapCallback)
        {
            _overlayClipDoubleTappedCallback = doubleTapCallback;
        }

        public void ConfigureBlankAreaTap(Func<Task>? tapCallback)
        {
            _blankAreaTappedCallback = tapCallback;
        }

        public void ConfigureReferenceLinesChanged(Func<Task>? callback)
        {
            _referenceLinesChangedCallback = callback;
        }

        public void ConfigureKeyframeCandidateCaptured(Action<string, uint, ClipPositionTuple>? callback)
        {
            _keyframeCandidateCapturedCallback = callback;
        }

        public void ConfigureManageReferenceLinesRequested(Action? callback)
        {
            _manageReferenceLinesRequestedCallback = callback;
        }

        public void ConfigureDefaultColorPickerRequested(Action<Color>? callback)
        {
            _defaultColorPickerRequestedCallback = callback;
        }

        public void ConfigureGetClipInstanceCallback(Func<ClipElementUI, IClip?>? callback)
        {
            _getClipInstanceCallback = callback;
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
            ClipOverlayState state;
            lock (_clipStatesLock)
            {
                if (_clipStates.TryGetValue(clipId, out state))
                {
                    return state;
                }

                state = new ClipOverlayState(this, clipId, displayName);
                _clipStates[clipId] = state;
            }

            try
            {
                Dispatcher.Dispatch(() =>
                {
                    if (!ClipStatesHost.Children.Contains(state.Root))
                    {
                        ClipStatesHost.Children.Add(state.Root);
                    }
                });
                return state;
            }
            catch (Exception e1)
            {
                Log(e1, "add the clip overlay state for clip " + clipId, this);
                lock (_clipStatesLock)
                {
                    _clipStates.Remove(clipId);
                }
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

        private void OnClipOverlayDoubleTapped(ClipOverlayState state)
        {
            var callback = _overlayClipDoubleTappedCallback;
            if (callback is null)
            {
                return;
            }

            _ = InvokeOverlayClipDoubleTappedAsync(callback, state.ClipId);
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

        private async Task InvokeOverlayClipDoubleTappedAsync(Func<string, Task> callback, string clipId)
        {
            try
            {
                await callback(clipId);
            }
            catch (Exception ex)
            {
                LogDiagnostic($"Overlay clip double-tap callback failed: {ex.Message}");
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

        private void NotifyKeyframeCandidateCaptured(double x, double y, double w, double h)
        {
            if (!EnableKeyframeRecording || _currentClip is null)
            {
                return;
            }

            var callback = _keyframeCandidateCapturedCallback;
            if (callback is null)
            {
                return;
            }

            var keyframePosition = new ClipPositionTuple(
                (int)Math.Round(x, MidpointRounding.AwayFromZero),
                (int)Math.Round(y, MidpointRounding.AwayFromZero),
                Math.Max(1, (int)Math.Round(w, MidpointRounding.AwayFromZero)),
                Math.Max(1, (int)Math.Round(h, MidpointRounding.AwayFromZero)),
                false);

            callback(_currentClip.Id, _currentFrame, keyframePosition);
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
            if (clip == null)
            {
                SetActiveState(null);
                //this.IsVisible = false;
                //this.InputTransparent = true;
                //RenderRectVisual.IsVisible = false;
                Interlocked.Exchange(ref _hasPendingPreviewRefresh, 0);
                return;
            }

            // 非可视Clip类型（AudioClip、MarkingClip）没有视觉布局，直接隐藏编辑器
            if (IsNonVisualClipType(clip.ClipType))
            {
                SetActiveState(null);
                Interlocked.Exchange(ref _hasPendingPreviewRefresh, 0);
                return;
            }

            this.IsVisible = true;
            this.InputTransparent = false;

            var baseW = _videoWidth;
            var baseH = _videoHeight;
            ComputeFittedRectFromAsset(_currentAsset, clip, _videoWidth, _videoHeight, ref baseW, ref baseH);
            if (baseW <= 0) baseW = _videoWidth;
            if (baseH <= 0) baseH = _videoHeight;
            _baseRect = new Rect(0, 0, baseW, baseH);

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

        private static bool IsNonVisualClipType(ClipMode clipType)
            => clipType is ClipMode.AudioClip or ClipMode.MarkingClip;

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
                Stopwatch sw = Stopwatch.StartNew();


                UpdateClipStateZIndex(state, clipId);

                if (!TryResolveClipRect(clipId, ignorePositionProvider, out var x, out var y, out var w, out var h, out var clipType, out var isCurrentClip))
                {
                    state.Hide();
                    continue;
                }

                // 跳过非可视Clip类型（AudioClip、MarkingClip），避免占据视觉布局空间
                if (IsNonVisualClipType(clipType))
                {
                    state.Hide();
                    _previewSourceClips.Remove(clipId);
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

                var showHandles = isCurrentClip;
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
                    isCurrentClip,
                    isCurrentClip ? GetClipOverlayStroke(_currentClip) : null);

                UpdatePreviewDebugOverlay(state, clipId, w, h, displayW, displayH);

                //LogDiagnostic($"[UpdateVisuals] clip {clipId} debug overlay updated in {sw.ElapsedMilliseconds}ms");
                //LogDiagnostic($"[UpdateVisuals] clip {clipId} total update time {sw.ElapsedMilliseconds}ms");
            }

            ReorderClipStateRootsByZIndex();
        }

        private void UpdatePreviewDebugOverlay(ClipOverlayState state, string clipId, double logicalW, double logicalH, double displayW, double displayH)
        {
            if (state.DebugLabel.IsVisible == ShowPreviewDebugOverlay) return;
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

        /// <summary>
        /// Render the preview requests from <see cref="DynamicPreview"/>.
        /// </summary>
        /// <param name="preparedPreviews">The prepared previews</param>
        public async Task<bool> ApplyPreparedPreviewsAsync(IReadOnlyList<DynamicPreview.PreparedPreview> preparedPreviews)
        {
            if (Dispatcher.IsDispatchRequired)
            {
                return await Dispatcher.DispatchAsync(() => ApplyPreparedPreviews(preparedPreviews));
            }

            return ApplyPreparedPreviews(preparedPreviews);
        }

        /// <summary>
        /// Render the preview requests from <see cref="DynamicPreview"/>.
        /// </summary>
        /// <remarks>
        /// <b>THIS FUNCTION MUST BE CALLED ON THE UI THREAD.</b>
        /// </remarks>
        /// <param name="preparedPreviews">The prepared previews</param>
        [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
        public bool ApplyPreparedPreviews(IReadOnlyList<DynamicPreview.PreparedPreview> preparedPreviews)
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

                // 跳过非可视Clip类型（AudioClip、MarkingClip等），它们不需要预览和叠加层
                if (prepared.Source?.ClipType is ClipMode.AudioClip or ClipMode.MarkingClip)
                {
                    _previewSourceClips.Remove(prepared.ClipId);
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
                    if (w <= 0) w = _videoWidth;
                    if (h <= 0) h = _videoHeight;
                }

                if (clipType == ClipMode.TextClip && TryResolveTextClipViewRect(uiClip.ExtraData, out var textRect))
                {
                    x += textRect.X;
                    y += textRect.Y;
                    w = textRect.Width;
                    h = textRect.Height;
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

                if (clipType == ClipMode.TextClip && TryResolveTextClipViewRect(iclip.ExtraData, out var textRect))
                {
                    x += textRect.X;
                    y += textRect.Y;
                    w = textRect.Width;
                    h = textRect.Height;
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

            var activeClips = _allClips.Values.Where(IsClipVisibleInCurrentFrame);
            var activeClipIds = activeClips.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

            // 遍历所有clips，筛选出在当前帧范围内的clips
            foreach (var clip in activeClips)
            {
                Stopwatch sw = Stopwatch.StartNew();

                var state = GetOrCreateClipState(clip.Id);
                UpdateClipStateZIndex(state, clip.Id);

                // 计算clip的位置和大小
                double x;
                double y;
                double w;
                double h;

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
                    if (w <= 0) w = _videoWidth;
                    if (h <= 0) h = _videoHeight;
                }

                if (clip.ClipType == ClipMode.TextClip && TryResolveTextClipViewRect(clip.ExtraData, out var textRect))
                {
                    x += textRect.X;
                    y += textRect.Y;
                    w = textRect.Width;
                    h = textRect.Height;
                }

                if (clip.Effects?.Count > 0 && !ignorePositionProvider)
                {
                    ApplyPositionProvidersToRect(
                        clip.Effects.Values,
                        clipSource: GetClipInstance(clip),
                        _currentFrame,
                        (int)Math.Round(_videoWidth),
                        (int)Math.Round(_videoHeight),
                        ref x, ref y, ref w, ref h);
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
                bool showHandles = isCurrentClip;
                bool showSizeLabel = isCurrentClip && _isHandleResizeInProgress;

                Dispatcher.Dispatch(() =>
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
                        showClipVisual: isCurrentClip,
                        clipStroke: isCurrentClip ? GetClipOverlayStroke(clip) : null);

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
                .Where(ClipStatesHost.Children.Contains)
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
                Stopwatch sw = Stopwatch.StartNew();
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
                //LogDiagnostic($"[UpdateVisuals] Render clip overlay for frame {_currentFrame} reordered {orderedRoots.Count} states by ZIndex in {sw.ElapsedMilliseconds} ms");
            });
        }

        private bool IsClipVisibleInCurrentFrame(ClipElementUI clip)
        {
            if (!clip.ShouldDisplayInUI)
                return false;

            if (clip.Id.StartsWith("ghost_", StringComparison.Ordinal)
                || clip.Id.StartsWith("shadow_", StringComparison.Ordinal))
                return false;

            if (IsNonVisualClipType(clip.ClipType))
                return false;

            if (_getClipInstanceCallback is not null && _getClipInstanceCallback(clip) is IClip c)
            {
                return c.ContainsFrame(_currentFrame);
            }

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

            // Auto-select the clip being interacted with when no clip is currently
            // selected or when a different clip's gesture recogniser fires first.
            if (_currentClip == null || !ReferenceEquals(state, _activeState))
            {
                if (!Clips.TryGetValue(state.ClipId, out var target) || !target.IsMoveable)
                    return;
                _currentClip = target;
                _activeState = state;
            }
            else if (!_currentClip.IsMoveable)
            {
                return;
            }
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
                        double snapThresholdVideo = _currentClip?.CanSnapWhilePlacing != false
                            ? ComputeSnapThresholdVideo(scale)
                            : 0;
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
                    _isClipPanInProgress = false;
                    double finalX = 0, finalY = 0, finalW = 0, finalH = 0;
                    if (_panPreviewRect.HasValue)
                    {
                        var r = _panPreviewRect.Value;
                        UpdateClipRenderRect(r.X, r.Y, r.Width, r.Height, updateTextStyle: false, isInRatio: false);
                        GetCurrentRect(true, out finalX, out finalY, out finalW, out finalH);
                        NotifyKeyframeCandidateCaptured(finalX, finalY, finalW, finalH);
                        UpdateVisuals(true);
                    }
                    // Reset translation after committing the drag position to TargetX/Y
                    _activeState.Root.TranslationX = 0;
                    _activeState.Root.TranslationY = 0;
                    UpdateVisuals(true);
                    GetCurrentRect(true, out finalX, out finalY, out _, out _);
                    LogDiagnostic($"[Pan] Completed: triggered {_panEventTriggerCounter} times, FinalPos=({finalX:F1}, {finalY:F1}), elapsed:{_panTimer.Elapsed}");
                    RequestCommitUpdate();
                    RequestInteractivePreviewRefreshIfMissing(state);
                    break;
                case GestureStatus.Canceled:
                    _isClipPanInProgress = false;
                    _activeState.Root.TranslationX = 0;
                    _activeState.Root.TranslationY = 0;
                    UpdateVisuals(true);
                    RequestInteractivePreviewRefreshIfMissing(state);
                    break;
            }
        }

        private void OnResizePanUpdated(ClipOverlayState state, ResizeHandle handle, PanUpdatedEventArgs e)
        {
            if (LockLayout) return;
            LogDiagnostic($"[Pan] OnResizePanUpdated fired, last update:{_panTimer.ElapsedTicks - _lastPanUpdateTicks}");

            // Auto-select the clip being interacted with (mirrors OnClipPanUpdated).
            if (_currentClip == null || !ReferenceEquals(state, _activeState))
            {
                if (!Clips.TryGetValue(state.ClipId, out var target) || !target.IsMoveable)
                    return;
                _currentClip = target;
                _activeState = state;
            }
            else if (!_currentClip.IsMoveable)
            {
                return;
            }
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

                    if (_currentClip is not null)
                    {
                        // For TextClip with partial axis constraints (FixedWidth /
                        // FixedHeight layout modes), allow free two-axis drag so that
                        // corner handles feel responsive.  HandleClipResize applies the
                        // correct per-mode constraint on completion and the text view
                        // rect read-back provides the auto-computed dimension.
                        bool isTextClip = _currentClip.ClipType == ClipMode.TextClip;
                        if (!isTextClip && !_currentClip.IsHorizontalResizable)
                            dx = 0;
                        if (!isTextClip && !_currentClip.IsVerticalResizable)
                            dy = 0;
                    }

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

                    double snapThresholdVideo = _currentClip?.CanSnapWhileResizing != false
                        ? ComputeSnapThresholdVideo(scale)
                        : 0;
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
                    _isHandleResizeInProgress = false;
                    double finalX = 0, finalY = 0, finalW = 0, finalH = 0;

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
                        UpdateClipRenderRect(r.X, r.Y, r.Width, r.Height, updateTextStyle: true, isInRatio: !allowFreeScale);
                        _panPreviewRect = null;
                        GetCurrentRect(true, out finalX, out finalY, out finalW, out finalH);
                        NotifyKeyframeCandidateCaptured(finalX, finalY, finalW, finalH);
                    }
                    LogDiagnostic($"[Resize] Completed: Pos=({finalX:F1}, {finalY:F1}), Size=({finalW:F1}x{finalH:F1}), elapsed:{_panTimer.Elapsed}");
                    UpdateVisuals(true);
                    RequestCommitUpdate();
                    break;
                case GestureStatus.Canceled:
                    _isHandleResizeInProgress = false;
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
                    _panPreviewRect = null;
                    UpdateVisuals(true);
                    RequestInteractivePreviewRefresh();
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

            if (_currentClip.ClipType == ClipMode.TextClip && TryResolveTextClipViewRect(_currentClip.ExtraData, out var textRect))
            {
                // Use the measured text bounds for clip dimensions.
                // Don't accumulate textRect.X/Y into the position — those are text-content
                // offsets (near zero for origin-placed entries) and would drift on every
                // GetCurrentRect → UpdateClipRenderRect round-trip.
                w = textRect.Width;
                h = textRect.Height;
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

        private void UpdateClipRenderRect(double x, double y, double w, double h, bool updateTextStyle, bool isInRatio)
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

            if (_currentClip.ClipType == ClipMode.TextClip && updateTextStyle)
            {
                TryUpdateTextClipStyleParameters(_currentClip,
                    targetWidth: Math.Max(1, (int)Math.Round(w, MidpointRounding.AwayFromZero)),
                    targetHeight: Math.Max(1, (int)Math.Round(h, MidpointRounding.AwayFromZero)),
                    isInRatio: isInRatio);

                // Read back the text view rect so that auto-computed dimensions
                // (e.g. auto-wrapped height in FixedWidth mode, computed wrapping
                // width in FixedHeight mode) replace the raw drag deltas.
                if (TryResolveTextClipViewRect(_currentClip.ExtraData, out var textRect))
                {
                    w = Math.Clamp(textRect.Width, MinSize, _videoWidth);
                    h = Math.Clamp(textRect.Height, MinSize, _videoHeight);
                }
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

        private bool TryUpdateTextClipStyleParameters(ClipElementUI clip, int targetWidth, int targetHeight, bool isInRatio)
        {
            if (clip.ExtraData is null)
            {
                return false;
            }

            if (!TryReadExtraDataString(clip.ExtraData, TextStyleProviderFromKey, out var providerFrom)
                || !TryReadExtraDataString(clip.ExtraData, TextStyleProviderTypeKey, out var providerType))
            {
                return false;
            }

            var parameters = ReadTextStyleParameters(clip.ExtraData);
            var provider = projectFrameCut.Services.TextStyleServices.RestoreTextStyleProvider(providerFrom, providerType, parameters);
            if (provider is null)
            {
                return false;
            }

            if (parameters is not null)
            {
                provider.Parameters = new Dictionary<string, string>(parameters);
            }

            var updated = provider.HandleClipResize(
                isInRatio: isInRatio,
                TargetX: clip.TargetX,
                TargetY: clip.TargetY,
                TargetWidth: Math.Max(1, targetWidth),
                TargetHeight: Math.Max(1, targetHeight));

            if (updated is { Count: > 0 })
            {
                provider.Parameters = new Dictionary<string, string>(updated);
            }

            // Editor resize overrides any previously-set manual font size.
            provider.Parameters.Remove(BasicTextStyleProvider.ManualSizeKey);

            clip.ExtraData[TextStyleParametersKey] = new Dictionary<string, string>(provider.Parameters);
            clip.ExtraData[TextStyleProviderParametersKey] = new Dictionary<string, string>(provider.Parameters);

            var entries = provider.BuildEntries();
            if (entries.Length > 0)
            {
                clip.ExtraData["TextEntries"] = new List<TextEntry>(entries);
            }

            return true;
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

            if (clip.ClipType == ClipMode.TextClip)
            {
                if (ReadBoolExtraData(clip.ExtraData, TextClipStyleAllowFreeResizeKey, out var textAllowFree))
                    return textAllowFree;

                // Not cached in ExtraData — read directly from the text style provider.
                if (TryGetTextStyleAllowFreeResize(clip.ExtraData, out var providerAllowFree))
                    return providerAllowFree;

                return false;
            }

            if (ReadBoolExtraData(clip.ExtraData, AllowFreeScaleResizeKey, out var allowFreeScale))
            {
                return allowFreeScale;
            }

            return false;
        }

        private static bool TryGetTextStyleAllowFreeResize(
            Dictionary<string, object>? extraData, out bool allowFree)
        {
            allowFree = false;
            if (extraData is null) return false;

            if (!TryReadExtraDataString(extraData, TextStyleProviderFromKey, out var providerFrom)
                || !TryReadExtraDataString(extraData, TextStyleProviderTypeKey, out var providerType))
                return false;

            var parameters = ReadTextStyleParameters(extraData);
            var provider = Services.TextStyleServices.RestoreTextStyleProvider(
                providerFrom, providerType, parameters);
            if (provider is null) return false;

            if (parameters is not null)
                provider.Parameters = new Dictionary<string, string>(parameters);

            allowFree = provider.AllowFreeRatioResize;
            return true;
        }

        private IClip? GetClipInstance(ClipElementUI clip)
        {
            if (_getClipInstanceCallback is not null)
            {
                return _getClipInstanceCallback(clip);
            }
            throw new InvalidOperationException("Couldn't get the clip instance.");
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

        private bool TryResolveTextClipViewRect(Dictionary<string, object>? data, out Rect rect)
        {
            rect = default;
            if (data is null || _videoWidth <= 0 || _videoHeight <= 0)
            {
                return false;
            }

            if (!TryReadExtraDataString(data, TextStyleProviderFromKey, out var providerFrom)
                || !TryReadExtraDataString(data, TextStyleProviderTypeKey, out var providerType))
            {
                return false;
            }

            var parameters = ReadTextStyleParameters(data);
            var provider = projectFrameCut.Services.TextStyleServices.RestoreTextStyleProvider(providerFrom, providerType, parameters);
            if (provider is null)
            {
                return false;
            }

            if (parameters is not null)
            {
                provider.Parameters = new Dictionary<string, string>(parameters);
            }

            // The provider builds entries in *project-pixel space*, where lengths
            // are interpreted against the clip's own bounding box. Ask
            // GetViewRect using the clip's current target dimensions (the ones
            // we just wrote via HandleClipResize) so the returned rect is in the
            // same pixel space the editor uses for TargetWidth/TargetHeight.
            // Fall back to the video canvas only when the clip has no explicit
            // target dimensions yet.
            int canvasW = _currentClip?.TargetWidth > 0
                ? _currentClip.TargetWidth
                : Math.Max(1, (int)Math.Round(_videoWidth));
            int canvasH = _currentClip?.TargetHeight > 0
                ? _currentClip.TargetHeight
                : Math.Max(1, (int)Math.Round(_videoHeight));

            var rectTuple = provider.GetViewRect(canvasW, canvasH);
            if (rectTuple.TargetWidth <= 0 || rectTuple.TargetHeight <= 0)
            {
                return false;
            }

            rect = new Rect(rectTuple.TargetX, rectTuple.TargetY, rectTuple.TargetWidth, rectTuple.TargetHeight);
            return true;
        }

        private static bool TryReadExtraDataString(Dictionary<string, object>? data, string key, out string value)
        {
            value = string.Empty;
            if (data == null || !data.TryGetValue(key, out var raw) || raw is null)
            {
                return false;
            }

            if (raw is string s)
            {
                value = s;
                return !string.IsNullOrWhiteSpace(value);
            }

            if (raw is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.String)
                {
                    value = je.GetString() ?? string.Empty;
                    return !string.IsNullOrWhiteSpace(value);
                }

                value = je.ToString();
                return !string.IsNullOrWhiteSpace(value);
            }

            value = raw.ToString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        private static Dictionary<string, string>? ReadTextStyleParameters(Dictionary<string, object>? data)
        {
            if (TryReadStringDictionary(data, TextStyleParametersKey, out var parameters))
            {
                return parameters;
            }

            if (TryReadStringDictionary(data, TextStyleProviderParametersKey, out var providerParameters))
            {
                return providerParameters;
            }

            return null;
        }

        private static bool TryReadStringDictionary(Dictionary<string, object>? data, string key, out Dictionary<string, string> values)
        {
            values = null!;
            if (data == null || !data.TryGetValue(key, out var raw) || raw is null)
            {
                return false;
            }

            if (raw is Dictionary<string, string> stringDict)
            {
                values = new Dictionary<string, string>(stringDict);
                return true;
            }

            if (raw is Dictionary<string, object> objDict)
            {
                values = new Dictionary<string, string>(objDict.Count, StringComparer.Ordinal);
                foreach (var kvp in objDict)
                {
                    values[kvp.Key] = kvp.Value?.ToString() ?? string.Empty;
                }
                return true;
            }

            if (raw is JsonElement je)
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(je);
                    if (parsed is { Count: > 0 })
                    {
                        values = parsed;
                        return true;
                    }
                }
                catch
                {
                    return false;
                }
            }

            if (raw is string json && !string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (parsed is { Count: > 0 })
                    {
                        values = parsed;
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

            Dispatcher.Dispatch(() =>
            {
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
            });
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