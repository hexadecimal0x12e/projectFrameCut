using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.Render.RPCProtocol;

public sealed class NamedPipeRenderServer(IRenderService service, bool allowAdditionalPipes = false, string? requestDirectory = null)
{
    private readonly IRenderService _service = service ?? throw new ArgumentNullException(nameof(service));

    public async Task RunAsync(string pipeName, string token, string? expectedParentPid = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("Pipe name is required.", nameof(pipeName));
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Pipe token is required.", nameof(token));

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var parentMonitor = StartParentMonitor(expectedParentPid, lifetime);
        await using var additional = allowAdditionalPipes ? new AdditionalPipeHost(_service, lifetime.Token, requestDirectory) : null;
        try
        {
            await RunListenerAsync(pipeName, token, additional ?? _service, lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            lifetime.Cancel();
            try { await parentMonitor.ConfigureAwait(false); } catch { }
        }
    }

    private static async Task RunListenerAsync(string pipeName, string token, IRenderService service, CancellationToken cancellationToken, TaskCompletionSource? ready = null)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var pipe = new NamedPipeServerStream(
                    pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                var waiting = pipe.WaitForConnectionAsync(cancellationToken);
                ready?.TrySetResult();
                await waiting.ConfigureAwait(false);
                using var closeOnCancel = cancellationToken.Register(() => pipe.Dispose());

                try
                {
                    using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                    var handshakeBytes = await RenderPipeFrame.ReadAsync(pipe, handshakeTimeout.Token).ConfigureAwait(false)
                        ?? throw new RenderPipeException("Render client closed the pipe during handshake.");
                    var handshake = RenderRpcSerializer.Deserialize<RenderPipeHandshake>(handshakeBytes);
                    var accepted = handshake.ProtocolVersion == RenderProtocol.PipeProtocolVersion
                        && string.Equals(handshake.Token, token, StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(handshake.ClientId);
                    var handshakeResponse = new RenderPipeHandshake
                    {
                        ProtocolVersion = RenderProtocol.PipeProtocolVersion,
                        ClientId = handshake.ClientId,
                        Accepted = accepted,
                        Error = accepted ? string.Empty : "Render pipe handshake was rejected.",
                        Capabilities = accepted ? new RenderCapabilities
                        {
                            ProtocolVersion = RenderProtocol.CurrentVersion,
                            MinimumProtocolVersion = RenderProtocol.MinimumSupportedVersion,
                        } : null,
                    };
                    await RenderPipeFrame.WriteAsync(pipe, RenderRpcSerializer.Serialize(handshakeResponse), handshakeTimeout.Token).ConfigureAwait(false);
                    if (!accepted) continue;

                    using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var writeGate = new SemaphoreSlim(1, 1);
                    var requests = new List<Task>();
                    try
                    {
                        while (pipe.IsConnected && !connectionCancellation.IsCancellationRequested)
                        {
                            var bytes = await RenderPipeFrame.ReadAsync(pipe, connectionCancellation.Token).ConfigureAwait(false);
                            if (bytes is null) break;
                            var request = RenderRpcSerializer.Deserialize<RenderRequestEnvelope>(bytes);
                            request.ClientId = handshake.ClientId;
                            requests.Add(DispatchAndWriteAsync(service, pipe, writeGate, request, connectionCancellation.Token));
                            requests.RemoveAll(static task => task.IsCompleted);
                        }
                    }
                    finally
                    {
                        connectionCancellation.Cancel();
                        pipe.Dispose();
                        try { await Task.WhenAll(requests).ConfigureAwait(false); }
                        finally { writeGate.Dispose(); }
                    }
                }
                catch (EndOfStreamException) { }
                catch (IOException) { }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    Log($"Render pipe connection failed ({ex.GetType().Name}).", "warn");
                }
            }
        }
        catch (Exception ex)
        {
            ready?.TrySetException(new RenderPipeException($"Render pipe listener failed ({ex.GetType().Name})."));
            throw;
        }
    }

    private static async Task DispatchAndWriteAsync(IRenderService service, NamedPipeServerStream pipe, SemaphoreSlim writeGate, RenderRequestEnvelope request, CancellationToken cancellationToken)
    {
        RenderResponseEnvelope response;
        try
        {
            response = await service.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            response = new RenderResponseEnvelope
            {
                RequestId = request.RequestId,
                Error = new RemoteError(ex, RenderErrorCode.Canceled, customMessage: "Render request was canceled."),
            };
        }
        catch (Exception ex)
        {
            response = new RenderResponseEnvelope
            {
                RequestId = request.RequestId,
                Error = new RemoteError(ex),
            };
        }

        try
        {
            await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await RenderPipeFrame.WriteAsync(pipe, RenderRpcSerializer.Serialize(response), CancellationToken.None).ConfigureAwait(false); }
            finally { writeGate.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) { pipe.Dispose(); }
        catch (ObjectDisposedException) { }
    }

    private sealed class AdditionalPipeHost(IRenderService service, CancellationToken cancellationToken, string? requestDirectory) : IRenderService, IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        private readonly object _gate = new();
        private readonly List<Task> _listeners = [];
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _pipeLifetimes = new();
        private readonly ExternalRpcRequestBroker? _requests = requestDirectory is null ? null : new(requestDirectory, cancellationToken);
        private bool _disposed;
        private readonly GuiProjectBroker _gui = new();

        public async ValueTask<RenderResponseEnvelope> DispatchAsync(RenderRequestEnvelope request, CancellationToken ct = default)
        {
            if (request.ProtocolVersion < RenderProtocol.MinimumSupportedVersion || request.ProtocolVersion > RenderProtocol.CurrentVersion)
                throw new ArgumentException("Unsupported render protocol version.");
            if (request.Operation is RenderOperation.RegisterGuiProject or RenderOperation.UnregisterGuiProject
                or RenderOperation.GetGuiProjectWork or RenderOperation.CompleteGuiProjectWork or RenderOperation.CreateGuiProjectPipe)
            {
                if (request.Operation == RenderOperation.CompleteGuiProjectWork)
                {
                    _gui.Complete(RenderRpcSerializer.Deserialize<GuiProjectResult>(request.Payload), request.ClientId);
                    return new() { RequestId = request.RequestId, Payload = RenderRpcSerializer.Serialize(new EmptyResponse()) };
                }
                var session = RenderRpcSerializer.Deserialize<GuiProjectSession>(request.Payload);
                if (request.Operation == RenderOperation.RegisterGuiProject) _gui.Register(session.SessionId, request.ClientId);
                else _gui.CheckOwner(session.SessionId, request.ClientId);
                if (request.Operation == RenderOperation.UnregisterGuiProject) _gui.Unregister(session.SessionId, request.ClientId);
                if (request.Operation == RenderOperation.GetGuiProjectWork)
                    return new() { RequestId = request.RequestId, Payload = RenderRpcSerializer.Serialize(await _gui.TakeAsync(session.SessionId, request.ClientId, ct).ConfigureAwait(false)) };
                if (request.Operation == RenderOperation.CreateGuiProjectPipe)
                    return new() { RequestId = request.RequestId, Payload = RenderRpcSerializer.Serialize(new CreateAdditionalPipeResponse { Token = await CreatePipeAsync(ct, session.SessionId).ConfigureAwait(false) }) };
                return new() { RequestId = request.RequestId, Payload = request.Operation == RenderOperation.RegisterGuiProject
                    ? RenderRpcSerializer.Serialize(session) : RenderRpcSerializer.Serialize(new EmptyResponse()) };
            }
            if (request.Operation is RenderOperation.GetExternalRpcRequest or RenderOperation.ResolveExternalRpcRequest)
            {
                if (request.ProtocolVersion < RenderProtocol.MinimumSupportedVersion || request.ProtocolVersion > RenderProtocol.CurrentVersion)
                    throw new ArgumentException("Unsupported render protocol version.");
                if (_requests is null) throw new NotSupportedException("External RPC requests are unavailable.");
                if (request.Operation == RenderOperation.GetExternalRpcRequest)
                    return new() { RequestId = request.RequestId, Payload = RenderRpcSerializer.Serialize(await _requests.GetAsync(ct).ConfigureAwait(false)) };
                var decision = RenderRpcSerializer.Deserialize<ResolveExternalRpcRequest>(request.Payload);
                if (decision.Approved) _gui.CheckOwner(decision.GuiSessionId, request.ClientId);
                await _requests.ResolveAsync(decision,
                    () => CreatePipeAsync(ct, decision.GuiSessionId), RevokePipe, ct).ConfigureAwait(false);
                return new() { RequestId = request.RequestId, Payload = RenderRpcSerializer.Serialize(new EmptyResponse()) };
            }
            if (request.Operation != RenderOperation.CreateAdditionalPipe)
            {
                var response = await service.DispatchAsync(request, ct).ConfigureAwait(false);
                if (request.Operation == RenderOperation.GetCapabilities && response.Error is null)
                {
                    var capabilities = RenderRpcSerializer.Deserialize<RenderCapabilities>(response.Payload);
                    capabilities.Operations.Add(nameof(RenderOperation.CreateAdditionalPipe));
                    capabilities.Operations.Add(nameof(RenderOperation.RegisterGuiProject));
                    if (_requests is not null)
                    {
                        capabilities.Operations.Add(nameof(RenderOperation.GetExternalRpcRequest));
                        capabilities.Operations.Add(nameof(RenderOperation.ResolveExternalRpcRequest));
                    }
                    response.Payload = RenderRpcSerializer.Serialize(capabilities);
                }
                return response;
            }
            if (request.ProtocolVersion < RenderProtocol.MinimumSupportedVersion || request.ProtocolVersion > RenderProtocol.CurrentVersion)
                return new() { RequestId = request.RequestId, Error = new() { Code = RenderErrorCode.ProtocolMismatch, Message = "Unsupported render protocol version." } };

            _ = RenderRpcSerializer.Deserialize<CreateAdditionalPipeRequest>(request.Payload);
            var createdToken = await CreatePipeAsync(ct).ConfigureAwait(false);
            return new() { RequestId = request.RequestId, Payload = RenderRpcSerializer.Serialize(new CreateAdditionalPipeResponse { Token = createdToken }) };
        }

        private async Task<string> CreatePipeAsync(CancellationToken ct, Guid guiSessionId = default)
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ct.ThrowIfCancellationRequested();
                _lifetime.Token.ThrowIfCancellationRequested();
                _listeners.Add(ListenAsync(token, ready, guiSessionId));
            }
            await ready.Task.ConfigureAwait(false);
            Log("Created an additional render RPC pipe.");
            return token;
        }

        private void RevokePipe(string token)
        {
            if (_pipeLifetimes.TryGetValue(token, out var lifetime))
                try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
        }

        private async Task ListenAsync(string token, TaskCompletionSource ready, Guid guiSessionId)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token,
                guiSessionId == Guid.Empty ? CancellationToken.None : _gui.GetLifetime(guiSessionId));
            _pipeLifetimes[token] = lifetime;
            try
            {
                // Only the internal listener receives this management service.
                await RunListenerAsync(RenderProtocol.AdditionalPipePrefix + token, token, guiSessionId == Guid.Empty ? service : _gui.Bind(guiSessionId), lifetime.Token, ready).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Log($"Additional render RPC listener stopped ({ex.GetType().Name}).", "error");
            }
            finally { _pipeLifetimes.TryRemove(token, out _); }
        }

        public async ValueTask DisposeAsync()
        {
            Task[] listeners;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                listeners = _listeners.ToArray();
            }
            _gui.Dispose();
            _lifetime.Cancel();
            if (_requests is not null) await _requests.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAll(listeners).ConfigureAwait(false);
            _lifetime.Dispose();
            Log("Closed additional render RPC pipes.");
        }
    }

    private static Task StartParentMonitor(string? parentPid, CancellationTokenSource cancellation)
    {
        if (!int.TryParse(parentPid, out var pid) || pid <= 0) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            try
            {
                using var parent = System.Diagnostics.Process.GetProcessById(pid);
                await parent.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch { }
            if (!cancellation.IsCancellationRequested) cancellation.Cancel();
        });
    }
}
