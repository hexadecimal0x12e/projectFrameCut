using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.VectorContent.Components;
using projectFrameCut.Render.RenderAPIBase.VectorContent;

namespace projectFrameCut.Render.VectorContent;

public class VectorViewportElement : VectorCanvasElement, IVectorClipElementTag
{
    public Guid SourceClipId { get; set; }
    private VectorSegment[] segments;

    public VectorViewportElement(VectorCanvasElement source, float canvasWidth, float canvasHeight, float x, float y, float width, float height)
    {
        float sx = source.UseUniformScale ? Math.Min(canvasWidth, canvasHeight) : canvasWidth;
        float sy = source.UseUniformScale ? Math.Min(canvasWidth, canvasHeight) : canvasHeight;
        RelativeX = (source.BaseX * canvasWidth + source.RelativeX * sx - x) / width;
        RelativeY = (source.BaseY * canvasHeight + source.RelativeY * sy - y) / height;
        LayerIndex = source.LayerIndex;
        SourceClipId = source is IVectorClipElementTag tag ? tag.SourceClipId : Guid.Empty;
        segments = source.Draw().Select(s => ComponentGroup.TransformSegment(
            ComponentGroup.TransformSegment(s, sx, sy, 1, 0),
            1 / width, 1 / height, MathF.Cos(source.Rotation), MathF.Sin(source.Rotation))).ToArray();
    }

    public void TransformSegments(Func<VectorSegment, VectorSegment> transform) => segments = segments.Select(transform).ToArray();

    public override VectorSegment[] Draw() => segments;
}
