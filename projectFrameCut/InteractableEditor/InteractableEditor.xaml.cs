using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.Shared;
using System.Diagnostics;
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
        private const string LegacyPlaceResizeSettingKey = "Edit_UseLegacyPlaceResizeEffects";

        private double _canvasWidth = 800;
        private double _canvasHeight = 240;
        private double _videoWidth = 1920;
        private double _videoHeight = 1080;

        private double _startX, _startY, _startW, _startH;
        private Rect _baseRect;
        private bool _isTextClip = false;

        private const double HandleSize = 15;
        private const double MinSize = 10;

        private readonly Dictionary<string, ClipOverlayState> _clipStates = new(StringComparer.Ordinal);
        private ClipOverlayState? _activeState;
        private Func<Task>? _previewRefreshCallback;
        private long _lastPreviewRefreshTick;
        private int _isPreviewRefreshRunning;
        private int _hasPendingPreviewRefresh;
        private int _isCommitUpdateRunning;
        private int _hasPendingCommitUpdate;

        private CancellationTokenSource? _commitUpdateDebounceCts;
        private readonly object _commitUpdateDebounceLock = new();

        private const int PreviewRefreshThrottleMs = 180;
        private const int CommitUpdateDebounceMs = 220;

        public bool UseLegacyPlaceResizeEffects { get; set; }
        public bool ShowRenderRectOverlay { get; set; } = true;

        public InteractableEditor()
        {
            InitializeComponent();
            UseLegacyPlaceResizeEffects = SettingsManager.IsBoolSettingTrue("Edit_UseLegacyPlaceResizeEffects");
        }

        private sealed class ClipOverlayState
        {
            private readonly InteractableEditor _owner;

            public ClipOverlayState(InteractableEditor owner)
            {
                _owner = owner;
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
                    BackgroundColor = Color.FromArgb("#33FFFF00"),
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

                SolidColorSizeLabel = new Label
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
                Root.Children.Add(SolidColorSizeLabel);

            }

            public AbsoluteLayout Root { get; }
            public Border ClipVisual { get; }
            public ContentView PreviewHost { get; }
            public BoxView HandleTL { get; }
            public BoxView HandleTR { get; }
            public BoxView HandleBL { get; }
            public BoxView HandleBR { get; }
            public Label SolidColorSizeLabel { get; }
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
                    ZIndex = 2
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

            public void UpdateLayout(double displayX, double displayY, double displayW, double displayH, double logicalW, double logicalH, bool showHandles, bool showSizeLabel, string? sizeText)
            {
                Root.IsVisible = true;
                AbsoluteLayout.SetLayoutBounds(Root, new Rect(displayX, displayY, displayW, displayH));
                AbsoluteLayout.SetLayoutBounds(ClipVisual, new Rect(0, 0, displayW, displayH));
                UpdatePreviewHostLayout(displayW, displayH, logicalW, logicalH);

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
                    SolidColorSizeLabel.Text = sizeText ?? string.Empty;
                    SolidColorSizeLabel.IsVisible = !string.IsNullOrWhiteSpace(SolidColorSizeLabel.Text);
                    AbsoluteLayout.SetLayoutBounds(SolidColorSizeLabel, new Rect(4, 4, AbsoluteLayout.AutoSize, AbsoluteLayout.AutoSize));
                }
                else
                {
                    SolidColorSizeLabel.Text = string.Empty;
                    SolidColorSizeLabel.IsVisible = false;
                }
            }

            public void Hide()
            {
                Root.IsVisible = false;
                PreviewHost.IsVisible = false;
                PreviewHost.Content = null;
                SolidColorSizeLabel.IsVisible = false;
            }

            public void SetPreviewView(View? view)
            {
                PreviewHost.Content = view;
                PreviewHost.IsVisible = view is not null;
            }

            private void UpdatePreviewHostLayout(double displayW, double displayH, double logicalW, double logicalH)
            {
                if (logicalW <= 0 || logicalH <= 0 || displayW <= 0 || displayH <= 0)
                {
                    PreviewHost.IsVisible = PreviewHost.Content is not null;
                    PreviewHost.Scale = 1d;
                    AbsoluteLayout.SetLayoutBounds(PreviewHost, new Rect(0, 0, 1, 1));
                    return;
                }

                PreviewHost.WidthRequest = logicalW;
                PreviewHost.HeightRequest = logicalH;
                PreviewHost.Scale = Math.Clamp(displayW / logicalW, 0.0001d, 1000d);
                AbsoluteLayout.SetLayoutBounds(PreviewHost, new Rect(0, 0, logicalW, logicalH));
                PreviewHost.IsVisible = PreviewHost.Content is not null;
            }
        }

        public void ConfigurePreviewRefresh(Func<Task>? refreshCallback)
        {
            _previewRefreshCallback = refreshCallback;
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
        {
            if (_clipStates.TryGetValue(clip.Id, out var state))
            {
                return state;
            }

            state = new ClipOverlayState(this);
            _clipStates[clip.Id] = state;
            ClipStatesHost.Children.Add(state.Root);
            return state;
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

            if (_activeState is not null)
            {
                _activeState.Hide();
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
            _activeState = null;
        }

        public void SetClip(projectFrameCut.DraftStuff.ClipElementUI? clip, AssetItem? asset)
        {
            CancelPendingCommitUpdate();

            _currentClip = clip;
            _currentAsset = asset;
            if (clip == null)
            {
                SetActiveState(null);
                this.IsVisible = false;
                this.InputTransparent = true;
                RenderRectVisual.IsVisible = false;
                RenderRectLabel.IsVisible = false;
                Interlocked.Exchange(ref _hasPendingPreviewRefresh, 0);
                return;
            }
            this.IsVisible = true;
            this.InputTransparent = false;

            _isTextClip = clip.ClipType == ClipMode.TextClip;
            if (_isTextClip && clip.ExtraData.TryGetValue("TextEntries", out var entriesObj))
            {
                List<TextClipEntry>? entries = null;
                if (entriesObj is List<TextClipEntry> list)
                {
                    entries = list;
                }
                else if (entriesObj is JsonElement je)
                {
                    try
                    {
                        entries = JsonSerializer.Deserialize<List<TextClipEntry>>(je);
                    }
                    catch { }
                }

                if (entries != null && entries.Count > 0)
                {
                    var entry = entries[0];
                    MeasurementLabel.Text = entry.text;
                    // Scale font size: ImageSharp points (1/72 inch) vs MAUI DIPs (1/96 inch approx, but depends on platform)
                    // 72 points = 1 inch. 96 DIPs = 1 inch.
                    // So 72 points should be 96 DIPs.
                    // Factor = 96/72 = 1.333
                    MeasurementLabel.FontSize = entry.fontSize * (96.0 / 72.0);

                    var size = MeasurementLabel.Measure(double.PositiveInfinity, double.PositiveInfinity);

                    // If measure fails (returns 0), fallback to something visible
                    double w = size.Width > 0 ? size.Width : 100;
                    double h = size.Height > 0 ? size.Height : 50;

                    // For text clips, position comes from TextEntries (not PlaceEffect_ImageSharp).
                    _baseRect = new Rect(entry.x, entry.y, w, h);

                    // Normalize storage to a mutable, strongly-typed list to simplify later edits.
                    if (entriesObj is not List<TextClipEntry>)
                    {
                        clip.ExtraData["TextEntries"] = entries;
                    }
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
                Debug.WriteLine($"Dynamic preview refresh failed: {ex.Message}");
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
            if (_currentClip == null) return;

            if (_videoWidth <= 0 || _videoHeight <= 0 || _canvasWidth <= 0 || _canvasHeight <= 0)
                return;

            var state = _activeState ?? GetOrCreateClipState(_currentClip);
            SetActiveState(state);

            double x, y, w, h;
            GetCurrentRect(out x, out y, out w, out h);

            // Clamp to keep UI stable.
            w = Math.Clamp(w, MinSize, _videoWidth);
            h = Math.Clamp(h, MinSize, _videoHeight);
            x = Math.Clamp(x, 0, _videoWidth - w);
            y = Math.Clamp(y, 0, _videoHeight - h);

            Rect renderRect = GetRenderRect();
            double scale = renderRect.Width / _videoWidth;

            UpdateRenderRectOverlay(renderRect);

            double displayX = renderRect.X + x * scale;
            double displayY = renderRect.Y + y * scale;
            double displayW = w * scale;
            double displayH = h * scale;

            state.UpdateLayout(
                displayX,
                displayY,
                displayW,
                displayH,
                w,
                h,
                !_isTextClip,
                _currentClip.ClipType == ClipMode.SolidColorClip,
                _currentClip.ClipType == ClipMode.SolidColorClip ? $"{Math.Round(w)} x {Math.Round(h)}" : null);
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
                foreach (var state in _clipStates.Values)
                {
                    state.SetPreviewView(null);
                }

                UpdateVisuals();
                return false;
            }

            var knownStates = new HashSet<string>(StringComparer.Ordinal);
            var hasVisiblePreview = false;

            foreach (var prepared in preparedPreviews)
            {
                if (string.IsNullOrWhiteSpace(prepared.ClipId))
                {
                    continue;
                }

                if (!_clipStates.TryGetValue(prepared.ClipId, out var state))
                {
                    if (_currentClip is null || !string.Equals(_currentClip.Id, prepared.ClipId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    state = GetOrCreateClipState(_currentClip);
                }

                knownStates.Add(prepared.ClipId);
                state.SetPreviewView(prepared.View);
                hasVisiblePreview |= prepared.View is not null;
            }

            foreach (var entry in _clipStates)
            {
                if (!knownStates.Contains(entry.Key))
                {
                    entry.Value.SetPreviewView(null);
                }
            }

            UpdateVisuals();
            return hasVisiblePreview;
        }

        private void UpdateRenderRectOverlay(Rect renderRect)
        {
            var visible = ShowRenderRectOverlay
                && renderRect.Width > 0
                && renderRect.Height > 0;

            if (!visible)
            {
                RenderRectVisual.IsVisible = false;
                RenderRectLabel.IsVisible = false;
                return;
            }

            AbsoluteLayout.SetLayoutBounds(RenderRectVisual, new Rect(renderRect.X, renderRect.Y, renderRect.Width, renderRect.Height));
            RenderRectVisual.IsVisible = true;

            RenderRectLabel.Text = $"renderRect {Math.Round(renderRect.X)} , {Math.Round(renderRect.Y)}  {Math.Round(renderRect.Width)} x {Math.Round(renderRect.Height)}";
            AbsoluteLayout.SetLayoutBounds(RenderRectLabel, new Rect(renderRect.X + 4, renderRect.Y + 4, AbsoluteLayout.AutoSize, AbsoluteLayout.AutoSize));
            RenderRectLabel.IsVisible = true;
        }

        private void OnClipPanUpdated(ClipOverlayState state, PanUpdatedEventArgs e)
        {
            if (_currentClip == null || !ReferenceEquals(state, _activeState)) return;

            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    GetCurrentRect(out _startX, out _startY, out _startW, out _startH);
                    System.Diagnostics.Debug.WriteLine($"[Pan] Started: Pos=({_startX:F1}, {_startY:F1}), Size=({_startW:F1}, {_startH:F1})");
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
                    RequestInteractivePreviewRefresh();
                    break;
                    
                case GestureStatus.Completed:
                case GestureStatus.Canceled:
                    GetCurrentRect(out var finalX, out var finalY, out _, out _);
                    System.Diagnostics.Debug.WriteLine($"[Pan] Completed: FinalPos=({finalX:F1}, {finalY:F1})");
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
                    GetCurrentRect(out _startX, out _startY, out _startW, out _startH);
                    System.Diagnostics.Debug.WriteLine($"[Resize] Started: Pos=({_startX:F1}, {_startY:F1}), Size=({_startW:F1}x{_startH:F1})");
                    break;
                    
                case GestureStatus.Running:
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

                    UpdateClipEffects(newX, newY, newW, newH);
                    UpdateVisuals();
                    RequestInteractivePreviewRefresh();
                    break;
                    
                case GestureStatus.Completed:
                case GestureStatus.Canceled:
                    GetCurrentRect(out var finalX, out var finalY, out var finalW, out var finalH);
                    System.Diagnostics.Debug.WriteLine($"[Resize] Completed: Pos=({finalX:F1}, {finalY:F1}), Size=({finalW:F1}x{finalH:F1})");
                    RequestCommitUpdate();
                    break;
            }
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
                Debug.WriteLine($"Commit update dispatch failed: {ex.Message}");
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
                Debug.WriteLine($"Commit update execution failed: {ex.Message}");
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
                var usedTargetRect = false;
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

                    usedTargetRect = true;
                }

                // Keep old projects visible when target fields are not set yet.
                if (!usedTargetRect)
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
            if (!_currentClip.ExtraData.TryGetValue("TextEntries", out var entriesObj)) return false;

            List<TextClipEntry>? entries = null;
            if (entriesObj is List<TextClipEntry> list)
            {
                entries = list;
            }
            else if (entriesObj is JsonElement je)
            {
                try
                {
                    entries = JsonSerializer.Deserialize<List<TextClipEntry>>(je);
                }
                catch
                {
                    return false;
                }

                if (entries != null)
                    _currentClip.ExtraData["TextEntries"] = entries;
            }

            if (entries == null || entries.Count == 0) return false;
            entry = entries[0];
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
            if (_currentClip?.Effects == null)
            {
                return false;
            }

            var resolved = false;
            if (_currentClip.Effects.TryGetValue(InternalPlaceKey, out var p) && p is PlaceEffect_IPicture place)
            {
                int relW = place.RelativeWidth > 0 ? place.RelativeWidth : (int)_videoWidth;
                int relH = place.RelativeHeight > 0 ? place.RelativeHeight : (int)_videoHeight;

                x = (double)place.StartX * _videoWidth / relW;
                y = (double)place.StartY * _videoHeight / relH;
                resolved = true;
            }

            if (_currentClip.ClipType != ClipMode.SolidColorClip
                && _currentClip.Effects.TryGetValue(InternalResizeKey, out var r)
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