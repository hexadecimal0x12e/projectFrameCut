using projectFrameCut.Drawing.Vector;

namespace projectFrameCut.Render.VectorContent.Components;

public class PolygonComponent : BaseShapeComponent
{
    public override string TypeName => "Polygon";
    protected override string[] ShapeFieldIds => [];

    protected override Dictionary<string, object> GetDefaultParameters() =>
        new()
        {
            ["Points"] = new List<Point>
            {
                new(0.3f, 0.3f),
                new(0.5f, 0.7f),
                new(0.7f, 0.3f),
            },
        };

    protected override ShapeCanvasElement BuildBaseShape()
    {
        var points = Parameters.TryGetValue("Points", out var value) && value is List<Point> pts
            ? pts
            : [];
        return ShapeCanvasElement.DrawPolygon(points.ToArray());
    }
}

