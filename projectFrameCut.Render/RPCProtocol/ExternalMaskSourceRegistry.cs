using System.Collections.Concurrent;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.Render.RPCProtocol;

internal static class ExternalMaskSourceRegistry
{
    private static readonly ConcurrentDictionary<Guid, ClientConnection> Clients = new();
    private static readonly string Identity = Guid.NewGuid().ToString("N");
    private static long revision;
    public static string CacheToken => Volatile.Read(ref revision) == 0 ? string.Empty : $"{Identity}-{Volatile.Read(ref revision)}";

    public static IDisposable Connect(Guid clientId, string clientName,
        Func<RenderRequestEnvelope, CancellationToken, ValueTask<RenderResponseEnvelope>> callback, Func<bool> isAuthorized)
    {
        if (clientId == Guid.Empty) throw new UnauthorizedAccessException("External mask source clients require a persistent authorization.");
        var connection = new ClientConnection(clientId, clientName, callback, isAuthorized);
        Clients.AddOrUpdate(clientId, connection, (_, previous) =>
        {
            previous.Disconnect();
            return connection;
        });
        Interlocked.Increment(ref revision);
        Log($"External RPC mask source client connected: {clientId}.");
        return new ConnectionLease(connection);
    }

    public static async Task RegisterAsync(Guid clientId, RegisterExternalMaskSourcesRequest request, CancellationToken ct)
    {
        var connection = GetAuthorized(clientId);
        if (request.Sources.Count > 256) throw new ArgumentException("An external RPC client may register at most 256 mask sources.");
        var sources = new Dictionary<string, ExternalMaskSourceDescriptor>(StringComparer.Ordinal);
        foreach (var source in request.Sources)
        {
            source.Validate();
            var copy = RenderRpcSerializer.Clone(source);
            copy.ClientId = clientId;
            copy.ClientName = connection.ClientName;
            if (!sources.TryAdd(copy.SourceId, copy)) throw new ArgumentException($"Duplicate external mask source ID '{copy.SourceId}'.");
        }
        await connection.SetSourcesAsync(sources, ct).ConfigureAwait(false);
        Log($"External RPC client {clientId} registered {sources.Count} mask source(s).");
    }

    public static async Task UnregisterAsync(Guid clientId, CancellationToken ct)
    {
        await GetAuthorized(clientId).SetSourcesAsync(new Dictionary<string, ExternalMaskSourceDescriptor>(), ct).ConfigureAwait(false);
        Log($"External RPC client {clientId} unregistered its mask sources.");
    }

    public static ExternalMaskSourceCatalog List() => new()
    {
        Sources = Clients.Values.Where(x => x.IsAvailable && x.Loaded)
            .SelectMany(x => x.Sources).Select(x => RenderRpcSerializer.Clone(x)).ToList()
    };

    public static ExternalVideoSourceClientCatalog ListClients() => new()
    {
        Clients = Clients.Values.Where(x => x.IsAvailable && x.Sources.Any()).Select(x => new ExternalVideoSourceClientStatus
        {
            ClientId = x.ClientId, ClientName = x.ClientName, Loaded = x.Loaded,
            MaskSources = x.Sources.Select(source => RenderRpcSerializer.Clone(source)).ToList()
        }).ToList()
    };

