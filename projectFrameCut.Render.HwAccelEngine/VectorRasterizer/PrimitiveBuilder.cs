using Microsoft.Extensions.Logging;
using projectFrameCut.Drawing.Vector;
using Point = projectFrameCut.Drawing.Vector.Point;

namespace projectFrameCut.Render.HwAccelEngine.VectorRasterizer
{
    /// <summary>
    /// GPU-friendly render primitive: either a fill-triangle or a stroked line segment.
    /// All coordinates are in pixel space (pre-scaled &amp; offset).
    /// </summary>
    internal enum GpuPrimType
    {
        /// <summary>Fill triangle (3 vertex pairs in Data[0..5]).</summary>
        TriangleFill = 0,
        /// <summary>Stroked line segment (x0,y0,x1,y1,thickness in Data[0..4]).</summary>
        StrokeLine = 1,
    }

    /// <summary>
    /// Flat 14-float representation of a render primitive for GPU upload.
    /// Layout: r,g,b,a, d0..d5, bboxMinX, bboxMinY, bboxMaxX, bboxMaxY.
    /// The bounding box is computed once on the CPU so the GPU kernel can
    /// cheaply cull primitives that do not cover the current pixel.
    /// </summary>
    internal readonly record struct GpuPrimitive(
        int Type,
        int Layer,
        float R, float G, float B, float A,
        float D0, float D1, float D2, float D3, float D4, float D5,
        float BBoxMinX, float BBoxMinY, float BBoxMaxX, float BBoxMaxY
    );

    /// <summary>Builds a flat primitive list from a <see cref="VectorPicture"/>.</summary>
    internal static class PrimitiveBuilder
    {
        public static List<GpuPrimitive> Build(VectorPicture canvas, int renderWidth, int renderHeight)
        {
            var primitives = new List<GpuPrimitive>();
            float scaleX = renderWidth;
            float scaleY = renderHeight;

            // Elements sorted by layer (stable sort preserves insertion order for equal layers)
            int seq = 0;
            var segs = canvas.Elements.OrderBy(e => e.LayerIndex).Select(c => (element: c, segment: c.Draw()));
            float total = segs.Sum(s => s.segment.Length);
            Shared.Logger.LogDiagnostic($"Total {canvas.Elements.Count} elements and {total} segments to draw in {renderWidth}x{renderHeight}.");
            foreach ((var element, var segments) in segs)
            {
                float ox, oy, sx, sy;
                if (element.UseUniformScale)
                {
                    float us = Math.Min(renderWidth, renderHeight);
                    sx = us; sy = us;
                    ox = element.BaseX * renderWidth + element.RelativeX * us;
                    oy = element.BaseY * renderHeight + element.RelativeY * us;
                }
                else
                {
                    sx = scaleX; sy = scaleY;
                    ox = element.RelativeX * renderWidth;
                    oy = element.RelativeY * renderHeight;
                }

                float cosA = MathF.Cos(element.Rotation);
                float sinA = MathF.Sin(element.Rotation);

                foreach (var seg in segments)
                {
                    var rotated = RotateSegment(seg, cosA, sinA);
                    DispatchSegment(primitives, rotated, ox, oy, sx, sy, element.LayerIndex, seq++);
                    //Shared.Logger.LogDiagnostic($"Drawing finished {seq / total:p2} (segment {seq} of {total})");
                }
            }

            return primitives;
        }

        // ---------------------------------------------------------------
        // Segment dispatch (RoundedRectangle MUST come before Rectangle)
        // ---------------------------------------------------------------

