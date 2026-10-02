using projectFrameCut.Drawing.Vector;

namespace projectFrameCut.Render.VectorContent.Components;

public class PolylineComponent : BaseShapeComponent
{
    public override string TypeName => "Polyline";

    protected override Dictionary<string, object> GetDefaultParameters() =>
        new()
        {
            ["Points"] = new List<Point>
            {
                new(0.1f, 0.5f),
                new(0.3f, 0.3f),
                new(0.5f, 0.7f),
                new(0.7f, 0.3f),
                new(0.9f, 0.5f),
            },
        };

    protected override ShapeCanvasElement BuildBaseShape()
    {
        return ShapeCanvasElement.DrawPolyline(GetPoints().ToArray());
    }
}

