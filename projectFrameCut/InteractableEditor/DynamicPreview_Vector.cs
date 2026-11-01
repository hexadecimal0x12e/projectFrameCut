using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.Controls;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using MauiPoint = Microsoft.Maui.Graphics.Point;
using ShapesPath = Microsoft.Maui.Controls.Shapes.Path;

namespace projectFrameCut.InteractableEditor;

public sealed partial class DynamicPreview
{
    #region Vector to MAUI Path conversion

    static int MaxVectorPathSize = 65535; // This is a safeguard limit to prevent creating excessively large paths that could crash the app. The actual maximum size may depend on the platform and device capabilities.
    private static int s_deviceMaxSize = 0; // Cached device limit, set once from CanvasDevice on Windows

    private static int GetDeviceMaxSize()
    {
        if (s_deviceMaxSize == 0)
        {
#if WINDOWS
            try
            {
                using var device = new Microsoft.Graphics.Canvas.CanvasDevice();
                s_deviceMaxSize = device.MaximumBitmapSizeInPixels;
            }
            catch
            {
                s_deviceMaxSize = MaxVectorPathSize;
            }
#else
            s_deviceMaxSize = MaxVectorPathSize;
#endif
        }
        return s_deviceMaxSize;
    }

    private static View BuildVectorPreviewView(
        IVectorContentClip vectorClip,
        int canvasWidth, int canvasHeight,
        int targetWidth, int targetHeight,
        uint frameIndex)
    {
        var clipW = vectorClip.TargetWidth > 0 ? vectorClip.TargetWidth : Math.Max(1, canvasWidth);
        var clipH = vectorClip.TargetHeight > 0 ? vectorClip.TargetHeight : Math.Max(1, canvasHeight);

        var vectorPicture = vectorClip.GetVectorPictureRelativeToStartPointOfSource(vectorClip.GetRelativeFrameIndex(frameIndex) ?? vectorClip.GetEffectiveDuration(), clipW, clipH);

        var container = new AbsoluteLayout
        {
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
        };

        var elements = vectorPicture.Elements;
        if (elements is null || elements.Count == 0)
            return container;

        var sortedElements = elements.OrderBy(e => e.LayerIndex);
        var deviceLimit = GetDeviceMaxSize();
        var skippedSegmentCount = 0;

        foreach (var element in sortedElements)
        {
            var segments = element.Draw();
            if (segments is null || segments.Length == 0)
                continue;

            float scaleX, scaleY, originX, originY;
            if (element.UseUniformScale)
            {
                float us = Math.Min(clipW, clipH);
                scaleX = us;
                scaleY = us;
                originX = element.BaseX * clipW + element.RelativeX * us;
                originY = element.BaseY * clipH + element.RelativeY * us;
            }
            else
            {
                scaleX = clipW;
                scaleY = clipH;
                originX = element.RelativeX * clipW;
                originY = element.RelativeY * clipH;
            }

            // Skip this entire element if the base scale alone already exceeds device limits
            if (scaleX > deviceLimit || scaleY > deviceLimit)
            {
                skippedSegmentCount++;
                continue;
            }

            float cosA = 0, sinA = 0;
            bool hasRotation = MathF.Abs(element.Rotation) > 0.0001f;
            if (hasRotation)
            {
                cosA = MathF.Cos(element.Rotation);
                sinA = MathF.Sin(element.Rotation);
            }

            foreach (var segment in segments)
            {
                if (segment is null) continue;
                try
                {
                    var path = CreatePathFromSegment(segment, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA, deviceLimit);
                    if (path is not null)
                        container.Children.Add(path);
                }
                catch (ArgumentOutOfRangeException)
                {
                    skippedSegmentCount++;
                    break;
                }
                catch (Exception ex) when (ex.Message.Contains("CanvasImageSource") || ex.Message.Contains("MaximumBitmapSize"))
                {
                    break;
                }
            }
        }

        if (skippedSegmentCount > 0)
        {
            Log($"Vector preview: skipped {skippedSegmentCount} oversize segment(s) exceeding device limit of {deviceLimit}.", "error");
        }

        if (skippedSegmentCount >= sortedElements.Count())
        {
            throw new ArgumentOutOfRangeException($"all vector segment exceeds device limit of {deviceLimit}. Please check your source, or disable Vector dynamic previewing function.");
        }

        return container;
    }

