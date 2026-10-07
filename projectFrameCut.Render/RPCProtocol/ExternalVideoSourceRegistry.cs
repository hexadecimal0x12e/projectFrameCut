using System.Collections.Concurrent;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.Render.RPCProtocol;

internal static class ExternalVideoSourceRegistry
{
    private static readonly ConcurrentDictionary<Guid, ClientConnection> Clients = new();
    private static readonly string Identity = Guid.NewGuid().ToString("N");
    private static long revision;
    public static string CacheToken => Volatile.Read(ref revision) == 0 ? string.Empty : $"{Identity}-{Volatile.Read(ref revision)}";

    public static IDisposable Connect(Guid clientId, string clientName,
        Func<RenderRequestEnvelope, CancellationToken, ValueTask<RenderResponseEnvelope>> callback,
        Func<bool> isAuthorized)
    {
        if (clientId == Guid.Empty) throw new UnauthorizedAccessException("External video source clients require a persistent authorization.");
        var connection = new ClientConnection(clientId, clientName, callback, isAuthorized);
        Clients.AddOrUpdate(clientId, connection, (_, previous) =>
        {
            previous.Disconnect();
            return connection;
        });
        Interlocked.Increment(ref revision);
        Log($"External RPC video source client connected: {clientId}.");
        return new ConnectionLease(connection);
    }

    public static async Task RegisterAsync(Guid clientId, RegisterExternalVideoSourcesRequest request, CancellationToken ct)
    {
        var connection = GetAuthorized(clientId);
        if (request.Sources.Count > 256) throw new ArgumentException("An external RPC client may register at most 256 video sources.");
        var sources = new Dictionary<string, ExternalVideoSourceDescriptor>(StringComparer.Ordinal);
        foreach (var source in request.Sources)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(source.SourceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(source.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(source.DecoderName);
            if (source.SourceId.Length > 256 || source.Name.Length > 512 || source.DecoderName.Length > 256)
                throw new ArgumentException("External video source fields are too long.");
            if (source.Width < 0 || source.Width > 65536 || source.Height < 0 || source.Height > 65536
                || !double.IsFinite(source.Fps) || source.Fps < 0 || source.ResultBitsPerPixel is not (0 or 8 or 16)
                || source.PreferredExtensions.Count > 64 || source.Metadata.Count > 64)
                throw new ArgumentException("External video source metadata is invalid.");
            if (source.PreferredExtensions.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 32)
                || source.Metadata.Any(x => x.Key.Length > 256 || x.Value.Length > 2048))
                throw new ArgumentException("External video source metadata is too large.");
            var copy = RenderRpcSerializer.Clone(source);
            copy.ClientId = clientId;
            copy.ClientName = connection.ClientName;
            if (!sources.TryAdd(copy.SourceId, copy)) throw new ArgumentException($"Duplicate external video source ID '{copy.SourceId}'.");
        }
        await connection.SetSourcesAsync(sources, ct).ConfigureAwait(false);
        Log($"External RPC client {clientId} registered {sources.Count} video source(s).");
    }

    public static async Task UnregisterAsync(Guid clientId, CancellationToken ct)
    {
        await GetAuthorized(clientId).SetSourcesAsync(new Dictionary<string, ExternalVideoSourceDescriptor>(), ct).ConfigureAwait(false);
        Log($"External RPC client {clientId} unregistered its video sources.");
    }

    public static ExternalVideoSourceCatalog List()
    {
        var result = new ExternalVideoSourceCatalog();
        foreach (var connection in Clients.Values)
        {
            if (!connection.IsAvailable || !connection.Loaded) continue;
            result.Sources.AddRange(connection.Sources.Select(static x => RenderRpcSerializer.Clone(x)));
        }
        return result;
    }

    public static ExternalVideoSourceClientCatalog ListClients() => new()
    {
        Clients = Clients.Values.Where(x => x.IsAvailable && x.Sources.Any()).Select(x => new ExternalVideoSourceClientStatus
        {
            ClientId = x.ClientId, ClientName = x.ClientName, Loaded = x.Loaded,
            Sources = x.Sources.Select(source => RenderRpcSerializer.Clone(source)).ToList()
        }).ToList()
    };

