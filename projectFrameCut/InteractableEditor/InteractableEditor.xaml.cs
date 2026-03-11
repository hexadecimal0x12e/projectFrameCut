using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.Shared;
using System.Collections.Concurrent;
using System.Linq;

using System.Text.Json;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Effect;

namespace InteractableEditor
{
    public partial class InteractableEditor : ContentView
    {
        private ClipElementUI? _currentClip;
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

        public InteractableEditor()
        {
            InitializeComponent();
            InitGestures();
        }

        protected override void OnSizeAllocated(double width, double height)
        {
            base.OnSizeAllocated(width, height);
            
            // 在 MultiWindowItem 中需要考虑容器的布局影响
            var (offsetX, offsetY) = GetContainerOffset();
            double adjustedHeight = height;
            
            // 如果在 MultiWindowItem 中，需要减去标题栏的高度影响
            if (offsetY > 0)
            {
                // 这里不需要调整 height，因为 OnSizeAllocated 给出的已经是内容区域的尺寸
            }
            
            UpdateCanvasSize(width, adjustedHeight);
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

        public void SetClip(ClipElementUI? clip, AssetItem? asset)
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
                    // 调试输出初始位置
                    System.Diagnostics.Debug.WriteLine($"Pan Started: StartX={_startX}, StartY={_startY}");
                    break;
                case GestureStatus.Running:
                    Rect renderRect = GetRenderRect();
                    double scale = renderRect.Width / _videoWidth;

                    // 简化坐标计算，直接使用手势的相对偏移
                    double newVisualX = _startX + e.TotalX / scale;
                    double newVisualY = _startY + e.TotalY / scale;
                    
                    // 调试信息（可以在发布版本中删除）
                    System.Diagnostics.Debug.WriteLine($"Pan Running: TotalX={e.TotalX}, TotalY={e.TotalY}, Scale={scale}, NewX={newVisualX}, NewY={newVisualY}");

                    if (_isTextClip)
                    {
                        UpdateTextEntryPosition(newVisualX, newVisualY);
                    }
                    else
                    {
                        UpdateClipEffects(newVisualX, newVisualY, _startW, _startH);
                    }
                    UpdateVisuals();
                    break;
                case GestureStatus.Completed:
                    _updateCallback?.Invoke();
                    break;
            }
        }

        private void OnResizePanUpdated(object? sender, PanUpdatedEventArgs e)
        {
            if (_currentClip == null) return;
            var handle = sender as BoxView;
            if (handle == null) return;

            if (_isTextClip) return;  //can't resize a TextClip, it'll cause a serious problem with mixturing

            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    GetCurrentRect(out _startX, out _startY, out _startW, out _startH);
                    System.Diagnostics.Debug.WriteLine($"Resize Started: StartX={_startX}, StartY={_startY}, StartW={_startW}, StartH={_startH}");
                    break;
                case GestureStatus.Running:
                    Rect renderRect = GetRenderRect();
                    double scale = renderRect.Width / _videoWidth;

                    // 简化坐标计算，直接使用手势的相对偏移
                    double dx = e.TotalX / scale;
                    double dy = e.TotalY / scale;
                    
                    // 调试信息（可以在发布版本中删除）
                    System.Diagnostics.Debug.WriteLine($"Resize Running: TotalX={e.TotalX}, TotalY={e.TotalY}, Scale={scale}, DX={dx}, DY={dy}");

                    double newX = _startX, newY = _startY, newW = _startW, newH = _startH;

                    if (handle == HandleTL)
                    {
                        newW = Math.Max(10, _startW - dx);
                        newH = Math.Max(10, _startH - dy);
                        newX = _startX + (_startW - newW);
                        newY = _startY + (_startH - newH);
                    }
                    else if (handle == HandleTR)
                    {
                        newW = Math.Max(10, _startW + dx);
                        newH = Math.Max(10, _startH - dy);
                        newY = _startY + (_startH - newH);
                    }
                    else if (handle == HandleBL)
                    {
                        newW = Math.Max(10, _startW - dx);
                        newH = Math.Max(10, _startH + dy);
                        newX = _startX + (_startW - newW);
                    }
                    else if (handle == HandleBR)
                    {
                        newW = Math.Max(10, _startW + dx);
                        newH = Math.Max(10, _startH + dy);
                    }

                    UpdateClipEffects(newX, newY, newW, newH);
                    UpdateVisuals();
                    break;
                case GestureStatus.Completed:
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
                    x = place.StartX;
                    y = place.StartY;
                    if (place.RelativeWidth > 0 && place.RelativeHeight > 0)
                    {
                        x = (double)place.StartX * _videoWidth / place.RelativeWidth;
                        y = (double)place.StartY * _videoHeight / place.RelativeHeight;
                    }
                }

