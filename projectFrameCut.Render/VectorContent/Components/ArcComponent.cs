using projectFrameCut.Drawing.Vector;

namespace projectFrameCut.Render.VectorContent.Components;

public class ArcComponent : BaseShapeComponent
{
    public override string TypeName => "Arc";
    protected override string[] ShapeFieldIds => ["CenterX", "CenterY", "RadiusX", "RadiusY", "StartAngle", "SweepAngle"];

    protected override Dictionary<string, object> GetDefaultParameters() =>
        new()
        {
            ["CenterX"] = 0.5f,
            ["CenterY"] = 0.5f,
            ["RadiusX"] = 0.3f,
            ["RadiusY"] = 0.3f,
            ["StartAngle"] = 0f,
            ["SweepAngle"] = MathF.PI,
        };

    protected override ShapeCanvasElement BuildBaseShape() =>
        ShapeCanvasElement.DrawArc(
            Parameters.GetFloat("CenterX", 0.5f),
            Parameters.GetFloat("CenterY", 0.5f),
            Parameters.GetFloat("RadiusX", 0.3f),
            Parameters.GetFloat("RadiusY", 0.3f),
            Parameters.GetFloat("StartAngle", 0f),
            Parameters.GetFloat("SweepAngle", MathF.PI));
}

