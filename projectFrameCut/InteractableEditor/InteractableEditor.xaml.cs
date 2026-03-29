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

namespace projectFrameCut.InteractableEditor
{
    public partial class InteractableEditor : ContentView
    {
        private projectFrameCut.DraftStuff.ClipElementUI? _currentClip;
        private AssetItem? _currentAsset;
        private Action? _updateCallback;

        private const string InternalPlaceKey = "__Internal_Place__";
        private const string InternalResizeKey = "__Internal_Resize__";
        private const string InternalCropKey = "__Internal_Crop__";

        private double _canvasWidth = 800;
        private double _canvasHeight = 240;
        private double _videoWidth = 1920;
        private double _videoHeight = 1080;

        private double _startX, _startY, _startW, _startH;
        private Rect _baseRect;
        private bool _isTextClip = false;

        private const double HandleSize = 15;
        private const double MinSize = 10;

        private PanGestureRecognizer? _clipPan;
        private PanGestureRecognizer? _tlPan;
        private PanGestureRecognizer? _trPan;
        private PanGestureRecognizer? _blPan;
        private PanGestureRecognizer? _brPan;
        private Func<Task>? _previewRefreshCallback;
        private long _lastPreviewRefreshTick;
        private int _isPreviewRefreshRunning;

        private const int PreviewRefreshThrottleMs = 120;

        public InteractableEditor()
        {
            InitializeComponent();
            InitGestures();
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

        public void Init(Action updateCallback, double videoWidth, double videoHeight)
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

        public void SetClip(projectFrameCut.DraftStuff.ClipElementUI? clip, AssetItem? asset)
        {
            _currentClip = clip;
            _currentAsset = asset;
            if (clip == null)
            {
                this.IsVisible = false;
                this.InputTransparent = true;
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

            UpdateVisuals();
            
            // 确保手势识别器在新的容器环境中正确工作
            RefreshGestureRecognizers();
        }

        private void RequestInteractivePreviewRefresh()
        {
            if (_currentClip is null)
            {
                return;
            }

            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastPreviewRefreshTick);
            if (now - last < PreviewRefreshThrottleMs)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _isPreviewRefreshRunning, 1, 0) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _lastPreviewRefreshTick, now);

            _ = RefreshInteractivePreviewCoreAsync();
        }

        private async Task RefreshInteractivePreviewCoreAsync()
        {
            try
            {
                if (_previewRefreshCallback is not null)
                {
                    await _previewRefreshCallback();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Dynamic preview refresh failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _isPreviewRefreshRunning, 0);
            }
        }

        private void InitGestures()
        {
            // Important: Do NOT recreate gesture recognizers on every frame/UI update.
            _clipPan ??= new PanGestureRecognizer();
            _tlPan ??= new PanGestureRecognizer();
            _trPan ??= new PanGestureRecognizer();
            _blPan ??= new PanGestureRecognizer();
            _brPan ??= new PanGestureRecognizer();

            _clipPan.PanUpdated += OnClipPanUpdated;
            _tlPan.PanUpdated += OnResizePanUpdated;
            _trPan.PanUpdated += OnResizePanUpdated;
            _blPan.PanUpdated += OnResizePanUpdated;
            _brPan.PanUpdated += OnResizePanUpdated;

            ClipVisual.GestureRecognizers.Clear();
            ClipVisual.GestureRecognizers.Add(_clipPan);

            HandleTL.GestureRecognizers.Clear();
            HandleTR.GestureRecognizers.Clear();
            HandleBL.GestureRecognizers.Clear();
            HandleBR.GestureRecognizers.Clear();

            HandleTL.GestureRecognizers.Add(_tlPan);
            HandleTR.GestureRecognizers.Add(_trPan);
            HandleBL.GestureRecognizers.Add(_blPan);
            HandleBR.GestureRecognizers.Add(_brPan);
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

            if (_currentClip.Effects == null) _currentClip.Effects = new Dictionary<string, IEffect>();

            double x, y, w, h;
            GetCurrentRect(out x, out y, out w, out h);

            // Clamp to keep UI stable.
            w = Math.Clamp(w, MinSize, _videoWidth);
            h = Math.Clamp(h, MinSize, _videoHeight);
            x = Math.Clamp(x, 0, _videoWidth - w);
            y = Math.Clamp(y, 0, _videoHeight - h);

            Rect renderRect = GetRenderRect();
            double scale = renderRect.Width / _videoWidth;

            double displayX = renderRect.X + x * scale;
            double displayY = renderRect.Y + y * scale;
            double displayW = w * scale;
            double displayH = h * scale;

            AbsoluteLayout.SetLayoutBounds(ClipVisual, new Rect(displayX, displayY, displayW, displayH));

            double hw = HandleSize;
            AbsoluteLayout.SetLayoutBounds(HandleTL, new Rect(displayX - hw / 2, displayY - hw / 2, hw, hw));
            AbsoluteLayout.SetLayoutBounds(HandleTR, new Rect(displayX + displayW - hw / 2, displayY - hw / 2, hw, hw));
            AbsoluteLayout.SetLayoutBounds(HandleBL, new Rect(displayX - hw / 2, displayY + displayH - hw / 2, hw, hw));
            AbsoluteLayout.SetLayoutBounds(HandleBR, new Rect(displayX + displayW - hw / 2, displayY + displayH - hw / 2, hw, hw));

            // Disable resize handles for text clips.
            bool showHandles = !_isTextClip;
            HandleTL.IsVisible = showHandles;
            HandleTR.IsVisible = showHandles;
            HandleBL.IsVisible = showHandles;
            HandleBR.IsVisible = showHandles;
        }