                // For size, prefer internal Resize (scale) over internal Crop (clip). We still fallback to Crop for legacy data.
                if (_currentClip.Effects.TryGetValue(InternalResizeKey, out var r) && r is ResizeEffect_ImageSharp resize)
                {
                    if (resize.Width > 0) w = resize.Width;
                    if (resize.Height > 0) h = resize.Height;
                    if (resize.RelativeWidth > 0 && resize.RelativeHeight > 0)
                    {
                        w = (double)resize.Width * _videoWidth / resize.RelativeWidth;
                        h = (double)resize.Height * _videoHeight / resize.RelativeHeight;
                    }
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

            // Place
            if (_currentClip.Effects.TryGetValue(InternalPlaceKey, out var p) && p is PlaceEffect_ImageSharp place)
            {
                int newX = (int)Math.Round(x);
                int newY = (int)Math.Round(y);

                // Normalize to relative coordinate space (current video resolution)
                if (place.RelativeWidth > 0 && place.RelativeHeight > 0)
                {
                    newX = (int)Math.Round(x * place.RelativeWidth / _videoWidth);
                    newY = (int)Math.Round(y * place.RelativeHeight / _videoHeight);
                    relW = place.RelativeWidth;
                    relH = place.RelativeHeight;
                }

                _currentClip.Effects["__Internal_Place__"] = new PlaceEffect_ImageSharp
                {
                    StartX = newX,
                    StartY = newY,
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
                    Index = int.MaxValue -100,
                    Name = InternalPlaceKey,
                    RelativeWidth = relW,
                    RelativeHeight = relH
                };
            }

            if (!_isTextClip)
            {
                if (_currentClip.Effects.TryGetValue(InternalResizeKey, out var r) && r is ResizeEffect_ImageSharp resize)
                {
                    int newW = (int)Math.Round(w, MidpointRounding.AwayFromZero);
                    int newH = (int)Math.Round(h, MidpointRounding.AwayFromZero);

                    int resizeRelW = relW;
                    int resizeRelH = relH;
                    if (resize.RelativeWidth > 0 && resize.RelativeHeight > 0)
                    {
                        newW = (int)Math.Round(w * resize.RelativeWidth / _videoWidth, MidpointRounding.AwayFromZero);
                        newH = (int)Math.Round(h * resize.RelativeHeight / _videoHeight, MidpointRounding.AwayFromZero);
                        resizeRelW = resize.RelativeWidth;
                        resizeRelH = resize.RelativeHeight;
                    }

                    _currentClip.Effects[InternalResizeKey] = new ResizeEffect_ImageSharp
                    {
                        Width = newW,
                        Height = newH,
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
        /// 获取相对于父容器的坐标偏移量，用于处理在 MultiWindowItem 中的坐标转换
        /// </summary>
        private (double offsetX, double offsetY) GetContainerOffset()
        {
            try
            {
                // 检查是否在 MultiWindowItem 中
                var parent = this.Parent;
                while (parent != null)
                {
                    if (parent.GetType().Name == "MultiWindowItem")
                    {
                        // 通过实际测量获取更准确的偏移
                        return GetActualContainerOffset(parent as View);
                    }
                    parent = (parent as Element)?.Parent;
                }
                return (0, 0);
            }
            catch
            {
                return (0, 0);
            }
        }
        
        /// <summary>
        /// 通过实际测量获取容器偏移
        /// </summary>
        private (double offsetX, double offsetY) GetActualContainerOffset(View? container)
        {
            if (container == null) return (0, 0);
            
            try
            {
                // MultiWindowItem 的布局结构：Grid(Padding=5) -> Border -> Grid(RowDefinitions="32, *")
                // 标题栏在第0行，内容在第1行
                // 所以实际偏移应该包括：外层Grid的Padding(5) + 标题栏高度(32) + Border的边框等
                
                // 更保守的估算：5(padding) + 32(title) + 1(border) + 一些额外的间距
                return (5, 38);
            }
            catch
            {
                return (0, 32); // 回退到之前的估计值
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
        
        /// <summary>
        /// 调试方法：获取当前控件在屏幕上的实际位置
        /// </summary>
        public (double x, double y) GetScreenPosition()
        {
            try
            {
                // 使用 MAUI 的实际可用属性
                System.Diagnostics.Debug.WriteLine($"InteractableEditor - X: {this.X}, Y: {this.Y}, Width: {this.Width}, Height: {this.Height}");
                
                // 尝试获取相对于窗口的位置
                var parent = this.Parent;
                double totalOffsetX = this.X;
                double totalOffsetY = this.Y;
                
                while (parent != null)
                {
                    if (parent is View view)
                    {
                        System.Diagnostics.Debug.WriteLine($"Parent {parent.GetType().Name} - X: {view.X}, Y: {view.Y}, Width: {view.Width}, Height: {view.Height}");
                        
                        totalOffsetX += view.X;
                        totalOffsetY += view.Y;
                        
                        if (parent.GetType().Name == "MultiWindowItem")
                        {
                            // 找到了 MultiWindowItem，记录其位置信息
                            System.Diagnostics.Debug.WriteLine($"Found MultiWindowItem at: X={view.X}, Y={view.Y}, W={view.Width}, H={view.Height}");
                            break;
                        }
                    }
                    parent = (parent as Element)?.Parent;
                }
                
                System.Diagnostics.Debug.WriteLine($"Total calculated offset: X={totalOffsetX}, Y={totalOffsetY}");
                return (totalOffsetX, totalOffsetY);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetScreenPosition error: {ex.Message}");
                return (0, 0);
            }
        }
    }
}