    internal static View BuildViewportVectorPreviewView(
        IReadOnlyList<VectorCanvasElement> elements,
        int canvasWidth,
        int canvasHeight,
        int viewportX,
        int viewportY,
        int viewportWidth,
        int viewportHeight)
    {
        float viewW = Math.Max(1, viewportWidth);
        float viewH = Math.Max(1, viewportHeight);
        float vpW = Math.Max(1f, viewportWidth);
        float vpH = Math.Max(1f, viewportHeight);
        float vpX = viewportX;
        float vpY = viewportY;
        float scaleX = viewW / vpW;
        float scaleY = viewH / vpH;

        var container = new PreviewClipLayout
        {
            WidthRequest = viewW,
            HeightRequest = viewH,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
            InputTransparent = true,
            ClipBounds = new Rect(0, 0, viewW, viewH),
        };

        if (elements.Count == 0)
        {
            return container;
        }

        int safeCanvasWidth = Math.Max(1, canvasWidth);
        int safeCanvasHeight = Math.Max(1, canvasHeight);
        int deviceLimit = GetDeviceMaxSize();

        foreach (var element in elements.OrderBy(e => e.LayerIndex))
        {
            var segments = element.Draw();
            if (segments is null || segments.Length == 0)
            {
                continue;
            }

            float elementScaleX;
            float elementScaleY;
            float originX;
            float originY;

            if (element.UseUniformScale)
            {
                float uniform = MathF.Min(safeCanvasWidth, safeCanvasHeight);
                elementScaleX = uniform;
                elementScaleY = uniform;
                originX = element.BaseX * safeCanvasWidth + element.RelativeX * uniform;
                originY = element.BaseY * safeCanvasHeight + element.RelativeY * uniform;
            }
            else
            {
                elementScaleX = safeCanvasWidth;
                elementScaleY = safeCanvasHeight;
                originX = element.RelativeX * safeCanvasWidth;
                originY = element.RelativeY * safeCanvasHeight;
            }

            float screenOriginX = (originX - vpX) * scaleX;
            float screenOriginY = (originY - vpY) * scaleY;
            float screenScaleX = elementScaleX * scaleX;
            float screenScaleY = elementScaleY * scaleY;

            float cosA = 0;
            float sinA = 0;
            bool hasRotation = MathF.Abs(element.Rotation) > 0.0001f;
            if (hasRotation)
            {
                cosA = MathF.Cos(element.Rotation);
                sinA = MathF.Sin(element.Rotation);
            }

            foreach (var segment in segments)
            {
                if (segment is null)
                {
                    continue;
                }

                var path = CreatePathFromSegment(
                    segment,
                    screenScaleX,
                    screenScaleY,
                    screenOriginX,
                    screenOriginY,
                    hasRotation,
                    cosA,
                    sinA,
                    deviceLimit);
                if (path is not null)
                {
                    container.Children.Add(path);
                }
            }
        }

        return container;
    }

    private static void ValidateAbsoluteBounds(float absoluteX, float absoluteY, int deviceLimit)
    {
        if (float.IsNaN(absoluteX) || float.IsNaN(absoluteY) ||
            float.IsInfinity(absoluteX) || float.IsInfinity(absoluteY) ||
            MathF.Abs(absoluteX) > deviceLimit || MathF.Abs(absoluteY) > deviceLimit)
        {
            throw new ArgumentOutOfRangeException($"Vector coordinate ({absoluteX}, {absoluteY}) exceeds device limit of {deviceLimit}");
        }
    }

