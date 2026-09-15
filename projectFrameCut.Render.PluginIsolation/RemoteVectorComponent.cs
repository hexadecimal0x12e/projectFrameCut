using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using System.Text.Json;

namespace projectFrameCut.Render.PluginIsolation;

internal sealed class RemoteVectorComponent : IVectorComponent
{
    private readonly IPluginIsolationSession _session;
    private readonly long _objectId;

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
        AnimationFrames = JsonSerializer.Deserialize<List<VectorAnimationKeyFrame>>(descriptor.AnimationFramesJson) ?? [];
        AnimatableFields = descriptor.AnimatableFields.ToDictionary(x => x.Id, x => new AnimatableField
        {
            Id = x.Id,
            DisplayName = x.DisplayName,
            Description = x.Description,
            MinimumValue = x.MinimumValue,
            MaximumValue = x.MaximumValue,
        });
    }

    public string FromPlugin { get; }
    public string TypeName { get; }
    public IReadOnlyDictionary<string, AnimatableField> AnimatableFields { get; }
    public string Name { get; set; }
    public Guid Id { get; set; }
    public Dictionary<string, object> Parameters { get; }
    public int Index { get; set; }
    public List<VectorAnimationKeyFrame> AnimationFrames { get; set; }

    public VectorCanvasElement Compute(float index) => ComputeAll(index).First();

    public IEnumerable<VectorCanvasElement> ComputeAll(float index)
    {
        var result = _session.InvokeAsync<IsolationVectorComponentRequest, IsolationVectorElementList>(RenderOperation.IsolationComputeVectorComponent, new()
        {
            ObjectId = _objectId,
            Name = Name,
            InstanceId = Id.ToString(),
            Index = Index,
            Parameters = Parameters.ToDictionary(x => x.Key, x => IsolationValueConverter.FromObject(x.Value)),
            AnimationFramesJson = JsonSerializer.Serialize(AnimationFrames),
            Progress = index,
        }).AsTask().GetAwaiter().GetResult();
        return result.Elements.Select(x => new RemoteVectorCanvasElement(x));
    }
}

internal sealed class RemoteVectorCanvasElement : VectorCanvasElement
{
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
