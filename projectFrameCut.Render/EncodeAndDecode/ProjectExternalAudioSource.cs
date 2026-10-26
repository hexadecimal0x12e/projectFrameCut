using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.PluginIsolation;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.RPCProtocol;

namespace projectFrameCut.Render.EncodeAndDecode;

public sealed class ProjectExternalAudioSource : RemoteRpcAudioSource
{
    public new const string DecoderTypeName = nameof(ProjectExternalAudioSource);
    private readonly ProjectExternalSourceHost host;
    private ProjectExternalSourceClient? client;

    private ProjectExternalAudioSource(ExternalAudioSourceReference source, ProjectExternalSourceHost host) : base(source, false) => this.host = host;
    public override string TypeName => DecoderTypeName;
    public new static bool IsPath(string? path) => path?.StartsWith($"#{DecoderTypeName}:", StringComparison.Ordinal) == true;

    public static string CreatePath(Guid importId, ExternalAudioSourceDescriptor descriptor)
    {
        var copy = RenderRpcSerializer.Clone(descriptor);
        copy.ClientId = importId;
        return $"#{DecoderTypeName}:{new ExternalAudioSourceReference { ClientId = importId, SourceId = descriptor.SourceId,
            DecoderName = descriptor.DecoderName, Metadata = new(descriptor.Metadata), Descriptor = copy }.Encode()}";
    }

    private static ExternalAudioSourceReference Parse(string path)
    {
        if (!IsPath(path)) throw new ArgumentException("Invalid project external audio source path.", nameof(path));
        var source = ExternalAudioSourceReference.Decode(path[($"#{DecoderTypeName}:").Length..]);
        if (source.ClientId == Guid.Empty || string.IsNullOrWhiteSpace(source.SourceId) || string.IsNullOrWhiteSpace(source.DecoderName))
            throw new InvalidDataException("Invalid project external audio source reference.");
        return source;
    }

    public new static IAudioSource<float> Open(string path) => Open(path, ProjectExternalSourceRuntime.Current);
    public static IAudioSource<float> Open(string path, ProjectExternalSourceHost host) => new ProjectExternalAudioSource(Parse(path), host);
    public new static bool TryGetDescriptor(string path, out ExternalAudioSourceDescriptor descriptor)
    {
        descriptor = null!;
        try { descriptor = Parse(path).Descriptor; return true; }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or FormatException or ProtoBuf.ProtoException or EndOfStreamException) { return false; }
    }
    public override IAudioSource<float> CreateNew(string newSource) => Open(newSource, host);
    protected override ValueTask<ExternalAudioSourceInstance> CreateInstanceAsync(ExternalAudioSourceReference source)
    {
        client = host.GetClient(source.ClientId);
        return client.CreateAudioAsync(new() { Source = source });
    }
    protected override ValueTask<ExternalAudioSourceInstance> InitializeInstanceAsync(Guid clientId, ExternalAudioSourceStateRequest state) => client!.InitializeAudioAsync(state);
    protected override ValueTask<ExternalAudioSamples> ReadInstanceAsync(Guid clientId, ExternalAudioSourceReadRequest request)
    {
        ProjectExternalSourceRuntime.EnsureAllowed();
        return client!.ReadSamplesAsync(request);
    }
    protected override ValueTask ReleaseInstanceAsync(Guid clientId, ExternalAudioSourceStateRequest state) => client!.ReleaseAudioAsync(state);
}
