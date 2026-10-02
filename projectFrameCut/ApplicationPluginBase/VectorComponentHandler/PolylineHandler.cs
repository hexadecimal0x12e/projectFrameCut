using projectFrameCut.ApplicationAPIBase.VectorComponentHandler;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.ApplicationAPIBase.Interaction;
using projectFrameCut.Render.VectorContent.Components;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;
using Point = projectFrameCut.Drawing.Vector.Point;

namespace projectFrameCut.ApplicationPluginBase.VectorComponentHandler;

public class PolylineHandler : BaseVectorComponentHandler
{
    public override string TypeName => "Polyline";
    public override string DisplayName => PPLocalizedResources.VectorContentHandler_Polyline_DisplayName;
    public override string Icon => "\uebbb";
    public override bool HasDefaultHandles => false;
    protected override IVectorComponent CreateComponent() => new PolylineComponent();

    protected override void AddShapeSpecificProperties(PropertyPanelBuilder builder, IVectorComponent component)
    {
        builder.AddCollapsibleSection(PPLocalizedResources.VectorContentHandler_Section_Shape, b =>
        {
            b.AddText(PPLocalizedResources.VectorContentHandler_Polyline_Description);
        }, defaultExpanded: true);
    }

    protected override Dictionary<string, object> GetDefaultParameters() =>
        new();

    public override IReadOnlyList<ShapeHandleDescriptor> CreateHandles(IVectorComponent component)
    {
        var points = ((PolylineComponent)component).GetPoints();

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

        var pts = ((PolylineComponent)component).GetPoints();
        if (idx >= 0 && idx < pts.Count)
            pts[idx] = new Point(newX, newY);
    }
}
