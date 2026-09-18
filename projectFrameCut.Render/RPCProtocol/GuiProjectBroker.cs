using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.Render.RPCProtocol;

public sealed class GuiProjectBroker : IDisposable
{
    private sealed class Session(string owner)
    {
        public string Owner { get; } = owner;
        public Channel<GuiProjectWork> Queue { get; } = Channel.CreateBounded<GuiProjectWork>(128);
        public ConcurrentDictionary<Guid, TaskCompletionSource<GuiProjectResult>> Pending { get; } = new();
        public CancellationTokenSource Lifetime { get; } = new();
    }

    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();

    public void Register(Guid id, string owner)
    {
        if (id == Guid.Empty || !_sessions.TryAdd(id, new(owner)))
            throw new ArgumentException("Invalid or duplicate GUI session.");
    }

    public void CheckOwner(Guid id, string owner)
    {
        if (Get(id).Owner != owner) throw new UnauthorizedAccessException("GUI session belongs to another client.");
    }

    private Session Get(Guid id) => _sessions.TryGetValue(id, out var session)
        ? session : throw new InvalidOperationException("The GUI project session is closed or unavailable.");

    public CancellationToken GetLifetime(Guid id) => Get(id).Lifetime.Token;

    public void Unregister(Guid id, string owner)
    {
        CheckOwner(id, owner);
        if (_sessions.TryRemove(id, out var session)) Close(session);
    }

    private static void Close(Session session)
    {
        session.Lifetime.Cancel();
        session.Queue.Writer.TryComplete();
        foreach (var completion in session.Pending.Values)
            completion.TrySetException(new InvalidOperationException("The GUI project session was closed."));
        session.Pending.Clear();
    }

