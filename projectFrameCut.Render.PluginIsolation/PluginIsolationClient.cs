using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using System.Text.Json;

namespace projectFrameCut.Render.PluginIsolation;

public sealed class PluginIsolationClient : IAsyncDisposable
{
    private readonly IPluginIsolationSession _session;
    private IsolationPluginDescriptor? _plugin;

    public PluginIsolationClient(IPluginIsolationSession session) => _session = session;

    public string PluginId => _plugin?.PluginId ?? _session.PluginId;
    public IsolationPluginDescriptor Descriptor => _plugin ?? throw new InvalidOperationException("The remote plugin has not been loaded.");

    public async ValueTask LoadAsync(string locale, Dictionary<string, string> configuration, CancellationToken cancellationToken = default)
    {
        _plugin = await _session.InvokeAsync<IsolationLoadPluginRequest, IsolationPluginDescriptor>(RenderOperation.IsolationLoadPlugin, new()
        {
            PluginId = _session.PluginId,
            Locale = locale,
            Configuration = new(configuration),
        }, cancellationToken).ConfigureAwait(false);
    }

    public Dictionary<string, Func<IEffectProvider>> CreateEffectProviders()
    {
        var plugin = _plugin ?? throw new InvalidOperationException("The remote plugin has not been loaded.");
        return plugin.Providers.ToDictionary(x => x.TypeName, x => (Func<IEffectProvider>)(() =>
        {
            var descriptor = Invoke<IsolationCreateProviderRequest, IsolationProviderDescriptor>(RenderOperation.IsolationCreateProvider, new() { TypeName = x.TypeName });
            return new RemoteEffectProvider(_session, descriptor);
        }));
    }

    public Dictionary<string, IVideoSource> CreateVideoSources()
    {
        var plugin = _plugin ?? throw new InvalidOperationException("The remote plugin has not been loaded.");
        return plugin.VideoSources.ToDictionary(x => x.TypeName, x => RemoteVideoSource.CreatePrototype(_session, x));
    }

    public Dictionary<string, Func<string, IAudioSource>> CreateAudioSources()
    {
        var plugin = _plugin ?? throw new InvalidOperationException("The remote plugin has not been loaded.");
        return plugin.AudioSources.ToDictionary(x => x.TypeName, x => (Func<string, IAudioSource>)(source => RemoteAudioSource.CreatePrototype(_session, x).CreateNew(source)));
    }

    public Dictionary<string, Func<string, IVideoWriter>> CreateVideoWriters()
    {
        var plugin = _plugin ?? throw new InvalidOperationException("The remote plugin has not been loaded.");
        return plugin.VideoWriters.ToDictionary(x => x, x => (Func<string, IVideoWriter>)(argument =>
        {
            var state = Invoke<IsolationCreateVideoWriterRequest, IsolationVideoWriterState>(RenderOperation.IsolationCreateVideoWriter,
                new() { TypeName = x, FactoryArgument = argument });
            return new RemoteVideoWriter(_session, state);
        }));
    }

    public Dictionary<string, Func<Guid, Guid, ITransform>> CreateTransforms()
    {
        var plugin = _plugin ?? throw new InvalidOperationException("The remote plugin has not been loaded.");
        return plugin.Transforms.ToDictionary(x => x, x => (Func<Guid, Guid, ITransform>)((left, right) =>
        {
            var state = Invoke<IsolationCreateTransformRequest, IsolationTransformState>(RenderOperation.IsolationCreateTransform,
                new() { TypeName = x, LeftClipId = left.ToString(), RightClipId = right.ToString() });
            return new RemoteTransform(_session, state);
        }));
    }

    public Dictionary<string, Func<IComputer>> CreateComputers()
    {
        var plugin = _plugin ?? throw new InvalidOperationException("The remote plugin has not been loaded.");
        return plugin.Computers.ToDictionary(x => x, x => (Func<IComputer>)(() =>
        {
            var descriptor = Invoke<IsolationProviderCatalogItem, IsolationComputerDescriptor>(RenderOperation.IsolationCreateComputer,
                new() { TypeName = x });
            return new RemoteComputer(_session, descriptor);
        }));
    }