        private static void DispatchSegment(List<GpuPrimitive> primitives, VectorSegment seg,
            float ox, float oy, float sx, float sy, int layer, int seq)
        {
            switch (seg)
            {
                case StraightLineVectorSegment s:
                    AddLineStroke(primitives, s, ox, oy, sx, sy, layer, seq);
                    break;
                case RoundedRectangleVectorSegment s:
                    AddRoundedRect(primitives, s, ox, oy, sx, sy, layer, seq);
                    break;
                case RectangleVectorSegment s:
                    AddRect(primitives, s, ox, oy, sx, sy, layer, seq);
                    break;
                case EllipseVectorSegment s:
                    AddEllipse(primitives, s, ox, oy, sx, sy, layer, seq);
                    break;
                case CubicBezierVectorSegment s:
                    AddCubicBezier(primitives, s, ox, oy, sx, sy, layer, seq);
                    break;
                case QuadraticBezierVectorSegment s:
                    AddQuadraticBezier(primitives, s, ox, oy, sx, sy, layer, seq);
                    break;
                case ArcVectorSegment s:
                    AddArc(primitives, s, ox, oy, sx, sy, layer, seq);
                    break;
                case PolygonVectorSegment s:
                    AddPolygon(primitives, s, ox, oy, sx, sy, layer, seq);
                    break;
                case PolylineVectorSegment s:
                    AddPolyline(primitives, s, ox, oy, sx, sy, layer, seq);
                    break;
            }
        }

        // ---------------------------------------------------------------
        // Coordinate helpers
        // ---------------------------------------------------------------

        private static float CX(float segX, float ox, float sx) => ox + segX * sx;
        private static float CY(float segY, float oy, float sy) => oy + segY * sy;

        // ---------------------------------------------------------------
        // Fill: add triangle fan from vertices
        // ---------------------------------------------------------------

        private static void AddFillTriangles(List<GpuPrimitive> primitives,
            ReadOnlySpan<(float x, float y)> pts,
            ushort r, ushort g, ushort b, float a, int layer)
        {
            if (pts.Length < 3 || a <= 0f) return;

            float ax = pts[0].x, ay = pts[0].y;
            for (int i = 1; i < pts.Length - 1; i++)
            {
                float v0x = ax, v0y = ay;
                float v1x = pts[i].x, v1y = pts[i].y;
                float v2x = pts[i + 1].x, v2y = pts[i + 1].y;
                AppendTriangle(primitives, r, g, b, a, layer,
                    v0x, v0y, v1x, v1y, v2x, v2y);
            }
        }

        private static void AddFillTriangles(List<GpuPrimitive> primitives,
            ReadOnlySpan<Point> pts,
            ushort r, ushort g, ushort b, float a, int layer)
        {
            if (pts.Length < 3 || a <= 0f) return;

            float ax = pts[0].X, ay = pts[0].Y;
            for (int i = 1; i < pts.Length - 1; i++)
            {
                float v0x = ax, v0y = ay;
                float v1x = pts[i].X, v1y = pts[i].Y;
                float v2x = pts[i + 1].X, v2y = pts[i + 1].Y;
                AppendTriangle(primitives, r, g, b, a, layer,
                    v0x, v0y, v1x, v1y, v2x, v2y);
            }
        }

        private static void AppendTriangle(List<GpuPrimitive> primitives,
            ushort r, ushort g, ushort b, float a, int layer,
            float v0x, float v0y,
            float v1x, float v1y,
            float v2x, float v2y)
        {
            float bbMinX = MathF.Min(v0x, MathF.Min(v1x, v2x));
            float bbMaxX = MathF.Max(v0x, MathF.Max(v1x, v2x));
            float bbMinY = MathF.Min(v0y, MathF.Min(v1y, v2y));
            float bbMaxY = MathF.Max(v0y, MathF.Max(v1y, v2y));
            primitives.Add(new GpuPrimitive(
                (int)GpuPrimType.TriangleFill, layer,
                r, g, b, a,
                v0x, v0y, v1x, v1y, v2x, v2y,
                bbMinX, bbMinY, bbMaxX, bbMaxY));
        }

        // ---------------------------------------------------------------
        // Stroke: add line primitive
        // ---------------------------------------------------------------

        private static void AddStrokeLine(List<GpuPrimitive> primitives,
            float x0, float y0, float x1, float y1,
            float thickness,
            ushort r, ushort g, ushort b, float a, int layer)
        {
            if (thickness <= 0f || a <= 0f) return;
            float halfT = thickness * 0.5f;
            // Stroke bbox = segment endpoints expanded by half thickness.
            float bbMinX = MathF.Min(x0, x1) - halfT;
            float bbMaxX = MathF.Max(x0, x1) + halfT;
            float bbMinY = MathF.Min(y0, y1) - halfT;
            float bbMaxY = MathF.Max(y0, y1) + halfT;
            primitives.Add(new GpuPrimitive(
                (int)GpuPrimType.StrokeLine, layer,
                r, g, b, a,
                x0, y0, x1, y1, thickness, 0,
                bbMinX, bbMinY, bbMaxX, bbMaxY));
        }

