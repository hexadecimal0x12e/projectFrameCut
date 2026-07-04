using projectFrameCut.Drawing.Vector;
using Color = Microsoft.Maui.Graphics.Color;
using PathF = Microsoft.Maui.Graphics.PathF;
using PointF = Microsoft.Maui.Graphics.PointF;
using RectF = Microsoft.Maui.Graphics.RectF;

namespace projectFrameCut.DraftStuff;

/// <summary>
/// Draws <see cref="VectorCanvasElement"/>s directly onto a <see cref="GraphicsView"/>
/// canvas, converting normalized-coordinate segments to screen-space primitives.
/// Supports all segment types, element rotation, uniform scaling, and layer ordering.
/// </summary>
public class VectorPreviewDrawable : IDrawable
{
    /// <summary>Elements to draw for the current frame.</summary>
    public List<VectorCanvasElement>? Elements { get; set; }

    /// <summary>Project canvas width for preview letterboxing.</summary>
    public int CanvasWidth { get; set; } = 320;

    /// <summary>Project canvas height for preview letterboxing.</summary>
    public int CanvasHeight { get; set; } = 240;

    /// <summary>Clip target X within the project canvas.</summary>
    public float ContentX { get; set; }

    /// <summary>Clip target Y within the project canvas.</summary>
    public float ContentY { get; set; }

    /// <summary>Clip target width within the project canvas.</summary>
    public float ContentWidth { get; set; } = 320;

    /// <summary>Clip target height within the project canvas.</summary>
    public float ContentHeight { get; set; } = 240;

    /// <summary>Whether to paint the dark canvas background before drawing elements.</summary>
    public bool DrawBackground { get; set; }

    /// <summary>Whether to paint the grid overlay.</summary>
    public bool DrawGrid { get; set; }

    /// <summary>
    /// When true, treat <see cref="ViewportX"/>, <see cref="ViewportY"/>,
    /// <see cref="ViewportWidth"/> and <see cref="ViewportHeight"/> as a crop
    /// window in project space and map that window to the full view.
    /// </summary>
    public bool UseViewportCrop { get; set; }

    public float ViewportX { get; set; }
    public float ViewportY { get; set; }
    public float ViewportWidth { get; set; } = 320;
    public float ViewportHeight { get; set; } = 240;

    // ── Static colours ──────────────────────────────────────
    private static readonly Color CanvasBg = Color.FromArgb("#111111");
    private static readonly Color GridColor = Color.FromArgb("#252525");

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        canvas.Antialias = true;

        float viewW = dirtyRect.Width;
        float viewH = dirtyRect.Height;
        if (viewW <= 0 || viewH <= 0) return;

        if (DrawBackground)
        {
            canvas.FillColor = CanvasBg;
            canvas.FillRectangle(0, 0, viewW, viewH);
        }

        var elements = Elements;
        if (elements is null || elements.Count == 0) return;

        if (UseViewportCrop)
        {
            DrawViewportCrop(canvas, dirtyRect, elements);
            return;
        }

        // Compute letterbox transform
        float baseScale = MathF.Min(viewW / CanvasWidth, viewH / CanvasHeight);
        float scale = baseScale;
        float offsetX = (viewW - CanvasWidth * scale) / 2f;
        float offsetY = (viewH - CanvasHeight * scale) / 2f;
        float contentX = ContentX;
        float contentY = ContentY;
        float contentW = Math.Max(1f, ContentWidth);
        float contentH = Math.Max(1f, ContentHeight);

        if (DrawGrid)
        {
            // Draw grid (snap-size: 10% of canvas)
            DrawGridLines(canvas, dirtyRect, scale, offsetX, offsetY, CanvasWidth);
        }

        // Sort by LayerIndex for correct z-order
        var sorted = elements.OrderBy(e => e.LayerIndex);

