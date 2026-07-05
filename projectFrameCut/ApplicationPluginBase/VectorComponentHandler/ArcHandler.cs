using projectFrameCut.ApplicationAPIBase.VectorComponentHandler;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Render.VectorContent.Components;

namespace projectFrameCut.ApplicationPluginBase.VectorComponentHandler;

public class ArcHandler : BaseVectorComponentHandler
{
    public override string TypeName => "Arc";
    public override string DisplayName => "弧线";
    public override string Icon => "\ue155";
    public override bool HasDefaultHandles => false;
    protected override IVectorComponent CreateComponent() => new ArcComponent();

    protected override void AddShapeSpecificProperties(PropertyPanelBuilder builder, IVectorComponent component)
    {
        builder.AddCollapsibleSection("形状", b =>
        {
            b.AddSlider("CenterX", "中心 X:", 0f, 1f, GetParam(component, "CenterX", 0.5f));
            b.AddSlider("CenterY", "中心 Y:", 0f, 1f, GetParam(component, "CenterY", 0.5f));
            b.AddSlider("RadiusX", "半径 X:", 0.001f, 1f, GetParam(component, "RadiusX", 0.3f));
            b.AddSlider("RadiusY", "半径 Y:", 0.001f, 1f, GetParam(component, "RadiusY", 0.3f));
            b.AddSlider("StartAngle", "起始角:", -MathF.PI * 2f, MathF.PI * 2f, GetParam(component, "StartAngle", 0f));
            b.AddSlider("SweepAngle", "扫过角:", -MathF.PI * 2f, MathF.PI * 2f, GetParam(component, "SweepAngle", MathF.PI));
        }, defaultExpanded: true);
    }

    protected override Dictionary<string, object> GetDefaultParameters() =>
        new()
        {
            ["CenterX"] = 0.5f,
            ["CenterY"] = 0.5f,
            ["RadiusX"] = 0.3f,
            ["RadiusY"] = 0.3f,
            ["StartAngle"] = 0f,
            ["SweepAngle"] = MathF.PI,
        };

    public override IReadOnlyList<ShapeHandleDescriptor> CreateHandles(IVectorComponent component)
    {
        float cx = GetParam(component, "CenterX", 0.5f);
        float cy = GetParam(component, "CenterY", 0.5f);
        float rx = GetParam(component, "RadiusX", 0.3f);
        float ry = GetParam(component, "RadiusY", 0.3f);
        float start = GetParam(component, "StartAngle", 0f);
        float sweep = GetParam(component, "SweepAngle", MathF.PI);
        float end = start + sweep;
        return new[]
        {
            new ShapeHandleDescriptor { Id = "center", NormalizedX = cx, NormalizedY = cy, PositionType = ShapeHandlePositionType.Center },
            new ShapeHandleDescriptor { Id = "rx", NormalizedX = cx + rx, NormalizedY = cy, PositionType = ShapeHandlePositionType.Radius },
            new ShapeHandleDescriptor { Id = "ry", NormalizedX = cx, NormalizedY = cy + ry, PositionType = ShapeHandlePositionType.Radius },
            new ShapeHandleDescriptor { Id = "start", NormalizedX = cx + rx * MathF.Cos(start), NormalizedY = cy + ry * MathF.Sin(start), PositionType = ShapeHandlePositionType.Angle },
            new ShapeHandleDescriptor { Id = "end", NormalizedX = cx + rx * MathF.Cos(end), NormalizedY = cy + ry * MathF.Sin(end), PositionType = ShapeHandlePositionType.Angle },
        };
    }

    public override void ApplyHandleDrag(IVectorComponent component, string handleId, float newX, float newY, bool isLive)
    {
        switch (handleId)
        {
            case "center":
                component.Parameters["CenterX"] = newX;
                component.Parameters["CenterY"] = newY;
                break;
            case "rx":
                {
                    float cx = GetParam(component, "CenterX", 0.5f);
                    component.Parameters["RadiusX"] = Math.Max(0.001f, newX - cx);
                    break;
                }
            case "ry":
                {
                    float cy = GetParam(component, "CenterY", 0.5f);
                    component.Parameters["RadiusY"] = Math.Max(0.001f, newY - cy);
                    break;
                }
            case "start":
                {
                    float cx = GetParam(component, "CenterX", 0.5f);
                    float cy = GetParam(component, "CenterY", 0.5f);
                    component.Parameters["StartAngle"] = MathF.Atan2(newY - cy, newX - cx);
                    break;
                }
            case "end":
                {
                    float cx = GetParam(component, "CenterX", 0.5f);
                    float cy = GetParam(component, "CenterY", 0.5f);
                    float startA = GetParam(component, "StartAngle", 0f);
                    float endAngle = MathF.Atan2(newY - cy, newX - cx);
                    float sweep = endAngle - startA;
                    if (sweep < 0) sweep += 2 * MathF.PI;
                    if (sweep < 0.001f) sweep = 0.001f;
                    component.Parameters["SweepAngle"] = sweep;
                    break;
                }
        }
    }
}