        // ---------------------------------------------------------------
        // Shape handlers
        // ---------------------------------------------------------------

        private static void AddLineStroke(List<GpuPrimitive> primitives, StraightLineVectorSegment s,
            float ox, float oy, float sx, float sy, int layer, int seq)
        {
            if (s.Thickness <= 0f || s.StrokeA <= 0f) return;
            AddStrokeLine(primitives,
                CX(s.X1, ox, sx), CY(s.Y1, oy, sy),
                CX(s.X2, ox, sx), CY(s.Y2, oy, sy),
                s.Thickness,
                s.StrokeR, s.StrokeG, s.StrokeB, s.StrokeA, layer);
        }

        private static void AddRect(List<GpuPrimitive> primitives, RectangleVectorSegment s,
            float ox, float oy, float sx, float sy, int layer, int seq)
        {
            float rx = CX(s.X, ox, sx);
            float ry = CY(s.Y, oy, sy);
            float rw = s.Width * sx;
            float rh = s.Height * sy;
            float x0 = rx, y0 = ry, x1 = rx + rw, y1 = ry + rh;

            if (s.FillA > 0f)
            {
                Span<(float x, float y)> verts = stackalloc (float, float)[4]
                {
                    (x0, y0), (x1, y0), (x1, y1), (x0, y1)
                };
                AddFillTriangles(primitives, verts, s.FillR, s.FillG, s.FillB, s.FillA, layer);
            }

            if (s.Thickness > 0f && s.StrokeA > 0f)
            {
                float t = s.Thickness;
                AddStrokeLine(primitives, x0, y0, x1, y0, t, s.StrokeR, s.StrokeG, s.StrokeB, s.StrokeA, layer);
                AddStrokeLine(primitives, x1, y0, x1, y1, t, s.StrokeR, s.StrokeG, s.StrokeB, s.StrokeA, layer);
                AddStrokeLine(primitives, x1, y1, x0, y1, t, s.StrokeR, s.StrokeG, s.StrokeB, s.StrokeA, layer);
                AddStrokeLine(primitives, x0, y1, x0, y0, t, s.StrokeR, s.StrokeG, s.StrokeB, s.StrokeA, layer);
            }
        }

