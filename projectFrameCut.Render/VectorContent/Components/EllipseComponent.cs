using projectFrameCut.Drawing.Vector;

namespace projectFrameCut.Render.VectorContent.Components;

public class EllipseComponent : BaseShapeComponent
{
    public override string TypeName => "Ellipse";
    protected override string[] ShapeFieldIds => ["RadiusX", "RadiusY"];

    protected override Dictionary<string, object> GetDefaultParameters() =>
        new()
        {
            ["RadiusX"] = 0.15f,
            ["RadiusY"] = 0.15f,
        };

    protected override ShapeCanvasElement BuildBaseShape() =>
        ShapeCanvasElement.DrawEllipse(
            Parameters.GetFloat("RadiusX", 0.15f),
            Parameters.GetFloat("RadiusY", 0.15f));
}

