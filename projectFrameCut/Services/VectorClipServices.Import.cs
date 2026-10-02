using projectFrameCut.ApplicationAPIBase.VectorComponentHandler;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using Point = projectFrameCut.Drawing.Vector.Point;

namespace projectFrameCut.Services;

public static partial class VectorClipServices
{
    private static IVectorComponent? ConvertElementToComponent(
        VectorCanvasElement element, int index,
        Dictionary<string, IVectorComponentHandler> handlerCache)
    {
        var segments = element.Draw();
        if (segments is null || segments.Length == 0) return null;

        var seg = segments[0];
        var visualProps = ExtractVisualProperties(seg);

        string typeName;
        Dictionary<string, object> shapeParams;

        switch (seg)
        {
            case RoundedRectangleVectorSegment rr:
                typeName = "RoundedRectangle";
                shapeParams = new Dictionary<string, object>(visualProps)
                {
                    ["RelativeX"] = element.RelativeX + rr.X,
                    ["RelativeY"] = element.RelativeY + rr.Y,
                    ["Width"] = rr.Width,
                    ["Height"] = rr.Height,
                    ["CornerRadius"] = rr.CornerRadius,
                };
                break;

            case RectangleVectorSegment r:
                typeName = "Rectangle";
                shapeParams = new Dictionary<string, object>(visualProps)
                {
                    ["RelativeX"] = element.RelativeX + r.X,
                    ["RelativeY"] = element.RelativeY + r.Y,
                    ["Width"] = r.Width,
                    ["Height"] = r.Height,
                };
                break;

            case EllipseVectorSegment e:
                typeName = "Ellipse";
                shapeParams = new Dictionary<string, object>(visualProps)
                {
                    ["RelativeX"] = element.RelativeX + e.X,
                    ["RelativeY"] = element.RelativeY + e.Y,
                    ["RadiusX"] = e.RadiusX,
                    ["RadiusY"] = e.RadiusY,
                };
                break;

            case StraightLineVectorSegment l:
                typeName = "Line";
                {
                    float lcx = (l.X1 + l.X2) / 2f;
                    float lcy = (l.Y1 + l.Y2) / 2f;
                    shapeParams = new Dictionary<string, object>(visualProps)
                    {
                        ["RelativeX"] = element.RelativeX + lcx,
                        ["RelativeY"] = element.RelativeY + lcy,
                        ["X1"] = l.X1 - lcx,
                        ["Y1"] = l.Y1 - lcy,
                        ["X2"] = l.X2 - lcx,
                        ["Y2"] = l.Y2 - lcy,
                    };
                }
                break;

            case CubicBezierVectorSegment b:
                typeName = "CubicBezier";
                {
                    float bcx = (b.X1 + b.X2 + b.X3 + b.X4) / 4f;
                    float bcy = (b.Y1 + b.Y2 + b.Y3 + b.Y4) / 4f;
                    shapeParams = new Dictionary<string, object>(visualProps)
                    {
                        ["RelativeX"] = element.RelativeX + bcx,
                        ["RelativeY"] = element.RelativeY + bcy,
                        ["X1"] = b.X1 - bcx,
                        ["Y1"] = b.Y1 - bcy,
                        ["X2"] = b.X2 - bcx,
                        ["Y2"] = b.Y2 - bcy,
                        ["X3"] = b.X3 - bcx,
                        ["Y3"] = b.Y3 - bcy,
                        ["X4"] = b.X4 - bcx,
                        ["Y4"] = b.Y4 - bcy,
                    };
                }
                break;

            case QuadraticBezierVectorSegment q:
                typeName = "QuadraticBezier";
                {
                    float qcx = (q.X1 + q.X2 + q.X3) / 3f;
                    float qcy = (q.Y1 + q.Y2 + q.Y3) / 3f;
                    shapeParams = new Dictionary<string, object>(visualProps)
                    {
                        ["RelativeX"] = element.RelativeX + qcx,
                        ["RelativeY"] = element.RelativeY + qcy,
                        ["X1"] = q.X1 - qcx,
                        ["Y1"] = q.Y1 - qcy,
                        ["X2"] = q.X2 - qcx,
                        ["Y2"] = q.Y2 - qcy,
                        ["X3"] = q.X3 - qcx,
                        ["Y3"] = q.Y3 - qcy,
                    };
                }
                break;

            case ArcVectorSegment a:
                typeName = "Arc";
                shapeParams = new Dictionary<string, object>(visualProps)
                {
                    ["RelativeX"] = element.RelativeX + a.X,
                    ["RelativeY"] = element.RelativeY + a.Y,
                    ["CenterX"] = 0f,
                    ["CenterY"] = 0f,
                    ["RadiusX"] = a.RadiusX,
                    ["RadiusY"] = a.RadiusY,
                    ["StartAngle"] = a.StartAngle,
                    ["SweepAngle"] = a.SweepAngle,
                };
                break;

            case PolygonVectorSegment p:
                typeName = "Polygon";
                {
                    var pts = p.Points;
                    float px = 0f, py = 0f;
                    if (pts is { Length: > 0 })
                    {
                        foreach (var pt in pts) { px += pt.X; py += pt.Y; }
                        px /= pts.Length;
                        py /= pts.Length;
                    }
                    shapeParams = new Dictionary<string, object>(visualProps)
                    {
                        ["RelativeX"] = element.RelativeX + px,
                        ["RelativeY"] = element.RelativeY + py,
                        ["Points"] = pts.Select(pt => new Point(pt.X - px, pt.Y - py)).ToList(),
                    };
                }
                break;

            case PolylineVectorSegment pl:
                typeName = "Polyline";
                {
                    var pts = pl.Points;
                    float px = 0f, py = 0f;
                    if (pts is { Length: > 0 })
                    {
                        foreach (var pt in pts) { px += pt.X; py += pt.Y; }
                        px /= pts.Length;
                        py /= pts.Length;
                    }
                    shapeParams = new Dictionary<string, object>(visualProps)
                    {
                        ["RelativeX"] = element.RelativeX + px,
                        ["RelativeY"] = element.RelativeY + py,
                        ["Points"] = pts.Select(pt => new Point(pt.X - px, pt.Y - py)).ToList(),
                    };
                }
                break;

            default:
                Log(null, $"Unknown SVG segment type '{seg.GetType().Name}' — skipping element.", nameof(VectorClipServices));
                return null;
        }

        shapeParams["Rotation"] = element.Rotation;
        shapeParams["LayerIndex"] = element.LayerIndex;

        if (!handlerCache.TryGetValue(typeName, out var handler))
        {
            Log(null, $"No handler found for component type '{typeName}'.", nameof(VectorClipServices));
            return null;
        }

        var component = handler.Create(shapeParams);
        component.Name = $"{handler.DisplayName} {index + 1}";
        return component;
    }

    private static Dictionary<string, object> ExtractVisualProperties(VectorSegment seg)
    {
        return new Dictionary<string, object>
        {
            ["StrokeR"] = (float)seg.StrokeR,
            ["StrokeG"] = (float)seg.StrokeG,
            ["StrokeB"] = (float)seg.StrokeB,
            ["StrokeA"] = seg.StrokeA,
            ["FillR"] = (float)seg.FillR,
            ["FillG"] = (float)seg.FillG,
            ["FillB"] = (float)seg.FillB,
            ["FillA"] = seg.FillA,
            ["Thickness"] = seg.Thickness,
        };
    }

}
