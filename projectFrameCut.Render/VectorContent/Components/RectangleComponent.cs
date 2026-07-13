using projectFrameCut.Drawing.Vector;

namespace projectFrameCut.Render.VectorContent.Components;

public class RectangleComponent : BaseShapeComponent
{
    public override string TypeName => "Rectangle";
    protected override string[] ShapeFieldIds => ["Width", "Height"];

    protected override Dictionary<string, object> GetDefaultParameters() =>
        new()
        {
            ["Width"] = 0.3f,
            ["Height"] = 0.3f,
        };

    protected override ShapeCanvasElement BuildBaseShape() =>
        ShapeCanvasElement.DrawRectangle(
            Parameters.GetFloat("Width", 0.3f),
            Parameters.GetFloat("Height", 0.3f));
}

