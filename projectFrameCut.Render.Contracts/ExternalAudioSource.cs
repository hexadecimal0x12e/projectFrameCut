using ProtoBuf;

namespace projectFrameCut.Render.Contracts;

public interface IExternalAudioSourceProvider
{
    IReadOnlyList<ExternalAudioSourceDescriptor> AudioSources { get; }
    ValueTask<ExternalAudioSourceInstance> CreateAudioAsync(ExternalAudioSourceCreateRequest request, CancellationToken cancellationToken = default);
    ValueTask<ExternalAudioSourceInstance> InitializeAudioAsync(ExternalAudioSourceStateRequest request, CancellationToken cancellationToken = default);
    ValueTask<ExternalAudioSamples> ReadSamplesAsync(ExternalAudioSourceReadRequest request, CancellationToken cancellationToken = default);
    ValueTask ReleaseAudioAsync(ExternalAudioSourceStateRequest request, CancellationToken cancellationToken = default);
}

[ProtoContract]
public sealed class RegisterExternalAudioSourcesRequest
{
    [ProtoMember(1)] public List<ExternalAudioSourceDescriptor> Sources { get; set; } = [];
}

[ProtoContract]
public sealed class ExternalAudioSourceCatalog
{
    [ProtoMember(1)] public List<ExternalAudioSourceDescriptor> Sources { get; set; } = [];
}

[ProtoContract]
public sealed class ExternalAudioSourceDescriptor
{
    [ProtoMember(11)] public bool IsDeterministic { get; set; }
    [ProtoMember(1)] public Guid ClientId { get; set; }
    [ProtoMember(2)] public string SourceId { get; set; } = string.Empty;
    [ProtoMember(3)] public string Name { get; set; } = string.Empty;
    [ProtoMember(4)] public string DecoderName { get; set; } = string.Empty;
    [ProtoMember(5)] public List<string> PreferredExtensions { get; set; } = [];
    [ProtoMember(6)] public uint Duration { get; set; }
    [ProtoMember(7)] public int ChannelCount { get; set; }
    [ProtoMember(8)] public int SamplePerSecond { get; set; }
    [ProtoMember(9)] public string ClientName { get; set; } = string.Empty;
    [ProtoMember(10)] public Dictionary<string, string> Metadata { get; set; } = [];
}

[ProtoContract]
public sealed class ExternalAudioSourceReference
{
    [ProtoMember(1)] public Guid ClientId { get; set; }
    [ProtoMember(2)] public string SourceId { get; set; } = string.Empty;
    [ProtoMember(3)] public string DecoderName { get; set; } = string.Empty;
    [ProtoMember(4)] public Dictionary<string, string> Metadata { get; set; } = [];
    [ProtoMember(5)] public ExternalAudioSourceDescriptor Descriptor { get; set; } = new();

    public string Encode() => Convert.ToBase64String(RenderRpcSerializer.Serialize(this)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static ExternalAudioSourceReference Decode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 262144) throw new InvalidDataException("External audio source reference is too large.");
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        return RenderRpcSerializer.Deserialize<ExternalAudioSourceReference>(Convert.FromBase64String(base64));
    }
}

[ProtoContract]
public sealed class ExternalAudioSourceCreateRequest
{
    [ProtoMember(1)] public ExternalAudioSourceReference Source { get; set; } = new();
}

[ProtoContract]
public sealed class ExternalAudioSourceInstance
{
    [ProtoMember(1)] public Guid InstanceId { get; set; }
    [ProtoMember(2)] public ExternalAudioSourceDescriptor Descriptor { get; set; } = new();
}

[ProtoContract]
public sealed class ExternalAudioSourceStateRequest
{
    [ProtoMember(1)] public Guid InstanceId { get; set; }
}

[ProtoContract]
public sealed class ExternalAudioSourceReadRequest
{
    [ProtoMember(1)] public ExternalAudioSourceStateRequest State { get; set; } = new();
    [ProtoMember(2)] public uint StartIndex { get; set; }
    [ProtoMember(3)] public long Count { get; set; }

    public void Validate()
    {
        if (Count < 0 || Count > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(Count));
    }
}

[ProtoContract]
public sealed class ExternalAudioSamples
{
    [ProtoMember(1)] public int ChannelCount { get; set; }
    [ProtoMember(2)] public int SamplePerSecond { get; set; }
    [ProtoMember(3)] public int SampleCount { get; set; }
    /// <summary>Planar float32 PCM, with little-endian samples ordered by channel.</summary>
    [ProtoMember(4)] public byte[] Samples { get; set; } = [];

    public void Validate()
    {
        if (ChannelCount <= 0 || ChannelCount > 256 || SamplePerSecond <= 0 || SampleCount < 0
            || (long)ChannelCount * SampleCount * sizeof(float) != Samples.LongLength)
            throw new InvalidDataException("External audio sample shape is invalid.");
    }
}
