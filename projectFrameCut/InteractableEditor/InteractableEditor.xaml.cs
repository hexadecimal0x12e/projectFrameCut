using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.Render;
using projectFrameCut.VideoMakeEngine;
using projectFrameCut.Shared;
using System.Collections.Concurrent;
using System.Linq;

using System.Text.Json;
using projectFrameCut.DraftStuff;

namespace InteractableEditor
{
    public partial class InteractableEditor : ContentView
    {
        private ClipElementUI? _currentClip;
        private Action? _updateCallback;

        private double _canvasWidth = 800;
        private double _canvasHeight = 240;
        private double _videoWidth = 1920;
        private double _videoHeight = 1080;

        private double _startX, _startY, _startW, _startH;
        private Rect _baseRect;
        private bool _isTextClip = false;

        public InteractableEditor()
        {
            InitializeComponent();
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
                List<TextClip.TextClipEntry>? entries = null;
                if (entriesObj is List<TextClip.TextClipEntry> list)
                {
                    entries = list;
                }
                else if (entriesObj is JsonElement je)
                {
                    try
                    {
                        entries = JsonSerializer.Deserialize<List<TextClip.TextClipEntry>>(je);
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

                    _baseRect = new Rect(entry.x + 50, entry.y + 50, w + 50, h);
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
        }

        private Rect GetRenderRect()
        {
            if (_canvasHeight == 0 || _videoHeight == 0) return new Rect(0, 0, _canvasWidth, _canvasHeight);

            double ratioCanvas = _canvasWidth / _canvasHeight;
            double ratioVideo = _videoWidth / _videoHeight;

            double drawW, drawH, offX, offY;

            // AspectFit logic:
            if (ratioVideo > ratioCanvas)
            {
                // Video is wider than canvas (relative to aspect ratio), so it fits by Width?
                // No, wait.
                // Example: Canvas 100x100 (1:1), Video 200x100 (2:1).
                // Video is wider. 2 > 1.
                // To fit 200x100 into 100x100:
                // Scale = 100/200 = 0.5.
                // DrawW = 100. DrawH = 50.
                // Fits by Width.

                // Example: Canvas 800x240 (3.33), Video 1920x1080 (1.77).
                // Video is 1.77. Canvas is 3.33.
                // RatioVideo (1.77) < RatioCanvas (3.33).
                // Video is NARROWER than canvas.
                // To fit 1920x1080 into 800x240:
                // Scale based on Height: 240/1080 = 0.222.
                // DrawH = 240. DrawW = 1920 * 0.222 = 426.6.
                // Fits by Height.

                // So:
                // If RatioVideo > RatioCanvas: Fits by Width.
                // If RatioVideo < RatioCanvas: Fits by Height.
            }

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

            double x = 0, y = 0, w = _baseRect.Width, h = _baseRect.Height;

            if (_currentClip.Effects == null) _currentClip.Effects = new Dictionary<string, IEffect>();

            if (!_currentClip.Effects.TryGetValue("__Internal_Place__", out var p) || !(p is PlaceEffect))
            {
                _currentClip.Effects["__Internal_Place__"] = new PlaceEffect { StartX = (int)x, StartY = (int)y, Enabled = true, Name = "__Internal_Place__", Index = int.MinValue + 1 };
            }

            if (_currentClip.Effects.TryGetValue("__Internal_Place__", out p) && p is PlaceEffect place)
            {
                x = place.StartX;
                y = place.StartY;
            }

            if (!_isTextClip)
            {
                bool resetCrop = false;
                if (!_currentClip.Effects.TryGetValue("__Internal_Crop__", out var c) || !(c is CropEffect))
                {
                    resetCrop = true;
                }
                else if (c is CropEffect existingCrop)
                {
                    if (!existingCrop.Enabled || (existingCrop.Width == 0 && existingCrop.Height == 0))
                    {
                        resetCrop = true;
                    }
                }

                if (resetCrop)
                {
                    _currentClip.Effects["__Internal_Crop__"] = new CropEffect { StartX = 0, StartY = 0, Width = (int)w, Height = (int)h, Enabled = true, Name = "__Internal_Crop__", Index = int.MinValue };
                }

                if (_currentClip.Effects.TryGetValue("__Internal_Crop__", out var c1) && c1 is CropEffect crop)
                {
                    w = crop.Width;
                    h = crop.Height;
                }
            }

            Rect renderRect = GetRenderRect();
            double scale = renderRect.Width / _videoWidth;

            double displayX = renderRect.X + x * scale;
            double displayY = renderRect.Y + y * scale;
            double displayW = w * scale;
            double displayH = h * scale;

            AbsoluteLayout.SetLayoutBounds(ClipVisual, new Rect(displayX, displayY, displayW, displayH));

            double hw = 15;
            AbsoluteLayout.SetLayoutBounds(HandleTL, new Rect(displayX - hw / 2, displayY - hw / 2, hw, hw));
            AbsoluteLayout.SetLayoutBounds(HandleTR, new Rect(displayX + displayW - hw / 2, displayY - hw / 2, hw, hw));
            AbsoluteLayout.SetLayoutBounds(HandleBL, new Rect(displayX - hw / 2, displayY + displayH - hw / 2, hw, hw));
            AbsoluteLayout.SetLayoutBounds(HandleBR, new Rect(displayX + displayW - hw / 2, displayY + displayH - hw / 2, hw, hw));

            PanGestureRecognizer tlGesture = new(), trGesture = new(), blGesture = new(), brGesture = new(), clipGesture = new();
            HandleTL.GestureRecognizers.Clear();
            HandleTR.GestureRecognizers.Clear();
            HandleBL.GestureRecognizers.Clear();
            HandleBR.GestureRecognizers.Clear();
            tlGesture.PanUpdated += OnResizePanUpdated;
            trGesture.PanUpdated += OnResizePanUpdated;
            blGesture.PanUpdated += OnResizePanUpdated;
            brGesture.PanUpdated += OnResizePanUpdated;

            HandleTL.GestureRecognizers.Add(tlGesture);
            HandleTR.GestureRecognizers.Add(trGesture);
            HandleBL.GestureRecognizers.Add(blGesture);
            HandleBR.GestureRecognizers.Add(brGesture);

            ClipVisual.GestureRecognizers.Clear();
            clipGesture.PanUpdated += OnClipPanUpdated;
            ClipVisual.GestureRecognizers.Add(clipGesture);
        }

        private void OnClipPanUpdated(object sender, PanUpdatedEventArgs e)
        {
            if (_currentClip == null) return;

            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    GetCurrentRect(out _startX, out _startY, out _startW, out _startH);
                    break;
                case GestureStatus.Running:
                    Rect renderRect = GetRenderRect();
                    double scale = renderRect.Width / _videoWidth;

                    double newVisualX = _startX + e.TotalX / scale;
                    double newVisualY = _startY + e.TotalY / scale;

                    UpdateClipEffects(newVisualX, newVisualY, _startW, _startH);
                    UpdateVisuals();
                    break;
                case GestureStatus.Completed:
                    _updateCallback?.Invoke();
                    break;
            }
        }

        private void OnResizePanUpdated(object sender, PanUpdatedEventArgs e)
        {
            if (_currentClip == null) return;
            var handle = sender as BoxView;
            if (handle == null) return;

            if (_isTextClip) return;  //can't resize a TextClip, it'll cause a serious problem with mixturing

            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    GetCurrentRect(out _startX, out _startY, out _startW, out _startH);
                    break;
                case GestureStatus.Running:
                    Rect renderRect = GetRenderRect();
                    double scale = renderRect.Width / _videoWidth;

                    double dx = e.TotalX / scale;
                    double dy = e.TotalY / scale;

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
            x = 0; y = 0; w = _baseRect.Width; h = _baseRect.Height;

            if (_currentClip?.Effects != null)
            {
                if (_currentClip.Effects.TryGetValue("__Internal_Place__", out var p) && p is PlaceEffect place)
                {
                    x = place.StartX;
                    y = place.StartY;
                }
                if (_currentClip.Effects.TryGetValue("__Internal_Crop__", out var c) && c is CropEffect crop)
                {
                    w = crop.Width;
                    h = crop.Height;
                }
            }
        }

        private void UpdateClipEffects(double x, double y, double w, double h)
        {
            if (_currentClip == null) return;
            if (_currentClip.Effects == null) _currentClip.Effects = new Dictionary<string, IEffect>();

            // Place
            if (_currentClip.Effects.TryGetValue("__Internal_Place__", out var p) && p is PlaceEffect place)
            {
                _currentClip.Effects["__Internal_Place__"] = new PlaceEffect { StartX = (int)x, StartY = (int)y, Enabled = place.Enabled, Index = place.Index };
            }
            else
            {
                _currentClip.Effects["__Internal_Place__"] = new PlaceEffect { StartX = (int)x, StartY = (int)y };
            }

            if (!_isTextClip)
            {
                // Crop
                int cropX = 0, cropY = 0;
                if (_currentClip.Effects.TryGetValue("__Internal_Crop__", out var c) && c is CropEffect crop)
                {
                    cropX = crop.StartX;
                    cropY = crop.StartY;
                    _currentClip.Effects["__Internal_Crop__"] = new CropEffect { StartX = cropX, StartY = cropY, Width = (int)w, Height = (int)h, Enabled = crop.Enabled, Index = crop.Index };
                }
                else
                {
                    _currentClip.Effects["__Internal_Crop__"] = new CropEffect { StartX = 0, StartY = 0, Width = (int)w, Height = (int)h };
                }
            }
        }
    }
}