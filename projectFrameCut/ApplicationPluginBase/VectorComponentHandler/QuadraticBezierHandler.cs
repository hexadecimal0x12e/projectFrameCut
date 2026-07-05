using projectFrameCut.ApplicationAPIBase.VectorComponentHandler;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Render.VectorContent.Components;

namespace projectFrameCut.ApplicationPluginBase.VectorComponentHandler;

public class QuadraticBezierHandler : BaseVectorComponentHandler
{
    public override string TypeName => "QuadraticBezier";
    public override string DisplayName => "二次贝塞尔";
    public override string Icon => "\ue922";
    public override bool HasDefaultHandles => false;
    protected override IVectorComponent CreateComponent() => new QuadraticBezierComponent();

    protected override void AddShapeSpecificProperties(PropertyPanelBuilder builder, IVectorComponent component)
    {
        builder.AddCollapsibleSection("形状", b =>
        {
            b.AddSlider("X1", "X1:", 0f, 1f, GetParam(component, "X1", 0.1f));
            b.AddSlider("Y1", "Y1:", 0f, 1f, GetParam(component, "Y1", 0.1f));
            b.AddSlider("X2", "X2:", 0f, 1f, GetParam(component, "X2", 0.5f));
            b.AddSlider("Y2", "Y2:", 0f, 1f, GetParam(component, "Y2", 0.9f));
            b.AddSlider("X3", "X3:", 0f, 1f, GetParam(component, "X3", 0.9f));
            b.AddSlider("Y3", "Y3:", 0f, 1f, GetParam(component, "Y3", 0.1f));
        }, defaultExpanded: true);
    }

    protected override Dictionary<string, object> GetDefaultParameters() =>
        new()
        {
            ["X1"] = 0.1f, ["Y1"] = 0.1f,
            ["X2"] = 0.5f, ["Y2"] = 0.9f,
            ["X3"] = 0.9f, ["Y3"] = 0.1f,
        };

    public override IReadOnlyList<ShapeHandleDescriptor> CreateHandles(IVectorComponent component)
    {
        return new[]
        {
            new ShapeHandleDescriptor { Id = "p1", NormalizedX = GetParam(component, "X1", 0.1f), NormalizedY = GetParam(component, "Y1", 0.1f), PositionType = ShapeHandlePositionType.Anchor },
            new ShapeHandleDescriptor { Id = "p2", NormalizedX = GetParam(component, "X2", 0.5f), NormalizedY = GetParam(component, "Y2", 0.9f), PositionType = ShapeHandlePositionType.Control },
            new ShapeHandleDescriptor { Id = "p3", NormalizedX = GetParam(component, "X3", 0.9f), NormalizedY = GetParam(component, "Y3", 0.1f), PositionType = ShapeHandlePositionType.Anchor },
        };
    }

    public override void ApplyHandleDrag(IVectorComponent component, string handleId, float newX, float newY, bool isLive)
    {
        switch (handleId)
        {
            case "p1": component.Parameters["X1"] = newX; component.Parameters["Y1"] = newY; break;
            case "p2": component.Parameters["X2"] = newX; component.Parameters["Y2"] = newY; break;
            case "p3": component.Parameters["X3"] = newX; component.Parameters["Y3"] = newY; break;
        }
    }
}
