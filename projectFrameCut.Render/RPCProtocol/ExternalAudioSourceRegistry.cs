using System.Collections.Concurrent;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.Render.RPCProtocol;

internal static class ExternalAudioSourceRegistry
{
    private static readonly ConcurrentDictionary<Guid, ClientConnection> Clients = new();
    private static readonly string Identity = Guid.NewGuid().ToString("N");
    private static long revision;
    public static string CacheToken => Volatile.Read(ref revision) == 0 ? string.Empty : $"{Identity}-{Volatile.Read(ref revision)}";

    public static IDisposable Connect(Guid clientId, string clientName,
        Func<RenderRequestEnvelope, CancellationToken, ValueTask<RenderResponseEnvelope>> callback,
        Func<bool> isAuthorized)
    {
        if (clientId == Guid.Empty) throw new UnauthorizedAccessException("External audio source clients require a persistent authorization.");
        var connection = new ClientConnection(clientId, clientName, callback, isAuthorized);
        Clients.AddOrUpdate(clientId, connection, (_, previous) =>
        {
            previous.Disconnect();
            return connection;
        });
        Interlocked.Increment(ref revision);
        Log($"External RPC audio source client connected: {clientId}.");
        return new ConnectionLease(connection);
    }

    public static async Task RegisterAsync(Guid clientId, RegisterExternalAudioSourcesRequest request, CancellationToken ct)
    {
        var connection = GetAuthorized(clientId);
        if (request.Sources.Count > 256) throw new ArgumentException("An external RPC client may register at most 256 audio sources.");
        var sources = new Dictionary<string, ExternalAudioSourceDescriptor>(StringComparer.Ordinal);
        foreach (var source in request.Sources)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(source.SourceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(source.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(source.DecoderName);
            if (source.SourceId.Length > 256 || source.Name.Length > 512 || source.DecoderName.Length > 256)
                throw new ArgumentException("External audio source fields are too long.");
            if (source.ChannelCount < 0 || source.ChannelCount > 256 || source.SamplePerSecond < 0
                || source.PreferredExtensions.Count > 64 || source.Metadata.Count > 64)
                throw new ArgumentException("External audio source metadata is invalid.");
            if (source.PreferredExtensions.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 32)
                || source.Metadata.Any(x => x.Key.Length > 256 || x.Value.Length > 2048))
                throw new ArgumentException("External audio source metadata is too large.");
            var copy = RenderRpcSerializer.Clone(source);
            copy.ClientId = clientId;
            copy.ClientName = connection.ClientName;
            if (!sources.TryAdd(copy.SourceId, copy)) throw new ArgumentException($"Duplicate external audio source ID '{copy.SourceId}'.");
        }
        await connection.SetSourcesAsync(sources, ct).ConfigureAwait(false);
        Log($"External RPC client {clientId} registered {sources.Count} audio source(s).");
    }

    public static async Task UnregisterAsync(Guid clientId, CancellationToken ct)
    {
        await GetAuthorized(clientId).SetSourcesAsync(new Dictionary<string, ExternalAudioSourceDescriptor>(), ct).ConfigureAwait(false);
        Log($"External RPC client {clientId} unregistered its audio sources.");
    }