    public static async Task ManageAsync(ManageExternalVideoSourceClientRequest request, CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Action)) throw new ArgumentException("Unknown external mask source client action.");
        await GetAuthorized(request.ClientId).ManageAsync(request.Action, ct).ConfigureAwait(false);
        Log($"External RPC mask source client {request.ClientId}: {request.Action}.");
    }

    public static ValueTask<ExternalVideoFrame> ProcessAsync(ExternalMaskSourceProcessRequest request, CancellationToken ct) =>
        GetAuthorized(request.Source.ClientId).ProcessAsync(request, ct);

    private static ClientConnection GetAuthorized(Guid clientId)
    {
        if (!Clients.TryGetValue(clientId, out var connection) || !connection.IsAvailable)
            throw new IOException($"External RPC mask source client '{clientId}' is not connected or authorized.");
        return connection;
    }

    private sealed class ClientConnection(Guid clientId, string clientName,
        Func<RenderRequestEnvelope, CancellationToken, ValueTask<RenderResponseEnvelope>> callback, Func<bool> isAuthorized)
    {
        private IReadOnlyDictionary<string, ExternalMaskSourceDescriptor> sources = new Dictionary<string, ExternalMaskSourceDescriptor>();
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly object authorizationGate = new();
        private long authorizationValidUntil;
        private bool authorized;
        private int connected = 1;
        private int loaded = 1;
        private bool removed;
        public Guid ClientId => clientId;
        public string ClientName => clientName;
        public bool IsAvailable => Volatile.Read(ref connected) != 0 && CheckAuthorization();
        public bool Loaded => Volatile.Read(ref loaded) != 0;
        public IEnumerable<ExternalMaskSourceDescriptor> Sources => sources.Values;

        public async Task SetSourcesAsync(IReadOnlyDictionary<string, ExternalMaskSourceDescriptor> value, CancellationToken ct)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Volatile.Write(ref sources, value);
                if (value.Count > 0 && removed)
                {
                    removed = false;
                    Volatile.Write(ref loaded, 1);
                }
                Interlocked.Increment(ref revision);
            }
            finally { gate.Release(); }
        }

        public async Task ManageAsync(ExternalVideoSourceClientAction action, CancellationToken ct)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Volatile.Write(ref loaded, action == ExternalVideoSourceClientAction.Load ? 1 : 0);
                if (action == ExternalVideoSourceClientAction.Remove)
                {
                    Volatile.Write(ref sources, new Dictionary<string, ExternalMaskSourceDescriptor>());
                    removed = true;
                }
                Interlocked.Increment(ref revision);
            }
            finally { gate.Release(); }
        }

        public void Disconnect()
        {
            Interlocked.Exchange(ref connected, 0);
            Interlocked.Increment(ref revision);
        }

        public async ValueTask<ExternalVideoFrame> ProcessAsync(ExternalMaskSourceProcessRequest process, CancellationToken ct)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!IsAvailable || !Loaded) throw new IOException($"External RPC mask source client '{ClientId}' is unavailable.");
                if (!sources.TryGetValue(process.Source.SourceId, out var descriptor))
                    throw new KeyNotFoundException($"External mask source '{process.Source.SourceId}' is not registered.");
                process.Validate();
                process.Source = new() { ClientId = ClientId, SourceId = descriptor.SourceId, Descriptor = RenderRpcSerializer.Clone(descriptor) };
                var request = new RenderRequestEnvelope { ClientId = ClientId.ToString("D"), Operation = RenderOperation.ExternalMaskSourceProcess,
                    Payload = RenderRpcSerializer.Serialize(process) };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var response = await callback(request, timeout.Token).ConfigureAwait(false);
                if (response.RequestId != request.RequestId) throw new RenderPipeException("External mask source callback response ID mismatch.");
                response.Error?.ThrowAsException();
                var frame = RenderRpcSerializer.Deserialize<ExternalVideoFrame>(response.Payload);
                process.ValidateResult(frame);
                return frame;
            }
            catch (Exception ex) { Log(ex, $"Process external RPC mask source {ClientId}/{process.Source.SourceId}", this); throw; }
            finally { gate.Release(); }
        }

        private bool CheckAuthorization()
        {
            lock (authorizationGate)
            {
                if (Environment.TickCount64 < authorizationValidUntil) return authorized;
                try { authorized = isAuthorized(); }
                catch (Exception ex) { authorized = false; Log(ex, $"Validate external RPC mask client {ClientId} authorization", this); }
                authorizationValidUntil = Environment.TickCount64 + 1000;
                return authorized;
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
            Log($"External RPC mask source client disconnected: {connection.ClientId}.");
        }
    }
}
