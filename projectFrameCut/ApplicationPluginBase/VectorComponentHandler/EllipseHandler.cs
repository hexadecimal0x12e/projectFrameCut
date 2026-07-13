using projectFrameCut.ApplicationAPIBase.VectorComponentHandler;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Render.VectorContent.Components;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;

namespace projectFrameCut.ApplicationPluginBase.VectorComponentHandler;

public class EllipseHandler : BaseVectorComponentHandler
{
    public override string TypeName => "Ellipse";
    public override string DisplayName => PPLocalizedResources.VectorContentHandler_Ellipse_DisplayName;
    public override string Icon => "\ue836";
    public override bool HasDefaultHandles => true;
    protected override IVectorComponent CreateComponent() => new EllipseComponent();

    protected override void AddShapeSpecificProperties(PropertyPanelBuilder builder, IVectorComponent component)
    {
        builder.AddCollapsibleSection(PPLocalizedResources.VectorContentHandler_Section_Shape, b =>
        {
            b.AddSlider("RadiusX", PPLocalizedResources.VectorContentHandler_RadiusX, 0.001f, 1f, GetParam(component, "RadiusX", 0.15f));
            b.AddSlider("RadiusY", PPLocalizedResources.VectorContentHandler_RadiusY, 0.001f, 1f, GetParam(component, "RadiusY", 0.15f));
        }, defaultExpanded: true);
    }

    protected override Dictionary<string, object> GetDefaultParameters() =>
        new() { ["RadiusX"] = 0.15f, ["RadiusY"] = 0.15f };

    public override IReadOnlyList<ShapeHandleDescriptor> CreateHandles(IVectorComponent component)
    {
        float rx = GetParam(component, "RadiusX", 0.15f);
        float ry = GetParam(component, "RadiusY", 0.15f);
        return new[]
        {
            // EllipseComponent.BuildBaseShape() uses (0,0) as the local centre
            // (via ShapeCanvasElement.DrawEllipse(rx, ry)), so radius handles
            // live at (rx, 0) and (0, ry) in element-local space.
            new ShapeHandleDescriptor { Id = "rx", NormalizedX = rx, NormalizedY = 0f, PositionType = ShapeHandlePositionType.Radius },
            new ShapeHandleDescriptor { Id = "ry", NormalizedX = 0f, NormalizedY = ry, PositionType = ShapeHandlePositionType.Radius },
        };
    }

    public override void ApplyHandleDrag(IVectorComponent component, string handleId, float newX, float newY, bool isLive)
    {
        switch (handleId)
        {
            case "rx": component.Parameters["RadiusX"] = Math.Max(0.001f, newX); break;
            case "ry": component.Parameters["RadiusY"] = Math.Max(0.001f, newY); break;
        }
    }
}
