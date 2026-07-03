using projectFrameCut.Drawing.Vector;

namespace projectFrameCut.Render.VectorContent.Components;

public class QuadraticBezierComponent : BaseShapeComponent
{
    public override string TypeName => "QuadraticBezier";
    protected override string[] ShapeFieldIds => ["X1", "Y1", "X2", "Y2", "X3", "Y3"];

    protected override Dictionary<string, object> GetDefaultParameters() =>
        new()
        {
            ["X1"] = 0.1f,
            ["Y1"] = 0.1f,
            ["X2"] = 0.5f,
            ["Y2"] = 0.9f,
            ["X3"] = 0.9f,
            ["Y3"] = 0.1f,
        };

    protected override ShapeCanvasElement BuildBaseShape() =>
        ShapeCanvasElement.DrawQuadraticBezier(
            Parameters.GetFloat("X1", 0.1f),
            Parameters.GetFloat("Y1", 0.1f),
            Parameters.GetFloat("X2", 0.5f),
            Parameters.GetFloat("Y2", 0.9f),
            Parameters.GetFloat("X3", 0.9f),
            Parameters.GetFloat("Y3", 0.1f));
}

