using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Render.VectorContent.Components;

namespace projectFrameCut.ApplicationPluginBase.VectorComponentHandler;

public class RectangleHandler : BaseVectorComponentHandler
{
    public override string TypeName => "Rectangle";
    public override string DisplayName => "矩形";
    public override string Icon => "\ueb54";
    public override bool HasDefaultHandles => true;
    protected override IVectorComponent CreateComponent() => new RectangleComponent();

    protected override void AddShapeSpecificProperties(PropertyPanelBuilder builder, IVectorComponent component)
    {
        builder.AddCollapsibleSection("形状", b =>
        {
            b.AddSlider("Width", "宽度:", 0.001f, 1f, GetParam(component, "Width", 0.3f));
            b.AddSlider("Height", "高度:", 0.001f, 1f, GetParam(component, "Height", 0.3f));
        }, defaultExpanded: true);
    }

    protected override Dictionary<string, object> GetDefaultParameters() =>
        new() { ["Width"] = 0.3f, ["Height"] = 0.3f };
}
