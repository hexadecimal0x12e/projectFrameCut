using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using System.Text.Json;

namespace projectFrameCut.Render.PluginIsolation;

internal sealed class SerializedVectorComponent : IVectorComponent
{
    private readonly List<IsolationVectorElement> elements;
    public string FromPlugin { get; }
    public string TypeName { get; }
    public string Name { get; set; }
    public Guid Id { get; set; }
    public int Index { get; set; }
    public Dictionary<string, object> Parameters { get; }

    public SerializedVectorComponent(JsonElement source, List<IsolationVectorElement> elements)
    {
        FromPlugin = source.GetProperty("FromPlugin").GetString()!;
        TypeName = source.GetProperty("TypeName").GetString()!;
        Name = source.GetProperty("Name").GetString()!;
        Id = source.GetProperty("Id").GetGuid();
        Index = source.GetProperty("Index").GetInt32();
        Parameters = source.GetProperty("Parameters").Deserialize<Dictionary<string, object>>() ?? [];
        this.elements = elements;
    }

    public VectorCanvasElement Compute() => ComputeAll().First();
    public IEnumerable<VectorCanvasElement> ComputeAll() => elements.Select(e => new RemoteVectorCanvasElement(e));
}
