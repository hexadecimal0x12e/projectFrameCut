using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.Render;
using projectFrameCut.VideoMakeEngine;
using projectFrameCut.Shared;
using System.Collections.Concurrent;
using System.Linq;

using System.Text.Json;

namespace projectFrameCut.DraftStuff.InteractableEditor;

public partial class InteractableEditor : ContentView
{
    private ClipElementUI? _currentClip;
    private Action? _updateCallback;

    private double _canvasWidth = 800;
    private double _canvasHeight = 240;
    private double _videoWidth = 1920;
    private double _videoHeight = 1080;

    // State for dragging
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
            return;
        }
        this.IsVisible = true;
        
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
             // Fit Width
             drawW = _canvasWidth;
             drawH = drawW / ratioVideo;
             offX = 0;
             offY = (_canvasHeight - drawH) / 2;
        }
        else
        {
             // Fit Height
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

        var effects = _currentClip.Effects?.Values ?? Enumerable.Empty<IEffect>();

        var place = effects.OfType<PlaceEffect>().FirstOrDefault();
        if (place != null)
        {
            x = place.StartX;
            y = place.StartY;
        }

        var resize = effects.OfType<ResizeEffect>().FirstOrDefault();
        if (resize != null)
        {
            // ResizeEffect scales the *entire* source.
            // If source is Video (1920x1080), Resize(960x540) means 0.5x scale.
            // If source is Text (1920x1080 transparent), Resize(960x540) means 0.5x scale.
            // So the visual rect of the text should also be scaled by (ResizeW / SourceW).
            
            double scaleX = resize.Width / _videoWidth;
            double scaleY = resize.Height / _videoHeight;
            
            w = _baseRect.Width * scaleX;
            h = _baseRect.Height * scaleY;
            
            // Position is also affected if we consider the base rect is relative to the source top-left.
            // _baseRect.X is the text position in the source.
            // After resize, it moves to _baseRect.X * scaleX.
            // Then PlaceEffect adds offset.
            
            // Wait, 'x' and 'y' above are from PlaceEffect.
            // The final visual position is:
            // (BaseX * ScaleX) + PlaceX
            
            // But 'x' variable currently holds PlaceX.
            // So let's calculate the visual X/Y relative to the canvas (before mapping to screen).
            
            // Let's redefine x,y to be the top-left of the visual box in Canvas coordinates.
            x = (_baseRect.X * scaleX) + place?.StartX ?? 0;
            y = (_baseRect.Y * scaleY) + place?.StartY ?? 0;
        }
        else
        {
            // No resize, scale is 1.
            x = _baseRect.X + (place?.StartX ?? 0);
            y = _baseRect.Y + (place?.StartY ?? 0);
            
            var crop = effects.OfType<CropEffect>().FirstOrDefault();
            if (crop != null)
            {
                // Crop is tricky. It changes the source size.
                // For now, let's assume Crop just changes the visible area but if we are tracking "the text",
                // and the text is cropped out, we might still show the box?
                // Or maybe Crop changes the coordinate system?
                // Usually Crop happens before Resize/Place? Or after?
                // In Effects.cs, the order depends on the list.
                // But here we are just visualizing.
                // Let's assume standard order or just ignore Crop for text box sizing for now if it's complex.
                // If Crop is used, w/h might be the crop size.
                w = crop.Width;
                h = crop.Height;
                // If it's text, this might be wrong if we want to show text bounds.
                // But if user cropped the text layer, maybe they want to see the crop rect?
                // Let's stick to _baseRect for Text if no resize.
                if (_isTextClip)
                {
                     w = _baseRect.Width;
                     h = _baseRect.Height;
                }
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

                // Calculate new visual position in Canvas coordinates
                double newVisualX = _startX + e.TotalX / scale;
                double newVisualY = _startY + e.TotalY / scale;

                // We need to map this back to Effects.
                // VisualX = (BaseX * ScaleX) + PlaceX
                // PlaceX = VisualX - (BaseX * ScaleX)
                
                // Determine current ScaleX/Y from ResizeEffect (or 1.0)
                double currentScaleX = 1.0;
                double currentScaleY = 1.0;
                var resize = _currentClip.Effects?.Values.OfType<ResizeEffect>().FirstOrDefault();
                if (resize != null)
                {
                    currentScaleX = (double)resize.Width / _videoWidth;
                    currentScaleY = (double)resize.Height / _videoHeight;
                }

                double newPlaceX = newVisualX - (_baseRect.X * currentScaleX);
                double newPlaceY = newVisualY - (_baseRect.Y * currentScaleY);

                // UpdateClipEffects expects the "Rect" parameters (x,y,w,h).
                // But wait, UpdateClipEffects uses x,y for Place.StartX/Y directly.
                // So we should pass newPlaceX, newPlaceY.
                // And for w,h, we pass the *Resize* target width/height?
                // UpdateClipEffects implementation:
                // place = new PlaceEffect { StartX = (int)x, StartY = (int)y };
                // resize = new ResizeEffect { Width = (int)w, Height = (int)h ... };
                
                // So we should pass:
                // x = newPlaceX
                // y = newPlaceY
                // w = resize?.Width ?? _videoWidth (unchanged)
                // h = resize?.Height ?? _videoHeight (unchanged)
                
                double currentResizeW = resize?.Width ?? _videoWidth;
                double currentResizeH = resize?.Height ?? _videoHeight;

                UpdateClipEffects(newPlaceX, newPlaceY, currentResizeW, currentResizeH);
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

                // _startX, _startY, _startW, _startH are the VISUAL bounds in Canvas coords.
                double newVisualX = _startX, newVisualY = _startY, newVisualW = _startW, newVisualH = _startH;

                if (handle == HandleTL)
                {
                    newVisualX += dx;
                    newVisualY += dy;
                    newVisualW -= dx;
                    newVisualH -= dy;
                }
                else if (handle == HandleTR)
                {
                    newVisualY += dy;
                    newVisualW += dx;
                    newVisualH -= dy;
                }
                else if (handle == HandleBL)
                {
                    newVisualX += dx;
                    newVisualW -= dx;
                    newVisualH += dy;
                }
                else if (handle == HandleBR)
                {
                    newVisualW += dx;
                    newVisualH += dy;
                }

                if (newVisualW < 10) newVisualW = 10;
                if (newVisualH < 10) newVisualH = 10;

                // Now map back to Effects.
                // VisualW = BaseW * ScaleX
                // ScaleX = VisualW / BaseW
                // ResizeW = ScaleX * VideoWidth
                
                double newScaleX = newVisualW / _baseRect.Width;
                double newScaleY = newVisualH / _baseRect.Height;
                
                double newResizeW = newScaleX * _videoWidth;
                double newResizeH = newScaleY * _videoHeight;
                
                // PlaceX = VisualX - (BaseX * ScaleX)
                double newPlaceX = newVisualX - (_baseRect.X * newScaleX);
                double newPlaceY = newVisualY - (_baseRect.Y * newScaleY);

                UpdateClipEffects(newPlaceX, newPlaceY, newResizeW, newResizeH);
                UpdateVisuals();
                break;
            case GestureStatus.Completed:
                _updateCallback?.Invoke();
                break;
        }
    }

    private void GetCurrentRect(out double x, out double y, out double w, out double h)
    {
        // Return the current VISUAL bounds in Canvas coordinates.
        // This matches the logic in UpdateVisuals.
        
        var effects = _currentClip?.Effects?.Values ?? Enumerable.Empty<IEffect>();
        var place = effects.OfType<PlaceEffect>().FirstOrDefault();
        var resize = effects.OfType<ResizeEffect>().FirstOrDefault();
        
        double scaleX = 1.0, scaleY = 1.0;
        if (resize != null)
        {
            scaleX = (double)resize.Width / _videoWidth;
            scaleY = (double)resize.Height / _videoHeight;
        }
        
        double placeX = place?.StartX ?? 0;
        double placeY = place?.StartY ?? 0;
        
        x = (_baseRect.X * scaleX) + placeX;
        y = (_baseRect.Y * scaleY) + placeY;
        w = _baseRect.Width * scaleX;
        h = _baseRect.Height * scaleY;
    }

    private void UpdateClipEffects(double x, double y, double w, double h)
    {
        if (_currentClip == null) return;
        if (_currentClip.Effects == null) _currentClip.Effects = new Dictionary<string, IEffect>();

        // Update Place
        var place = _currentClip.Effects.Values.OfType<PlaceEffect>().FirstOrDefault();
        if (place == null)
        {
            place = new PlaceEffect { StartX = (int)x, StartY = (int)y };
            _currentClip.Effects["Place"] = place;
        }
        else
        {
            _currentClip.Effects["Place"] = new PlaceEffect { StartX = (int)x, StartY = (int)y, Enabled = place.Enabled, Index = place.Index };
        }

        // Update Resize
        var resize = _currentClip.Effects.Values.OfType<ResizeEffect>().FirstOrDefault();
        if (resize == null)
        {
            resize = new ResizeEffect { Width = (int)w, Height = (int)h, PreserveAspectRatio = false };
            _currentClip.Effects["Resize"] = resize;
        }
        else
        {
            _currentClip.Effects["Resize"] = new ResizeEffect { Width = (int)w, Height = (int)h, PreserveAspectRatio = false, Enabled = resize.Enabled, Index = resize.Index };
        }
    }
}