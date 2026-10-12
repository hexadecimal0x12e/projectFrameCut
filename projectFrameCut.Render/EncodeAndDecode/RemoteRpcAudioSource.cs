using System.Buffers.Binary;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.RPCProtocol;

namespace projectFrameCut.Render.EncodeAndDecode;

public class RemoteRpcAudioSource : IAudioSource<float>
{
    public const string DecoderTypeName = nameof(RemoteRpcAudioSource);
    private readonly ExternalAudioSourceReference source;
    private readonly object gate = new();
    private ExternalAudioSourceDescriptor descriptor;
    private Guid instanceId;

    protected RemoteRpcAudioSource(ExternalAudioSourceReference source, bool useRpcCatalog = true)
    {
        this.source = source;
        descriptor = (useRpcCatalog ? ExternalAudioSourceRegistry.Find(source) : null) ?? source.Descriptor;
    }

    public virtual string TypeName => DecoderTypeName;
    public string[] PreferredExtension => descriptor.PreferredExtensions.ToArray();
    public uint Duration => descriptor.Duration;
    public int ChannelCount => descriptor.ChannelCount;
    public int SamplePerSecond => descriptor.SamplePerSecond;
    public bool Disposed { get; private set; }

    public static string CreatePath(ExternalAudioSourceDescriptor source) => $"#{DecoderTypeName}:{new ExternalAudioSourceReference
    {
        ClientId = source.ClientId, SourceId = source.SourceId, DecoderName = source.DecoderName,
        Metadata = new(source.Metadata), Descriptor = RenderRpcSerializer.Clone(source),
    }.Encode()}";

    public static bool IsPath(string? path) => path?.StartsWith($"#{DecoderTypeName}:", StringComparison.Ordinal) == true;
    public static bool IsExternalPath(string? path) => IsPath(path) || ProjectExternalAudioSource.IsPath(path);
    public static IAudioSource<float> Open(string path) => new RemoteRpcAudioSource(Parse(path));
    public virtual IAudioSource<float> CreateNew(string newSource) => Open(newSource);

    public static bool TryGetDescriptor(string path, out ExternalAudioSourceDescriptor descriptor)
    {
        descriptor = null!;
        try
        {
            var reference = Parse(path);
            descriptor = ExternalAudioSourceRegistry.Find(reference) ?? reference.Descriptor;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or FormatException or ProtoBuf.ProtoException or EndOfStreamException) { return false; }
    }

    public void Initialize()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (instanceId != Guid.Empty) return;
            try
            {
                var created = CreateInstanceAsync(source).AsTask().GetAwaiter().GetResult();
                if (created.InstanceId == Guid.Empty) throw new InvalidDataException("External audio source returned an empty instance ID.");
                instanceId = created.InstanceId;
                var initialized = InitializeInstanceAsync(source.ClientId, State()).AsTask().GetAwaiter().GetResult();
                if (initialized.InstanceId != instanceId || initialized.Descriptor.ChannelCount <= 0
                    || initialized.Descriptor.ChannelCount > 256 || initialized.Descriptor.SamplePerSecond <= 0)
                    throw new InvalidDataException("External audio source initialization returned invalid metadata.");
                descriptor = initialized.Descriptor;
            }
            catch (Exception ex)
            {
                ReleaseInstance();
                Log(ex, "Initialize external audio source", this);
                throw;
            }
        }
    }

    public IAudioSamples<float> GetSample(uint startIndex, long count)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (count < 0 || count > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(count));
            Initialize();
            if (count > (RenderProtocol.MaxPipeFrameBytes - 4096L) / ChannelCount / sizeof(float))
                throw new ArgumentOutOfRangeException(nameof(count), "Requested audio payload is too large.");
            ExternalAudioSamples samples;
            try { samples = ReadInstanceAsync(source.ClientId, new() { State = State(), StartIndex = startIndex, Count = count }).AsTask().GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                ReleaseInstance();
                Initialize();
                samples = ReadInstanceAsync(source.ClientId, new() { State = State(), StartIndex = startIndex, Count = count }).AsTask().GetAwaiter().GetResult();
            }
            samples.Validate();
            if (samples.SampleCount > count || samples.ChannelCount != ChannelCount || samples.SamplePerSecond != SamplePerSecond)
                throw new InvalidDataException("External audio samples do not match the source format or requested count.");
            var channels = new float[samples.ChannelCount][];
            var offset = 0;
            for (int channel = 0; channel < channels.Length; channel++)
            {
                channels[channel] = new float[samples.SampleCount];
                for (int i = 0; i < samples.SampleCount; i++, offset += sizeof(float))
                    channels[channel][i] = BinaryPrimitives.ReadSingleLittleEndian(samples.Samples.AsSpan(offset, sizeof(float)));
            }
            return new FloatAudioSamples { Channels = channels, SampleCount = samples.SampleCount, SamplePerSecond = samples.SamplePerSecond };
        }
    }

    public float GetSingleSample(uint index) => GetSample(index, 1).GetSamples(0)[0];

    public void Dispose()
    {
        lock (gate)
        {
            if (Disposed) return;
            Disposed = true;
            ReleaseInstance();
        }
    }

    private void ReleaseInstance()
    {
        if (instanceId == Guid.Empty) return;
        try { ReleaseInstanceAsync(source.ClientId, State()).AsTask().GetAwaiter().GetResult(); }
        catch (Exception ex) { Log(ex, "Release external audio source", this); }
        finally { instanceId = Guid.Empty; }
    }

    private ExternalAudioSourceStateRequest State() => new() { InstanceId = instanceId };
    protected virtual ValueTask<ExternalAudioSourceInstance> CreateInstanceAsync(ExternalAudioSourceReference source) => ExternalAudioSourceRegistry.CreateAsync(source, CancellationToken.None);
    protected virtual ValueTask<ExternalAudioSourceInstance> InitializeInstanceAsync(Guid clientId, ExternalAudioSourceStateRequest state) => ExternalAudioSourceRegistry.InitializeAsync(clientId, state, CancellationToken.None);
    protected virtual ValueTask<ExternalAudioSamples> ReadInstanceAsync(Guid clientId, ExternalAudioSourceReadRequest request) => ExternalAudioSourceRegistry.ReadAsync(clientId, request, CancellationToken.None);
    protected virtual ValueTask ReleaseInstanceAsync(Guid clientId, ExternalAudioSourceStateRequest state) => ExternalAudioSourceRegistry.ReleaseAsync(clientId, state);

    private static ExternalAudioSourceReference Parse(string path)
    {
        if (!IsPath(path)) throw new ArgumentException("Invalid external RPC audio source path.", nameof(path));
        var source = ExternalAudioSourceReference.Decode(path[($"#{DecoderTypeName}:").Length..]);
        if (source.ClientId == Guid.Empty || string.IsNullOrWhiteSpace(source.SourceId) || string.IsNullOrWhiteSpace(source.DecoderName))
            throw new InvalidDataException("External RPC audio source reference is invalid.");
        return source;
    }
}
