using projectFrameCut.ApplicationAPIBase.VectorComponentHandler;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Render.VectorContent.Components;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;
using Point = projectFrameCut.Drawing.Vector.Point;

namespace projectFrameCut.ApplicationPluginBase.VectorComponentHandler;

public class PolygonHandler : BaseVectorComponentHandler
{
    public override string TypeName => "Polygon";
    public override string DisplayName => PPLocalizedResources.VectorContentHandler_Polygon_DisplayName;
    public override string Icon => "\ueb39";
    public override bool HasDefaultHandles => false;
    protected override IVectorComponent CreateComponent() => new PolygonComponent();

    protected override void AddShapeSpecificProperties(PropertyPanelBuilder builder, IVectorComponent component)
    {
        var points = component.Parameters.TryGetValue("Points", out var val) && val is List<Point> pts
            ? pts
            : new List<Point>();
        int count = Math.Max(3, points.Count);

        builder.AddCollapsibleSection(PPLocalizedResources.VectorContentHandler_Section_Shape, b =>
        {
            b.AddEntry("Sides", PPLocalizedResources.VectorContentHandler_Sides, count.ToString(), "3-20", c => c.Keyboard = Keyboard.Numeric, EntryUpdateEventCallMode.OnAnyTextChange);
            b.AddText(PPLocalizedResources.VectorContentHandler_Polygon_Description);
        }, defaultExpanded: true);
    }

    protected override Dictionary<string, object> GetDefaultParameters() =>
        new();

    public override void HandlePropertyChange(IVectorComponent component, PropertyPanelPropertyChangedEventArgs args)
    {
        if (args.Id == "Sides")
        {
            int newSides = 3;
            try
            {
                newSides = Convert.ToInt32(args.Value);
            }
            catch
            {
                // 忽略转换错误，保持默认值
            }
            newSides = Math.Clamp(newSides, 3, 20);

            // 计算当前多边形中心点和平均半径（或使用默认值）
            float cx, cy, avgRadius;
            if (component.Parameters.TryGetValue("Points", out var val) && val is List<Point> existing && existing.Count > 0)
            {
                cx = existing.Average(p => (float)p.X);
                cy = existing.Average(p => (float)p.Y);
                avgRadius = existing.Average(p =>
                {
                    float dx = (float)p.X - cx;
                    float dy = (float)p.Y - cy;
                    return (float)Math.Sqrt(dx * dx + dy * dy);
                });
            }
            else
            {
                cx = 0.5f;
                cy = 0.5f;
                avgRadius = 0.25f;
            }

            // 生成正多边形顶点
            var newPoints = new List<Point>(newSides);
            for (int i = 0; i < newSides; i++)
            {
                double angle = 2 * Math.PI * i / newSides - Math.PI / 2; // 从顶部开始
                newPoints.Add(new Point(
                    cx + avgRadius * (float)Math.Cos(angle),
                    cy + avgRadius * (float)Math.Sin(angle)
                ));
            }
            component.Parameters["Points"] = newPoints;
            return;
        }

        base.HandlePropertyChange(component, args);
    }

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
