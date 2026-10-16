using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.PluginIsolation;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RPCProtocol;

namespace projectFrameCut.Render.Sources;

public class RemoteRpcMaskSource : MaskSource
{
    protected readonly ExternalMaskSourceReference Source;
    private readonly Func<ExternalMaskSourceProcessRequest, CancellationToken, ValueTask<ExternalVideoFrame>>? processor;

    public RemoteRpcMaskSource(ExternalMaskSourceDescriptor descriptor,
        Func<ExternalMaskSourceProcessRequest, CancellationToken, ValueTask<ExternalVideoFrame>>? processor = null)
        : this(CreateReference(descriptor), CreatePath(descriptor), processor) { }

    protected RemoteRpcMaskSource(ExternalMaskSourceReference source, string typeName,
        Func<ExternalMaskSourceProcessRequest, CancellationToken, ValueTask<ExternalVideoFrame>>? processor = null)
    {
        source.Descriptor.Validate();
        Source = source;
        TypeName = typeName;
        this.processor = processor;
        Fields = source.Descriptor.Fields.Select(f => new EffectArgumentFieldDescriptor
        {
            Id = f.Id, TypeName = f.TypeName, FromPlugin = f.FromPlugin, FieldType = (EffectArgumentFieldType)f.FieldType,
            DefaultValue = f.DefaultValue, MinValue = f.MinimumValue, MaxValue = f.MaximumValue,
            PresetOptions = f.PresetOptions.ToArray(), Remarks = f.Remarks, IsDynamic = f.IsDynamic,
        }).ToArray();
    }

    public override string TypeName { get; }
    public ExternalMaskSourceDescriptor Descriptor => Source.Descriptor;
    public override IReadOnlyList<EffectArgumentFieldDescriptor> Fields { get; }

    protected static ExternalMaskSourceReference CreateReference(ExternalMaskSourceDescriptor descriptor) => new()
    {
        ClientId = descriptor.ClientId, SourceId = descriptor.SourceId, Descriptor = RenderRpcSerializer.Clone(descriptor),
    };

    public static string CreatePath(ExternalMaskSourceDescriptor descriptor) => $"#{nameof(RemoteRpcMaskSource)}:{CreateReference(descriptor).Encode()}";
    public static bool IsPath(string? path) => path?.StartsWith($"#{nameof(RemoteRpcMaskSource)}:", StringComparison.Ordinal) == true;
    public static RemoteRpcMaskSource Open(string path)
    {
        if (!IsPath(path)) throw new ArgumentException("Invalid external RPC mask source path.", nameof(path));
        return new(ExternalMaskSourceReference.Decode(path[($"#{nameof(RemoteRpcMaskSource)}:").Length..]), path);
    }

    public override IPicture Process(IPicture backdrop, IReadOnlyDictionary<string, object> arguments, float scale, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var request = new ExternalMaskSourceProcessRequest
        {
            Source = Source, Backdrop = ExternalVideoFrameCodec.Encode(backdrop), Scale = scale,
            Arguments = Fields.ToDictionary(f => f.Id, f => IsolationValueConverter.FromObject(ReadArgument(f.Id, arguments.GetValueOrDefault(f.Id)))),
        };
        request.Validate();
        var frame = ProcessAsync(request, token).AsTask().GetAwaiter().GetResult();
        token.ThrowIfCancellationRequested();
        request.ValidateResult(frame);
        return ExternalVideoFrameCodec.Decode(frame);
    }

    protected virtual ValueTask<ExternalVideoFrame> ProcessAsync(ExternalMaskSourceProcessRequest request, CancellationToken token) =>
        processor is not null ? processor(request, token) : ExternalMaskSourceRegistry.ProcessAsync(request, token);
}
