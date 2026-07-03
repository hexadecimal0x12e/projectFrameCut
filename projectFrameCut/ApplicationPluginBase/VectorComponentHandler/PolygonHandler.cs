using projectFrameCut.ApplicationAPIBase.VectorComponentHandler;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Render.VectorContent.Components;
using Point = projectFrameCut.Drawing.Vector.Point;

namespace projectFrameCut.ApplicationPluginBase.VectorComponentHandler;

public class PolygonHandler : BaseVectorComponentHandler
{
    public override string TypeName => "Polygon";
    public override string DisplayName => "多边形";
    public override string Icon => "⬣";
    public override bool HasDefaultHandles => false;
    protected override IVectorComponent CreateComponent() => new PolygonComponent();

    protected override void AddShapeSpecificProperties(PropertyPanelBuilder builder, IVectorComponent component)
    {
        builder.AddCollapsibleSection("形状", b =>
        {
            b.AddText("多边形顶点通过手柄在画布上编辑。");
        }, defaultExpanded: true);
    }

    protected override Dictionary<string, object> GetDefaultParameters() =>
        new();

    public override IReadOnlyList<ShapeHandleDescriptor> CreateHandles(IVectorComponent component)
    {
        var points = component.Parameters.TryGetValue("Points", out var val) && val is List<Point> pts
            ? pts
            : new List<Point>();

        var handles = new ShapeHandleDescriptor[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            handles[i] = new ShapeHandleDescriptor
            {
                Id = $"v{i}",
                NormalizedX = (float)points[i].X,
                NormalizedY = (float)points[i].Y,
                PositionType = ShapeHandlePositionType.Anchor,
            };
        }
        return handles;
    }

    public override void ApplyHandleDrag(IVectorComponent component, string handleId, float newX, float newY, bool isLive)
    {
        if (!handleId.StartsWith("v") || !int.TryParse(handleId[1..], out int idx))
            return;

        if (component.Parameters.TryGetValue("Points", out var val) && val is List<Point> pts)
        {
            if (idx >= 0 && idx < pts.Count)
                pts[idx] = new Point(newX, newY);
        }
    }
}