        private static void AddRoundedRect(List<GpuPrimitive> primitives, RoundedRectangleVectorSegment s,
            float ox, float oy, float sx, float sy, int layer, int seq)
        {
            float rx = CX(s.X, ox, sx);
            float ry = CY(s.Y, oy, sy);
            float rw = s.Width * sx;
            float rh = s.Height * sy;
            float radius = s.CornerRadius * Math.Min(sx, sy);
            radius = Math.Min(radius, Math.Min(rw, rh) * 0.5f);

            if (radius <= 1f)
            {
                // Fall back to plain rect
                var plain = new RectangleVectorSegment
                {
                    X = s.X,
                    Y = s.Y,
                    Width = s.Width,
                    Height = s.Height,
                    FillR = s.FillR,
                    FillG = s.FillG,
                    FillB = s.FillB,
                    FillA = s.FillA,
                    Thickness = s.Thickness,
                    StrokeR = s.StrokeR,
                    StrokeG = s.StrokeG,
                    StrokeB = s.StrokeB,
                    StrokeA = s.StrokeA,
                };
                AddRect(primitives, plain, ox, oy, sx, sy, layer, seq);
                return;
            }

            // Build rounded rect polygon approximation (32 vertices)
            const int cornerSegs = 8;
            int totalVerts = 4 * cornerSegs;
            var pts = new (float x, float y)[totalVerts];

            float cx1 = rx + radius, cy1 = ry + radius;
            float cx2 = rx + rw - radius, cy2 = ry + radius;
            float cx3 = rx + rw - radius, cy3 = ry + rh - radius;
            float cx4 = rx + radius, cy4 = ry + rh - radius;
            int idx = 0;

            for (int i = 0; i < cornerSegs; i++)
            { float a = -MathF.PI * 0.5f + MathF.PI * 0.5f * i / cornerSegs; pts[idx++] = (cx2 + radius * MathF.Cos(a), cy2 + radius * MathF.Sin(a)); }
            for (int i = 0; i < cornerSegs; i++)
            { float a = 0f + MathF.PI * 0.5f * i / cornerSegs; pts[idx++] = (cx3 + radius * MathF.Cos(a), cy3 + radius * MathF.Sin(a)); }
            for (int i = 0; i < cornerSegs; i++)
            { float a = MathF.PI * 0.5f + MathF.PI * 0.5f * i / cornerSegs; pts[idx++] = (cx4 + radius * MathF.Cos(a), cy4 + radius * MathF.Sin(a)); }
            for (int i = 0; i < cornerSegs; i++)
            { float a = MathF.PI + MathF.PI * 0.5f * i / cornerSegs; pts[idx++] = (cx1 + radius * MathF.Cos(a), cy1 + radius * MathF.Sin(a)); }

            if (s.FillA > 0f)
                AddFillTriangles(primitives, pts.AsSpan(), s.FillR, s.FillG, s.FillB, s.FillA, layer);

            if (s.Thickness > 0f && s.StrokeA > 0f)
            {
                float t = s.Thickness;
                for (int i = 0; i < totalVerts; i++)
                {
                    int j = (i + 1) % totalVerts;
                    AddStrokeLine(primitives, pts[i].x, pts[i].y, pts[j].x, pts[j].y,
                        t, s.StrokeR, s.StrokeG, s.StrokeB, s.StrokeA, layer);
                }
            }
        }

        private static void AddEllipse(List<GpuPrimitive> primitives, EllipseVectorSegment s,
            float ox, float oy, float sx, float sy, int layer, int seq)
        {
            float cx = CX(s.X, ox, sx);
            float cy = CY(s.Y, oy, sy);
            float rx = s.RadiusX * sx;
            float ry = s.RadiusY * sy;

            if (rx <= 0f || ry <= 0f) return;

            int segs = Math.Max(12, (int)(MathF.PI * MathF.Sqrt(rx + ry) * 0.5f));

            if (s.FillA > 0f)
            {
                var pts = new (float x, float y)[segs];
                for (int i = 0; i < segs; i++)
                {
                    float a = MathF.PI * 2f * i / segs;
                    pts[i] = (cx + rx * MathF.Cos(a), cy + ry * MathF.Sin(a));
                }
                AddFillTriangles(primitives, pts.AsSpan(), s.FillR, s.FillG, s.FillB, s.FillA, layer);
            }

            if (s.Thickness > 0f && s.StrokeA > 0f)
            {
                float t = s.Thickness;
                float prevX = cx + rx, prevY = cy;
                for (int i = 1; i <= segs; i++)
                {
                    float a = MathF.PI * 2f * i / segs;
                    float curX = cx + rx * MathF.Cos(a);
                    float curY = cy + ry * MathF.Sin(a);
                    AddStrokeLine(primitives, prevX, prevY, curX, curY,
                        t, s.StrokeR, s.StrokeG, s.StrokeB, s.StrokeA, layer);
                    prevX = curX; prevY = curY;
                }
            }
        }

        private static void AddCubicBezier(List<GpuPrimitive> primitives, CubicBezierVectorSegment s,
            float ox, float oy, float sx, float sy, int layer, int seq)
        {
            if (s.Thickness <= 0f || s.StrokeA <= 0f) return;

            float t = s.Thickness;
            var pts = new List<(float x, float y)>();
            FlattenCubicBezier(
                CX(s.X1, ox, sx), CY(s.Y1, oy, sy),
                CX(s.X2, ox, sx), CY(s.Y2, oy, sy),
                CX(s.X3, ox, sx), CY(s.Y3, oy, sy),
                CX(s.X4, ox, sx), CY(s.Y4, oy, sy),
                pts, 0);

            for (int i = 1; i < pts.Count; i++)
                AddStrokeLine(primitives, pts[i - 1].x, pts[i - 1].y, pts[i].x, pts[i].y,
                    t, s.StrokeR, s.StrokeG, s.StrokeB, s.StrokeA, layer);
        }

