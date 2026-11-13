using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using System.Text.Json;

namespace projectFrameCut.Render.PluginIsolation;

internal static class VectorPictureCodec
{
    public static VectorPicture Decode(IEnumerable<IsolationVectorElement> elements) => new()
    {
        Elements = elements.Select(e => (VectorCanvasElement)new RemoteVectorCanvasElement(e)).ToList()
    };

    public static List<IsolationVectorElement> Encode(VectorPicture picture) => picture.Elements.Select(e => new IsolationVectorElement
    {
        RelativeX = e.RelativeX, RelativeY = e.RelativeY, BaseX = e.BaseX, BaseY = e.BaseY,
        Rotation = e.Rotation, LayerIndex = e.LayerIndex, UseUniformScale = e.UseUniformScale,
        SourceClipId = e is IVectorClipElementTag tag ? tag.SourceClipId.ToString() : "",
        Segments = e.Draw().Select(s => new IsolationVectorSegment
        {
            TypeName = s.GetType().Name, Json = JsonSerializer.Serialize(s, s.GetType())
        }).ToList()
    }).ToList();
}
