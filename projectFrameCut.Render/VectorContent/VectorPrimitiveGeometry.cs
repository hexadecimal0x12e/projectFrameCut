using projectFrameCut.Drawing.Vector;

namespace projectFrameCut.Render.VectorContent;

internal static class VectorPrimitiveGeometry
{
    // Axis-aligned primitives need a path before an arbitrary rotation.
    public static VectorSegment ToPath(VectorSegment s)
    {
        var points = new List<Point>();
        bool closed = true;
        void Arc(float x, float y, float rx, float ry, float start, float sweep)
        {
            int count = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) * 64));
            for (int i = 0; i <= count; i++)
            {
                float a = start + sweep * i / count;
                points.Add(new Point(x + rx * MathF.Cos(a), y + ry * MathF.Sin(a)));
            }
        }
        switch (s)
        {
            case RoundedRectangleVectorSegment r:
                float radius = Math.Clamp(r.CornerRadius, 0, Math.Max(0, Math.Min(r.Width, r.Height) / 2));
                Arc(r.X + r.Width - radius, r.Y + radius, radius, radius, -MathF.PI / 2, MathF.PI / 2);
                Arc(r.X + r.Width - radius, r.Y + r.Height - radius, radius, radius, 0, MathF.PI / 2);
                Arc(r.X + radius, r.Y + r.Height - radius, radius, radius, MathF.PI / 2, MathF.PI / 2);
                Arc(r.X + radius, r.Y + radius, radius, radius, MathF.PI, MathF.PI / 2);
                break;
            case RectangleVectorSegment r:
                points.AddRange([new(r.X, r.Y), new(r.X + r.Width, r.Y), new(r.X + r.Width, r.Y + r.Height), new(r.X, r.Y + r.Height)]);
                break;
            case EllipseVectorSegment e:
                Arc(e.X, e.Y, e.RadiusX, e.RadiusY, 0, 2 * MathF.PI);
                points.RemoveAt(points.Count - 1);
                break;
            case ArcVectorSegment a:
                Arc(a.X, a.Y, a.RadiusX, a.RadiusY, a.StartAngle, a.SweepAngle);
                closed = false;
                break;
            default:
                return s;
        }
        VectorSegment path = closed ? new PolygonVectorSegment { Points = points.ToArray() }
            : new PolylineVectorSegment { Points = points.ToArray() };
        return path with
        {
            Thickness = s.Thickness,
            StrokeR = s.StrokeR, StrokeG = s.StrokeG, StrokeB = s.StrokeB, StrokeA = s.StrokeA,
            FillR = s.FillR, FillG = s.FillG, FillB = s.FillB, FillA = s.FillA
        };
    }
}
