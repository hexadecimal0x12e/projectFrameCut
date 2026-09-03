using projectFrameCut.ApplicationAPIBase.Interaction;
using projectFrameCut.ApplicationPluginBase.Text;
using projectFrameCut.Asset;
using projectFrameCut.Controls;
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
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace projectFrameCut.InteractableEditor
{
    public partial class InteractableEditor : ContentView, IInteractableEditor
    {
        #region types

        public enum ResizeHandle
        {
            TopLeft,
            TopRight,
            BottomLeft,
            BottomRight,
            ClipPan
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

        private readonly Dictionary<Guid, ClipOverlayState> _clipStates = new();
        private readonly object _clipStatesLock = new();
        private readonly Dictionary<Guid, object> _previewSourceClips = new();
        private readonly Dictionary<Guid, IInteractableElement> _genericElements = new();
        private readonly Dictionary<Guid, InteractiveRect> _genericLastRects = new();
        private ClipOverlayState? _activeState;
        private Func<Task>? _previewRefreshCallback;
        private Func<Guid, Task>? _overlayClipTappedCallback;
        private Func<Guid, Task>? _overlayClipDoubleTappedCallback;
        private Func<Task>? _blankAreaTappedCallback;
        private InteractiveElementChangedHandler? _interactiveElementChangedCallback;
        private Func<Task>? _referenceLinesChangedCallback;
        private Action<string, uint, ClipPositionTuple, ResizeHandle>? _keyframeCandidateCapturedCallback;
        private Func<ClipElementUI, IClip?>? _getClipInstanceCallback;
        private bool _suppressReferenceLinesChangedNotify;
        private readonly Dictionary<string, ReferenceLine> _referenceLines = new(StringComparer.Ordinal);
        private readonly Dictionary<string, BoxView> _referenceLineVisuals = new(StringComparer.Ordinal);
        private Action? _manageReferenceLinesRequestedCallback;
        private Action<Color>? _defaultColorPickerRequestedCallback;
        private Func<string, Task>? _previewResolutionChangedCallback;
        private List<string> _previewResolutionOptions = [];
        private bool _suppressPreviewResolutionChanged;
        private long _lastPreviewRefreshTick;
        private int _isPreviewRefreshRunning;
        private int _hasPendingPreviewRefresh;
        private int _isCommitUpdateRunning;
        private int _hasPendingCommitUpdate;
        private int _isUpdatingVisuals;
        private long _lastOverlayTapTick;

        private ShapeHandleProvider? _shapeHandleProvider;
        private ShapeHandleDragHandler? _shapeHandleDragHandler;
        private bool _isShapeHandleDragInProgress;

        private CancellationTokenSource? _commitUpdateDebounceCts;
        private readonly object _commitUpdateDebounceLock = new();
        private bool _autoHideBottomControls;
        private bool _isPointerOverBottomControls;
        private bool _autoHideInfoIndicator;
        private bool _isPointerOverInfoIndicator;
        private string? _infoIndicatorMessage;
        private long _infoIndicatorRevealUntilTick;
        private CancellationTokenSource? _hideBottomControlsCts;
        private CancellationTokenSource? _hideInfoIndicatorCts;

        private const int PreviewRefreshThrottleMs = 180;
        private const int CommitUpdateDebounceMs = 220;
        private const int OverlayTapBlankSuppressMs = 180;
        private const int InfoIndicatorRevealMs = 5_000;
        private const double InfoIndicatorIdleOpacity = 0.45d;

        #endregion

        #region properties

        public ConcurrentDictionary<Guid, ClipElementUI> Clips { get; private set; } = new();

        // 用于直接显示DraftPage中的所有clips
        private IReadOnlyDictionary<Guid, ClipElementUI>? _allClips;
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
                DebugOverlay.IsVisible = value;
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
        public bool ShowAllBorders
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
        } = false;

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

        public Brush EditorCanvasBackground
        {
            get;
            set
            {
                field = value;
                OnPropertyChanged(nameof(EditorCanvasBackground));
            }
        } = Colors.Black;

        public bool ShowDetailReferenceLineControl { get; set; } = false;

        public bool ShowBottomControls
        {
            get => BottomControlsHost.IsVisible;
            set => BottomControlsHost.IsVisible = value;
        }

        public IReadOnlyList<string> PreviewResolutionOptions => _previewResolutionOptions;

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
            public Guid ClipId { get; init; }

            // Dynamic custom shape handles
            private readonly List<(View View, PanGestureRecognizer Pan, string HandleId, double StartX, double StartY)> _customHandles = new();

            public ClipOverlayState(InteractableEditor owner, Guid clipId, string? displayName = null)
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

            // Preview frames are streamed much faster than the surrounding layout
            // needs to change. Keep two fixed Image instances so a new source is
            // assigned only to the inactive buffer; the image currently being
            // presented is never mutated in place.
            private Grid? _imageBufferHost;
            private Image? _imageBufferA;
            private Image? _imageBufferB;
            private Image? _activeImageBuffer;

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
                // The editor's clip collection can be replaced while a draft is loading or a
                // history snapshot is being applied. Prefer the current selection as a fallback
                // so a transient collection mismatch cannot crash overlay layout.
                _owner.Clips.TryGetValue(ClipId, out var clip);
                if (clip is null && _owner._currentClip?.Id == ClipId)
                {
                    clip = _owner._currentClip;
                }

                Root.IsVisible = true;
                AbsoluteLayout.SetLayoutBounds(Root, new Rect(displayX, displayY, displayW, displayH));
                AbsoluteLayout.SetLayoutBounds(ClipVisual, new Rect(0, 0, displayW, displayH));
                ClipVisual.IsVisible = _owner.ShowAllBorders || showClipVisual;
                if (ClipVisual.IsVisible)
                {
                    ClipVisual.Stroke = (clip?.ShowDefaultBorder != false || _owner.ShowAllBorders)
                        ? (clipStroke ?? Colors.Yellow)
                        : Colors.Transparent;
                }
                UpdatePreviewHostLayout(displayW, displayH, logicalW, logicalH);
                UpdateRootInputTransparency();

                double handleSize = HandleSize;
                AbsoluteLayout.SetLayoutBounds(HandleTL, new Rect(-handleSize / 2, -handleSize / 2, handleSize, handleSize));
                AbsoluteLayout.SetLayoutBounds(HandleTR, new Rect(displayW - handleSize / 2, -handleSize / 2, handleSize, handleSize));
                AbsoluteLayout.SetLayoutBounds(HandleBL, new Rect(-handleSize / 2, displayH - handleSize / 2, handleSize, handleSize));
                AbsoluteLayout.SetLayoutBounds(HandleBR, new Rect(displayW - handleSize / 2, displayH - handleSize / 2, handleSize, handleSize));

                bool resizeHandleVisible = showHandles
                    && clip is not null
                    && (clip.IsHorizontalResizable || clip.IsVerticalResizable);
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
                ClearCustomHandles();
            }

            public void UpdateCustomHandles(
                IReadOnlyList<ShapeHandleDescriptor> descriptors,
                double displayW,
                double displayH,
                Action<string, PanUpdatedEventArgs>? dragCallback)
            {
                int targetCount = descriptors?.Count ?? 0;

                // Remove excess handles
                while (_customHandles.Count > targetCount)
                {
                    var last = _customHandles[^1];
                    Root.Children.Remove(last.View);
                    _customHandles.RemoveAt(_customHandles.Count - 1);
                }

                // Update or create handles
                for (int i = 0; i < targetCount; i++)
                {
                    var desc = descriptors![i];
                    double size = desc.Size > 0 ? desc.Size : 12;
                    double hx = desc.NormalizedX * displayW - size / 2;
                    double hy = desc.NormalizedY * displayH - size / 2;

                    if (i < _customHandles.Count)
                    {
                        var existing = _customHandles[i];
                        AbsoluteLayout.SetLayoutBounds(existing.View, new Rect(hx, hy, size, size));
                        if (desc.HandleGetter is null && existing.View is BoxView boxView)
                        {
                            boxView.Color = desc.FillColor;
                        }
                        existing.View.IsVisible = true;
                        _customHandles[i] = (existing.View, existing.Pan, desc.Id, hx, hy);
                    }
                    else
                    {
                        var handle = desc.HandleGetter?.Invoke() ?? new BoxView
                        {
                            WidthRequest = size,
                            HeightRequest = size,
                            CornerRadius = size / 2,
                            Color = desc.FillColor,
                            InputTransparent = false,
                            ZIndex = int.MaxValue
                        };
                        var pan = new PanGestureRecognizer();
                        var handleId = desc.Id;
                        // Capture the initial position for visual drag tracking
                        double startHx = hx;
                        double startHy = hy;
                        pan.PanUpdated += (_, e) =>
                        {
                            // Visual tracking: move the handle with the finger
                            switch (e.StatusType)
                            {
                                case GestureStatus.Started:
                                    startHx = handle.TranslationX;
                                    startHy = handle.TranslationY;
                                    break;
                                case GestureStatus.Running:
                                    handle.TranslationX = startHx + e.TotalX;
                                    handle.TranslationY = startHy + e.TotalY;
                                    break;
                                case GestureStatus.Completed:
                                case GestureStatus.Canceled:
                                    handle.TranslationX = 0;
                                    handle.TranslationY = 0;
                                    break;
                            }
                            dragCallback?.Invoke(handleId, e);
                        };
                        handle.GestureRecognizers.Add(pan);
                        AbsoluteLayout.SetLayoutBounds(handle, new Rect(hx, hy, size, size));
                        Root.Children.Add(handle);
                        _customHandles.Add((handle, pan, desc.Id, hx, hy));
                    }
                }
            }

            public void ClearCustomHandles()
            {
                foreach (var (view, _, _, _, _) in _customHandles)
                    Root.Children.Remove(view);
                _customHandles.Clear();
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

                if (view is null)
                {
                    ResetImageBuffers();
                }

                if (view is Image incomingImage && TrySetBufferedImage(incomingImage))
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
                    if (!_owner.Dispatcher.IsDispatchRequired)
                    {
                        PreviewHost.Content = view;
                    }
                    else
                    {
                        _ = MainThread.InvokeOnMainThreadAsync(() =>
                        {
                            PreviewHost.Content = view;
                        });
                    }
                }

                UpdatePreviewHostVisibility();
            }

            private void ResetImageBuffers()
            {
                _imageBufferHost = null;
                _imageBufferA = null;
                _imageBufferB = null;
                _activeImageBuffer = null;
            }

            private bool TrySetBufferedImage(Image incoming)
            {
                if (incoming.Source is null)
                {
                    return false;
                }

                EnsureImageBuffers();
                if (_imageBufferA is null || _imageBufferB is null || _activeImageBuffer is null)
                {
                    return false;
                }

                var inactive = ReferenceEquals(_activeImageBuffer, _imageBufferA)
                    ? _imageBufferB
                    : _imageBufferA;

                // Copy only non-source presentation state. Source is assigned last,
                // while the target buffer is hidden and therefore does not participate
                // in the currently visible layout pass.
                ApplySharedViewState(inactive, incoming);
                inactive.Aspect = incoming.Aspect;
                inactive.IsVisible = false;
                inactive.Source = incoming.Source;

                var previous = _activeImageBuffer;
                inactive.IsVisible = true;
                previous.IsVisible = false;
                _activeImageBuffer = inactive;
                return true;
            }

            private void EnsureImageBuffers()
            {
                if (_imageBufferHost is not null
                    && _imageBufferA is not null
                    && _imageBufferB is not null
                    && _activeImageBuffer is not null)
                {
                    return;
                }

                var existingImage = PreviewHost.Content as Image;
                _imageBufferA = CreateImageBuffer();
                _imageBufferB = CreateImageBuffer();

                if (existingImage is not null)
                {
                    // Do not re-parent the currently attached Image into the new
                    // Grid. Copy its state to an unattached buffer instead.
                    ApplySharedViewState(_imageBufferA, existingImage);
                    _imageBufferA.Aspect = existingImage.Aspect;
                    _imageBufferA.Source = existingImage.Source;
                }

                _imageBufferA.IsVisible = true;
                _imageBufferB.IsVisible = false;

                _imageBufferHost = new Grid
                {
                    BackgroundColor = Colors.Transparent,
                    InputTransparent = true,
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Fill
                };
                _imageBufferHost.Children.Add(_imageBufferA);
                _imageBufferHost.Children.Add(_imageBufferB);
                _activeImageBuffer = _imageBufferA;

                // This method is called from the UI-thread apply path. Replacing the
                // host once is safe; subsequent frames only touch the two buffers.
                PreviewHost.Content = _imageBufferHost;
            }

            private static Image CreateImageBuffer()
            {
                return new Image
                {
                    Aspect = Aspect.Fill,
                    InputTransparent = true,
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Fill,
                    IsVisible = false
                };
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

        public void SelectClip(Guid? clipId)
        {
            CancelPendingCommitUpdate();
            _isClipPanInProgress = false;
            _isHandleResizeInProgress = false;
            _panPreviewRect = null;

            if (!clipId.HasValue || !Clips.TryGetValue(clipId.Value, out var clip))
            {
                _currentClip = null;
                SetActiveState(null);
                UpdateVisuals();
                return;
            }

            _currentClip = clip;
            SetActiveState(GetOrCreateClipState(clip));
            UpdateVisuals();
        }

        #endregion

        #region init/config
        public InteractableEditor()
        {
            BindingContext = this;
            InitializeComponent();
            ThicknessEntry.Text = DefaultReferenceLineThickness.ToString("F1");

            var canvasTap = new TapGestureRecognizer();
            canvasTap.Tapped += OnEditorCanvasTapped;
            EditorCanvas.GestureRecognizers.Add(canvasTap);

            var hoverPointer = new PointerGestureRecognizer();
            hoverPointer.PointerEntered += OnBottomControlsHostEntered;
            hoverPointer.PointerExited += OnBottomControlsHostExited;
            BottomControlsHost.GestureRecognizers.Add(hoverPointer);

            var infoHoverPointer = new PointerGestureRecognizer();
            infoHoverPointer.PointerEntered += OnInfoIndicatorEntered;
            infoHoverPointer.PointerExited += OnInfoIndicatorExited;
            InfoIndicatorHost.GestureRecognizers.Add(infoHoverPointer);

        }


        public void Init(Func<Task> updateCallback, double videoWidth, double videoHeight)
        {
            _updateCallback = updateCallback;
            _videoWidth = videoWidth;
            _videoHeight = videoHeight;
        }

        public InteractableEditor ConfigurePreviewRefresh(Func<Task>? refreshCallback)
        {
            _previewRefreshCallback = refreshCallback;
            return this;
        }

        public InteractableEditor ConfigureInfoIndicator(bool isVisible, string? message)
        {
            void ApplyConfiguration()
            {
                _infoIndicatorMessage = message;
                InfoDetailsLabel.Text = message ?? string.Empty;
                InfoIndicatorHost.IsVisible = isVisible;
                InfoIndicatorHost.Opacity = string.IsNullOrWhiteSpace(message)
                    ? InfoIndicatorIdleOpacity
                    : 1d;
                SemanticProperties.SetDescription(InfoIndicatorHost,
                    string.IsNullOrWhiteSpace(message) ? "Information" : message);

                if (!isVisible)
                {
                    InfoDetailsOverlay.IsVisible = false;
                    _isPointerOverInfoIndicator = false;
                    _infoIndicatorRevealUntilTick = 0;
                    CancelHideInfoIndicatorDebounce();
                }
                else
                {
                    _infoIndicatorRevealUntilTick = Environment.TickCount64 + InfoIndicatorRevealMs;
                }

                Dispatcher.Dispatch(() => UpdateBottomControlsVisibility(GetRenderRect()));
            }

            if (Dispatcher.IsDispatchRequired)
            {
                Dispatcher.Dispatch(ApplyConfiguration);
            }
            else
            {
                ApplyConfiguration();
            }

            return this;
        }

        public InteractableEditor ConfigurePreviewResolution(
            IEnumerable<string> options,
            string? selectedOption,
            Func<string, Task>? changedCallback)
        {
            _previewResolutionOptions = options?.ToList() ?? [];
            _previewResolutionChangedCallback = changedCallback;

            _suppressPreviewResolutionChanged = true;
            try
            {
                PreviewResolutionPicker.ItemsSource = _previewResolutionOptions;
                PreviewResolutionPicker.SelectedItem = selectedOption;
            }
            finally
            {
                _suppressPreviewResolutionChanged = false;
            }

            return this;
        }

        public void SelectPreviewResolution(string option)
        {
            if (_previewResolutionOptions.Contains(option, StringComparer.Ordinal))
            {
                PreviewResolutionPicker.SelectedItem = option;
            }
        }

        public InteractableEditor ConfigureOverlayClipTap(Func<Guid, Task>? tapCallback)
        {
            _overlayClipTappedCallback = tapCallback;
            foreach (var state in _clipStates.Values)
            {
                state.RefreshPreviewVisibility();
            }
            UpdateVisuals();
            return this;
        }

        public InteractableEditor ConfigureOverlayClipDoubleTap(Func<Guid, Task>? doubleTapCallback)
        {
            _overlayClipDoubleTappedCallback = doubleTapCallback;
            return this;
        }

        public InteractableEditor ConfigureBlankAreaTap(Func<Task>? tapCallback)
        {
            _blankAreaTappedCallback = tapCallback;
            return this;
        }

        public InteractableEditor ConfigureReferenceLinesChanged(Func<Task>? callback)
        {
            _referenceLinesChangedCallback = callback;
            return this;
        }

        public InteractableEditor ConfigureKeyframeCandidateCaptured(Action<string, uint, ClipPositionTuple, ResizeHandle>? callback)
        {
            _keyframeCandidateCapturedCallback = callback;
            return this;
        }

        public InteractableEditor ConfigureManageReferenceLinesRequested(Action? callback)
        {
            _manageReferenceLinesRequestedCallback = callback;
            return this;
        }

        public InteractableEditor ConfigureDefaultColorPickerRequested(Action<Color>? callback)
        {
            _defaultColorPickerRequestedCallback = callback;
            return this;
        }

        public InteractableEditor ConfigureGetClipInstanceCallback(Func<ClipElementUI, IClip?>? callback)
        {
            _getClipInstanceCallback = callback;
            return this;
        }

        public InteractableEditor ConfigureCustomHandles(ShapeHandleProvider? provider, ShapeHandleDragHandler? dragHandler)
        {
            _shapeHandleProvider = provider;
            _shapeHandleDragHandler = dragHandler;
            return this;
        }

        // IInteractableEditor compatibility surface. The legacy ClipElementUI path remains the
        // optimized timeline implementation; hosts using the common contract can progressively
        // move to an adapter without taking a dependency on timeline types here.
        void IInteractableEditor.SetInteractiveElements(IReadOnlyCollection<IInteractableElement> elements)
        {
            var clips = new ConcurrentDictionary<Guid, ClipElementUI>();
            _genericElements.Clear();
            _genericLastRects.Clear();
            foreach (var element in elements)
            {
                _genericElements[element.Id] = element;
                var rect = element.LogicalRect;
                _genericLastRects[element.Id] = rect;
                clips[element.Id] = new ClipElementUI
                {
                    Id = element.Id,
                    DisplayName = element.DisplayName,
                    ShouldDisplayInUI = element.IsVisible,
                    TargetX = (int)Math.Round(rect.X),
                    TargetY = (int)Math.Round(rect.Y),
                    TargetWidth = (int)Math.Round(rect.Width),
                    TargetHeight = (int)Math.Round(rect.Height),
                    origTrack = element.Layer,
                    origLength = Math.Max(rect.Width, 1_000_000_000d),
                    Clip = new Border(),
                    LeftHandle = new Border(),
                    RightHandle = new Border(),
                    IsMoveable = element.Capabilities.CanMove,
                    IsHorizontalResizable = element.Capabilities.CanResizeHorizontally,
                    IsVerticalResizable = element.Capabilities.CanResizeVertically,
                    AllowFreeScaleResize = element.Capabilities.AllowFreeScale,
                    CanSnapWhilePlacing = element.Capabilities.CanSnapWhileMoving,
                    CanSnapWhileResizing = element.Capabilities.CanSnapWhileResizing,
                };
            }
            _ = UpdateClips(clips);
        }

        void IInteractableEditor.SetSelectedElement(Guid? elementId) => SelectClip(elementId);
        void IInteractableEditor.SetCanvasSize(double width, double height) => UpdateCanvasSize(width, height);
        void IInteractableEditor.SetVideoSize(double width, double height) => UpdateVideoResolution(width, height);
        void IInteractableEditor.AddReferenceLine(projectFrameCut.ApplicationAPIBase.Interaction.ReferenceLineOrientation? orientation)
            => AddAReferenceLine(orientation is projectFrameCut.ApplicationAPIBase.Interaction.ReferenceLineOrientation.Horizontal
                ? ReferenceLineOrientation.Horizontal
                : ReferenceLineOrientation.Vertical);
        void IInteractableEditor.RemoveReferenceLine(string id) => RemoveReferenceLine(id);
        void IInteractableEditor.ClearReferenceLines() => ClearReferenceLines();
        string IInteractableEditor.SerializeReferenceLines() => GetReferenceLinesJson();
        void IInteractableEditor.RestoreReferenceLines(string? json) => RestoreReferenceLinesFromJson(json);
        IInteractableEditor IInteractableEditor.ConfigurePreviewRefresh(Func<Task>? callback)
            => ConfigurePreviewRefresh(callback);
        IInteractableEditor IInteractableEditor.ConfigureInfoIndicator(bool isVisible, string? message)
            => ConfigureInfoIndicator(isVisible, message);
        IInteractableEditor IInteractableEditor.ConfigureElementClicked(InteractiveElementClickedHandler? callback)
            => ConfigureOverlayClipTap(callback is null ? null : id => callback(id));
        IInteractableEditor IInteractableEditor.ConfigureBlankAreaClicked(Func<Task>? callback)
            => ConfigureBlankAreaTap(callback);
        IInteractableEditor IInteractableEditor.ConfigureElementChanged(InteractiveElementChangedHandler? callback)
        {
            _interactiveElementChangedCallback = callback;
            return this;
        }
        IInteractableEditor IInteractableEditor.ConfigureCustomHandles(CustomHandleProvider? provider, CustomHandleDragHandler? dragHandler)
        {
            if (provider is null && dragHandler is null)
            {
                return ConfigureCustomHandles(null, null);
            }

            ShapeHandleProvider? legacyProvider = provider is null
                ? null
                : id => provider(id).Select(h => new projectFrameCut.InteractableEditor.ShapeHandleDescriptor(
                    h.Id, h.NormalizedX, h.NormalizedY, h.FillColor, h.Size, h.ViewFactory)).ToList();
            ShapeHandleDragHandler? legacyDragHandler = dragHandler is null
                ? null
                : (id, handleId, args, context) => dragHandler(
                    id,
                    handleId,
                    args,
                    new CustomHandleDragContext(id, handleId, context.DisplayW, context.DisplayH, context.LogicalW, context.LogicalH));
            return ConfigureCustomHandles(legacyProvider, legacyDragHandler);
        }

        protected override void OnSizeAllocated(double width, double height)
        {
            base.OnSizeAllocated(width, height);
            UpdateCanvasSize(width, height, true);
        }

        public void UpdateCanvasSize(double width, double height, bool ignorePositionProvider = false)
        {
            // Round to integer pixels to match the canvas dimensions DynamicPreview
            // uses when it calls providers. If _canvasWidth/_canvasHeight keep
            // sub-pixel values while DynamicPreview rounds away, the two scales
            // drift by a fraction of a pixel and the preview ends up slightly
            // offset from the selection rectangle.
            _canvasWidth = Math.Max(1d, Math.Round(width, MidpointRounding.AwayFromZero));
            _canvasHeight = Math.Max(1d, Math.Round(height, MidpointRounding.AwayFromZero));

            // ClipStatesHost and ReferenceLinesHost are AbsoluteLayout children of
            // EditorCanvas (also an AbsoluteLayout). They are declared in XAML with
            // LayoutFlags="None" but no LayoutBounds, so they default to AutoSize
            // and are measured to the union of their children — which is the raw
            // 1920x1080 project space. That bubbles up through EditorCanvas's own
            // MeasureOverride, causing the overlay to be laid out as if the canvas
            // were 1920x1080 instead of the visible editor area. Pin them to the
            // actual rendered canvas so EditorCanvas's measure reflects the real
            // preview rect.
            AbsoluteLayout.SetLayoutBounds(ClipStatesHost, new Rect(0, 0, _canvasWidth, _canvasHeight));
            AbsoluteLayout.SetLayoutBounds(ReferenceLinesHost, new Rect(0, 0, _canvasWidth, _canvasHeight));
            // Keep DebugOverlay pinned to the top-left at a fixed size so its AutoSize
            // can't drag EditorCanvas's measure away from the real canvas size.
            AbsoluteLayout.SetLayoutBounds(DebugOverlay, new Rect(8, 8, 380, 240));

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

        private ClipOverlayState GetOrCreateClipState(Guid clipId, string? displayName = null)
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
                        try
                        {
                            ClipStatesHost.Children.Add(state.Root);
                        }
                        catch (Exception ex) when (ex is NullReferenceException or InvalidComObjectException)
                        {
                            Dispatcher.Dispatch(() =>
                            {
                                ClipStatesHost.Children.Add(state.Root);
                            });
                        }
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
                && state.ClipId != Guid.Empty;
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

            SelectClip(state.ClipId);

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

        private async Task InvokeOverlayClipTappedAsync(Func<Guid, Task> callback, Guid clipId)
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

        private async Task InvokeOverlayClipDoubleTappedAsync(Func<Guid, Task> callback, Guid clipId)
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

        private void NotifyKeyframeCandidateCaptured(double x, double y, double w, double h, ResizeHandle handle)
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

            callback(_currentClip.Id.ToString(), _currentFrame, keyframePosition, handle);
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
                state.ClearCustomHandles();
                state.Hide();
            }

            ClipStatesHost.Children.Clear();
            _clipStates.Clear();
            _previewSourceClips.Clear();
            _activeState = null;
        }

        private void OnCustomHandlePanUpdated(Guid clipId, string handleId, PanUpdatedEventArgs e)
        {
            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    _isShapeHandleDragInProgress = true;
                    break;
                case GestureStatus.Completed:
                case GestureStatus.Canceled:
                    _isShapeHandleDragInProgress = false;
                    break;
            }

            if (_shapeHandleDragHandler is null) return;

            double logicalW = _videoWidth;
            double logicalH = _videoHeight;
            if (_currentClip is not null && _currentClip.Id == clipId)
            {
                logicalW = _currentClip.TargetWidth > 0 ? _currentClip.TargetWidth : _videoWidth;
                logicalH = _currentClip.TargetHeight > 0 ? _currentClip.TargetHeight : _videoHeight;
            }
            else if (Clips.TryGetValue(clipId, out var clip))
            {
                logicalW = clip.TargetWidth > 0 ? clip.TargetWidth : _videoWidth;
                logicalH = clip.TargetHeight > 0 ? clip.TargetHeight : _videoHeight;
            }

            var state = GetOrCreateClipState(clipId);
            var rootBounds = AbsoluteLayout.GetLayoutBounds(state.Root);

            var context = new ShapeHandleDragContext
            {
                ClipId = clipId,
                HandleId = handleId,
                DisplayW = rootBounds.Width,
                DisplayH = rootBounds.Height,
                LogicalW = logicalW,
                LogicalH = logicalH,
            };

            _shapeHandleDragHandler(clipId, handleId, e, context);

            if (e.StatusType == GestureStatus.Completed)
                RequestInteractivePreviewRefresh();
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
            IReadOnlyDictionary<Guid, ClipElementUI> allClips,
            double framePerPixel,
            double tracksZoomOffset = 1d,
            float secondPerFrameRatio = 1f)
        {
            Clips = allClips as ConcurrentDictionary<Guid, ClipElementUI>
                ?? new ConcurrentDictionary<Guid, ClipElementUI>(allClips);
            _allClips = allClips;
            _framePerPixel = framePerPixel;
            _tracksZoomOffset = tracksZoomOffset;
            _secondPerFrameRatio = secondPerFrameRatio;
            UpdateVisuals();
        }

        public Task UpdateClips(ConcurrentDictionary<Guid, ClipElementUI> clips)
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

            if (Interlocked.CompareExchange(ref _isUpdatingVisuals, 1, 0) != 0)
            {
                LogDiagnostic("[UpdateVisuals] Skipped — reentrant call detected");
                return;
            }

            try
            {
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

                    // 在同一原子步骤中更新 ZIndex 和布局边界，避免分离的依赖属性修改
                    // 触发中间的布局过程导致原生层崩溃。
                    UpdateClipStateZIndex(state, clipId);
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

                    // Update custom shape handles (only for the selected clip)
                    if (isCurrentClip && _shapeHandleProvider is not null)
                    {
                        var handles = _shapeHandleProvider(clipId);
                        state.UpdateCustomHandles(handles, displayW, displayH,
                            (handleId, e) => OnCustomHandlePanUpdated(clipId, handleId, e));
                    }
                    else
                    {
                        state.ClearCustomHandles();
                    }

                    //LogDiagnostic($"[UpdateVisuals] clip {clipId} debug overlay updated in {sw.ElapsedMilliseconds}ms");
                    //LogDiagnostic($"[UpdateVisuals] clip {clipId} total update time {sw.ElapsedMilliseconds}ms");
                }

                ReorderClipStateRootsByZIndex();

                RefreshDebugOverlay(renderRect, scale);
            }
            finally
            {
                Interlocked.Exchange(ref _isUpdatingVisuals, 0);
            }
        }

        private void RefreshDebugOverlay(Rect renderRect, double scale)
        {
            try
            {
                if (DebugInfoLabel is null) return;

                var editorW = Math.Round(Width);
                var editorH = Math.Round(Height);
                var canvasW = Math.Round(EditorCanvas.Width);
                var canvasH = Math.Round(EditorCanvas.Height);
                var statesW = Math.Round(ClipStatesHost.Width);
                var statesH = Math.Round(ClipStatesHost.Height);
                var refsW = Math.Round(ReferenceLinesHost.Width);
                var refsH = Math.Round(ReferenceLinesHost.Height);
                var renderVW = Math.Round(PreviewOverlayImage.Width);
                var renderVH = Math.Round(PreviewOverlayImage.Height);
                var renderVVisible = PreviewOverlayImage.IsVisible;
                var renderVAspect = PreviewOverlayImage.Aspect.ToString();

                // Identify which element is actually painting the visible video.
                var liveHostW = Math.Round(LivePreviewerHost.Width);
                var liveHostH = Math.Round(LivePreviewerHost.Height);
                var liveHostVisible = LivePreviewerHost.IsVisible;
                var liveContent = LivePreviewerHost.Content;
                var liveContentType = liveContent?.GetType().Name ?? "(null)";
                var liveContentW = liveContent is VisualElement v1 ? Math.Round(v1.Width) : -1;
                var liveContentH = liveContent is VisualElement v2 ? Math.Round(v2.Height) : -1;
                string liveContentAspect = "?";
                if (liveContent is Image img) liveContentAspect = img.Aspect.ToString();

                var ratioCanvas = _canvasHeight > 0 ? _canvasWidth / _canvasHeight : 0;
                var ratioVideo = _videoHeight > 0 ? _videoWidth / _videoHeight : 0;

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"self    : {editorW} x {editorH}");
                sb.AppendLine($"EditorCanvas       : {canvasW} x {canvasH}");
                sb.AppendLine($"ClipStatesHost     : {statesW} x {statesH}");
                sb.AppendLine($"ReferenceLinesHost : {refsW} x {refsH}");
                sb.AppendLine($"PreviewImg         : vis={renderVVisible} {renderVW} x {renderVH} asp={renderVAspect}");
                sb.AppendLine($"LiveHost           : vis={liveHostVisible} {liveHostW} x {liveHostH}");
                sb.AppendLine($"  content          : {liveContentType} {liveContentW} x {liveContentH} asp={liveContentAspect}");
                sb.AppendLine($"_canvas (logic)    : {Math.Round(_canvasWidth)} x {Math.Round(_canvasHeight)}");
                sb.AppendLine($"_video             : {Math.Round(_videoWidth)} x {Math.Round(_videoHeight)}");
                sb.AppendLine($"ratio c/v          : {ratioCanvas:F3} / {ratioVideo:F3}");
                sb.AppendLine($"renderRect         : X={Math.Round(renderRect.X)} Y={Math.Round(renderRect.Y)} W={Math.Round(renderRect.Width)} H={Math.Round(renderRect.Height)}");
                sb.AppendLine($"scale              : {scale:F4}");

                if (_currentClip is not null)
                {
                    var c = _currentClip;
                    var tx = c.TargetX;
                    var ty = c.TargetY;
                    var tw = c.TargetWidth > 0 ? c.TargetWidth : _videoWidth;
                    var th = c.TargetHeight > 0 ? c.TargetHeight : _videoHeight;
                    var dx = renderRect.X + tx * scale;
                    var dy = renderRect.Y + ty * scale;
                    var dw = tw * scale;
                    var dh = th * scale;
                    sb.AppendLine($"clip T             : {Math.Round((double)tx)} {Math.Round((double)ty)} {Math.Round(tw)} x {Math.Round(th)}");
                    sb.AppendLine($"clip D             : X={Math.Round(dx)} Y={Math.Round(dy)} {Math.Round(dw)} x {Math.Round(dh)}");
                }

                DebugInfoLabel.Text = sb.ToString();
            }
            catch
            {
                // Best-effort debug overlay, never throw from here.
            }
        }

        private void UpdatePreviewDebugOverlay(ClipOverlayState state, Guid clipId, double logicalW, double logicalH, double displayW, double displayH)
        {
            if (state.DebugLabel.IsVisible == ShowPreviewDebugOverlay) return;
            if (!ShowPreviewDebugOverlay)
            {
                state.UpdateDebugInfo(false, null, displayW, displayH);
                return;
            }

            var content = state.PreviewHost.Content;
            var contentType = content?.GetType().Name ?? "null";
            var nativeContentType = content?.Handler?.PlatformView?.GetType().Name ?? "null";
            var contentDebugTag = content?.AutomationId;
            var shortClipId = clipId.ToString().Length > 8 ? clipId.ToString()[..8] : clipId.ToString();
            var info =
                clipId.ToString()
                + Environment.NewLine
                + $"view:{state.HasPreviewView}/{state.PreviewHost.IsVisible} show:{ShouldShowPreviewHost(state)} sup:{ShouldSuppressPreviewForResize(clipId)}"
                + Environment.NewLine
                + $"L:{Math.Round(logicalW)}x{Math.Round(logicalH)} D:{Math.Round(displayW)}x{Math.Round(displayH)}"
                + Environment.NewLine
                + $"T:{contentType} N:{nativeContentType}";

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
        public async Task<bool> ApplyPreparedPreviewsAsync(IReadOnlyList<PreparedPreview> preparedPreviews)
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
        public bool ApplyPreparedPreviews(IReadOnlyList<PreparedPreview> preparedPreviews)
        {
            var canvasPreview = preparedPreviews.FirstOrDefault(static preview => preview.IsCanvasPreview);
            if (canvasPreview is not null)
            {
                foreach (var state in _clipStates.Values)
                {
                    state.SetPreviewView(null);
                    state.RefreshPreviewVisibility();
                }
                _previewSourceClips.Clear();

                var canvasView = canvasPreview.View;
                if (LivePreviewerHost.Content is HdrPreviewView existingHdr && canvasView is HdrPreviewView incomingHdr)
                {
                    existingHdr.Frame = incomingHdr.Frame;
                    canvasView = existingHdr;
                }
                else if (LivePreviewerHost.Content is Image existingImage && canvasView is Image incomingImage)
                {
                    existingImage.Source = incomingImage.Source;
                    existingImage.Aspect = incomingImage.Aspect;
                    canvasView = existingImage;
                }
                SetRealtimePreviewContent(canvasView);
                LivePreviewerHost.IsVisible = canvasView is not null;
                PreviewOverlayImage.IsVisible = false;
                return canvasView is not null;
            }

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

                    foreach (var state in _clipStates.Values)
                    {
                        state.RefreshPreviewVisibility();
                    }
                    return hasPreviewView;
                }
                else
                {
                    foreach (var state in _clipStates.Values)
                    {
                        state.SetPreviewView(null);
                    }

                    _previewSourceClips.Clear();

                    foreach (var state in _clipStates.Values)
                    {
                        state.RefreshPreviewVisibility();
                    }
                    return false;
                }
            }

            var knownStates = new HashSet<Guid>();
            var hasVisiblePreview = false;

            foreach (var prepared in preparedPreviews)
            {
                if (prepared.ClipId == Guid.Empty)
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

            // Applying a new frame must not invalidate every overlay layout. Layout
            // is updated by the geometry/selection paths; the streaming path only
            // refreshes visibility after changing the buffered image.
            foreach (var state in _clipStates.Values)
            {
                state.RefreshPreviewVisibility();
            }
            return hasVisiblePreview;
        }

        void IInteractableEditor.ApplyPreparedPreviews(IReadOnlyList<PreparedPreview> previews)
            => ApplyPreparedPreviews(previews);

        private bool TryResolveClipRect(Guid clipId, bool ignorePosotionProvider, out double x, out double y, out double w, out double h, out ClipMode clipType, out bool isCurrentClip)
        {
            x = 0;
            y = 0;
            w = _videoWidth;
            h = _videoHeight;
            clipType = ClipMode.AudioClip;
            isCurrentClip = _currentClip is not null && _currentClip.Id == clipId;

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
                    ComputeFittedRectFromAsset(null, uiClip, _videoWidth, _videoHeight, ref w, ref h);
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
            var activeClipIds = activeClips.Select(c => c.Id).ToHashSet();

            // 遍历所有clips，筛选出在当前帧范围内的clips
            foreach (var clip in activeClips)
            {
                //LogDiagnostic($"Updating layout of {clip.Id}");
                Stopwatch sw = Stopwatch.StartNew();

                var state = GetOrCreateClipState(clip.Id);

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
                    ComputeFittedRectFromAsset(null, clip, _videoWidth, _videoHeight, ref w, ref h);
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

                // 安全获取 clip 实例用于位置提供器；回调未配置时跳过。
                var clipInstance = GetClipInstance(clip);
                if (clip.Effects?.Count > 0 && !ignorePositionProvider && clipInstance is not null)
                {
                    ApplyPositionProvidersToRect(
                        clip.Effects.Values,
                        clipSource: clipInstance,
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

                bool isCurrentClip = _currentClip is not null
                    && _currentClip.Id == clip.Id;

                if (_genericElements.TryGetValue(clip.Id, out var genericElement))
                {
                    var nextRect = new InteractiveRect(x, y, w, h);
                    var previousRect = _genericLastRects.GetValueOrDefault(clip.Id, nextRect);
                    genericElement.LogicalRect = nextRect;
                    genericElement.IsSelected = isCurrentClip;
                    if (_interactiveElementChangedCallback is not null && previousRect != nextRect)
                    {
                        _genericLastRects[clip.Id] = nextRect;
                        _ = _interactiveElementChangedCallback(new InteractiveChange(
                            clip.Id, previousRect, nextRect, InteractiveOperation.None, InteractiveChangeKind.Changed));
                    }
                }

                double displayX = renderRect.X + x * scale;
                double displayY = renderRect.Y + y * scale;
                double displayW = w * scale;
                double displayH = h * scale;

                bool showHandles = isCurrentClip;
                bool showSizeLabel = isCurrentClip && _isHandleResizeInProgress;

                // 在同一原子步骤中更新 ZIndex 和布局边界，无需嵌套 Dispatcher.Dispatch。
                // 该方法已在 UI 线程上被调用（由外层 Dispatcher.DispatchAsync 保证）。
                // 移除嵌套 Dispatch 可避免在布局过程中创建同步重入上下文导致原生层崩溃 (0xc000027b)。
                UpdateClipStateZIndex(state, clip.Id);
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

                // Update custom shape handles (only for the selected clip)
                if (isCurrentClip && _shapeHandleProvider is not null)
                {
                    var handles = _shapeHandleProvider(clip.Id);
                    state.UpdateCustomHandles(handles, displayW, displayH,
                        (handleId, e) => OnCustomHandlePanUpdated(clip.Id, handleId, e));
                }
                else
                {
                    state.ClearCustomHandles();
                }

                //LogDiagnostic($"Updated layout for {clip.Id}");


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

        private void UpdateClipStateZIndex(ClipOverlayState state, Guid clipId)
        {
            if (_allClips is not null && _allClips.TryGetValue(clipId, out var allClip))
            {
                state.Root.ZIndex = ResolveClipOverlayZIndex(allClip);
                return;
            }

            if (_currentClip is not null && _currentClip.Id == clipId)
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

            // 直接在 UI 线程上操作 Children 集合；该方法总是在 UpdateVisuals 的重入保护内被调用。
            // 避免嵌套 Dispatcher.Dispatch 以防止布局重入导致原生层崩溃 (0xc000027b)。
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
        }

        [DebuggerStepThrough()]
        private bool IsClipVisibleInCurrentFrame(ClipElementUI clip)
        {
            if (!clip.ShouldDisplayInUI)
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
                    // For vector clips, include position providers so drag starts from
                    // the exact on-screen rect currently rendered.
                    bool ignorePositionProviderOnStart = _currentClip?.ClipType != ClipMode.VectorCanvasClip;
                    GetCurrentRect(ignorePositionProviderOnStart, out _startX, out _startY, out _startW, out _startH);
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
                        // Move gestures must not mutate size. Keep width/height from the
                        // current clip definition and only commit the translated position.
                        // This avoids subtle size drift when provider-derived preview rects
                        // differ by rounding from stored TargetWidth/TargetHeight.
                        double committedW = _currentClip?.TargetWidth > 0 ? _currentClip.TargetWidth : r.Width;
                        double committedH = _currentClip?.TargetHeight > 0 ? _currentClip.TargetHeight : r.Height;
                        UpdateClipRenderRect(r.X, r.Y, committedW, committedH, updateTextStyle: false, isInRatio: false);
                        GetCurrentRect(true, out finalX, out finalY, out finalW, out finalH);
                        NotifyKeyframeCandidateCaptured(finalX, finalY, finalW, finalH, ResizeHandle.ClipPan);
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
                    // For vector clips, include position providers so resize starts from
                    // the exact on-screen rect currently rendered.
                    bool ignorePositionProviderOnStart = _currentClip?.ClipType != ClipMode.VectorCanvasClip;
                    GetCurrentRect(ignorePositionProviderOnStart, out _startX, out _startY, out _startW, out _startH);
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
                    var snapped = allowFreeScale
                        ? ApplyClipSnapping(new Rect(newX, newY, newW, newH), snapThresholdVideo, handle)
                        : ApplyAspectLockedClipSnapping(new Rect(newX, newY, newW, newH), snapThresholdVideo, handle);
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
                        NotifyKeyframeCandidateCaptured(finalX, finalY, finalW, finalH, handle);
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
            => _isClipPanInProgress || _isHandleResizeInProgress || _isShapeHandleDragInProgress;

        private bool ShouldKeepExistingPreviewFrame(Guid clipId)
            => (_isClipPanInProgress || _isShapeHandleDragInProgress)
                && !_isHandleResizeInProgress
                && _currentClip is not null
                && _currentClip.Id == clipId;

        private bool ShouldSuppressPreviewForResize(Guid clipId)
            => _isHandleResizeInProgress
                && _currentClip is not null
                && _currentClip.Id == clipId;

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
            var targets = new List<double>(3 + _referenceLines.Count)
            {
                0,
                _videoWidth,
                _videoWidth / 2.0
            };
            foreach (var rl in _referenceLines.Values)
            {
                if (rl.Orientation == ReferenceLineOrientation.Vertical)
                    targets.Add(rl.Position);
            }

            return targets;
        }

        private List<double> GetVerticalSnapTargets()
        {
            var targets = new List<double>(3 + _referenceLines.Count)
            {
                0,
                _videoHeight,
                _videoHeight / 2.0
            };
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
                if (clip.Id == currentId)
                    continue;
                if (!IsClipVisibleInCurrentFrame(clip))
                    continue;

                double cx = clip.TargetX;
                double cy = clip.TargetY;
                double cw = clip.TargetWidth > 0 ? clip.TargetWidth : _videoWidth;
                double ch = clip.TargetHeight > 0 ? clip.TargetHeight : _videoHeight;

                var clipInstance = GetClipInstance(clip);
                if (clip.Effects?.Count > 0 && clipInstance is not null)
                {
                    ApplyPositionProvidersToRect(
                        clip.Effects.Values,
                        clipSource: clipInstance,
                        _currentFrame,
                        (int)Math.Round(_videoWidth),
                        (int)Math.Round(_videoHeight),
                        ref cx, ref cy, ref cw, ref ch);
                }

                // Keep snapping targets in the same clamped video-space used by live drag.
                cw = Math.Clamp(cw, MinSize, _videoWidth);
                ch = Math.Clamp(ch, MinSize, _videoHeight);
                if (!AllowClipOutOfBounds)
                {
                    cx = Math.Clamp(cx, 0, _videoWidth - cw);
                    cy = Math.Clamp(cy, 0, _videoHeight - ch);
                }

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

        private Rect ApplyAspectLockedClipSnapping(Rect rect, double snapThresholdVideo, ResizeHandle handle)
        {
            if (snapThresholdVideo <= 0)
                return rect;

            double aspect = ResolveLockedResizeAspectRatio();
            if (aspect <= 0.0001 || double.IsNaN(aspect))
                return rect;

            double x = rect.X;
            double y = rect.Y;
            double w = rect.Width;
            double h = rect.Height;

            // For an aspect-locked corner resize the opposite corner must stay fixed.
            bool fixedXIsLeft = handle is ResizeHandle.TopRight or ResizeHandle.BottomRight;
            bool fixedYIsTop = handle is ResizeHandle.BottomLeft or ResizeHandle.BottomRight;
            double fixedX = fixedXIsLeft ? x : x + w;
            double fixedY = fixedYIsTop ? y : y + h;

            bool canSnapLeft = false, canSnapRight = false, canSnapTop = false, canSnapBottom = false;
            switch (handle)
            {
                case ResizeHandle.TopLeft:
                    canSnapLeft = true;
                    canSnapTop = true;
                    break;
                case ResizeHandle.TopRight:
                    canSnapRight = true;
                    canSnapTop = true;
                    break;
                case ResizeHandle.BottomLeft:
                    canSnapLeft = true;
                    canSnapBottom = true;
                    break;
                case ResizeHandle.BottomRight:
                default:
                    canSnapRight = true;
                    canSnapBottom = true;
                    break;
            }

            var hTargets = GetHorizontalSnapTargets();
            var vTargets = GetVerticalSnapTargets();
            AddOtherClipEdgesToSnapTargets(hTargets, vTargets);

            double bestDist = snapThresholdVideo;
            double bestX = x, bestY = y, bestW = w, bestH = h;

            foreach (var target in hTargets)
            {
                if (canSnapLeft)
                {
                    double dist = Math.Abs(x - target);
                    if (dist < bestDist)
                    {
                        double newW = Math.Max(MinSize, fixedX - target);
                        double newH = Math.Max(MinSize, newW / aspect);
                        bestDist = dist;
                        bestX = fixedX - newW;
                        bestY = fixedYIsTop ? fixedY : fixedY - newH;
                        bestW = newW;
                        bestH = newH;
                    }
                }

                if (canSnapRight)
                {
                    double right = x + w;
                    double dist = Math.Abs(right - target);
                    if (dist < bestDist)
                    {
                        double newW = Math.Max(MinSize, target - fixedX);
                        double newH = Math.Max(MinSize, newW / aspect);
                        bestDist = dist;
                        bestX = fixedX;
                        bestY = fixedYIsTop ? fixedY : fixedY - newH;
                        bestW = newW;
                        bestH = newH;
                    }
                }
            }

            foreach (var target in vTargets)
            {
                if (canSnapTop)
                {
                    double dist = Math.Abs(y - target);
                    if (dist < bestDist)
                    {
                        double newH = Math.Max(MinSize, fixedY - target);
                        double newW = Math.Max(MinSize, newH * aspect);
                        bestDist = dist;
                        bestX = fixedXIsLeft ? fixedX : fixedX - newW;
                        bestY = target;
                        bestW = newW;
                        bestH = newH;
                    }
                }

                if (canSnapBottom)
                {
                    double bottom = y + h;
                    double dist = Math.Abs(bottom - target);
                    if (dist < bestDist)
                    {
                        double newH = Math.Max(MinSize, target - fixedY);
                        double newW = Math.Max(MinSize, newH * aspect);
                        bestDist = dist;
                        bestX = fixedXIsLeft ? fixedX : fixedX - newW;
                        bestY = fixedY;
                        bestW = newW;
                        bestH = newH;
                    }
                }
            }

            return new Rect(bestX, bestY, bestW, bestH);
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
            if (clip.ClipType == ClipMode.SolidColorClip || clip.AllowFreeScaleResize) return true;

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
            // 安全地获取 clip 实例；当回调未配置时返回 null 而非抛出异常，
            // 避免在布局更新过程中传播未处理异常导致 XAML 原生层状态损坏 (0xc000027b)。
            return _getClipInstanceCallback?.Invoke(clip);
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

        private void OptionsButton_Clicked(object sender, EventArgs e)
        {
            OptionsOverlay.IsVisible = true;
        }

        private void CloseOptionsButton_Clicked(object sender, EventArgs e)
        {
            OptionsOverlay.IsVisible = false;
        }

        private void OptionsOverlayBackground_Clicked(object sender, EventArgs e)
        {
            OptionsOverlay.IsVisible = false;
        }

        private async void PreviewResolutionPicker_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_suppressPreviewResolutionChanged
                || PreviewResolutionPicker.SelectedItem is not string selected
                || _previewResolutionChangedCallback is null)
            {
                return;
            }

            PreviewResolutionPicker.IsEnabled = false;
            try
            {
                await _previewResolutionChangedCallback(selected);
            }
            finally
            {
                PreviewResolutionPicker.IsEnabled = true;
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
            OptionsOverlay.IsVisible = false;
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
            var leftGap = renderRect.X;
            var rightGap = _canvasWidth - (renderRect.X + renderRect.Width);
            var controlsWidth = BottomControlsHost.Width;
            var hasControlsHorizontalRoom = controlsWidth > 0d
                && rightGap >= controlsWidth + BottomControlsHost.Margin.Right;
            var infoIndicatorWidth = InfoIndicatorHost.Width > 0d
                ? InfoIndicatorHost.Width
                : InfoIndicatorHost.WidthRequest;
            var infoIndicatorHeight = InfoIndicatorHost.Height > 0d
                ? InfoIndicatorHost.Height
                : InfoIndicatorHost.HeightRequest;
            var hasInfoHorizontalRoom = infoIndicatorWidth > 0d
                && leftGap >= infoIndicatorWidth + InfoIndicatorHost.Margin.Left;
            var hasInfoVerticalRoom = infoIndicatorHeight > 0d
                && bottomGap >= infoIndicatorHeight + InfoIndicatorHost.Margin.Bottom;

            // Keep the controls visible whenever they fit beside or below the preview.
            // Only fall back to hover-to-show when both directions would overlap it.
            _autoHideBottomControls = bottomGap < 35d && !hasControlsHorizontalRoom;
            _autoHideInfoIndicator = !hasInfoVerticalRoom && !hasInfoHorizontalRoom;

            Dispatcher.Dispatch(() =>
            {
                if (!_autoHideBottomControls)
                {
                    CancelHideBottomControlsDebounce();
                    BottomControlsHost.Opacity = 1d;
                }
                else if (!_isPointerOverBottomControls)
                {
                    BottomControlsHost.Opacity = 0d;
                }

                if (!InfoIndicatorHost.IsVisible)
                {
                    CancelHideInfoIndicatorDebounce();
                    InfoDetailsOverlay.IsVisible = false;
                }
                else if (!_autoHideInfoIndicator)
                {
                    CancelHideInfoIndicatorDebounce();
                    InfoIndicatorHost.Opacity = GetInfoIndicatorVisibleOpacity();
                }
                else if (!_isPointerOverInfoIndicator)
                {
                    var revealRemainingMs = _infoIndicatorRevealUntilTick - Environment.TickCount64;
                    if (revealRemainingMs > 0)
                    {
                        InfoIndicatorHost.Opacity = GetInfoIndicatorVisibleOpacity();
                        ScheduleInfoIndicatorHide((int)Math.Min(int.MaxValue, revealRemainingMs));
                    }
                    else
                    {
                        InfoIndicatorHost.Opacity = 0d;
                    }
                }
            });
        }

        private void OnBottomControlsHostSizeChanged(object? sender, EventArgs e)
        {
            if (_videoWidth > 0d && _videoHeight > 0d && _canvasWidth > 0d && _canvasHeight > 0d)
            {
                UpdateBottomControlsVisibility(GetRenderRect());
            }
        }

        private void OnBottomControlsHostEntered(object? sender, PointerEventArgs e)
        {
            _isPointerOverBottomControls = true;
            CancelHideBottomControlsDebounce();
            BottomControlsHost.Opacity = 1d;
        }

        private void OnBottomControlsHostExited(object? sender, PointerEventArgs e)
        {
            _isPointerOverBottomControls = false;
            if (!_autoHideBottomControls)
            {
                return;
            }

            ScheduleBottomControlsHide(250);
        }

        private void OnInfoIndicatorEntered(object? sender, PointerEventArgs e)
        {
            _isPointerOverInfoIndicator = true;
            CancelHideInfoIndicatorDebounce();
            InfoIndicatorHost.Opacity = GetInfoIndicatorVisibleOpacity();
            InfoDetailsOverlay.IsVisible = !string.IsNullOrWhiteSpace(_infoIndicatorMessage);
        }

        private void OnInfoIndicatorExited(object? sender, PointerEventArgs e)
        {
            _isPointerOverInfoIndicator = false;
            InfoDetailsOverlay.IsVisible = false;
            if (_autoHideInfoIndicator)
            {
                _infoIndicatorRevealUntilTick = 0;
                ScheduleInfoIndicatorHide(250);
            }
        }

        private double GetInfoIndicatorVisibleOpacity() =>
            string.IsNullOrWhiteSpace(_infoIndicatorMessage) ? InfoIndicatorIdleOpacity : 1d;

        private void ScheduleBottomControlsHide(int delayMs)
        {
            CancelHideBottomControlsDebounce();
            var cts = new CancellationTokenSource();
            _hideBottomControlsCts = cts;
            _ = HideBottomControlsAfterDelayAsync(delayMs, cts);
        }

        private async Task HideBottomControlsAfterDelayAsync(int delayMs, CancellationTokenSource cts)
        {
            try
            {
                await Task.Delay(Math.Max(0, delayMs), cts.Token);
                if (!_isPointerOverBottomControls && _autoHideBottomControls)
                {
                    BottomControlsHost.Opacity = 0d;
                }
            }
            catch (OperationCanceledException)
            {
                // 鼠标在延迟期间重新进入，取消隐藏。
            }
            finally
            {
                if (ReferenceEquals(Interlocked.CompareExchange(ref _hideBottomControlsCts, null, cts), cts))
                {
                    cts.Dispose();
                }
            }
        }

        private void ScheduleInfoIndicatorHide(int delayMs)
        {
            CancelHideInfoIndicatorDebounce();
            var cts = new CancellationTokenSource();
            _hideInfoIndicatorCts = cts;
            _ = HideInfoIndicatorAfterDelayAsync(delayMs, cts);
        }

        private async Task HideInfoIndicatorAfterDelayAsync(int delayMs, CancellationTokenSource cts)
        {
            try
            {
                await Task.Delay(Math.Max(0, delayMs), cts.Token);
                if (!_isPointerOverInfoIndicator && _autoHideInfoIndicator)
                {
                    InfoIndicatorHost.Opacity = 0d;
                    InfoDetailsOverlay.IsVisible = false;
                }
            }
            catch (OperationCanceledException)
            {
                // 鼠标在延迟期间重新进入，取消隐藏。
            }
            finally
            {
                if (ReferenceEquals(Interlocked.CompareExchange(ref _hideInfoIndicatorCts, null, cts), cts))
                {
                    cts.Dispose();
                }
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

        private void CancelHideInfoIndicatorDebounce()
        {
            var cts = Interlocked.Exchange(ref _hideInfoIndicatorCts, null);
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