    public Dictionary<string, Func<string, string, ISoundTrack>> CreateSoundTracks()
    {
        var plugin = _plugin ?? throw new InvalidOperationException("The remote plugin has not been loaded.");
        return plugin.SoundTracks.ToDictionary(x => x, x => (Func<string, string, ISoundTrack>)((id, name) =>
        {
            var descriptor = Invoke<IsolationCreateSerializedObjectRequest, IsolationSoundTrackDescriptor>(RenderOperation.IsolationCreateSoundTrack,
                new() { TypeName = x, FirstId = id, SecondId = name });
            return new RemoteSoundTrack(_session, descriptor);
        }));
    }

    public IClip CreateClip(JsonElement element)
    {
        var descriptor = Invoke<IsolationCreateSerializedObjectRequest, IsolationClipObjectDescriptor>(RenderOperation.IsolationCreateClip,
            new() { Json = element.GetRawText() });
        return new RemoteClip(_session, descriptor);
    }

    public ISoundTrack RestoreSoundTrack(JsonElement element)
    {
        var descriptor = Invoke<IsolationCreateSerializedObjectRequest, IsolationSoundTrackDescriptor>(RenderOperation.IsolationCreateSoundTrack,
            new() { Json = element.GetRawText() });
        return new RemoteSoundTrack(_session, descriptor);
    }

    public IVectorComponent CreateVectorComponent(JsonElement element)
    {
        var descriptor = Invoke<IsolationVectorComponentRequest, IsolationVectorComponentDescriptor>(RenderOperation.IsolationCreateVectorComponent,
            new() { Json = element.GetRawText() });
        return new RemoteVectorComponent(_session, descriptor);
    }

    public ITransform RestoreTransform(JsonElement element)
    {
        var state = Invoke<IsolationCreateTransformRequest, IsolationTransformState>(RenderOperation.IsolationCreateTransform,
            new() { Json = element.GetRawText() });
        return new RemoteTransform(_session, state);
    }

    public IReadOnlyCollection<string> ProjectTools => _plugin?.ProjectTools ?? [];

    public async ValueTask<string> InvokeProjectToolAsync(string toolId, string inputJson, CancellationToken cancellationToken = default)
    {
        var response = await _session.InvokeAsync<IsolationInvokeProjectToolRequest, IsolationInvokeProjectToolResponse>(
            RenderOperation.IsolationInvokeProjectTool,
            new() { ToolId = toolId, InputJson = inputJson },
            cancellationToken).ConfigureAwait(false);
        return response.OutputJson;
    }

    public async ValueTask UpdateConfigurationAsync(Dictionary<string, string> configuration, CancellationToken cancellationToken = default) =>
        _ = await _session.InvokeAsync<IsolationUpdateProjectPluginConfigurationRequest, EmptyResponse>(
            RenderOperation.IsolationUpdateProjectPluginConfiguration,
            new() { Configuration = new(configuration) },
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<string?> InvokeProjectLifecycleAsync(RenderOperation operation, string projectJson, CancellationToken cancellationToken = default)
    {
        var response = await _session.InvokeAsync<IsolationPluginProjectRequest, IsolationPluginProjectResponse>(
            operation, new() { ProjectJson = projectJson }, cancellationToken).ConfigureAwait(false);
        return response.HasProject ? response.ProjectJson : null;
    }

    public async ValueTask CreateChannelToAsync(PluginIsolationClient target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(this, target) || string.Equals(PluginId, target.PluginId, StringComparison.Ordinal))
            throw new InvalidOperationException("A plugin cannot create a channel to itself.");
        var descriptor = await target._session.InvokeAsync<IsolationCreatePluginChannelRequest, PluginChannelDescriptor>(
            RenderOperation.IsolationCreatePluginChannel,
            new() { SourcePluginId = PluginId, TargetPluginId = target.PluginId }, cancellationToken).ConfigureAwait(false);
        await _session.InvokeAsync<IsolationRegisterPluginChannelRequest, EmptyResponse>(
            RenderOperation.IsolationRegisterPluginChannel,
            new() { SourcePluginId = PluginId, Descriptor = descriptor }, cancellationToken).ConfigureAwait(false);
        projectFrameCut.Shared.Logger.Log($"Authorized isolated plugin channel '{PluginId}' -> '{target.PluginId}'.");
    }

    internal TResponse Invoke<TRequest, TResponse>(RenderOperation operation, TRequest request)
        => _session.InvokeAsync<TRequest, TResponse>(operation, request).AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
