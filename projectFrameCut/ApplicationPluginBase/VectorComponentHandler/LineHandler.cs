using projectFrameCut.ApplicationAPIBase.VectorComponentHandler;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Render.VectorContent.Components;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;

namespace projectFrameCut.ApplicationPluginBase.VectorComponentHandler;

public class LineHandler : BaseVectorComponentHandler
{
    public override string TypeName => "Line";
    public override string DisplayName => PPLocalizedResources.VectorContentHandler_Line_DisplayName;
    public override string Icon => "\uf108";
    public override bool HasDefaultHandles => true;
    protected override IVectorComponent CreateComponent() => new LineComponent();

    protected override void AddShapeSpecificProperties(PropertyPanelBuilder builder, IVectorComponent component)
    {
        builder.AddCollapsibleSection(PPLocalizedResources.VectorContentHandler_Section_Shape, b =>
        {
            b.AddSlider("X1", PPLocalizedResources.VectorContentHandler_X1, 0f, 1f, GetParam(component, "X1", 0.1f));
            b.AddSlider("Y1", PPLocalizedResources.VectorContentHandler_Y1, 0f, 1f, GetParam(component, "Y1", 0.1f));
            b.AddSlider("X2", PPLocalizedResources.VectorContentHandler_X2, 0f, 1f, GetParam(component, "X2", 0.9f));
            b.AddSlider("Y2", PPLocalizedResources.VectorContentHandler_Y2, 0f, 1f, GetParam(component, "Y2", 0.9f));
        }, defaultExpanded: true);
    }

    protected override Dictionary<string, object> GetDefaultParameters() =>
        new() { ["X1"] = 0.1f, ["Y1"] = 0.1f, ["X2"] = 0.9f, ["Y2"] = 0.9f };

    public override IReadOnlyList<ShapeHandleDescriptor> CreateHandles(IVectorComponent component)
    {
        float x1 = GetParam(component, "X1", 0.1f);
        float y1 = GetParam(component, "Y1", 0.1f);
        float x2 = GetParam(component, "X2", 0.9f);
        float y2 = GetParam(component, "Y2", 0.9f);
        return new[]
        {
            new ShapeHandleDescriptor { Id = "p1", NormalizedX = x1, NormalizedY = y1, PositionType = ShapeHandlePositionType.Anchor },
            new ShapeHandleDescriptor { Id = "p2", NormalizedX = x2, NormalizedY = y2, PositionType = ShapeHandlePositionType.Anchor },
        };
    }

    public override void ApplyHandleDrag(IVectorComponent component, string handleId, float newX, float newY, bool isLive)
    {
        switch (handleId)
        {
            case "p1": component.Parameters["X1"] = newX; component.Parameters["Y1"] = newY; break;
            case "p2": component.Parameters["X2"] = newX; component.Parameters["Y2"] = newY; break;
        }
    }
}