    public static async Task ManageAsync(ManageExternalVideoSourceClientRequest request, CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Action)) throw new ArgumentException("Unknown external video source client action.");
        await GetAuthorized(request.ClientId).ManageAsync(request.Action, ct).ConfigureAwait(false);
        Interlocked.Increment(ref revision);
        Log($"External RPC video source client {request.ClientId}: {request.Action}.");
    }

    public static ExternalVideoSourceDescriptor? Find(ExternalVideoSourceReference source)
    {
        if (!Clients.TryGetValue(source.ClientId, out var connection) || !connection.IsAvailable || !connection.Loaded) return null;
        return connection.Sources.FirstOrDefault(x => string.Equals(x.SourceId, source.SourceId, StringComparison.Ordinal));
    }

    public static async ValueTask<ExternalVideoSourceInstance> CreateAsync(ExternalVideoSourceReference source, CancellationToken ct)
    {
        var connection = GetAuthorized(source.ClientId);
        if (!connection.Contains(source.SourceId)) throw new KeyNotFoundException($"External video source '{source.SourceId}' is not connected.");
        return await connection.CreateAsync(source, ct).ConfigureAwait(false);
    }

    public static ValueTask<ExternalVideoSourceInstance> InitializeAsync(Guid clientId, ExternalVideoSourceStateRequest request, CancellationToken ct) =>
        GetAuthorized(clientId).InitializeAsync(request, ct);

    public static ValueTask<ExternalVideoFrame> ReadAsync(Guid clientId, ExternalVideoSourceReadRequest request, CancellationToken ct) =>
        GetAuthorized(clientId).ReadAsync(request, ct);

    public static async ValueTask ReleaseAsync(Guid clientId, ExternalVideoSourceStateRequest request)
    {
        if (!Clients.TryGetValue(clientId, out var connection) || !connection.IsAvailable) return;
        await connection.ReleaseAsync(request).ConfigureAwait(false);
    }

    private static ClientConnection GetAuthorized(Guid clientId)
    {
        if (!Clients.TryGetValue(clientId, out var connection) || !connection.IsAvailable)
            throw new IOException($"External RPC video source client '{clientId}' is not connected or authorized.");
        return connection;
    }

    private sealed class ClientConnection(Guid clientId, string clientName,
        Func<RenderRequestEnvelope, CancellationToken, ValueTask<RenderResponseEnvelope>> callback,
        Func<bool> isAuthorized)
    {
        private IReadOnlyDictionary<string, ExternalVideoSourceDescriptor> _sources = new Dictionary<string, ExternalVideoSourceDescriptor>();
        private readonly ConcurrentDictionary<Guid, string> _instances = new();
        private readonly object _authorizationGate = new();
        private readonly SemaphoreSlim _instanceGate = new(1, 1);
        private long _authorizationValidUntil;
        private bool _authorized;
        private int _connected = 1;
        private int _loaded = 1;
        private int _removed;
        public Guid ClientId { get; } = clientId;
        public string ClientName { get; } = clientName;
        public bool IsAvailable => Volatile.Read(ref _connected) != 0 && CheckAuthorization();
        public bool Loaded => Volatile.Read(ref _loaded) != 0;
        public IEnumerable<ExternalVideoSourceDescriptor> Sources => _sources.Values;
        public bool Contains(string sourceId) => _sources.ContainsKey(sourceId);
        public async Task SetSourcesAsync(IReadOnlyDictionary<string, ExternalVideoSourceDescriptor> sources, CancellationToken ct)
        {
            await _instanceGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await ReleaseInstancesAsync(_instances.Where(x => !sources.ContainsKey(x.Value)).Select(x => x.Key).ToArray()).ConfigureAwait(false);
                Volatile.Write(ref _sources, sources);
                if (sources.Count > 0 && Interlocked.Exchange(ref _removed, 0) != 0) Interlocked.Exchange(ref _loaded, 1);
                Interlocked.Increment(ref revision);
            }
            finally { _instanceGate.Release(); }
        }
        public void Disconnect()
        {
            Interlocked.Exchange(ref _connected, 0);
            _instances.Clear();
            Interlocked.Increment(ref revision);
        }

        public async Task ManageAsync(ExternalVideoSourceClientAction action, CancellationToken ct)
        {
            await _instanceGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (action == ExternalVideoSourceClientAction.Load)
                {
                    if (_sources.Count == 0) throw new InvalidOperationException("The client must register video sources before loading them.");
                    Interlocked.Exchange(ref _loaded, 1);
                    return;
                }
                Interlocked.Exchange(ref _loaded, 0);
                await ReleaseInstancesAsync(_instances.Keys.ToArray()).ConfigureAwait(false);
                if (action == ExternalVideoSourceClientAction.Remove)
                {
                    Volatile.Write(ref _sources, new Dictionary<string, ExternalVideoSourceDescriptor>());
                    Interlocked.Exchange(ref _removed, 1);
                }
            }
            finally { _instanceGate.Release(); }
        }

        private async Task ReleaseInstancesAsync(IEnumerable<Guid> ids)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            foreach (var id in ids)
            {
                if (!_instances.TryRemove(id, out _)) continue;
                try
                {
                    await InvokeAsync<ExternalVideoSourceStateRequest, EmptyResponse>(RenderOperation.ExternalVideoSourceRelease,
                        new() { InstanceId = id }, timeout.Token).AsTask().WaitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) { Log(ex, $"Release external RPC source instance {id}", this); }
            }
        }

        private void CheckLoaded()
        {
            if (!Loaded) throw new IOException($"External RPC video source client '{ClientId}' is unloaded.");
        }

        public async ValueTask<ExternalVideoSourceInstance> CreateAsync(ExternalVideoSourceReference source, CancellationToken ct)
        {
            await _instanceGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                CheckLoaded();
                if (!Contains(source.SourceId)) throw new KeyNotFoundException($"External video source '{source.SourceId}' is not registered.");
                var created = await InvokeAsync<ExternalVideoSourceCreateRequest, ExternalVideoSourceInstance>(
                    RenderOperation.ExternalVideoSourceCreate, new() { Source = source }, ct).ConfigureAwait(false);
                if (created.InstanceId == Guid.Empty || !_instances.TryAdd(created.InstanceId, source.SourceId))
                    throw new InvalidDataException("External video source returned an invalid or duplicate instance ID.");
                var allowCaching = created.Descriptor.AllowCachingResult;
                created.Descriptor = RenderRpcSerializer.Clone(_sources[source.SourceId]);
                created.Descriptor.AllowCachingResult &= allowCaching;
                return created;
            }
            finally { _instanceGate.Release(); }
        }

        public async ValueTask<ExternalVideoSourceInstance> InitializeAsync(ExternalVideoSourceStateRequest request, CancellationToken ct)
        {
            await _instanceGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                CheckLoaded();
                var sourceId = GetInstanceSource(request.InstanceId);
                var initialized = await InvokeAsync<ExternalVideoSourceStateRequest, ExternalVideoSourceInstance>(
                    RenderOperation.ExternalVideoSourceInitialize, request, ct).ConfigureAwait(false);
                if (initialized.InstanceId != request.InstanceId)
                    throw new InvalidDataException("External video source initialization returned a different instance ID.");
                var allowCaching = initialized.Descriptor.AllowCachingResult;
                initialized.Descriptor = RenderRpcSerializer.Clone(_sources[sourceId]);
                initialized.Descriptor.AllowCachingResult &= allowCaching;
                return initialized;
            }
            finally { _instanceGate.Release(); }
        }

        public async ValueTask<ExternalVideoFrame> ReadAsync(ExternalVideoSourceReadRequest request, CancellationToken ct)
        {
            await _instanceGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                CheckLoaded();
                _ = GetInstanceSource(request.State.InstanceId);
                return await InvokeAsync<ExternalVideoSourceReadRequest, ExternalVideoFrame>(RenderOperation.ExternalVideoSourceReadFrame, request, ct).ConfigureAwait(false);
            }
            finally { _instanceGate.Release(); }
        }

        public async ValueTask ReleaseAsync(ExternalVideoSourceStateRequest request)
        {
            await _instanceGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_instances.ContainsKey(request.InstanceId)) return;
                _ = GetInstanceSource(request.InstanceId);
                try
                {
                    _ = await InvokeAsync<ExternalVideoSourceStateRequest, EmptyResponse>(
                        RenderOperation.ExternalVideoSourceRelease, request, CancellationToken.None).ConfigureAwait(false);
                }
                finally { _instances.TryRemove(request.InstanceId, out _); }
            }
            finally { _instanceGate.Release(); }
        }

        private string GetInstanceSource(Guid instanceId)
        {
            if (instanceId == Guid.Empty || !_instances.TryGetValue(instanceId, out var sourceId))
                throw new UnauthorizedAccessException("External video source instance does not belong to this authorized connection.");
            return sourceId;
        }

        private bool CheckAuthorization()
        {
            var now = Environment.TickCount64;
            lock (_authorizationGate)
            {
                if (now < _authorizationValidUntil) return _authorized;
                try { _authorized = isAuthorized(); }
                catch (Exception ex)
                {
                    _authorized = false;
                    Log(ex, $"Validate external RPC client {ClientId} authorization", this);
                }
                _authorizationValidUntil = now + 1000;
                return _authorized;
            }
        }

        public async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(RenderOperation operation, TRequest payload, CancellationToken ct)
        {
            if (!IsAvailable) throw new IOException($"External RPC video source client '{ClientId}' is not connected or authorized.");
            var request = new RenderRequestEnvelope { RequestId = Guid.NewGuid(), ClientId = ClientId.ToString("D"), Operation = operation, Payload = RenderRpcSerializer.Serialize(payload) };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var response = await callback(request, timeout.Token).ConfigureAwait(false);
                if (response.RequestId != request.RequestId) throw new RenderPipeException("External video source callback response ID mismatch.");
                response?.Error?.ThrowAsException();
                return RenderRpcSerializer.Deserialize<TResponse>(response.Payload);
            }
            catch (Exception ex)
            {
                Log(ex, $"External RPC video source callback {operation} failed for client {ClientId}", this);
                throw;
            }
        }
    }

    private sealed class ConnectionLease(ClientConnection connection) : IDisposable
    {
        public void Dispose()
        {
            connection.Disconnect();
            if (Clients.TryGetValue(connection.ClientId, out var current) && ReferenceEquals(current, connection))
                Clients.TryRemove(connection.ClientId, out _);
            Log($"External RPC video source client disconnected: {connection.ClientId}.");
        }
    }
}