        private static void FlattenCubicBezier(float x0, float y0, float x1, float y1,
            float x2, float y2, float x3, float y3,
            List<(float x, float y)> points, int depth)
        {
            if (depth > 12) return;

            float dx = x3 - x0;
            float dy = y3 - y0;
            float len2 = dx * dx + dy * dy;

            if (len2 > 0f)
            {
                float t1 = ((x1 - x0) * dx + (y1 - y0) * dy) / len2;
                float t2 = ((x2 - x0) * dx + (y2 - y0) * dy) / len2;
                float d1 = MathF.Abs((y1 - y0) - t1 * dy) + MathF.Abs((x1 - x0) - t1 * dx);
                float d2 = MathF.Abs((y2 - y0) - t2 * dy) + MathF.Abs((x2 - x0) - t2 * dx);

                if ((d1 + d2) * (d1 + d2) < len2 * 0.001f)
                {
                    points.Add((x3, y3));
                    return;
                }
            }

            float mx01 = (x0 + x1) * 0.5f, my01 = (y0 + y1) * 0.5f;
            float mx12 = (x1 + x2) * 0.5f, my12 = (y1 + y2) * 0.5f;
            float mx23 = (x2 + x3) * 0.5f, my23 = (y2 + y3) * 0.5f;
            float mx012 = (mx01 + mx12) * 0.5f, my012 = (my01 + my12) * 0.5f;
            float mx123 = (mx12 + mx23) * 0.5f, my123 = (my12 + my23) * 0.5f;
            float mx0123 = (mx012 + mx123) * 0.5f, my0123 = (my012 + my123) * 0.5f;

            points.Add((mx0123, my0123));
            FlattenCubicBezier(x0, y0, mx01, my01, mx012, my012, mx0123, my0123, points, depth + 1);
            FlattenCubicBezier(mx0123, my0123, mx123, my123, mx23, my23, x3, y3, points, depth + 1);
        }

        private static void AddQuadraticBezier(List<GpuPrimitive> primitives, QuadraticBezierVectorSegment s,
            float ox, float oy, float sx, float sy, int layer, int seq)
        {
            if (s.Thickness <= 0f || s.StrokeA <= 0f) return;

            float t = s.Thickness;
            var pts = new List<(float x, float y)>();
            FlattenQuadraticBezier(
                CX(s.X1, ox, sx), CY(s.Y1, oy, sy),
                CX(s.X2, ox, sx), CY(s.Y2, oy, sy),
                CX(s.X3, ox, sx), CY(s.Y3, oy, sy),
                pts, 0);

            for (int i = 1; i < pts.Count; i++)
                AddStrokeLine(primitives, pts[i - 1].x, pts[i - 1].y, pts[i].x, pts[i].y,
                    t, s.StrokeR, s.StrokeG, s.StrokeB, s.StrokeA, layer);
        }

        private static void FlattenQuadraticBezier(float x0, float y0, float x1, float y1,
            float x2, float y2, List<(float x, float y)> points, int depth)
        {
            if (depth > 12) return;

            float dx = x2 - x0;
            float dy = y2 - y0;
            float len2 = dx * dx + dy * dy;

            if (len2 > 0f)
            {
                float t = ((x1 - x0) * dx + (y1 - y0) * dy) / len2;
                float d = MathF.Abs((y1 - y0) - t * dy) + MathF.Abs((x1 - x0) - t * dx);
                if (d * d < len2 * 0.001f)
                {
                    points.Add((x2, y2));
                    return;
                }
            }

            float mx01 = (x0 + x1) * 0.5f, my01 = (y0 + y1) * 0.5f;
            float mx12 = (x1 + x2) * 0.5f, my12 = (y1 + y2) * 0.5f;
            float mx012 = (mx01 + mx12) * 0.5f, my012 = (my01 + my12) * 0.5f;

            points.Add((mx012, my012));
            FlattenQuadraticBezier(x0, y0, mx01, my01, mx012, my012, points, depth + 1);
            FlattenQuadraticBezier(mx012, my012, mx12, my12, x2, y2, points, depth + 1);
        }

