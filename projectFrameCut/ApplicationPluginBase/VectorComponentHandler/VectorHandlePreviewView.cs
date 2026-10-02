using Microsoft.Maui.Graphics;
using projectFrameCut.ApplicationAPIBase.VectorComponentHandler;
using projectFrameCut.Drawing.Vector;
using Point = projectFrameCut.Drawing.Vector.Point;

namespace projectFrameCut.ApplicationPluginBase.VectorComponentHandler;

internal sealed class VectorHandlePreviewView : GraphicsView, IDrawable
{
    private readonly List<(PathF Path, VectorSegment Segment, bool Closed)> paths = [];
    private float width = 1, height = 1;

    public VectorHandlePreviewView()
    {
        Drawable = this;
        InputTransparent = true;
    }

    public bool Update(VectorHandlePreviewContext context)
    {
        paths.Clear();
        width = Math.Max(1, context.Width);
        height = Math.Max(1, context.Height);
        foreach (var element in context.Elements.OrderBy(e => e.LayerIndex))
        foreach (var s in element.Draw())
        {
            var p = new PathF();
            bool closed = false;
            float X(float x) => (element.RelativeX + x) * width;
            float Y(float y) => (element.RelativeY + y) * height;
            void Contour(Point[] points, bool close)
            {
                if (points.Length == 0) return;
                p.MoveTo(X(points[0].X), Y(points[0].Y));
                foreach (var point in points.Skip(1)) p.LineTo(X(point.X), Y(point.Y));
                if (close) p.Close();
            }
            void Arc(float x, float y, float rx, float ry, float start, float sweep, bool move)
            {
                int count = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) * 64));
                for (int i = 0; i <= count; i++)
                {
                    float a = start + sweep * i / count;
                    float px = X(x + rx * MathF.Cos(a)), py = Y(y + ry * MathF.Sin(a));
                    if (i == 0 && move) p.MoveTo(px, py);
                    else p.LineTo(px, py);
                }
            }
            switch (s)
            {
                case StraightLineVectorSegment l:
                    p.MoveTo(X(l.X1), Y(l.Y1));
                    p.LineTo(X(l.X2), Y(l.Y2));
                    break;
                case CubicBezierVectorSegment b:
                    p.MoveTo(X(b.X1), Y(b.Y1));
                    p.CurveTo(X(b.X2), Y(b.Y2), X(b.X3), Y(b.Y3), X(b.X4), Y(b.Y4));
                    break;
                case QuadraticBezierVectorSegment q:
                    p.MoveTo(X(q.X1), Y(q.Y1));
                    p.QuadTo(X(q.X2), Y(q.Y2), X(q.X3), Y(q.Y3));
                    break;
                case RoundedRectangleVectorSegment r:
                    float radius = Math.Clamp(r.CornerRadius, 0, Math.Max(0, Math.Min(r.Width, r.Height) / 2));
                    Arc(r.X + r.Width - radius, r.Y + radius, radius, radius, -MathF.PI / 2, MathF.PI / 2, true);
                    Arc(r.X + r.Width - radius, r.Y + r.Height - radius, radius, radius, 0, MathF.PI / 2, false);
                    Arc(r.X + radius, r.Y + r.Height - radius, radius, radius, MathF.PI / 2, MathF.PI / 2, false);
                    Arc(r.X + radius, r.Y + radius, radius, radius, MathF.PI, MathF.PI / 2, false);
                    p.Close();
                    closed = true;
                    break;
                case RectangleVectorSegment r:
                    Contour([new(r.X, r.Y), new(r.X + r.Width, r.Y), new(r.X + r.Width, r.Y + r.Height), new(r.X, r.Y + r.Height)], true);
                    closed = true;
                    break;
                case EllipseVectorSegment e:
                    Arc(e.X, e.Y, e.RadiusX, e.RadiusY, 0, MathF.PI * 2, true);
                    p.Close();
                    closed = true;
                    break;
                case ArcVectorSegment a:
                    Arc(a.X, a.Y, a.RadiusX, a.RadiusY, a.StartAngle, a.SweepAngle, true);
                    break;
                case GradientPolygonVectorSegment:
                    return false;
                case PolygonVectorSegment polygon:
                    Contour(polygon.Points, true);
                    foreach (var points in polygon.AdditionalContours ?? []) Contour(points, true);
                    foreach (var points in polygon.Holes ?? []) Contour(points, true);
                    closed = true;
                    break;
                case PolylineVectorSegment line:
                    Contour(line.Points, false);
                    break;
                default:
                    return false;
            }
            paths.Add((p, s, closed));
        }
        Invalidate();
        return true;
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        canvas.SaveState();
        canvas.Scale(dirtyRect.Width / width, dirtyRect.Height / height);
        foreach (var (path, s, closed) in paths)
        {
            if (closed && s.FillA > 0)
            {
                canvas.FillColor = Color.FromRgba(s.FillR / 65535f, s.FillG / 65535f, s.FillB / 65535f, Math.Clamp(s.FillA, 0, 1));
                canvas.FillPath(path, WindingMode.NonZero);
            }
            if (s.StrokeA > 0 && s.Thickness > 0)
            {
                canvas.StrokeColor = Color.FromRgba(s.StrokeR / 65535f, s.StrokeG / 65535f, s.StrokeB / 65535f, Math.Clamp(s.StrokeA, 0, 1));
                canvas.StrokeSize = s.Thickness;
                canvas.DrawPath(path);
            }
        }
        canvas.RestoreState();
    }
}