    private static void ValidateSegmentExtent(float width, float height, int deviceLimit)
    {
        if (width > deviceLimit || height > deviceLimit)
        {
            throw new ArgumentOutOfRangeException($"Vector segment extent ({width}, {height}) exceeds device limit of {deviceLimit}");
        }
    }

    private static ShapesPath? CreatePathFromSegment(
        VectorSegment segment,
        float scaleX, float scaleY,
        float originX, float originY,
        bool hasRotation, float cosA, float sinA,
        int deviceLimit)
    {
        Geometry? geometry = null;

        switch (segment)
        {
            case StraightLineVectorSegment s:
                geometry = CreateLineGeometry(s, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA, deviceLimit);
                break;
            case RoundedRectangleVectorSegment s:
                geometry = CreateRoundedRectGeometry(s, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA, deviceLimit);
                break;
            case RectangleVectorSegment s:
                geometry = CreateRectGeometry(s, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA, deviceLimit);
                break;
            case EllipseVectorSegment s:
                geometry = CreateEllipseGeometry(s, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA, deviceLimit);
                break;
            case CubicBezierVectorSegment s:
                geometry = CreateCubicBezierGeometry(s, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA, deviceLimit);
                break;
            case QuadraticBezierVectorSegment s:
                geometry = CreateQuadraticBezierGeometry(s, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA, deviceLimit);
                break;
            case ArcVectorSegment s:
                geometry = CreateArcGeometry(s, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA, deviceLimit);
                break;
            case PolygonVectorSegment s:
                geometry = CreatePolygonGeometry(s.Points, s.Holes, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA, deviceLimit);
                break;
            case PolylineVectorSegment s:
                geometry = CreatePolylineGeometry(s.Points, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA, deviceLimit);
                break;
        }

        if (geometry is null) return null;

        bool hasFill = (float)segment.FillA > 0;
        bool hasStroke = segment.Thickness > 0 && (float)segment.StrokeA > 0;

        return new ShapesPath
        {
            Data = geometry,
            Fill = hasFill
                ? new SolidColorBrush(Color.FromRgba(segment.FillR / 65535f, segment.FillG / 65535f, segment.FillB / 65535f, (float)segment.FillA))
                : null,
            Stroke = hasStroke
                ? new SolidColorBrush(Color.FromRgba(segment.StrokeR / 65535f, segment.StrokeG / 65535f, segment.StrokeB / 65535f, (float)segment.StrokeA))
                : null,
            StrokeThickness = hasStroke ? segment.Thickness : 0,
        };
    }

    private static MauiPoint NToP(float nx, float ny, float scaleX, float scaleY, float originX, float originY, bool rot, float cosA, float sinA, int deviceLimit)
    {
        if (rot)
        {
            float rx = nx * cosA - ny * sinA;
            float ry = nx * sinA + ny * cosA;
            float absX = rx * scaleX + originX;
            float absY = ry * scaleY + originY;
            ValidateAbsoluteBounds(absX, absY, deviceLimit);
            return new MauiPoint(absX, absY);
        }
        float ax = nx * scaleX + originX;
        float ay = ny * scaleY + originY;
        ValidateAbsoluteBounds(ax, ay, deviceLimit);
        return new MauiPoint(ax, ay);
    }

    private static Geometry CreateLineGeometry(StraightLineVectorSegment s,
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA, int deviceLimit)
    {
        var start = NToP(s.X1, s.Y1, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit);
        var end = NToP(s.X2, s.Y2, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit);

        ValidateSegmentExtent((float)Math.Abs(end.X - start.X), (float)Math.Abs(end.Y - start.Y), deviceLimit);

        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new LineSegment { Point = end });

