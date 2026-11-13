using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using System.Text.Json;

namespace projectFrameCut.Render.VectorContent;

public class EvaluatedVectorComponent : IVectorComponent
{
    public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
    public string TypeName => "EvaluatedVectorComponent";
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Vector";
    public int Index { get; set; }
    public Dictionary<string, object> Parameters { get; set; } = new();

    public EvaluatedVectorComponent() { }
    public EvaluatedVectorComponent(IEnumerable<VectorCanvasElement> elements)
        => Parameters["Elements"] = JsonSerializer.Serialize(elements.Select(e => new ElementData
        {
            X = e.RelativeX, Y = e.RelativeY, BaseX = e.BaseX, BaseY = e.BaseY,
            Rotation = e.Rotation, Layer = e.LayerIndex, Uniform = e.UseUniformScale,
            SourceClipId = e is IVectorClipElementTag tag ? tag.SourceClipId : Guid.Empty,
            Segments = e.Draw().Select(s => new SegmentData { Type = s.GetType().FullName!, Json = JsonSerializer.Serialize(s, s.GetType()) }).ToArray()
        }).ToArray());

    public VectorCanvasElement Compute() => ComputeAll().First();
    public IEnumerable<VectorCanvasElement> ComputeAll()
        => (JsonSerializer.Deserialize<ElementData[]>(Parameters["Elements"].ToString()!) ?? []).Select(e => new StoredElement(e));

    public class SegmentData
    {
        public string Type { get; set; } = "";
        public string Json { get; set; } = "";
    }

    public class ElementData
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float BaseX { get; set; }
        public float BaseY { get; set; }
        public float Rotation { get; set; }
        public int Layer { get; set; }
        public bool Uniform { get; set; }
        public Guid SourceClipId { get; set; }
        public SegmentData[] Segments { get; set; } = [];
    }

    private class StoredElement : VectorCanvasElement, IVectorClipElementTag
    {
        public Guid SourceClipId { get; }
        private readonly VectorSegment[] segments;
        public StoredElement(ElementData e)
        {
            RelativeX = e.X; RelativeY = e.Y; BaseX = e.BaseX; BaseY = e.BaseY;
            Rotation = e.Rotation; LayerIndex = e.Layer; UseUniformScale = e.Uniform;
            SourceClipId = e.SourceClipId;
            segments = e.Segments.Select(s =>
            {
                var type = typeof(VectorSegment).Assembly.GetType(s.Type);
                if (type is null || !typeof(VectorSegment).IsAssignableFrom(type))
                    throw new InvalidDataException($"Unknown vector segment: {s.Type}");
                return (VectorSegment)JsonSerializer.Deserialize(s.Json, type)!;
            }).ToArray();
        }
        public override VectorSegment[] Draw() => segments;
    }
}
