using projectFrameCut.ApplicationAPIBase.VectorComponentHandler;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Render.VectorContent.Components;

namespace projectFrameCut.ApplicationPluginBase.VectorComponentHandler;

public class CubicBezierHandler : BaseVectorComponentHandler
{
    public override string TypeName => "CubicBezier";
    public override string DisplayName => "三次贝塞尔";
    public override string Icon => "\ue6e1";
    public override bool HasDefaultHandles => false;
    protected override IVectorComponent CreateComponent() => new CubicBezierComponent();

    protected override void AddShapeSpecificProperties(PropertyPanelBuilder builder, IVectorComponent component)
    {
        builder.AddCollapsibleSection("形状", b =>
        {
            b.AddSlider("X1", "X1:", 0f, 1f, GetParam(component, "X1", 0.1f));
            b.AddSlider("Y1", "Y1:", 0f, 1f, GetParam(component, "Y1", 0.3f));
            b.AddSlider("X2", "X2:", 0f, 1f, GetParam(component, "X2", 0.3f));
            b.AddSlider("Y2", "Y2:", 0f, 1f, GetParam(component, "Y2", 0.7f));
            b.AddSlider("X3", "X3:", 0f, 1f, GetParam(component, "X3", 0.7f));
            b.AddSlider("Y3", "Y3:", 0f, 1f, GetParam(component, "Y3", 0.3f));
            b.AddSlider("X4", "X4:", 0f, 1f, GetParam(component, "X4", 0.9f));
            b.AddSlider("Y4", "Y4:", 0f, 1f, GetParam(component, "Y4", 0.7f));
        }, defaultExpanded: true);
    }

    protected override Dictionary<string, object> GetDefaultParameters() =>
        new()
        {
            ["X1"] = 0.1f, ["Y1"] = 0.3f,
            ["X2"] = 0.3f, ["Y2"] = 0.7f,
            ["X3"] = 0.7f, ["Y3"] = 0.3f,
            ["X4"] = 0.9f, ["Y4"] = 0.7f,
        };

    public override IReadOnlyList<ShapeHandleDescriptor> CreateHandles(IVectorComponent component)
    {
        return new[]
        {
            new ShapeHandleDescriptor { Id = "p1", NormalizedX = GetParam(component, "X1", 0.1f), NormalizedY = GetParam(component, "Y1", 0.3f), PositionType = ShapeHandlePositionType.Anchor },
            new ShapeHandleDescriptor { Id = "p2", NormalizedX = GetParam(component, "X2", 0.3f), NormalizedY = GetParam(component, "Y2", 0.7f), PositionType = ShapeHandlePositionType.Control },
            new ShapeHandleDescriptor { Id = "p3", NormalizedX = GetParam(component, "X3", 0.7f), NormalizedY = GetParam(component, "Y3", 0.3f), PositionType = ShapeHandlePositionType.Control },
            new ShapeHandleDescriptor { Id = "p4", NormalizedX = GetParam(component, "X4", 0.9f), NormalizedY = GetParam(component, "Y4", 0.7f), PositionType = ShapeHandlePositionType.Anchor },
        };
    }

    public override void ApplyHandleDrag(IVectorComponent component, string handleId, float newX, float newY, bool isLive)
    {
        switch (handleId)
        {
            case "p1": component.Parameters["X1"] = newX; component.Parameters["Y1"] = newY; break;
            case "p2": component.Parameters["X2"] = newX; component.Parameters["Y2"] = newY; break;
            case "p3": component.Parameters["X3"] = newX; component.Parameters["Y3"] = newY; break;
            case "p4": component.Parameters["X4"] = newX; component.Parameters["Y4"] = newY; break;
        }
    }
}
