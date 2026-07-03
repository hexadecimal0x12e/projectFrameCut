using projectFrameCut.Drawing.Vector;

namespace projectFrameCut.Render.VectorContent.Components;

public class LineComponent : BaseShapeComponent
{
    public override string TypeName => "Line";
    protected override string[] ShapeFieldIds => ["X1", "Y1", "X2", "Y2"];

    protected override Dictionary<string, object> GetDefaultParameters() =>
        new()
        {
            ["X1"] = 0.1f,
            ["Y1"] = 0.1f,
            ["X2"] = 0.9f,
            ["Y2"] = 0.9f,
        };

    protected override ShapeCanvasElement BuildBaseShape() =>
        ShapeCanvasElement.DrawLine(
            Parameters.GetFloat("X1", 0.1f),
            Parameters.GetFloat("Y1", 0.1f),
            Parameters.GetFloat("X2", 0.9f),
            Parameters.GetFloat("Y2", 0.9f));
}

