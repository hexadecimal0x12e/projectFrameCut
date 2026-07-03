using projectFrameCut.Drawing.Vector;

namespace projectFrameCut.Render.VectorContent.Components;

public class RoundedRectangleComponent : BaseShapeComponent
{
    public override string TypeName => "RoundedRectangle";
    protected override string[] ShapeFieldIds => ["Width", "Height", "CornerRadius"];

    protected override Dictionary<string, object> GetDefaultParameters() =>
        new()
        {
            ["Width"] = 0.3f,
            ["Height"] = 0.3f,
            ["CornerRadius"] = 0.05f,
        };

    protected override ShapeCanvasElement BuildBaseShape() =>
        ShapeCanvasElement.DrawRoundedRectangle(
            Parameters.GetFloat("Width", 0.3f),
            Parameters.GetFloat("Height", 0.3f),
            Parameters.GetFloat("CornerRadius", 0.05f));
}

