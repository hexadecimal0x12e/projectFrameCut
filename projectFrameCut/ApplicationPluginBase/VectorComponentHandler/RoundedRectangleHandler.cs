using projectFrameCut.ApplicationAPIBase.VectorComponentHandler;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Render.VectorContent.Components;

namespace projectFrameCut.ApplicationPluginBase.VectorComponentHandler;

public class RoundedRectangleHandler : BaseVectorComponentHandler
{
    public override string TypeName => "RoundedRectangle";
    public override string DisplayName => "圆角矩形";
    public override string Icon => "\ue3c6";
    public override bool HasDefaultHandles => true;
    protected override IVectorComponent CreateComponent() => new RoundedRectangleComponent();

    protected override void AddShapeSpecificProperties(PropertyPanelBuilder builder, IVectorComponent component)
    {
        builder.AddCollapsibleSection("形状", b =>
        {
            b.AddSlider("Width", "宽度:", 0.001f, 1f, GetParam(component, "Width", 0.3f));
            b.AddSlider("Height", "高度:", 0.001f, 1f, GetParam(component, "Height", 0.3f));
            b.AddSlider("CornerRadius", "圆角:", 0f, 0.5f, GetParam(component, "CornerRadius", 0.05f));
        }, defaultExpanded: true);
    }

    protected override Dictionary<string, object> GetDefaultParameters() =>
        new() { ["Width"] = 0.3f, ["Height"] = 0.3f, ["CornerRadius"] = 0.05f };

    public override IReadOnlyList<ShapeHandleDescriptor> CreateHandles(IVectorComponent component)
    {
        float w = GetParam(component, "Width", 0.3f);
        float h = GetParam(component, "Height", 0.3f);
        float cr = GetParam(component, "CornerRadius", 0.05f);
        float maxR = Math.Min(w, h) / 2f;
        float r = Math.Clamp(cr, 0f, maxR);
        return new[]
        {
            new ShapeHandleDescriptor { Id = "corner-r", NormalizedX = w - r, NormalizedY = r, PositionType = ShapeHandlePositionType.Corner },
        };
    }

    public override void ApplyHandleDrag(IVectorComponent component, string handleId, float newX, float newY, bool isLive)
    {
        if (handleId != "corner-r") return;
        float w = GetParam(component, "Width", 0.3f);
        float h = GetParam(component, "Height", 0.3f);
        float maxR = Math.Min(w, h) / 2f;

        // The handle sits at (w-r, r) — diagonally inward from the top-right
        // corner.  Project the new position back onto that diagonal so that
        // both horizontal and vertical drag components contribute to the
        // radius change (previously only newY was used, making horizontal
        // drags feel dead).
        float rFromX = w - newX;
        float rFromY = newY;
        float r = (rFromX + rFromY) / 2f;
        component.Parameters["CornerRadius"] = Math.Clamp(r, 0f, maxR);
    }
}