    public static ExternalAudioSourceCatalog List()
    {
        var result = new ExternalAudioSourceCatalog();
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
            AudioSources = x.Sources.Select(source => RenderRpcSerializer.Clone(source)).ToList()
        }).ToList()
    };

    public static async Task ManageAsync(ManageExternalVideoSourceClientRequest request, CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Action)) throw new ArgumentException("Unknown external audio source client action.");
        await GetAuthorized(request.ClientId).ManageAsync(request.Action, ct).ConfigureAwait(false);
        Interlocked.Increment(ref revision);
        Log($"External RPC audio source client {request.ClientId}: {request.Action}.");
    }

    public static ExternalAudioSourceDescriptor? Find(ExternalAudioSourceReference source)
    {
        if (!Clients.TryGetValue(source.ClientId, out var connection) || !connection.IsAvailable || !connection.Loaded) return null;
        return connection.Sources.FirstOrDefault(x => string.Equals(x.SourceId, source.SourceId, StringComparison.Ordinal));
    }

    public static async ValueTask<ExternalAudioSourceInstance> CreateAsync(ExternalAudioSourceReference source, CancellationToken ct)
    {
        var connection = GetAuthorized(source.ClientId);
        if (!connection.Contains(source.SourceId)) throw new KeyNotFoundException($"External audio source '{source.SourceId}' is not connected.");
        return await connection.CreateAsync(source, ct).ConfigureAwait(false);
    }

    public static ValueTask<ExternalAudioSourceInstance> InitializeAsync(Guid clientId, ExternalAudioSourceStateRequest request, CancellationToken ct) =>
        GetAuthorized(clientId).InitializeAsync(request, ct);

    public static ValueTask<ExternalAudioSamples> ReadAsync(Guid clientId, ExternalAudioSourceReadRequest request, CancellationToken ct) =>
        GetAuthorized(clientId).ReadAsync(request, ct);

    public static async ValueTask ReleaseAsync(Guid clientId, ExternalAudioSourceStateRequest request)
    {
        if (!Clients.TryGetValue(clientId, out var connection) || !connection.IsAvailable) return;
        await connection.ReleaseAsync(request).ConfigureAwait(false);
    }

    private static ClientConnection GetAuthorized(Guid clientId)
    {
        if (!Clients.TryGetValue(clientId, out var connection) || !connection.IsAvailable)
            throw new IOException($"External RPC audio source client '{clientId}' is not connected or authorized.");
        return connection;
    }

    private sealed class ClientConnection(Guid clientId, string clientName,
        Func<RenderRequestEnvelope, CancellationToken, ValueTask<RenderResponseEnvelope>> callback,
        Func<bool> isAuthorized)
    {
        private IReadOnlyDictionary<string, ExternalAudioSourceDescriptor> _sources = new Dictionary<string, ExternalAudioSourceDescriptor>();
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
        public IEnumerable<ExternalAudioSourceDescriptor> Sources => _sources.Values;
        public bool Contains(string sourceId) => _sources.ContainsKey(sourceId);
        public async Task SetSourcesAsync(IReadOnlyDictionary<string, ExternalAudioSourceDescriptor> sources, CancellationToken ct)
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
                    Interlocked.Exchange(ref _loaded, 1);
                    return;
                }
                Interlocked.Exchange(ref _loaded, 0);
                await ReleaseInstancesAsync(_instances.Keys.ToArray()).ConfigureAwait(false);
                if (action == ExternalVideoSourceClientAction.Remove)
                {
                    Volatile.Write(ref _sources, new Dictionary<string, ExternalAudioSourceDescriptor>());
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
                    await InvokeAsync<ExternalAudioSourceStateRequest, EmptyResponse>(RenderOperation.ExternalAudioSourceRelease,
                        new() { InstanceId = id }, timeout.Token).AsTask().WaitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) { Log(ex, $"Release external RPC source instance {id}", this); }
            }
        }

        private void CheckLoaded()
        {
            if (!Loaded) throw new IOException($"External RPC audio source client '{ClientId}' is unloaded.");
        }

        public async ValueTask<ExternalAudioSourceInstance> CreateAsync(ExternalAudioSourceReference source, CancellationToken ct)
        {
            await _instanceGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                CheckLoaded();
                if (!Contains(source.SourceId)) throw new KeyNotFoundException($"External audio source '{source.SourceId}' is not registered.");
                if (_sources[source.SourceId].DecoderName != source.DecoderName) throw new UnauthorizedAccessException("External audio source decoder does not match its catalog.");
                var created = await InvokeAsync<ExternalAudioSourceCreateRequest, ExternalAudioSourceInstance>(
                    RenderOperation.ExternalAudioSourceCreate, new() { Source = source }, ct).ConfigureAwait(false);
                if (created.InstanceId == Guid.Empty || !_instances.TryAdd(created.InstanceId, source.SourceId))
                    throw new InvalidDataException("External audio source returned an invalid or duplicate instance ID.");
                ApplyIdentity(created.Descriptor, _sources[source.SourceId]);
                return created;
            }
            finally { _instanceGate.Release(); }
        }

        public async ValueTask<ExternalAudioSourceInstance> InitializeAsync(ExternalAudioSourceStateRequest request, CancellationToken ct)
        {
            await _instanceGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                CheckLoaded();
                var sourceId = GetInstanceSource(request.InstanceId);
                var initialized = await InvokeAsync<ExternalAudioSourceStateRequest, ExternalAudioSourceInstance>(
                    RenderOperation.ExternalAudioSourceInitialize, request, ct).ConfigureAwait(false);
                if (initialized.InstanceId != request.InstanceId)
                    throw new InvalidDataException("External audio source initialization returned a different instance ID.");
                if (initialized.Descriptor.ChannelCount <= 0 || initialized.Descriptor.ChannelCount > 256 || initialized.Descriptor.SamplePerSecond <= 0)
                    throw new InvalidDataException("External audio source returned invalid channel count or sample rate.");
                ApplyIdentity(initialized.Descriptor, _sources[sourceId]);
                return initialized;
            }
            finally { _instanceGate.Release(); }
        }

        public async ValueTask<ExternalAudioSamples> ReadAsync(ExternalAudioSourceReadRequest request, CancellationToken ct)
        {
            await _instanceGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                CheckLoaded();
                _ = GetInstanceSource(request.State.InstanceId);
                request.Validate();
                var samples = await InvokeAsync<ExternalAudioSourceReadRequest, ExternalAudioSamples>(RenderOperation.ExternalAudioSourceReadSamples, request, ct).ConfigureAwait(false);
                samples.Validate();
                if (samples.SampleCount > request.Count) throw new InvalidDataException("External audio source returned too many samples.");
                return samples;
            }
            finally { _instanceGate.Release(); }
        }

        public async ValueTask ReleaseAsync(ExternalAudioSourceStateRequest request)
        {
            await _instanceGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_instances.ContainsKey(request.InstanceId)) return;
                _ = GetInstanceSource(request.InstanceId);
                try
                {
                    _ = await InvokeAsync<ExternalAudioSourceStateRequest, EmptyResponse>(
                        RenderOperation.ExternalAudioSourceRelease, request, CancellationToken.None).ConfigureAwait(false);
                }
                finally { _instances.TryRemove(request.InstanceId, out _); }
            }
            finally { _instanceGate.Release(); }
        }

        private static void ApplyIdentity(ExternalAudioSourceDescriptor descriptor, ExternalAudioSourceDescriptor catalog)
        {
            descriptor.ClientId = catalog.ClientId;
            descriptor.ClientName = catalog.ClientName;
            descriptor.SourceId = catalog.SourceId;
            descriptor.DecoderName = catalog.DecoderName;
            descriptor.Name = catalog.Name;
        }

        private string GetInstanceSource(Guid instanceId)
        {
            if (instanceId == Guid.Empty || !_instances.TryGetValue(instanceId, out var sourceId))
                throw new UnauthorizedAccessException("External audio source instance does not belong to this authorized connection.");
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
            if (!IsAvailable) throw new IOException($"External RPC audio source client '{ClientId}' is not connected or authorized.");
            var request = new RenderRequestEnvelope { RequestId = Guid.NewGuid(), ClientId = ClientId.ToString("D"), Operation = operation, Payload = RenderRpcSerializer.Serialize(payload) };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var response = await callback(request, timeout.Token).ConfigureAwait(false);
                if (response.RequestId != request.RequestId) throw new RenderPipeException("External audio source callback response ID mismatch.");
                response?.Error?.ThrowAsException();
                return RenderRpcSerializer.Deserialize<TResponse>(response.Payload);
            }
            catch (Exception ex)
            {
                Log(ex, $"External RPC audio source callback {operation} failed for client {ClientId}", this);
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
            Log($"External RPC audio source client disconnected: {connection.ClientId}.");
        }
    }
}
