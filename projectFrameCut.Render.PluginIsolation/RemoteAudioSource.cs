using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.PluginIsolation;

internal sealed class RemoteAudioSource : IAudioSource<float>
{
    private readonly IPluginIsolationSession _session;
    private readonly string _typeName;
    private long _objectId;

    private RemoteAudioSource(IPluginIsolationSession session, string typeName, string[] extensions, long objectId = 0)
    {
        _session = session;
        _typeName = typeName;
        PreferredExtension = extensions;
        _objectId = objectId;
    }

    public static RemoteAudioSource CreatePrototype(IPluginIsolationSession session, IsolationAudioSourceCatalogItem item) =>
        new(session, item.TypeName, item.PreferredExtensions.ToArray());

    public string[] PreferredExtension { get; private set; }
    public uint Duration { get; private set; }
    public int ChannelCount { get; private set; }
    public int SamplePerSecond { get; private set; }
    public bool Disposed { get; private set; }

    public IAudioSource<float> CreateNew(string newSource)
    {
        var descriptor = Invoke<IsolationCreateAudioSourceRequest, IsolationAudioSourceDescriptor>(RenderOperation.IsolationCreateAudioSource,
            new() { TypeName = _typeName, Source = newSource });
        var result = new RemoteAudioSource(_session, _typeName, descriptor.PreferredExtensions.ToArray(), descriptor.ObjectId);
        result.Apply(descriptor);
        return result;
    }

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (_objectId == 0) return;
        Apply(Invoke<IsolationReleaseObjectRequest, IsolationAudioSourceDescriptor>(RenderOperation.IsolationInitializeAudioSource,
            new() { ObjectId = _objectId }));
    }

    public IAudioSamples<float> GetSample(uint startIndex, long count)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        var response = Invoke<IsolationReadAudioSamplesRequest, IsolationAudioSamplesResponse>(RenderOperation.IsolationReadAudioSamples,
            new() { ObjectId = _objectId, StartIndex = startIndex, Count = count });
        try { return AudioPayloadCodec.ReadAsync(response, _session.Payloads, CancellationToken.None).AsTask().GetAwaiter().GetResult(); }
        finally { _session.Payloads.ReleaseAsync(response.Samples).AsTask().GetAwaiter().GetResult(); }
    }

    public float GetSingleSample(uint index) => GetSample(index, 1).GetSamples(0)[0];

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        if (_objectId != 0)
            try { Invoke<IsolationReleaseObjectRequest, EmptyResponse>(RenderOperation.IsolationReleaseObject, new() { ObjectId = _objectId }); } catch { }
    }

    private void Apply(IsolationAudioSourceDescriptor descriptor)
    {
        _objectId = descriptor.ObjectId;
        PreferredExtension = descriptor.PreferredExtensions.ToArray();
        Duration = descriptor.Duration;
        ChannelCount = descriptor.ChannelCount;
        SamplePerSecond = descriptor.SamplePerSecond;
    }

    private TResponse Invoke<TRequest, TResponse>(RenderOperation operation, TRequest request) =>
        _session.InvokeAsync<TRequest, TResponse>(operation, request).AsTask().GetAwaiter().GetResult();
}
