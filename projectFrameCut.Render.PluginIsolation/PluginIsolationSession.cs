using projectFrameCut.Render.Contracts;
using System.Diagnostics;

namespace projectFrameCut.Render.PluginIsolation;

public sealed class PluginIsolationSession : IPluginIsolationSession
{
    private readonly PluginIsolationTransportOptions _options;
    private readonly Func<string, ValueTask> _terminate;
    private readonly IIsolationPayloadExchange _payloads;
    private string _terminationReason = "The backend connection was closed. Reload the plugin to reconnect.";
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
        _payloads = payloads;
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
    public IIsolationPayloadExchange Payloads
    {
        get
        {
            ThrowIfClosed();
            return _payloads;
        }
    }
    public IIsolationResourceBroker Resources { get; }
    public IsolationChannelCapabilities Capabilities { get; }
    public IsolationPayloadKind PreferredPayloadKind { get; }

    public async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(RenderOperation operation, TRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
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
            if (response.Error is not null)
            {
                var error = response.Error;
                if (_options.TerminateOnRemoteError)
                    await AbortAsync($"Operation {operation} failed in the external backend: {error.Message}").ConfigureAwait(false);
                error.ThrowAsException();
            }
            return RenderRpcSerializer.Deserialize<TResponse>(response.Payload);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await AbortAsync($"Operation {operation} exceeded {_options.RequestTimeout}.").ConfigureAwait(false);
            throw new TimeoutException($"Plugin '{PluginId}' did not complete {operation} within {_options.RequestTimeout}.");
        }
        catch (InvalidDataException)
        {
            await AbortAsync($"Operation {operation} returned an invalid protocol payload.").ConfigureAwait(false);
            throw;
        }
        catch (ProtoBuf.ProtoException)
        {
            await AbortAsync($"Operation {operation} returned malformed protobuf data.").ConfigureAwait(false);
            throw;
        }
        catch (RenderPipeException ex)
        {
            await AbortAsync($"The isolation control channel failed during {operation}: {ex.Message}").ConfigureAwait(false);
            throw;
        }
        catch (IOException ex)
        {
            await AbortAsync($"The isolation runtime disconnected during {operation}: {ex.Message}").ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask TerminateAsync(string reason) => AbortAsync(reason);

    private void ThrowIfClosed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new RenderPipeException($"Plugin '{PluginId}' backend session closed: {_terminationReason}");
    }

    private async ValueTask AbortAsync(string reason)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _terminationReason = reason;
        try { await Control.DisposeAsync().ConfigureAwait(false); } catch { }
        try { await Resources.DisposeAsync().ConfigureAwait(false); } catch { }
        try { await _payloads.DisposeAsync().ConfigureAwait(false); } catch { }
        await _terminate(reason).ConfigureAwait(false);
    }

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
            response?.Error?.ThrowAsException();
        }
        catch { }
        await Control.DisposeAsync().ConfigureAwait(false);
        await Resources.DisposeAsync().ConfigureAwait(false);
        await _payloads.DisposeAsync().ConfigureAwait(false);
        await _terminate("Isolation session closed.").ConfigureAwait(false);
    }
}
