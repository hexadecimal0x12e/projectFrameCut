using projectFrameCut.Render.Contracts;
using System.Diagnostics;

namespace projectFrameCut.Render.PluginIsolation;

public sealed class PluginIsolationSession : IPluginIsolationSession
{
    private readonly PluginIsolationTransportOptions _options;
    private readonly Func<string, ValueTask> _terminate;
    private int _disposed;

    public PluginIsolationSession(
        string pluginId,
        IIsolationControlChannel control,
        IIsolationPayloadExchange payloads,
        IIsolationResourceBroker resources,
        IsolationChannelCapabilities capabilities,
        PluginIsolationTransportOptions options,
        Func<string, ValueTask> terminate)
    {
        PluginId = pluginId;
        Control = control;
        Payloads = payloads;
        Resources = resources;
        Capabilities = capabilities;
        PreferredPayloadKind = options.PayloadMode ?? (capabilities.PayloadKinds.Contains(IsolationPayloadKind.SharedMemory)
            ? IsolationPayloadKind.SharedMemory
            : capabilities.PayloadKinds.Contains(IsolationPayloadKind.LocalFile) ? IsolationPayloadKind.LocalFile : IsolationPayloadKind.Inline);
        if (!capabilities.PayloadKinds.Contains(PreferredPayloadKind))
            throw new NotSupportedException($"The isolation runtime does not support payload mode '{PreferredPayloadKind}'.");
        _options = options;
        _terminate = terminate;
    }

    public string PluginId { get; }
    public IIsolationControlChannel Control { get; }
    public IIsolationPayloadExchange Payloads { get; }
    public IIsolationResourceBroker Resources { get; }
    public IsolationChannelCapabilities Capabilities { get; }
    public IsolationPayloadKind PreferredPayloadKind { get; }

    public async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(RenderOperation operation, TRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var response = await Control.SendAsync(new RenderRequestEnvelope
            {
                ClientId = $"plugin-host-{Environment.ProcessId}",
                Operation = operation,
                Payload = RenderRpcSerializer.Serialize(request),
            }, timeout.Token).ConfigureAwait(false);
            foreach (var log in response.Logs)
                projectFrameCut.Shared.Logger.Log($"[PluginIsolation:{PluginId}/{operation}/{stopwatch.ElapsedMilliseconds}ms] {log.Message}", log.Level);
            if (response.Error is not null) response.Error.ThrowAsException();
            return RenderRpcSerializer.Deserialize<TResponse>(response.Payload);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await TerminateAsync($"Operation {operation} exceeded {_options.RequestTimeout}.").ConfigureAwait(false);
            throw new TimeoutException($"Plugin '{PluginId}' did not complete {operation} within {_options.RequestTimeout}.");
        }
        catch (InvalidDataException)
        {
            await TerminateAsync($"Operation {operation} returned an invalid protocol payload.").ConfigureAwait(false);
            throw;
        }
        catch (ProtoBuf.ProtoException)
        {
            await TerminateAsync($"Operation {operation} returned malformed protobuf data.").ConfigureAwait(false);
            throw;
        }
        catch (RenderPipeException)
        {
            await TerminateAsync($"The isolation control channel failed during {operation}.").ConfigureAwait(false);
            throw;
        }
        catch (IOException)
        {
            await TerminateAsync($"The isolation runtime disconnected during {operation}.").ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask TerminateAsync(string reason) => _terminate(reason);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var response = await Control.SendAsync(new RenderRequestEnvelope
            {
                ClientId = $"plugin-host-{Environment.ProcessId}",
                Operation = RenderOperation.IsolationShutdown,
                Payload = RenderRpcSerializer.Serialize(new EmptyRequest()),
            }, timeout.Token).ConfigureAwait(false);
            if (response.Error is not null) response.Error.ThrowAsException();
        }
        catch { }
        await Control.DisposeAsync().ConfigureAwait(false);
        await Resources.DisposeAsync().ConfigureAwait(false);
        await Payloads.DisposeAsync().ConfigureAwait(false);
        await _terminate("Isolation session closed.").ConfigureAwait(false);
    }
}
