using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using System.Text.Json;

namespace projectFrameCut.Render.PluginIsolation;

internal sealed class RemoteVectorComponent : IVectorComponent, IDisposable
{
    private readonly IPluginIsolationSession _session;
    private readonly long _objectId;
    private bool _disposed;

    public RemoteVectorComponent(IPluginIsolationSession session, IsolationVectorComponentDescriptor descriptor)
    {
        _session = session;
        _objectId = descriptor.ObjectId;
        FromPlugin = descriptor.FromPlugin;
        TypeName = descriptor.TypeName;
        Name = descriptor.Name;
        Id = Guid.Parse(descriptor.InstanceId);
        Index = descriptor.Index;
        Parameters = descriptor.Parameters.ToDictionary(x => x.Key, x => IsolationValueConverter.ToObject(x.Value)!);
    }

    public string FromPlugin { get; }
    public string TypeName { get; }
    public string Name { get; set; }
    public Guid Id { get; set; }
    public Dictionary<string, object> Parameters { get; }
    public int Index { get; set; }

    public VectorCanvasElement Compute() => ComputeAll().First();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.InvokeAsync<IsolationReleaseObjectRequest, EmptyResponse>(RenderOperation.IsolationReleaseObject,
            new() { ObjectId = _objectId }).AsTask().GetAwaiter().GetResult();
    }

    public IEnumerable<VectorCanvasElement> ComputeAll()
    {
        var result = _session.InvokeAsync<IsolationVectorComponentRequest, IsolationVectorElementList>(RenderOperation.IsolationComputeVectorComponent, new()
        {
            ObjectId = _objectId,
            Name = Name,
            InstanceId = Id.ToString(),
            Index = Index,
            Parameters = Parameters.ToDictionary(x => x.Key, x => IsolationValueConverter.FromObject(x.Value)),
        }).AsTask().GetAwaiter().GetResult();
        return result.Elements.Select(x => new RemoteVectorCanvasElement(x));
    }
}

internal sealed class RemoteVectorCanvasElement : VectorCanvasElement, IVectorClipElementTag
{
    public Guid SourceClipId { get; }
    private readonly VectorSegment[] _segments;

    public RemoteVectorCanvasElement(IsolationVectorElement element)
    {
        RelativeX = element.RelativeX;
        RelativeY = element.RelativeY;
        BaseX = element.BaseX;
        BaseY = element.BaseY;
        LayerIndex = element.LayerIndex;
        Rotation = element.Rotation;
        UseUniformScale = element.UseUniformScale;
        SourceClipId = Guid.TryParse(element.SourceClipId, out var id) ? id : Guid.Empty;
        _segments = element.Segments.Select(x => Deserialize(x.TypeName, x.Json)).ToArray();
    }

    public override VectorSegment[] Draw() => _segments;

    private static VectorSegment Deserialize(string typeName, string json) => typeName switch
    {
        nameof(VectorSegment) => JsonSerializer.Deserialize<VectorSegment>(json)!,
        nameof(StraightLineVectorSegment) => JsonSerializer.Deserialize<StraightLineVectorSegment>(json)!,
        nameof(RectangleVectorSegment) => JsonSerializer.Deserialize<RectangleVectorSegment>(json)!,
        nameof(RoundedRectangleVectorSegment) => JsonSerializer.Deserialize<RoundedRectangleVectorSegment>(json)!,
        nameof(EllipseVectorSegment) => JsonSerializer.Deserialize<EllipseVectorSegment>(json)!,
        nameof(CubicBezierVectorSegment) => JsonSerializer.Deserialize<CubicBezierVectorSegment>(json)!,
        nameof(QuadraticBezierVectorSegment) => JsonSerializer.Deserialize<QuadraticBezierVectorSegment>(json)!,
        nameof(ArcVectorSegment) => JsonSerializer.Deserialize<ArcVectorSegment>(json)!,
        nameof(PolygonVectorSegment) => JsonSerializer.Deserialize<PolygonVectorSegment>(json)!,
        nameof(GradientPolygonVectorSegment) => JsonSerializer.Deserialize<GradientPolygonVectorSegment>(json)!,
        nameof(PolylineVectorSegment) => JsonSerializer.Deserialize<PolylineVectorSegment>(json)!,
        _ => throw new NotSupportedException($"Unknown remote vector segment type '{typeName}'."),
    };
}
