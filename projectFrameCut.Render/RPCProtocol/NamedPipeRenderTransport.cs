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

    private static async Task RunListenerAsync(string pipeName, string token, IRenderService service, CancellationToken cancellationToken, TaskCompletionSource? ready = null, Guid expectedExternalClientId = default)
    {
        try
        {
            var connected = false;
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
                        && !string.IsNullOrWhiteSpace(handshake.ClientId)
                        && (expectedExternalClientId == Guid.Empty || string.Equals(handshake.ClientId, expectedExternalClientId.ToString("D"), StringComparison.OrdinalIgnoreCase));
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
                    connected = true;
                    if (service is IRenderConnectionObserver observer) observer.Connected(handshake.ClientId);

                    using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var writeGate = new SemaphoreSlim(1, 1);
                    var callbacks = new ConnectionCallbacks(pipe, writeGate, connectionCancellation.Token);
                    var externalConnection = service is IExternalVideoSourceConnectionHost host
                        ? host.Connect(handshake.ClientId, callbacks.InvokeAsync)
                        : null;
                    var requests = new List<Task>();
                    try
                    {
                        while (pipe.IsConnected && !connectionCancellation.IsCancellationRequested)
                        {
                            var bytes = await RenderPipeFrame.ReadAsync(pipe, connectionCancellation.Token).ConfigureAwait(false);
                            if (bytes is null) break;
                            var request = RenderRpcSerializer.Deserialize<RenderRequestEnvelope>(bytes);
                            request.ClientId = handshake.ClientId;
                            if (request.Operation == RenderOperation.CompleteExternalVideoSourceCallback)
                            {
                                callbacks.Complete(RenderRpcSerializer.Deserialize<RenderResponseEnvelope>(request.Payload));
                                continue;
                            }
                            requests.Add(DispatchAndWriteAsync(service, pipe, writeGate, request, connectionCancellation.Token));
                            requests.RemoveAll(static task => task.IsCompleted);
                        }
                    }
                    finally
                    {
                        connectionCancellation.Cancel();
                        externalConnection?.Dispose();
                        callbacks.Dispose();
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
                if (connected) break;
            }
        }
        catch (Exception ex)
        {
            ready?.TrySetException(new RenderPipeException($"Render pipe listener failed ({ex.GetType().Name})."));
            throw;
        }
    }

    private interface IExternalVideoSourceConnectionHost
    {
        IDisposable Connect(string clientId, Func<RenderRequestEnvelope, CancellationToken, ValueTask<RenderResponseEnvelope>> callback);
    }

    private sealed class ConnectionCallbacks(NamedPipeServerStream pipe, SemaphoreSlim writeGate, CancellationToken lifetime) : IDisposable
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RenderResponseEnvelope>> _pending = new();

        public async ValueTask<RenderResponseEnvelope> InvokeAsync(RenderRequestEnvelope request, CancellationToken ct)
        {
            var completion = new TaskCompletionSource<RenderResponseEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(request.RequestId, completion)) throw new RenderPipeException($"Duplicate external video source callback ID '{request.RequestId}'.");
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime);
                await writeGate.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    linked.Token.ThrowIfCancellationRequested();
                    await RenderPipeFrame.WriteAsync(pipe, RenderRpcSerializer.Serialize(new RenderResponseEnvelope
                    {
                        RequestId = Guid.Empty,
                        CallbackRequest = request,
                    }), CancellationToken.None).ConfigureAwait(false);
                }
                finally { writeGate.Release(); }
                return await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            finally { _pending.TryRemove(request.RequestId, out _); }
        }

        public void Complete(RenderResponseEnvelope response)
        {
            if (_pending.TryRemove(response.RequestId, out var completion)) completion.TrySetResult(response);
            else Log($"Ignored late external video source callback {response.RequestId}.", "warn");
        }

        public void Dispose()
        {
            var error = new RenderPipeException("External video source client disconnected.");
            foreach (var completion in _pending.Values) completion.TrySetException(error);
            _pending.Clear();
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
        catch (UnauthorizedAccessException ex)
        {
            response = new RenderResponseEnvelope
            {
                RequestId = request.RequestId,
                Error = new RemoteError(ex, RenderErrorCode.Unauthorized),
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
        private readonly string? _authorizationPath = requestDirectory is null ? null : ExternalRpcAuthorizationStore.GetPath(requestDirectory);
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
                    (clientId, clientName) => CreatePipeAsync(ct, decision.GuiSessionId, clientId, clientName), RevokePipe, ct).ConfigureAwait(false);
                return new() { RequestId = request.RequestId, Payload = RenderRpcSerializer.Serialize(new EmptyResponse()) };
            }
            if (request.Operation is RenderOperation.RegisterExternalVideoSources or RenderOperation.UnregisterExternalVideoSources or RenderOperation.ListExternalVideoSources)
            {
                if (request.Operation == RenderOperation.ListExternalVideoSources)
                    return new() { RequestId = request.RequestId, Payload = RenderRpcSerializer.Serialize(ExternalVideoSourceRegistry.List()) };
                if (!Guid.TryParse(request.ClientId, out var clientId)) throw new UnauthorizedAccessException("External video source client ID is invalid.");
                if (request.Operation == RenderOperation.RegisterExternalVideoSources)
                    ExternalVideoSourceRegistry.Register(clientId, RenderRpcSerializer.Deserialize<RegisterExternalVideoSourcesRequest>(request.Payload));
                else ExternalVideoSourceRegistry.Unregister(clientId);
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
                        capabilities.Operations.Add(nameof(RenderOperation.ListExternalVideoSources));
                    }
                    response.Payload = RenderRpcSerializer.Serialize(capabilities);
                }
                return response;
            }
            if (request.ProtocolVersion < RenderProtocol.MinimumSupportedVersion || request.ProtocolVersion > RenderProtocol.CurrentVersion)
                return new() { RequestId = request.RequestId, Error = new() { Code = RenderErrorCode.ProtocolMismatch, Message = "Unsupported render protocol version." } };

            _ = RenderRpcSerializer.Deserialize<EmptyRequest>(request.Payload);
            var createdToken = await CreatePipeAsync(ct).ConfigureAwait(false);
            return new() { RequestId = request.RequestId, Payload = RenderRpcSerializer.Serialize(new CreateAdditionalPipeResponse { Token = createdToken }) };
        }

        private async Task<string> CreatePipeAsync(CancellationToken ct, Guid guiSessionId = default, Guid externalClientId = default, string clientName = "")
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ct.ThrowIfCancellationRequested();
                _lifetime.Token.ThrowIfCancellationRequested();
                _listeners.Add(ListenAsync(token, ready, guiSessionId, externalClientId, clientName));
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

        private async Task ListenAsync(string token, TaskCompletionSource ready, Guid guiSessionId, Guid externalClientId, string clientName)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token,
                guiSessionId == Guid.Empty ? CancellationToken.None : _gui.GetLifetime(guiSessionId));
            _pipeLifetimes[token] = lifetime;
            try
            {
                IRenderService pipeService = guiSessionId == Guid.Empty ? service : _gui.Bind(guiSessionId);
                if (!string.IsNullOrWhiteSpace(clientName))
                    pipeService = new ExternalRpcMetadataPipeService(pipeService, clientName);
                if (externalClientId != Guid.Empty)
                    pipeService = new ExternalVideoSourcePipeService(this, pipeService, externalClientId, clientName);
                // Only the internal listener receives this management service.
                await RunListenerAsync(RenderProtocol.AdditionalPipePrefix + token, token, pipeService, lifetime.Token, ready, externalClientId).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Log($"Additional render RPC listener stopped ({ex.GetType().Name}).", "error");
            }
            finally { _pipeLifetimes.TryRemove(token, out _); }
        }

        private sealed class ExternalVideoSourcePipeService(AdditionalPipeHost host, IRenderService inner, Guid clientId, string clientName) : IRenderService, IExternalVideoSourceConnectionHost, IRenderConnectionObserver
        {
            public void Connected(string connectedClientId) => (inner as IRenderConnectionObserver)?.Connected(connectedClientId);

            public IDisposable Connect(string connectedClientId, Func<RenderRequestEnvelope, CancellationToken, ValueTask<RenderResponseEnvelope>> callback)
            {
                if (!string.Equals(connectedClientId, clientId.ToString("D"), StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("External RPC client ID does not match its authorization.");
                return ExternalVideoSourceRegistry.Connect(clientId, clientName, callback, () => host.IsAuthorized(clientId));
            }

            public async ValueTask<RenderResponseEnvelope> DispatchAsync(RenderRequestEnvelope request, CancellationToken cancellationToken = default)
            {
                if (request.Operation is RenderOperation.RegisterExternalVideoSources or RenderOperation.UnregisterExternalVideoSources)
                    return await host.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
                var response = await inner.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
                if (request.Operation == RenderOperation.GetCapabilities && response.Error is null)
                {
                    var capabilities = RenderRpcSerializer.Deserialize<RenderCapabilities>(response.Payload);
                    capabilities.Operations.Add(nameof(RenderOperation.RegisterExternalVideoSources));
                    capabilities.Operations.Add(nameof(RenderOperation.UnregisterExternalVideoSources));
                    response.Payload = RenderRpcSerializer.Serialize(capabilities);
                }
                return response;
            }
        }

        private sealed class ExternalRpcMetadataPipeService(IRenderService inner, string clientName) : IRenderService, IRenderConnectionObserver
        {
            public void Connected(string _) => (inner as IRenderConnectionObserver)?.Connected(clientName);

            public ValueTask<RenderResponseEnvelope> DispatchAsync(RenderRequestEnvelope request, CancellationToken cancellationToken = default)
            {
                if (request.Operation == RenderOperation.InvokeGuiProject)
                {
                    var projectRequest = RenderRpcSerializer.Deserialize<GuiProjectRequest>(request.Payload);
                    projectRequest.ClientName = clientName;
                    request.Payload = RenderRpcSerializer.Serialize(projectRequest);
                }
                return inner.DispatchAsync(request, cancellationToken);
            }
        }

        private bool IsAuthorized(Guid clientId) => _authorizationPath is not null
            && ExternalRpcAuthorizationStore.Find(_authorizationPath, clientId) is { Revoked: false };

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

internal interface IRenderConnectionObserver
{
    void Connected(string clientId);
}