        foreach (var element in sorted)
        {
            var segments = element.Draw();
            if (segments is null || segments.Length == 0) continue;

            // Calculate element origin transform
            float elScaleX, elScaleY, originX, originY;
            if (element.UseUniformScale)
            {
                float us = MathF.Min(contentW, contentH);
                elScaleX = us;
                elScaleY = us;
                originX = contentX + element.BaseX * contentW + element.RelativeX * us;
                originY = contentY + element.BaseY * contentH + element.RelativeY * us;
            }
            else
            {
                elScaleX = contentW;
                elScaleY = contentH;
                originX = contentX + element.RelativeX * contentW;
                originY = contentY + element.RelativeY * contentH;
            }

            float screenOriginX = originX * scale + offsetX;
            float screenOriginY = originY * scale + offsetY;
            float screenScaleX = elScaleX * scale;
            float screenScaleY = elScaleY * scale;

            bool hasRotation = MathF.Abs(element.Rotation) > 0.0001f;

            canvas.SaveState();

            // Translate to element origin on screen
            canvas.Translate(screenOriginX, screenOriginY);
            if (hasRotation)
                canvas.Rotate(element.Rotation * 180f / MathF.PI);

            foreach (var segment in segments)
                DrawSegment(canvas, segment, screenScaleX, screenScaleY);

            canvas.RestoreState();
        }
    }

    private void DrawViewportCrop(ICanvas canvas, RectF dirtyRect, IEnumerable<VectorCanvasElement> elements)
    {
        float viewW = dirtyRect.Width;
        float viewH = dirtyRect.Height;
        float viewportW = Math.Max(1f, ViewportWidth);
        float viewportH = Math.Max(1f, ViewportHeight);
        float viewportX = ViewportX;
        float viewportY = ViewportY;
        float scaleX = viewW / viewportW;
        float scaleY = viewH / viewportH;

        foreach (var element in elements.OrderBy(e => e.LayerIndex))
        {
            var segments = element.Draw();
            if (segments is null || segments.Length == 0) continue;

            float elementScaleX;
            float elementScaleY;
            float originX;
            float originY;

            if (element.UseUniformScale)
            {
                float uniform = MathF.Min(CanvasWidth, CanvasHeight);
                elementScaleX = uniform;
                elementScaleY = uniform;
                originX = element.BaseX * CanvasWidth + element.RelativeX * uniform;
                originY = element.BaseY * CanvasHeight + element.RelativeY * uniform;
            }
            else
            {
                elementScaleX = CanvasWidth;
                elementScaleY = CanvasHeight;
                originX = element.RelativeX * CanvasWidth;
                originY = element.RelativeY * CanvasHeight;
            }

            float screenOriginX = (originX - viewportX) * scaleX;
            float screenOriginY = (originY - viewportY) * scaleY;
            float screenScaleX = elementScaleX * scaleX;
            float screenScaleY = elementScaleY * scaleY;

            canvas.SaveState();
            canvas.Translate(screenOriginX, screenOriginY);
            if (MathF.Abs(element.Rotation) > 0.0001f)
            {
                canvas.Rotate(element.Rotation * 180f / MathF.PI);
            }

            foreach (var segment in segments)
            {
                DrawSegment(canvas, segment, screenScaleX, screenScaleY);
            }

            canvas.RestoreState();
        }
    }

    private static void DrawGridLines(ICanvas canvas, RectF dirtyRect,
        float scale, float offsetX, float offsetY, int canvasWidth)
    {
        float step = canvasWidth * scale * 0.1f; // 10% grid
        if (step < 8f) step = 8f; // Don't draw too-dense grid

        canvas.StrokeColor = GridColor;
        canvas.StrokeSize = 0.5f;
        canvas.StrokeLineCap = LineCap.Butt;

        for (float x = offsetX; x < dirtyRect.Width - offsetX + 1; x += step)
            canvas.DrawLine(x, 0, x, dirtyRect.Height);
        for (float y = offsetY; y < dirtyRect.Height - offsetY + 1; y += step)
            canvas.DrawLine(0, y, dirtyRect.Width, y);
    }

    // ═══════════════════════════════════════════════════════════
    // Segment dispatch
    // ═══════════════════════════════════════════════════════════

    private static void DrawSegment(ICanvas canvas, VectorSegment segment,
        float scaleX, float scaleY)
    {
        // Stroke thickness scales with the canvas
        float thicknessScale = MathF.Min(scaleX, scaleY);
        float drawThick = MathF.Max(segment.Thickness * thicknessScale, 1f);

        bool hasFill = segment.FillA > 0;
        bool hasStroke = segment.Thickness > 0 && segment.StrokeA > 0;

        Color fillColor = default, strokeColor = default;
        if (hasFill)
            fillColor = Color.FromRgba(
                segment.FillR / 65535f, segment.FillG / 65535f,
                segment.FillB / 65535f, segment.FillA);
        if (hasStroke)
            strokeColor = Color.FromRgba(
                segment.StrokeR / 65535f, segment.StrokeG / 65535f,
                segment.StrokeB / 65535f, segment.StrokeA);

        switch (segment)
        {
            case StraightLineVectorSegment s:
                DrawLineSegment(canvas, s, scaleX, scaleY,
                    hasStroke, strokeColor, drawThick);
                break;
            case RoundedRectangleVectorSegment s:
                DrawRoundedRectSegment(canvas, s, scaleX, scaleY,
                    hasFill, fillColor, hasStroke, strokeColor, drawThick);
                break;
            case RectangleVectorSegment s:
                DrawRectSegment(canvas, s, scaleX, scaleY,
                    hasFill, fillColor, hasStroke, strokeColor, drawThick);
                break;
            case EllipseVectorSegment s:
                DrawEllipseSegment(canvas, s, scaleX, scaleY,
                    hasFill, fillColor, hasStroke, strokeColor, drawThick);
                break;
            case CubicBezierVectorSegment s:
                DrawBezierSegment(canvas, s, scaleX, scaleY,
                    hasFill, fillColor, hasStroke, strokeColor, drawThick);
                break;
            case QuadraticBezierVectorSegment s:
                DrawQuadBezierSegment(canvas, s, scaleX, scaleY,
                    hasFill, fillColor, hasStroke, strokeColor, drawThick);
                break;
            case ArcVectorSegment s:
                DrawArcSegment(canvas, s, scaleX, scaleY,
                    hasStroke, strokeColor, drawThick);
                break;
            case PolygonVectorSegment s:
                DrawPolygonSegment(canvas, s, scaleX, scaleY,
                    hasFill, fillColor, hasStroke, strokeColor, drawThick);
                break;
            case PolylineVectorSegment s:
                DrawPolylineSegment(canvas, s, scaleX, scaleY,
                    hasStroke, strokeColor, drawThick);
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Per-segment drawing helpers
    // ═══════════════════════════════════════════════════════════

    private static void DrawLineSegment(ICanvas canvas,
        StraightLineVectorSegment s, float sx, float sy,
        bool stroke, Color sc, float thick)
    {
        if (!stroke) return;
        canvas.StrokeColor = sc;
        canvas.StrokeSize = thick;
        canvas.StrokeLineCap = LineCap.Round;
        canvas.DrawLine(s.X1 * sx, s.Y1 * sy, s.X2 * sx, s.Y2 * sy);
    }

    private static void DrawRectSegment(ICanvas canvas,
        RectangleVectorSegment s, float sx, float sy,
        bool fill, Color fc, bool stroke, Color sc, float thick)
    {
        float x = s.X * sx, y = s.Y * sy, w = s.Width * sx, h = s.Height * sy;

        if (fill)
        {
            canvas.FillColor = fc;
            canvas.FillRectangle(x, y, w, h);
        }
        if (stroke)
        {
            canvas.StrokeColor = sc;
            canvas.StrokeSize = thick;
            canvas.StrokeLineJoin = LineJoin.Miter;
            canvas.DrawRectangle(x, y, w, h);
        }
    }

    private static void DrawRoundedRectSegment(ICanvas canvas,
        RoundedRectangleVectorSegment s, float sx, float sy,
        bool fill, Color fc, bool stroke, Color sc, float thick)
    {
        float x = s.X * sx, y = s.Y * sy,
              w = s.Width * sx, h = s.Height * sy;
        float r = s.CornerRadius * MathF.Min(sx, sy);

        if (fill)
        {
            canvas.FillColor = fc;
            canvas.FillRoundedRectangle(x, y, w, h, r);
        }
        if (stroke)
        {
            canvas.StrokeColor = sc;
            canvas.StrokeSize = thick;
            canvas.StrokeLineJoin = LineJoin.Round;
            canvas.DrawRoundedRectangle(x, y, w, h, r);
        }
    }

    private static void DrawEllipseSegment(ICanvas canvas,
        EllipseVectorSegment s, float sx, float sy,
        bool fill, Color fc, bool stroke, Color sc, float thick)
    {
        float rx = s.RadiusX * sx, ry = s.RadiusY * sy;
        float left = s.X * sx - rx, top = s.Y * sy - ry;
        float w = rx * 2f, h = ry * 2f;

        if (fill)
        {
            canvas.FillColor = fc;
            canvas.FillEllipse(left, top, w, h);
        }
        if (stroke)
        {
            canvas.StrokeColor = sc;
            canvas.StrokeSize = thick;
            canvas.DrawEllipse(left, top, w, h);
        }
    }

    private static void DrawBezierSegment(ICanvas canvas,
        CubicBezierVectorSegment s, float sx, float sy,
        bool fill, Color fc, bool stroke, Color sc, float thick)
    {
        var path = new PathF();
        path.MoveTo(s.X1 * sx, s.Y1 * sy);
        path.CurveTo(
            s.X2 * sx, s.Y2 * sy,
            s.X3 * sx, s.Y3 * sy,
            s.X4 * sx, s.Y4 * sy);

        ApplyPath(canvas, path, fill, fc, stroke, sc, thick, closeFigure: false, closed: false);
    }

    private static void DrawQuadBezierSegment(ICanvas canvas,
        QuadraticBezierVectorSegment s, float sx, float sy,
        bool fill, Color fc, bool stroke, Color sc, float thick)
    {
        var path = new PathF();
        path.MoveTo(s.X1 * sx, s.Y1 * sy);
        path.QuadTo(s.X2 * sx, s.Y2 * sy, s.X3 * sx, s.Y3 * sy);

        ApplyPath(canvas, path, fill, fc, stroke, sc, thick, closeFigure: false, closed: false);
    }

    private static void DrawArcSegment(ICanvas canvas,
        ArcVectorSegment s, float sx, float sy,
        bool stroke, Color sc, float thick)
    {
        if (!stroke) return;

        float cx = s.X * sx, cy = s.Y * sy;
        float rx = s.RadiusX * sx, ry = s.RadiusY * sy;
        float startAngle = s.StartAngle;
        float sweep = s.SweepAngle;

        // Approximate arc with line segments
        int steps = Math.Max(8, (int)(MathF.Abs(sweep) / 0.05f));
        float step = sweep / steps;

        var path = new PathF();
        float a = startAngle;
        float px = cx + MathF.Cos(a) * rx;
        float py = cy + MathF.Sin(a) * ry;
        path.MoveTo(px, py);

        for (int i = 1; i <= steps; i++)
        {
            a += step;
            px = cx + MathF.Cos(a) * rx;
            py = cy + MathF.Sin(a) * ry;
            path.LineTo(px, py);
        }

        canvas.StrokeColor = sc;
        canvas.StrokeSize = thick;
        canvas.StrokeLineCap = LineCap.Round;
        canvas.StrokeLineJoin = LineJoin.Round;
        canvas.DrawPath(path);
    }

    private static void DrawPolygonSegment(ICanvas canvas,
        PolygonVectorSegment s, float sx, float sy,
        bool fill, Color fc, bool stroke, Color sc, float thick)
    {
        if (s.Points is null || s.Points.Length < 3) return;

        var path = new PathF();
        path.MoveTo(s.Points[0].X * sx, s.Points[0].Y * sy);

        for (int i = 1; i < s.Points.Length; i++)
            path.LineTo(s.Points[i].X * sx, s.Points[i].Y * sy);
        path.Close();

        // Holes (NonZero winding)
        if (s.Holes is not null)
        {
            foreach (var hole in s.Holes)
            {
                if (hole is not { Length: >= 3 }) continue;
                path.MoveTo(hole[0].X * sx, hole[0].Y * sy);
                for (int i = 1; i < hole.Length; i++)
                    path.LineTo(hole[i].X * sx, hole[i].Y * sy);
                path.Close();
            }
        }

        ApplyPath(canvas, path, fill, fc, stroke, sc, thick, closeFigure: true, closed: true);
    }

    private static void DrawPolylineSegment(ICanvas canvas,
        PolylineVectorSegment s, float sx, float sy,
        bool stroke, Color sc, float thick)
    {
        if (!stroke || s.Points is null || s.Points.Length < 2) return;

        var path = new PathF();
        path.MoveTo(s.Points[0].X * sx, s.Points[0].Y * sy);

        for (int i = 1; i < s.Points.Length; i++)
            path.LineTo(s.Points[i].X * sx, s.Points[i].Y * sy);

        canvas.StrokeColor = sc;
        canvas.StrokeSize = thick;
        canvas.StrokeLineCap = LineCap.Round;
        canvas.StrokeLineJoin = LineJoin.Round;
        canvas.DrawPath(path);
    }

    // ═══════════════════════════════════════════════════════════
    // Shared path application
    // ═══════════════════════════════════════════════════════════

    private static void ApplyPath(ICanvas canvas, PathF path,
        bool fill, Color fc, bool stroke, Color sc, float thick,
        bool closeFigure, bool closed)
    {
        if (fill)
        {
            canvas.FillColor = fc;
            canvas.FillPath(path);
        }
        if (stroke)
        {
            canvas.StrokeColor = sc;
            canvas.StrokeSize = thick;
            canvas.StrokeLineCap = LineCap.Round;
            canvas.StrokeLineJoin = LineJoin.Round;
            canvas.DrawPath(path);
        }
    }
}


/// <summary>
/// Draws the keyframe timeline: time ruler, track lanes,
/// keyframe dots, connection curves, and the playhead.
/// Supports both SVG-element tracks and per-component tracks.
/// </summary>
public class TimelineDrawable : IDrawable
{
    public VectorContentEditorView? View { get; set; }

    // Layout constants
    public const float LeftMargin = 92f;
    public const float RightMargin = 12f;
    public const float RulerHeight = 22f;
    public const float TrackRowHeight = 30f;
    private const float KeyFrameRadius = 6f;
    private const float PlayheadWidth = 2f;

    // Colors
    private static readonly Color RulerBg = Color.FromArgb("#1a1a1a");
    private static readonly Color RulerText = Color.FromArgb("#9CA3AF");
    private static readonly Color RulerTick = Color.FromArgb("#555555");
    private static readonly Color TrackBgEven = Color.FromArgb("#222222");
    private static readonly Color TrackBgOdd = Color.FromArgb("#282828");
    private static readonly Color TrackLabelColor = Color.FromArgb("#9CA3AF");
    private static readonly Color KeyFrameFill = Color.FromArgb("#FFD272");
    private static readonly Color KeyFrameFillSelected = Color.FromArgb("#4A9EFF");
    private static readonly Color KeyFrameStroke = Color.FromArgb("#B0893E");
    private static readonly Color KeyFrameStrokeSelected = Color.FromArgb("#2A6ECC");
    private static readonly Color ConnLineColor = Color.FromArgb("#888888");
    private static readonly Color ConnLineSelectedColor = Color.FromArgb("#4A9EFF");
    private static readonly Color PlayheadColor = Color.FromArgb("#FFD272");
    private static readonly Color GridLineColor = Color.FromArgb("#3a3a3a");

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        if (View is null) return;

        canvas.Antialias = true;
        float width = dirtyRect.Width;
        float timelineWidth = Math.Max(1, width - LeftMargin - RightMargin);

        // 1. Draw time ruler background
        canvas.FillColor = RulerBg;
        canvas.FillRectangle(0, 0, width, RulerHeight);

        // Ruler ticks at 0%, 25%, 50%, 75%, 100%
        float[] tickPositions = { 0f, 0.25f, 0.5f, 0.75f, 1f };
        foreach (float t in tickPositions)
        {
            float x = LeftMargin + t * timelineWidth;
            canvas.StrokeColor = t == 0f || t == 1f ? RulerTick : GridLineColor;
            canvas.StrokeSize = 1;
            canvas.DrawLine(x, 0, x, RulerHeight);

            canvas.FontColor = RulerText;
            canvas.FontSize = 10;
            string label = $"{t * 100:F0}%";
            canvas.DrawString(label, x - 15, 2, 30, RulerHeight - 4,
                HorizontalAlignment.Center, VerticalAlignment.Center);
        }

        // 2. Draw grid lines extending below ruler
        foreach (float t in new[] { 0.25f, 0.5f, 0.75f })
        {
            float x = LeftMargin + t * timelineWidth;
            canvas.StrokeColor = GridLineColor.WithAlpha(0.3f);
            canvas.StrokeSize = 0.5f;
            canvas.DrawLine(x, RulerHeight, x, dirtyRect.Height);
        }

        // 3. Get active tracks (prefer component tracks if selected)
        var activeTracks = GetActiveTracks();
        float contentTop = RulerHeight + 4;

        if (activeTracks.Count == 0)
        {
            string msg = View.SelectedComponent is not null
                ? "No tracks — add one from the left panel."
                : "Select a component or SVG element, then add a track.";
            canvas.FontColor = Color.FromArgb("#555555");
            canvas.FontSize = 13;
            canvas.DrawString(msg,
                LeftMargin, contentTop + 20, timelineWidth, 30,
                HorizontalAlignment.Center, VerticalAlignment.Center);
            DrawPlayhead(canvas, 0, dirtyRect.Height, timelineWidth);
            return;
        }

        for (int i = 0; i < activeTracks.Count; i++)
        {
            float y = contentTop + i * TrackRowHeight;

            // Row background
            canvas.FillColor = i % 2 == 0 ? TrackBgEven : TrackBgOdd;
            canvas.FillRectangle(0, y, width, TrackRowHeight - 2);

            // Track label
            canvas.FontColor = TrackLabelColor;
            canvas.FontSize = 9;
            canvas.DrawString(activeTracks[i].DisplayName,
                3, y + 2, LeftMargin - 6, TrackRowHeight - 6,
                HorizontalAlignment.Left, VerticalAlignment.Center);

            // Keyframe connections and dots
            DrawTrackLane(canvas, activeTracks[i], y, timelineWidth,
                activeTracks[i] == View.SelectedTrack);
        }

        // 4. Draw playhead
        DrawPlayhead(canvas, contentTop, dirtyRect.Height - contentTop, timelineWidth);
    }

    private System.Collections.ObjectModel.ObservableCollection<AnimationTrackItem> GetActiveTracks()
    {
        if (View?.SelectedComponent?.Tracks is { Count: > 0 } compTracks)
            return compTracks;
        return View?.Tracks ?? new();
    }

    private void DrawTrackLane(ICanvas canvas, AnimationTrackItem track,
        float y, float timelineWidth, bool isSelected)
    {
        var keyframes = track.KeyFrames;
        if (keyframes.Count == 0) return;

        float centerY = y + TrackRowHeight / 2f;
        var lineColor = isSelected ? ConnLineSelectedColor : ConnLineColor;
        var dotFill = isSelected ? KeyFrameFillSelected : KeyFrameFill;
        var dotStroke = isSelected ? KeyFrameStrokeSelected : KeyFrameStroke;

        // Collect dot positions
        var positions = new PointF[keyframes.Count];
        for (int i = 0; i < keyframes.Count; i++)
        {
            float x = LeftMargin + Math.Clamp(keyframes[i].Time, 0f, 1f) * timelineWidth;
            positions[i] = new PointF(x, centerY);
        }

        // Draw connecting line
        if (positions.Length >= 2)
        {
            canvas.StrokeColor = lineColor;
            canvas.StrokeSize = 1.5f;
            canvas.StrokeDashPattern = null;
            canvas.StrokeLineCap = LineCap.Round;

            var path = new PathF();
            path.MoveTo(positions[0].X, positions[0].Y);
            for (int i = 1; i < positions.Length; i++)
            {
                float midX = (positions[i - 1].X + positions[i].X) / 2f;
                path.CurveTo(
                    midX, positions[i - 1].Y,
                    midX, positions[i].Y,
                    positions[i].X, positions[i].Y);
            }
            canvas.DrawPath(path);
        }

        // Draw keyframe dots
        var selectedKf = View?.SelectedKeyFrame;
        for (int i = 0; i < positions.Length; i++)
        {
            bool isThisSelected = selectedKf is not null
                && selectedKf == keyframes[i];

            if (isThisSelected)
            {
                canvas.FillColor = Color.FromArgb("#FFFFFF").WithAlpha(0.3f);
                canvas.FillCircle(positions[i].X, positions[i].Y, KeyFrameRadius + 5f);
            }

            canvas.FillColor = dotStroke;
            canvas.FillCircle(positions[i].X, positions[i].Y, KeyFrameRadius + 1.5f);

            canvas.FillColor = dotFill;
            canvas.FillCircle(positions[i].X, positions[i].Y, KeyFrameRadius);

            if (i == keyframes.Count - 1)
            {
                canvas.StrokeColor = dotStroke;
                canvas.StrokeSize = 1.5f;
                canvas.DrawCircle(positions[i].X, positions[i].Y, KeyFrameRadius + 2f);
            }
            else if (isThisSelected)
            {
                canvas.StrokeColor = Color.FromArgb("#FFFFFF").WithAlpha(0.7f);
                canvas.StrokeSize = 1.5f;
                canvas.DrawCircle(positions[i].X, positions[i].Y, KeyFrameRadius + 3f);
            }
        }
    }

    private void DrawPlayhead(ICanvas canvas, float top, float height, float timelineWidth)
    {
        if (View is null) return;

        float x = LeftMargin + Math.Clamp(View.CurrentProgress, 0f, 1f) * timelineWidth;

        canvas.StrokeColor = PlayheadColor;
        canvas.StrokeSize = PlayheadWidth;
        canvas.StrokeLineCap = LineCap.Butt;
        canvas.DrawLine(x, top + RulerHeight, x, top + height);

        float triSize = 5f;
        var triPath = new PathF();
        triPath.MoveTo(x - triSize, RulerHeight);
        triPath.LineTo(x + triSize, RulerHeight);
        triPath.LineTo(x, RulerHeight - triSize);
        triPath.Close();

        canvas.FillColor = PlayheadColor;
        canvas.FillPath(triPath);
    }
}