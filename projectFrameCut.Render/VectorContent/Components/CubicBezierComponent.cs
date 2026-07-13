using projectFrameCut.Drawing.Vector;

namespace projectFrameCut.Render.VectorContent.Components;

public class CubicBezierComponent : BaseShapeComponent
{
    public override string TypeName => "CubicBezier";
    protected override string[] ShapeFieldIds => ["X1", "Y1", "X2", "Y2", "X3", "Y3", "X4", "Y4"];

    protected override Dictionary<string, object> GetDefaultParameters() =>
        new()
        {
            ["X1"] = 0.1f,
            ["Y1"] = 0.3f,
            ["X2"] = 0.3f,
            ["Y2"] = 0.7f,
            ["X3"] = 0.7f,
            ["Y3"] = 0.3f,
            ["X4"] = 0.9f,
            ["Y4"] = 0.7f,
        };

    protected override ShapeCanvasElement BuildBaseShape() =>
        ShapeCanvasElement.DrawCubicBezier(
            Parameters.GetFloat("X1", 0.1f),
            Parameters.GetFloat("Y1", 0.3f),
            Parameters.GetFloat("X2", 0.3f),
            Parameters.GetFloat("Y2", 0.7f),
            Parameters.GetFloat("X3", 0.7f),
            Parameters.GetFloat("Y3", 0.3f),
            Parameters.GetFloat("X4", 0.9f),
            Parameters.GetFloat("Y4", 0.7f));
}