        private static void AddArc(List<GpuPrimitive> primitives, ArcVectorSegment s,
            float ox, float oy, float sx, float sy, int layer, int seq)
        {
            if (s.Thickness <= 0f || s.StrokeA <= 0f) return;

            float cx = CX(s.X, ox, sx);
            float cy = CY(s.Y, oy, sy);
            float rx = s.RadiusX * sx;
            float ry = s.RadiusY * sy;

            if (rx <= 0f || ry <= 0f) return;

            float t = s.Thickness;
            int segs = Math.Max(4, (int)(MathF.Abs(s.SweepAngle) * MathF.Sqrt(rx + ry) * 0.3f));
            float step = s.SweepAngle / segs;
            float angle = s.StartAngle;
            float prevX = cx + rx * MathF.Cos(angle);
            float prevY = cy + ry * MathF.Sin(angle);

            for (int i = 1; i <= segs; i++)
            {
                angle += step;
                float curX = cx + rx * MathF.Cos(angle);
                float curY = cy + ry * MathF.Sin(angle);
                AddStrokeLine(primitives, prevX, prevY, curX, curY,
                    t, s.StrokeR, s.StrokeG, s.StrokeB, s.StrokeA, layer);
                prevX = curX; prevY = curY;
            }
        }

        private static void AddPolygon(List<GpuPrimitive> primitives, PolygonVectorSegment s,
            float ox, float oy, float sx, float sy, int layer, int seq)
        {
            var pts = s.Points;
            if (pts.Length < 3) return;

            // Transform to pixel space
            Span<(float x, float y)> canvasPts = pts.Length <= 256
                ? stackalloc (float, float)[pts.Length]
                : new (float, float)[pts.Length];
            for (int i = 0; i < pts.Length; i++)
                canvasPts[i] = (CX(pts[i].X, ox, sx), CY(pts[i].Y, oy, sy));

            if (s.FillA > 0f)
            {
                AddFillTriangles(primitives, canvasPts, s.FillR, s.FillG, s.FillB, s.FillA, layer);

                // Holes: punch through with transparent triangles
                if (s.Holes is { Length: > 0 })
                {
                    foreach (var hole in s.Holes!)
                    {
                        if (hole.Length < 3) continue;
                        Span<(float x, float y)> holePts = hole.Length <= 256
                            ? stackalloc (float, float)[hole.Length]
                            : new (float, float)[hole.Length];
                        for (int i = 0; i < hole.Length; i++)
                            holePts[i] = (CX(hole[i].X, ox, sx), CY(hole[i].Y, oy, sy));
                        AddFillTriangles(primitives, holePts, 0, 0, 0, 0f, layer);
                    }
                }
            }

            if (s.Thickness > 0f && s.StrokeA > 0f)
            {
                float t = s.Thickness;
                for (int i = 0; i < pts.Length; i++)
                {
                    int j = (i + 1) % pts.Length;
                    AddStrokeLine(primitives, canvasPts[i].x, canvasPts[i].y,
                        canvasPts[j].x, canvasPts[j].y,
                        t, s.StrokeR, s.StrokeG, s.StrokeB, s.StrokeA, layer);
                }
            }
        }

        private static void AddPolyline(List<GpuPrimitive> primitives, PolylineVectorSegment s,
            float ox, float oy, float sx, float sy, int layer, int seq)
        {
            if (s.Thickness <= 0f || s.StrokeA <= 0f || s.Points.Length < 2) return;

            float t = s.Thickness;
            var pts = s.Points;
            for (int i = 1; i < pts.Length; i++)
            {
                AddStrokeLine(primitives,
                    CX(pts[i - 1].X, ox, sx), CY(pts[i - 1].Y, oy, sy),
                    CX(pts[i].X, ox, sx), CY(pts[i].Y, oy, sy),
                    t, s.StrokeR, s.StrokeG, s.StrokeB, s.StrokeA, layer);
            }
        }

