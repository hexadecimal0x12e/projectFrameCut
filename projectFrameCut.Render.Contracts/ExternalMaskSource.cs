using ProtoBuf;

namespace projectFrameCut.Render.Contracts;

public interface IExternalMaskSourceProvider
{
    IReadOnlyList<ExternalMaskSourceDescriptor> MaskSources { get; }
    ValueTask<ExternalVideoFrame> ProcessMaskAsync(ExternalMaskSourceProcessRequest request, CancellationToken cancellationToken = default);
}

[ProtoContract]
public sealed class RegisterExternalMaskSourcesRequest
{
    [ProtoMember(1)] public List<ExternalMaskSourceDescriptor> Sources { get; set; } = [];
}

[ProtoContract]
public sealed class ExternalMaskSourceCatalog
{
    [ProtoMember(1)] public List<ExternalMaskSourceDescriptor> Sources { get; set; } = [];
}

[ProtoContract]
public sealed class ExternalMaskSourceDescriptor
{
    [ProtoMember(1)] public Guid ClientId { get; set; }
    [ProtoMember(2)] public string SourceId { get; set; } = string.Empty;
    [ProtoMember(3)] public string Name { get; set; } = string.Empty;
    [ProtoMember(4)] public string ClientName { get; set; } = string.Empty;
    [ProtoMember(5)] public List<IsolationFieldDescriptor> Fields { get; set; } = [];

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        if (SourceId.Length > 256 || Name.Length > 512 || Fields.Count > 256
            || Fields.Any(f => string.IsNullOrWhiteSpace(f.Id) || f.Id.Length > 256
                || f.DefaultValue.Length > 2048 || f.MinimumValue.Length > 256 || f.MaximumValue.Length > 256
                || f.PresetOptions.Count > 256 || f.PresetOptions.Any(x => x.Length > 2048) || f.Remarks.Length > 4096)
            || Fields.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != Fields.Count)
            throw new InvalidDataException("External mask source fields are invalid.");
        if (RenderRpcSerializer.Serialize(this).Length > 128 * 1024)
            throw new InvalidDataException("External mask source descriptor is too large.");
    }
}

[ProtoContract]
public sealed class ExternalMaskSourceReference
{
    [ProtoMember(1)] public Guid ClientId { get; set; }
    [ProtoMember(2)] public string SourceId { get; set; } = string.Empty;
    [ProtoMember(3)] public ExternalMaskSourceDescriptor Descriptor { get; set; } = new();

    public string Encode() => Convert.ToBase64String(RenderRpcSerializer.Serialize(this)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static ExternalMaskSourceReference Decode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 262144) throw new InvalidDataException("External mask source reference is too large.");
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        var source = RenderRpcSerializer.Deserialize<ExternalMaskSourceReference>(Convert.FromBase64String(base64));
        if (source.ClientId == Guid.Empty || string.IsNullOrWhiteSpace(source.SourceId)
            || source.Descriptor.SourceId != source.SourceId)
            throw new InvalidDataException("External mask source reference is invalid.");
        source.Descriptor.Validate();
        return source;
    }
}

[ProtoContract]
public sealed class ExternalMaskSourceProcessRequest
{
    [ProtoMember(1)] public ExternalMaskSourceReference Source { get; set; } = new();
    [ProtoMember(2)] public ExternalVideoFrame Backdrop { get; set; } = new();
    [ProtoMember(3)] public Dictionary<string, IsolationValue> Arguments { get; set; } = [];
    [ProtoMember(4)] public float Scale { get; set; } = 1;

    public void Validate()
    {
        Backdrop.Validate();
        if (!float.IsFinite(Scale) || Scale <= 0 || Arguments.Count > 256)
            throw new InvalidDataException("External mask source request is invalid.");
    }

    public void ValidateResult(ExternalVideoFrame frame)
    {
        frame.Validate();
        if (frame.Width != Backdrop.Width || frame.Height != Backdrop.Height)
            throw new InvalidDataException("External mask source changed the backdrop dimensions.");
    }
}

[ProtoContract]
public sealed class ProjectExternalMaskSourceProcessRequest
{
    [ProtoMember(1)] public ExternalMaskSourceProcessRequest Request { get; set; } = new();
    [ProtoMember(2)] public IsolationPayloadReference Backdrop { get; set; } = new();
}
