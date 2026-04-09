using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.Shared;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

using System.Text.Json;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Effect;
using projectFrameCut.Setting.SettingManager;

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

        private const string InternalPlaceKey = "__Internal_Place__";
        private const string InternalResizeKey = "__Internal_Resize__";
        private const string SolidColorOutputWidthKey = "SolidColorOutputWidth";
        private const string SolidColorOutputHeightKey = "SolidColorOutputHeight";
        private const string SolidColorUseFixedOutputSizeKey = "SolidColorUseFixedOutputSize";
        private const string AllowFreeScaleResizeKey = "AllowFreeScaleResize";
        private const string LegacyPlaceResizeSettingKey = "Edit_UseLegacyPlaceResizeEffects";

        private double _canvasWidth = 800;
        private double _canvasHeight = 240;
        private double _videoWidth = 1920;
        private double _videoHeight = 1080;

        private double _startX, _startY, _startW, _startH;
        private Rect _baseRect;
        private bool _isTextClip = false;
        private bool _isClipPanInProgress;
        private bool _isHandleResizeInProgress;

        private const double HandleSize = 15;
        private const double MinSize = 10;

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

        private CancellationTokenSource? _commitUpdateDebounceCts;
        private readonly object _commitUpdateDebounceLock = new();

        private const int PreviewRefreshThrottleMs = 180;
        private const int CommitUpdateDebounceMs = 220;

        public ConcurrentDictionary<string, ClipElementUI> Clips { get; private set; } = new();

        // 用于直接显示DraftPage中的所有clips
        private IReadOnlyDictionary<string, ClipElementUI>? _allClips;
        private uint _currentFrame;
        private double _framePerPixel = 1d;
        private double _tracksZoomOffset = 1d;
        private float _secondPerFrameRatio = 1f;

        public bool UseLegacyPlaceResizeEffects { get; set; }
        public bool ShowRenderRectOverlay { get; set; } = true;
        public bool ShowClipPreviewOverlays
        {
            get => _showClipPreviewOverlays;
            set
            {
                if (_showClipPreviewOverlays == value)
                {
                    return;
                }

                _showClipPreviewOverlays = value;
                foreach (var state in _clipStates.Values)
                {
                    state.RefreshPreviewVisibility();
                }
            }
        }

        private bool _showClipPreviewOverlays = true;

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
            UseLegacyPlaceResizeEffects = SettingsManager.IsBoolSettingTrue("Edit_UseLegacyPlaceResizeEffects");

            var canvasTap = new TapGestureRecognizer();
            canvasTap.Tapped += OnEditorCanvasTapped;
            EditorCanvas.GestureRecognizers.Add(canvasTap);
        }

        private sealed class ClipOverlayState
        {
            private readonly InteractableEditor _owner;
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

            }

            public AbsoluteLayout Root { get; }
            public Border ClipVisual { get; }
            public ContentView PreviewHost { get; }
            public BoxView HandleTL { get; }
            public BoxView HandleTR { get; }
            public BoxView HandleBL { get; }
            public BoxView HandleBR { get; }
            public Label SizeLabel { get; }
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

            public void UpdateLayout(double displayX, double displayY, double displayW, double displayH, double logicalW, double logicalH, bool showHandles, bool showSizeLabel, string? sizeText, bool showClipVisual)
            {
                Root.IsVisible = true;
                AbsoluteLayout.SetLayoutBounds(Root, new Rect(displayX, displayY, displayW, displayH));
                AbsoluteLayout.SetLayoutBounds(ClipVisual, new Rect(0, 0, displayW, displayH));
                ClipVisual.IsVisible = showClipVisual;
                UpdatePreviewHostLayout(displayW, displayH, logicalW, logicalH);
                Root.InputTransparent = !_owner.ShouldAllowOverlayTapSelection(this)
                    || (!ClipVisual.IsVisible && !PreviewHost.IsVisible);

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
                PreviewHost.IsVisible = _owner.ShouldShowPreviewHost(this) && PreviewHost.Content is not null;
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
            _clipStates[clipId] = state;
            ClipStatesHost.Children.Add(state.Root);
            return state;
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

            _ = InvokeOverlayClipTappedAsync(callback, state.ClipId);
        }

        private void OnEditorCanvasTapped(object? sender, TappedEventArgs e)
        {
            var callback = _blankAreaTappedCallback;
            if (callback is null)
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

                var showHandles = isCurrentClip && !_isTextClip;
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
                    isCurrentClip);
            }

            ReorderClipStateRootsByZIndex();
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

            if (HasExplicitTargetRect(clip))
            {
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

                // Fill only missing fields from legacy effects for partially migrated clips.
                ApplyLegacyRectFallbackForMissingTargetFields(clip, ref x, ref y, ref w, ref h);
            }
            else
            {
                TryReadRectFromLegacyEffects(clip, ref x, ref y, ref w, ref h);
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

                    // 从legacy effects补全缺失字段（兼容部分target字段的旧数据）
                    if (HasExplicitTargetRect(clip))
                    {
                        ApplyLegacyRectFallbackForMissingTargetFields(clip, ref x, ref y, ref w, ref h);
                    }
                    else
                    {
                        TryReadRectFromLegacyEffects(clip, ref x, ref y, ref w, ref h);
                    }
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
                bool showHandles = isCurrentClip && !_isTextClip;
                bool showSizeLabel = isCurrentClip && _isHandleResizeInProgress;

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
                    showClipVisual: isCurrentClip);
            }

            // 隐藏不再活跃的clips
            var inactiveIds = _clipStates.Keys.Except(activeClipIds).ToList();
            foreach (var inactiveId in inactiveIds)
            {
                if (_clipStates.TryGetValue(inactiveId, out var state))
                {
                    state.Hide();
                }
            }

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
            LogDiagnostic($"For clip {clip.Id}/{clip.DisplayName}, track:{layer}, sub:{subLayer}, ZIndex is {zIndex}");
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
                ClipStatesHost.Children.Remove(root);
                ClipStatesHost.Children.Add(root);
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

        private void ApplyLegacyRectFallbackForMissingTargetFields(ClipElementUI clip, ref double x, ref double y, ref double w, ref double h)
        {
            double legacyX = x;
            double legacyY = y;
            double legacyW = w;
            double legacyH = h;

            if (!TryReadRectFromLegacyEffects(clip, ref legacyX, ref legacyY, ref legacyW, ref legacyH))
            {
                return;
            }

            var hasTargetWidth = clip.TargetWidth > 0;
            var hasTargetHeight = clip.TargetHeight > 0;
            // When size is already explicit, treat (0,0) as intentional position.
            var hasExplicitPosition = clip.TargetX != 0 || clip.TargetY != 0 || (hasTargetWidth && hasTargetHeight);

            if (!hasExplicitPosition)
            {
                x = legacyX;
                y = legacyY;
            }

            if (!hasTargetWidth)
            {
                w = legacyW;
            }

            if (!hasTargetHeight)
            {
                h = legacyH;
            }
        }

        private void OnClipPanUpdated(ClipOverlayState state, PanUpdatedEventArgs e)
        {
            if (_currentClip == null || !ReferenceEquals(state, _activeState)) return;

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

                    if (_isTextClip)
                    {
                        UpdateTextEntryPosition(newVisualX, newVisualY);
                    }
                    else
                    {
                        UpdateClipEffects(newVisualX, newVisualY, _startW, _startH);
                    }

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

            var aspect = ResolveLockedResizeAspectRatio();
            var relW = Math.Abs((w / Math.Max(_startW, 0.0001)) - 1d);
            var relH = Math.Abs((h / Math.Max(_startH, 0.0001)) - 1d);

            if (relW >= relH)
            {
                h = Math.Max(MinSize, w / aspect);
            }
            else
            {
                w = Math.Max(MinSize, h * aspect);
            }

            switch (handle)
            {
                case ResizeHandle.TopLeft:
                    x = _startX + (_startW - w);
                    y = _startY + (_startH - h);
                    break;
                case ResizeHandle.TopRight:
                    y = _startY + (_startH - h);
                    break;
                case ResizeHandle.BottomLeft:
                    x = _startX + (_startW - w);
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

            if (ShouldUseLegacyPlaceResizeEffects())
            {
                TryReadRectFromLegacyEffects(ref x, ref y, ref w, ref h);
            }
            else
            {
                if (HasExplicitTargetRect(_currentClip))
                {
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

                    // Fill only missing fields from legacy effects for partially migrated clips.
                    ApplyLegacyRectFallbackForMissingTargetFields(_currentClip, ref x, ref y, ref w, ref h);
                }
                else
                {
                    TryReadRectFromLegacyEffects(ref x, ref y, ref w, ref h);
                }
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
            var fontSize = Math.Max(1d, entry.fontSize * (96.0 / 72.0));
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

        private void UpdateTextEntryPosition(double desiredX, double desiredY)
        {
            if (_currentClip == null) return;
            if (!_currentClip.ExtraData.TryGetValue("TextEntries", out var entriesObj)) return;
            if (entriesObj is not List<TextClipEntry> entries || entries.Count == 0) return;

            double w = _baseRect.Width > 0 ? _baseRect.Width : MinSize;
            double h = _baseRect.Height > 0 ? _baseRect.Height : MinSize;

            int newX = (int)Math.Round(Math.Clamp(desiredX, 0, _videoWidth - w));
            int newY = (int)Math.Round(Math.Clamp(desiredY, 0, _videoHeight - h));

            var old = entries[0];
            entries[0] = old with { x = newX, y = newY };
            _currentClip.ExtraData["TextEntries"] = entries;
        }

        private void UpdateClipEffects(double x, double y, double w, double h)
        {
            if (_currentClip == null) return;

            // Clamp in video coordinate space.
            w = Math.Clamp(w, MinSize, _videoWidth);
            h = Math.Clamp(h, MinSize, _videoHeight);
            x = Math.Clamp(x, 0, _videoWidth - w);
            y = Math.Clamp(y, 0, _videoHeight - h);

            if (ShouldUseLegacyPlaceResizeEffects())
            {
                UpdateLegacyPlaceResizeEffects(x, y, w, h);
                return;
            }

            _currentClip.TargetX = (int)Math.Round(x, MidpointRounding.AwayFromZero);
            _currentClip.TargetY = (int)Math.Round(y, MidpointRounding.AwayFromZero);
            _currentClip.TargetWidth = Math.Max(1, (int)Math.Round(w, MidpointRounding.AwayFromZero));
            _currentClip.TargetHeight = Math.Max(1, (int)Math.Round(h, MidpointRounding.AwayFromZero));

            if (_currentClip.ClipType == ClipMode.SolidColorClip)
            {
                UpdateSolidColorOutputSize(w, h);
            }

            RemoveLegacyPlaceResizeEffects();
        }

        private bool ShouldUseLegacyPlaceResizeEffects()
            => UseLegacyPlaceResizeEffects || SettingsManager.IsBoolSettingTrue(LegacyPlaceResizeSettingKey);

        private static bool HasExplicitTargetRect(ClipElementUI clip)
            => clip.TargetX != 0 || clip.TargetY != 0 || clip.TargetWidth > 0 || clip.TargetHeight > 0;

        private bool TryReadRectFromLegacyEffects(ref double x, ref double y, ref double w, ref double h)
        {
            if (_currentClip is null)
            {
                return false;
            }

            return TryReadRectFromLegacyEffects(_currentClip, ref x, ref y, ref w, ref h);
        }

        private bool TryReadRectFromLegacyEffects(ClipElementUI clip, ref double x, ref double y, ref double w, ref double h)
        {
            if (clip.Effects == null)
            {
                return false;
            }

            var resolved = false;
            if (clip.Effects.TryGetValue(InternalPlaceKey, out var p) && p is PlaceEffect_IPicture place)
            {
                int relW = place.RelativeWidth > 0 ? place.RelativeWidth : (int)_videoWidth;
                int relH = place.RelativeHeight > 0 ? place.RelativeHeight : (int)_videoHeight;

                x = (double)place.StartX * _videoWidth / relW;
                y = (double)place.StartY * _videoHeight / relH;
                resolved = true;
            }

            if (clip.ClipType != ClipMode.SolidColorClip
                && clip.Effects.TryGetValue(InternalResizeKey, out var r)
                && r is ResizeEffect_ImageSharp resize)
            {
                int relW = resize.RelativeWidth > 0 ? resize.RelativeWidth : (int)_videoWidth;
                int relH = resize.RelativeHeight > 0 ? resize.RelativeHeight : (int)_videoHeight;

                w = (double)resize.Width * _videoWidth / relW;
                h = (double)resize.Height * _videoHeight / relH;
                resolved = true;
            }

            return resolved;
        }

        private void UpdateLegacyPlaceResizeEffects(double x, double y, double w, double h)
        {
            if (_currentClip == null) return;
            if (_currentClip.Effects == null) _currentClip.Effects = new Dictionary<string, IEffect>();

            int relW = (int)Math.Round(_videoWidth);
            int relH = (int)Math.Round(_videoHeight);

            // Place - Always store in current video coordinate space
            if (_currentClip.Effects.TryGetValue(InternalPlaceKey, out var p) && p is PlaceEffect_IPicture place)
            {
                relW = place.RelativeWidth > 0 ? place.RelativeWidth : relW;
                relH = place.RelativeHeight > 0 ? place.RelativeHeight : relH;

                _currentClip.Effects["__Internal_Place__"] = new PlaceEffect_IPicture
                {
                    StartX = (int)Math.Round(x * relW / _videoWidth),
                    StartY = (int)Math.Round(y * relH / _videoHeight),
                    Enabled = place.Enabled,
                    Index = place.Index,
                    Name = string.IsNullOrWhiteSpace(place.Name) ? InternalPlaceKey : place.Name,
                    RelativeWidth = relW,
                    RelativeHeight = relH
                };
            }
            else
            {
                _currentClip.Effects[InternalPlaceKey] = new PlaceEffect_IPicture
                {
                    StartX = (int)Math.Round(x),
                    StartY = (int)Math.Round(y),
                    Enabled = true,
                    Index = int.MaxValue - 100,
                    Name = InternalPlaceKey,
                    RelativeWidth = relW,
                    RelativeHeight = relH
                };
            }

            if (!_isTextClip)
            {
                if (_currentClip.ClipType == ClipMode.SolidColorClip)
                {
                    UpdateSolidColorOutputSize(w, h);
                    _currentClip.Effects.Remove(InternalResizeKey);
                    return;
                }

                if (_currentClip.Effects.TryGetValue(InternalResizeKey, out var r) && r is ResizeEffect_ImageSharp resize)
                {
                    int resizeRelW = resize.RelativeWidth > 0 ? resize.RelativeWidth : relW;
                    int resizeRelH = resize.RelativeHeight > 0 ? resize.RelativeHeight : relH;

                    _currentClip.Effects[InternalResizeKey] = new ResizeEffect_ImageSharp
                    {
                        Width = (int)Math.Round(w * resizeRelW / _videoWidth, MidpointRounding.AwayFromZero),
                        Height = (int)Math.Round(h * resizeRelH / _videoHeight, MidpointRounding.AwayFromZero),
                        PreserveAspectRatio = false,
                        Enabled = resize.Enabled,
                        Index = resize.Index,
                        Name = string.IsNullOrWhiteSpace(resize.Name) ? InternalResizeKey : resize.Name,
                        RelativeWidth = resizeRelW,
                        RelativeHeight = resizeRelH
                    };
                }
                else
                {
                    _currentClip.Effects[InternalResizeKey] = new ResizeEffect_ImageSharp
                    {
                        Width = (int)Math.Round(w, MidpointRounding.AwayFromZero),
                        Height = (int)Math.Round(h, MidpointRounding.AwayFromZero),
                        PreserveAspectRatio = false,
                        Enabled = true,
                        Index = int.MinValue + 50,
                        Name = InternalResizeKey,
                        RelativeWidth = relW,
                        RelativeHeight = relH
                    };
                }
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

        private void RemoveLegacyPlaceResizeEffects()
        {
            if (_currentClip?.Effects == null)
            {
                return;
            }

            _currentClip.Effects.Remove(InternalPlaceKey);
            _currentClip.Effects.Remove(InternalResizeKey);
        }

        private static bool IsAllowFreeScaleResizeEnabled(ClipElementUI clip)
        {
            if (ReadBoolExtraData(clip.ExtraData, AllowFreeScaleResizeKey, out var allowFreeScale))
            {
                return allowFreeScale;
            }

            if (clip.Effects != null
                && clip.Effects.TryGetValue(InternalResizeKey, out var effect)
                && effect is ResizeEffect_ImageSharp resize)
            {
                return !resize.PreserveAspectRatio;
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