    public async Task<GuiProjectResult> InvokeAsync(Guid id, GuiProjectRequest request, CancellationToken ct)
    {
        var session = Get(id);
        if (!Enum.IsDefined(request.Operation) || request.RequestId == Guid.Empty || request.TimeoutSeconds is < 1 or > 3600
            || request.ParametersJson is null || request.ParametersJson.Length > 1024 * 1024
            || request.ChangeReason is null || request.ChangeReason.Length > 512 || request.ClientName is null || request.ClientName.Length > 128)
            throw new ArgumentException("Invalid GUI project request.");
        var completion = new TaskCompletionSource<GuiProjectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!session.Pending.TryAdd(request.RequestId, completion)) throw new ArgumentException("Duplicate GUI request.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        try
        {
            await session.Queue.Writer.WriteAsync(new() { SessionId = id, Request = request }, timeout.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested)
        { throw new InvalidOperationException("The GUI project session was closed."); }
        finally { session.Pending.TryRemove(request.RequestId, out _); }
    }

    public async Task<GuiProjectWork> TakeAsync(Guid id, string owner, CancellationToken ct)
    {
        CheckOwner(id, owner);
        var session = Get(id);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            while (true)
            {
                var work = await session.Queue.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                if (work.Request is null || session.Pending.ContainsKey(work.Request.RequestId)) return work;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !session.Lifetime.IsCancellationRequested)
        { return new() { SessionId = id }; }
    }

    public void Complete(GuiProjectResult result, string owner)
    {
        CheckOwner(result.SessionId, owner);
        if (Get(result.SessionId).Pending.TryGetValue(result.RequestId, out var completion)) completion.TrySetResult(result);
    }

    public void NotifyConnected(Guid id, string clientName)
    {
        if (string.IsNullOrWhiteSpace(clientName)) return;
        Get(id).Queue.Writer.TryWrite(new() { SessionId = id, ConnectedClientName = clientName });
    }

    public IRenderService Bind(Guid id) => new BoundService(this, id);

    private sealed class BoundService(GuiProjectBroker broker, Guid id) : IRenderService, IRenderConnectionObserver
    {
        public void Connected(string clientId) => broker.NotifyConnected(id, clientId);

        public async ValueTask<RenderResponseEnvelope> DispatchAsync(RenderRequestEnvelope request, CancellationToken cancellationToken = default)
        {
            broker.Get(id);
            if (request.ProtocolVersion < RenderProtocol.MinimumSupportedVersion || request.ProtocolVersion > RenderProtocol.CurrentVersion)
                throw new ArgumentException("Unsupported RPC version.");
            if (request.Operation is RenderOperation.GetProjectHistory or RenderOperation.UndoProjectHistory
                or RenderOperation.RedoProjectHistory or RenderOperation.RestoreProjectHistory)
                return await DispatchHistoryAsync(request, cancellationToken).ConfigureAwait(false);
            byte[] payload = request.Operation switch
            {
                RenderOperation.GetCapabilities => RenderRpcSerializer.Serialize(new RenderCapabilities
                {
                    ProtocolVersion = RenderProtocol.CurrentVersion, MinimumProtocolVersion = RenderProtocol.MinimumSupportedVersion,
                    Operations = [nameof(RenderOperation.GetGuiProjectSession), nameof(RenderOperation.InvokeGuiProject),
                        nameof(RenderOperation.GetProjectHistory), nameof(RenderOperation.UndoProjectHistory),
                        nameof(RenderOperation.RedoProjectHistory), nameof(RenderOperation.RestoreProjectHistory)],
                }),
                RenderOperation.GetGuiProjectSession => RenderRpcSerializer.Serialize(new GuiProjectSession { SessionId = id }),
                RenderOperation.InvokeGuiProject => RenderRpcSerializer.Serialize(await broker.InvokeAsync(id,
                    RenderRpcSerializer.Deserialize<GuiProjectRequest>(request.Payload), cancellationToken).ConfigureAwait(false)),
                _ => throw new UnauthorizedAccessException("Operation is unavailable on a GUI project connection."),
            };
            return new() { RequestId = request.RequestId, Payload = payload };
        }

        private async ValueTask<RenderResponseEnvelope> DispatchHistoryAsync(RenderRequestEnvelope request, CancellationToken cancellationToken)
        {
            var restore = request.Operation == RenderOperation.RestoreProjectHistory
                ? RenderRpcSerializer.Deserialize<RestoreProjectHistoryRequest>(request.Payload) : null;
            var timeoutSeconds = restore?.TimeoutSeconds
                ?? RenderRpcSerializer.Deserialize<ProjectHistoryRequest>(request.Payload).TimeoutSeconds;
            if (timeoutSeconds is < 1 or > 3600) throw new ArgumentException("Invalid history request timeout.");

            var result = await broker.InvokeAsync(id, new GuiProjectRequest
            {
                Operation = request.Operation switch
                {
                    RenderOperation.GetProjectHistory => GuiProjectOperation.GetProjectHistory,
                    RenderOperation.UndoProjectHistory => GuiProjectOperation.UndoProjectHistory,
                    RenderOperation.RedoProjectHistory => GuiProjectOperation.RedoProjectHistory,
                    RenderOperation.RestoreProjectHistory => GuiProjectOperation.RestoreProjectHistory,
                    _ => throw new ArgumentOutOfRangeException(nameof(request.Operation)),
                },
                ParametersJson = restore is null ? "{}" : JsonSerializer.Serialize(new { restore.SnapshotId }),
                TimeoutSeconds = timeoutSeconds,
            }, cancellationToken).ConfigureAwait(false);
            if (result.Error is not null) return new() { RequestId = request.RequestId, Error = result.Error };

            var payload = request.Operation == RenderOperation.GetProjectHistory
                ? RenderRpcSerializer.Serialize(JsonSerializer.Deserialize<ProjectHistory>(result.Json)
                    ?? throw new InvalidOperationException("GUI returned an invalid project history."))
                : RenderRpcSerializer.Serialize(JsonSerializer.Deserialize<ProjectHistoryState>(result.Json)
                    ?? throw new InvalidOperationException("GUI returned an invalid project history state."));
            return new() { RequestId = request.RequestId, Payload = payload };
        }
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values) Close(session);
        _sessions.Clear();
    }
}