        var pg = new PathGeometry();
        pg.Figures.Add(figure);
        return pg;
    }

    private static Geometry CreateRectGeometry(RectangleVectorSegment s,
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA, int deviceLimit)
    {
        if (!rot)
        {
            var topLeft = NToP(s.X, s.Y, sx, sy, ox, oy, false, 0, 0, deviceLimit);
            ValidateSegmentExtent(s.Width * sx, s.Height * sy, deviceLimit);
            return new RectangleGeometry
            {
                Rect = new Rect(topLeft.X, topLeft.Y, s.Width * sx, s.Height * sy)
            };
        }

        var p0 = NToP(s.X, s.Y, sx, sy, ox, oy, true, cosA, sinA, deviceLimit);
        var p1 = NToP(s.X + s.Width, s.Y, sx, sy, ox, oy, true, cosA, sinA, deviceLimit);
        var p2 = NToP(s.X + s.Width, s.Y + s.Height, sx, sy, ox, oy, true, cosA, sinA, deviceLimit);
        var p3 = NToP(s.X, s.Y + s.Height, sx, sy, ox, oy, true, cosA, sinA, deviceLimit);

        ValidateSegmentExtent((float)Math.Abs(p1.X - p0.X), (float)Math.Abs(p2.Y - p1.Y), deviceLimit);

        return BuildPolygonPathGeometry([p0, p1, p2, p3], closeFigure: true, deviceLimit);
    }

    private static Geometry CreateRoundedRectGeometry(RoundedRectangleVectorSegment s,
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA, int deviceLimit)
    {
        float nX = s.X, nY = s.Y, nW = s.Width, nH = s.Height;
        float r = Math.Min(s.CornerRadius, Math.Min(nW, nH) * 0.5f);

        if (!rot)
        {
            var pg = new PathGeometry();
            var figure = new PathFigure { IsClosed = true, IsFilled = true };

            float pxR = r * sx, pyR = r * sy;
            var c0 = NToP(nX + r, nY, sx, sy, ox, oy, false, 0, 0, deviceLimit);
            var c1 = NToP(nX + nW - r, nY, sx, sy, ox, oy, false, 0, 0, deviceLimit);
            var c2 = NToP(nX + nW, nY + r, sx, sy, ox, oy, false, 0, 0, deviceLimit);
            var c3 = NToP(nX + nW, nY + nH - r, sx, sy, ox, oy, false, 0, 0, deviceLimit);
            var c4 = NToP(nX + nW - r, nY + nH, sx, sy, ox, oy, false, 0, 0, deviceLimit);
            var c5 = NToP(nX + r, nY + nH, sx, sy, ox, oy, false, 0, 0, deviceLimit);
            var c6 = NToP(nX, nY + nH - r, sx, sy, ox, oy, false, 0, 0, deviceLimit);
            var c7 = NToP(nX, nY + r, sx, sy, ox, oy, false, 0, 0, deviceLimit);
            ValidateSegmentExtent(nW * sx, nH * sy, deviceLimit);
            figure.StartPoint = c0;
            figure.Segments.Add(new LineSegment { Point = c1 });
            figure.Segments.Add(new ArcSegment { Point = c2, Size = new Size(pxR, pyR), IsLargeArc = false, SweepDirection = SweepDirection.Clockwise });
            figure.Segments.Add(new LineSegment { Point = c3 });
            figure.Segments.Add(new ArcSegment { Point = c4, Size = new Size(pxR, pyR), IsLargeArc = false, SweepDirection = SweepDirection.Clockwise });
            figure.Segments.Add(new LineSegment { Point = c5 });
            figure.Segments.Add(new ArcSegment { Point = c6, Size = new Size(pxR, pyR), IsLargeArc = false, SweepDirection = SweepDirection.Clockwise });
            figure.Segments.Add(new LineSegment { Point = c7 });
            figure.Segments.Add(new ArcSegment { Point = c0, Size = new Size(pxR, pyR), IsLargeArc = false, SweepDirection = SweepDirection.Clockwise });

            pg.Figures.Add(figure);
            return pg;
        }

        // Rotated: approximate as 4-corner polygon (corner rounding is secondary for preview)
        var p0 = NToP(nX, nY, sx, sy, ox, oy, true, cosA, sinA, deviceLimit);
        var p1 = NToP(nX + nW, nY, sx, sy, ox, oy, true, cosA, sinA, deviceLimit);
        var p2 = NToP(nX + nW, nY + nH, sx, sy, ox, oy, true, cosA, sinA, deviceLimit);
        var p3 = NToP(nX, nY + nH, sx, sy, ox, oy, true, cosA, sinA, deviceLimit);
        return BuildPolygonPathGeometry([p0, p1, p2, p3], closeFigure: true, deviceLimit);
    }

    private static Geometry CreateEllipseGeometry(EllipseVectorSegment s,
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA, int deviceLimit)
    {
        var center = NToP(s.X, s.Y, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit);
        ValidateSegmentExtent(s.RadiusX * sx * 2, s.RadiusY * sy * 2, deviceLimit);
        return new EllipseGeometry
        {
            Center = center,
            RadiusX = s.RadiusX * sx,
            RadiusY = s.RadiusY * sy,
        };
    }

    private static Geometry CreateCubicBezierGeometry(CubicBezierVectorSegment s,
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA, int deviceLimit)
    {
        var p1 = NToP(s.X1, s.Y1, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit);
        var p2 = NToP(s.X2, s.Y2, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit);
        var p3 = NToP(s.X3, s.Y3, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit);
        var p4 = NToP(s.X4, s.Y4, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit);
        ValidateSegmentExtent(
            (float)(Math.Max(Math.Max(p1.X, p2.X), Math.Max(p3.X, p4.X)) - Math.Min(Math.Min(p1.X, p2.X), Math.Min(p3.X, p4.X))),
            (float)(Math.Max(Math.Max(p1.Y, p2.Y), Math.Max(p3.Y, p4.Y)) - Math.Min(Math.Min(p1.Y, p2.Y), Math.Min(p3.Y, p4.Y))),
            deviceLimit);

        var figure = new PathFigure { StartPoint = p1, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new BezierSegment { Point1 = p2, Point2 = p3, Point3 = p4 });

        var pg = new PathGeometry();
        pg.Figures.Add(figure);
        return pg;
    }

    private static Geometry CreateQuadraticBezierGeometry(QuadraticBezierVectorSegment s,
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA, int deviceLimit)
    {
        var p1 = NToP(s.X1, s.Y1, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit);
        var p2 = NToP(s.X2, s.Y2, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit);
        var p3 = NToP(s.X3, s.Y3, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit);
        ValidateSegmentExtent(
            (float)(Math.Max(Math.Max(p1.X, p2.X), p3.X) - Math.Min(Math.Min(p1.X, p2.X), p3.X)),
            (float)(Math.Max(Math.Max(p1.Y, p2.Y), p3.Y) - Math.Min(Math.Min(p1.Y, p2.Y), p3.Y)),
            deviceLimit);
        var figure = new PathFigure { StartPoint = p1, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new QuadraticBezierSegment { Point1 = p2, Point2 = p3 });

        var pg = new PathGeometry();
        pg.Figures.Add(figure);
        return pg;
    }

    private static Geometry CreateArcGeometry(ArcVectorSegment s,
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA, int deviceLimit)
    {
        float startAngle = s.StartAngle;
        float endAngle = s.StartAngle + s.SweepAngle;

        // Compute start/end in normalized space
        float startNX = s.X + s.RadiusX * MathF.Cos(startAngle);
        float startNY = s.Y + s.RadiusY * MathF.Sin(startAngle);
        float endNX = s.X + s.RadiusX * MathF.Cos(endAngle);
        float endNY = s.Y + s.RadiusY * MathF.Sin(endAngle);

        var startPt = NToP(startNX, startNY, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit);
        var endPt = NToP(endNX, endNY, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit);

        bool isLargeArc = MathF.Abs(s.SweepAngle) > MathF.PI;
        var sweepDir = s.SweepAngle >= 0 ? SweepDirection.Clockwise : SweepDirection.CounterClockwise;
        ValidateSegmentExtent(s.RadiusX * sx * 2, s.RadiusY * sy * 2, deviceLimit);
        var figure = new PathFigure { StartPoint = startPt, IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = endPt,
            Size = new Size(s.RadiusX * sx, s.RadiusY * sy),
            RotationAngle = 0,
            IsLargeArc = isLargeArc,
            SweepDirection = sweepDir,
        });

        var pg = new PathGeometry();
        pg.Figures.Add(figure);
        return pg;
    }

    private static Geometry CreatePolygonGeometry(Drawing.Vector.Point[] points, Drawing.Vector.Point[][]? holes,
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA, int deviceLimit)
    {
        if (points is null || points.Length == 0)
            return null!;

        var pg = new PathGeometry();
        if (holes is { Length: > 0 })
            pg.FillRule = FillRule.EvenOdd;

        var pts = points.Select(p => NToP(p.X, p.Y, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit)).ToArray();
        var mainFigure = new PathFigure { StartPoint = pts[0], IsClosed = true, IsFilled = true };
        if (pts.Length > 1)
        {
            var segPoints = new PointCollection();
            for (int i = 1; i < pts.Length; i++) segPoints.Add(pts[i]);
            ValidateSegmentExtent(
                (float)(pts.Max(p => p.X) - pts.Min(p => p.X)),
                (float)(pts.Max(p => p.Y) - pts.Min(p => p.Y)),
                deviceLimit);
            mainFigure.Segments.Add(new PolyLineSegment { Points = segPoints });
        }
        pg.Figures.Add(mainFigure);

        if (holes is not null)
        {
            foreach (var hole in holes)
            {
                if (hole is null || hole.Length == 0) continue;
                var holePts = hole.Select(p => NToP(p.X, p.Y, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit)).ToArray();
                var holeFigure = new PathFigure { StartPoint = holePts[0], IsClosed = true, IsFilled = true };
                if (holePts.Length > 1)
                {
                    var holeSegPoints = new PointCollection();
                    for (int i = 1; i < holePts.Length; i++) holeSegPoints.Add(holePts[i]);
                    holeFigure.Segments.Add(new PolyLineSegment { Points = holeSegPoints });
                }
                pg.Figures.Add(holeFigure);
            }
        }

        return pg;
    }

    private static Geometry CreatePolylineGeometry(Drawing.Vector.Point[] points,
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA, int deviceLimit)
    {
        if (points is null || points.Length < 2)
            return null!;

        var pts = points.Select(p => NToP(p.X, p.Y, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit)).ToArray();
        var figure = new PathFigure { StartPoint = pts[0], IsClosed = false, IsFilled = false };
        var segPoints = new PointCollection();
        for (int i = 1; i < pts.Length; i++) segPoints.Add(pts[i]);
        ValidateSegmentExtent(
            (float)(pts.Max(p => p.X) - pts.Min(p => p.X)),
            (float)(pts.Max(p => p.Y) - pts.Min(p => p.Y)),
            deviceLimit);
        figure.Segments.Add(new PolyLineSegment { Points = segPoints });

        var pg = new PathGeometry();
        pg.Figures.Add(figure);
        return pg;
    }

    private static Geometry BuildPolygonPathGeometry(IReadOnlyList<MauiPoint> pts, bool closeFigure, int deviceLimit)
    {
        if (pts.Count == 0) return null!;

        var figure = new PathFigure { StartPoint = pts[0], IsClosed = closeFigure, IsFilled = closeFigure };
        if (pts.Count > 1)
        {
            var segPoints = new PointCollection();
            for (int i = 1; i < pts.Count; i++) segPoints.Add(pts[i]);
            ValidateSegmentExtent(
                (float)(pts.Max(p => p.X) - pts.Min(p => p.X)),
                (float)(pts.Max(p => p.Y) - pts.Min(p => p.Y)),
                deviceLimit);
            figure.Segments.Add(new PolyLineSegment { Points = segPoints });
        }

        var pg = new PathGeometry();
        pg.Figures.Add(figure);
        return pg;
    }

    #endregion

    // ═══════════════════════════════════════════════════════════
    // Rasterization fallback for large ComponentGroup previews
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Rasterises a set of <see cref="VectorCanvasElement"/>s to a bitmap and crops to the
    /// specified viewport rectangle.  Used as a drop-in replacement for
    /// <see cref="BuildViewportVectorPreviewView"/> when a <see cref="Render.VectorContent.Components.ComponentGroup"/>
    /// contains more children than <see cref="GroupRasterizationChildThreshold"/>.
    /// The resulting <see cref="Image"/> view carries a single bitmap rather than N individual
    /// Path objects, avoiding GPU / platform path-count limits.
    /// </summary>
    internal static View BuildRasterizedGroupPreviewView(
        IReadOnlyList<VectorCanvasElement> elements,
        int canvasWidth, int canvasHeight,
        int viewportX, int viewportY,
        int viewportWidth, int viewportHeight)
    {
        int safeW = Math.Max(1, canvasWidth);
        int safeH = Math.Max(1, canvasHeight);

        var vectorPicture = new VectorPicture { Elements = elements.ToList() };

        IPicture? fullRaster = null;
        try
        {
            fullRaster = IVectorContentClip.GlobalDefaultRasterizer.Convert(
                vectorPicture, safeW, safeH, false,
                IVectorContentClip.GlobalDefaultAntiAliasMode);

            if (fullRaster is null || fullRaster.Disposed)
            {
                return CreateEmptyGroupPreviewView(viewportWidth, viewportHeight);
            }

            // Crop to viewport region.  If the viewport is within canvas bounds we
            // can extract a sub-region; otherwise fall back to a scaled preview.
            int vpX = Math.Clamp(viewportX, 0, safeW - 1);
            int vpY = Math.Clamp(viewportY, 0, safeH - 1);
            int vpW = Math.Min(viewportWidth, safeW - vpX);
            int vpH = Math.Min(viewportHeight, safeH - vpY);

            if (vpW <= 0 || vpH <= 0)
            {
                return CreateEmptyGroupPreviewView(viewportWidth, viewportHeight);
            }

            var cropped = CropPictureRegion(fullRaster, vpX, vpY, viewportWidth, viewportHeight);

            return new PreviewImage
            {
                Source = cropped.ToImageSource(),
                WidthRequest = Math.Max(1, viewportWidth),
                HeightRequest = Math.Max(1, viewportHeight),
                HorizontalOptions = LayoutOptions.Start,
                VerticalOptions = LayoutOptions.Start,
                Aspect = Aspect.Fill,
                InputTransparent = true,
            };
        }
        catch (Exception ex)
        {
            Log($"Rasterized group preview failed (elements={elements.Count}, " +
                $"viewport={viewportWidth}×{viewportHeight}): {ex.Message}");
            return CreateEmptyGroupPreviewView(viewportWidth, viewportHeight);
        }
        finally
        {
            if (fullRaster is not null && !fullRaster.Disposed)
            {
                fullRaster.Dispose();
            }
        }
    }

    /// <summary>
    /// Returns a transparent placeholder Image for the group preview when
    /// rasterization fails or produces no visible content.
    /// </summary>
    private static View CreateEmptyGroupPreviewView(int width, int height)
    {
        return new PreviewImage
        {
            WidthRequest = Math.Max(1, width),
            HeightRequest = Math.Max(1, height),
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
            InputTransparent = true,
        };
    }

    /// <summary>
    /// Extracts a rectangular region from <paramref name="source"/> and returns it as a new
    /// <see cref="PictureFP16"/> of the requested <paramref name="w"/> × <paramref name="h"/> size.
    /// The crop rectangle is clamped to the source bounds; pixels outside the source become
    /// transparent (zero alpha).  If the source uses 16bpp it is first down-converted to 8bpp.
    /// </summary>
    private static IPicture CropPictureRegion(IPicture source, int x, int y, int w, int h)
    {
        using var fp = projectFrameCut.Drawing.Base.Picture.FP16Conversion.FromPicture(source);
        return projectFrameCut.Drawing.Base.Picture.FP16Raster.Crop(fp, x, y, Math.Max(1, w), Math.Max(1, h));
    }
}