        // ---------------------------------------------------------------
        // Rotation
        // ---------------------------------------------------------------

        private static VectorSegment RotateSegment(VectorSegment seg, float cosA, float sinA)
        {
            return seg switch
            {
                StraightLineVectorSegment s => s with
                {
                    X1 = s.X1 * cosA - s.Y1 * sinA,
                    Y1 = s.X1 * sinA + s.Y1 * cosA,
                    X2 = s.X2 * cosA - s.Y2 * sinA,
                    Y2 = s.X2 * sinA + s.Y2 * cosA,
                },
                QuadraticBezierVectorSegment s => s with
                {
                    X1 = s.X1 * cosA - s.Y1 * sinA,
                    Y1 = s.X1 * sinA + s.Y1 * cosA,
                    X2 = s.X2 * cosA - s.Y2 * sinA,
                    Y2 = s.X2 * sinA + s.Y2 * cosA,
                    X3 = s.X3 * cosA - s.Y3 * sinA,
                    Y3 = s.X3 * sinA + s.Y3 * cosA,
                },
                CubicBezierVectorSegment s => s with
                {
                    X1 = s.X1 * cosA - s.Y1 * sinA,
                    Y1 = s.X1 * sinA + s.Y1 * cosA,
                    X2 = s.X2 * cosA - s.Y2 * sinA,
                    Y2 = s.X2 * sinA + s.Y2 * cosA,
                    X3 = s.X3 * cosA - s.Y3 * sinA,
                    Y3 = s.X3 * sinA + s.Y3 * cosA,
                    X4 = s.X4 * cosA - s.Y4 * sinA,
                    Y4 = s.X4 * sinA + s.Y4 * cosA,
                },
                RoundedRectangleVectorSegment s => SegToRotatedPolygon(s, cosA, sinA),
                RectangleVectorSegment s => SegToRotatedPolygon(s, cosA, sinA),
                EllipseVectorSegment s => s with
                {
                    X = s.X * cosA - s.Y * sinA,
                    Y = s.X * sinA + s.Y * cosA,
                },
                ArcVectorSegment s => s with
                {
                    X = s.X * cosA - s.Y * sinA,
                    Y = s.X * sinA + s.Y * cosA,
                    StartAngle = s.StartAngle + MathF.Atan2(sinA, cosA),
                },
                PolygonVectorSegment s => s with
                {
                    Points = Array.ConvertAll(s.Points, p => new Point(
                        p.X * cosA - p.Y * sinA,
                        p.X * sinA + p.Y * cosA)),
                    Holes = s.Holes?.Select(h => Array.ConvertAll(h, p => new Point(
                        p.X * cosA - p.Y * sinA,
                        p.X * sinA + p.Y * cosA))).ToArray(),
                },
                PolylineVectorSegment s => s with
                {
                    Points = Array.ConvertAll(s.Points, p => new Point(
                        p.X * cosA - p.Y * sinA,
                        p.X * sinA + p.Y * cosA)),
                },
                _ => seg,
            };
        }

        private static PolygonVectorSegment SegToRotatedPolygon(RectangleVectorSegment s, float cosA, float sinA)
        {
            float x = s.X, y = s.Y, w = s.Width, h = s.Height;

            Span<Point> corners = stackalloc Point[4]
            {
                new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h),
            };

            var rotated = new Point[4];
            for (int i = 0; i < 4; i++)
                rotated[i] = new Point(
                    corners[i].X * cosA - corners[i].Y * sinA,
                    corners[i].X * sinA + corners[i].Y * cosA);

            return new PolygonVectorSegment
            {
                Points = rotated,
                FillR = s.FillR,
                FillG = s.FillG,
                FillB = s.FillB,
                FillA = s.FillA,
                Thickness = s.Thickness,
                StrokeR = s.StrokeR,
                StrokeG = s.StrokeG,
                StrokeB = s.StrokeB,
                StrokeA = s.StrokeA,
            };
        }
    }
}