        private void OnClipPanUpdated(object? sender, PanUpdatedEventArgs e)
        {
            if (_currentClip == null) return;

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
                    GetCurrentRect(out var finalX, out var finalY, out _, out _);
                    System.Diagnostics.Debug.WriteLine($"[Pan] Completed: FinalPos=({finalX:F1}, {finalY:F1})");
                    _updateCallback?.Invoke();
                    break;
            }
        }

        private void OnResizePanUpdated(object? sender, PanUpdatedEventArgs e)
        {
            if (_currentClip == null) return;
            var handle = sender as BoxView;
            if (handle == null) return;

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

                    if (handle == HandleTL)
                    {
                        // Top-Left: resize from top-left corner
                        newW = Math.Max(MinSize, _startW - dx);
                        newH = Math.Max(MinSize, _startH - dy);
                        newX = _startX + (_startW - newW);
                        newY = _startY + (_startH - newH);
                    }
                    else if (handle == HandleTR)
                    {
                        // Top-Right: resize from top-right corner
                        newW = Math.Max(MinSize, _startW + dx);
                        newH = Math.Max(MinSize, _startH - dy);
                        newY = _startY + (_startH - newH);
                    }
                    else if (handle == HandleBL)
                    {
                        // Bottom-Left: resize from bottom-left corner
                        newW = Math.Max(MinSize, _startW - dx);
                        newH = Math.Max(MinSize, _startH + dy);
                        newX = _startX + (_startW - newW);
                    }
                    else if (handle == HandleBR)
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
                    GetCurrentRect(out var finalX, out var finalY, out var finalW, out var finalH);
                    System.Diagnostics.Debug.WriteLine($"[Resize] Completed: Pos=({finalX:F1}, {finalY:F1}), Size=({finalW:F1}x{finalH:F1})");
                    _updateCallback?.Invoke();
                    break;
            }
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

            if (_currentClip.Effects != null)
            {
                if (_currentClip.Effects.TryGetValue(InternalPlaceKey, out var p) && p is PlaceEffect_ImageSharp place)
                {
                    // Always work in current video coordinate space
                    int relW = place.RelativeWidth > 0 ? place.RelativeWidth : (int)_videoWidth;
                    int relH = place.RelativeHeight > 0 ? place.RelativeHeight : (int)_videoHeight;
                    
                    // Convert from relative coordinates to current video coordinates
                    x = (double)place.StartX * _videoWidth / relW;
                    y = (double)place.StartY * _videoHeight / relH;
                }

                // For size, prefer internal Resize (scale) over internal Crop (clip). We still fallback to Crop for legacy data.
                if (_currentClip.Effects.TryGetValue(InternalResizeKey, out var r) && r is ResizeEffect_ImageSharp resize)
                {
                    int relW = resize.RelativeWidth > 0 ? resize.RelativeWidth : (int)_videoWidth;
                    int relH = resize.RelativeHeight > 0 ? resize.RelativeHeight : (int)_videoHeight;
                    
                    // Convert from relative coordinates to current video coordinates
                    w = (double)resize.Width * _videoWidth / relW;
                    h = (double)resize.Height * _videoHeight / relH;
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
            if (_currentClip.Effects == null) _currentClip.Effects = new Dictionary<string, IEffect>();

            // Clamp in video coordinate space.
            w = Math.Clamp(w, MinSize, _videoWidth);
            h = Math.Clamp(h, MinSize, _videoHeight);
            x = Math.Clamp(x, 0, _videoWidth - w);
            y = Math.Clamp(y, 0, _videoHeight - h);

            int relW = (int)Math.Round(_videoWidth);
            int relH = (int)Math.Round(_videoHeight);

            // Place - Always store in current video coordinate space
            if (_currentClip.Effects.TryGetValue(InternalPlaceKey, out var p) && p is PlaceEffect_ImageSharp place)
            {
                relW = place.RelativeWidth > 0 ? place.RelativeWidth : relW;
                relH = place.RelativeHeight > 0 ? place.RelativeHeight : relH;

                _currentClip.Effects["__Internal_Place__"] = new PlaceEffect_ImageSharp
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
                _currentClip.Effects[InternalPlaceKey] = new PlaceEffect_ImageSharp
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

        /// <summary>
        /// 强制更新手势识别器以避免与父容器的手势冲突
        /// </summary>
        public void RefreshGestureRecognizers()
        {
            // 临时移除手势识别器
            ClipVisual.GestureRecognizers.Clear();
            HandleTL.GestureRecognizers.Clear();
            HandleTR.GestureRecognizers.Clear();
            HandleBL.GestureRecognizers.Clear();
            HandleBR.GestureRecognizers.Clear();
            
            // 重新初始化
            InitGestures();
        }
    }
}