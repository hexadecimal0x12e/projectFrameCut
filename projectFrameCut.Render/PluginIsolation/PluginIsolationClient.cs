using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Sources;

namespace projectFrameCut.Render.PluginIsolation;

public sealed class PluginIsolationClient : IAsyncDisposable
{
    private readonly IPluginIsolationSession _session;
    private IsolationPluginDescriptor? _plugin;

    public PluginIsolationClient(IPluginIsolationSession session) => _session = session;

    public string PluginId => _plugin?.PluginId ?? _session.PluginId;

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

    internal TResponse Invoke<TRequest, TResponse>(RenderOperation operation, TRequest request)
        => _session.InvokeAsync<TRequest, TResponse>(operation, request).AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
