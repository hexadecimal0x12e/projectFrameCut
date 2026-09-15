namespace projectFrameCut.ApplicationAPIBase.Workspace.Modules;

public sealed class WorkspaceAssistanceSessionChangedEventArgs(Guid? previousSessionId, Guid? currentSessionId) : EventArgs
{
    public Guid? PreviousSessionId { get; } = previousSessionId;
    public Guid? CurrentSessionId { get; } = currentSessionId;
}

public sealed class WorkspaceAssistanceMessageRequest
{
    public required string SourceModuleId { get; init; }
    public required string Message { get; init; }
}

public sealed class WorkspaceAssistanceMessageResponse
{
    public required Guid SessionId { get; init; }
    public required string SourceModuleId { get; init; }
    public required string Text { get; init; }
}

/// <summary>
/// Implemented by workspace modules that want to contribute project-specific context to an assistance session.
/// </summary>
public interface IWorkspaceAssistanceContextProvider
{
    ValueTask<string?> GetAssistanceContextAsync(
        WorkspaceContext context,
        Guid sessionId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Coordinates assistance sessions and extension context within a workspace.
/// The concrete chat UI remains owned by the application layer.
/// </summary>
public sealed class AssistanceModule : WorkspaceModuleBase, IWorkspaceModuleDependencies
{
    private Func<WorkspaceAssistanceMessageRequest, CancellationToken, Task<WorkspaceAssistanceMessageResponse>>? _messageHandler;
    private CancellationTokenSource _lifetimeCancellation = new();

    public const string ModuleId = "assistance.core";

    public override string Id => ModuleId;
    public IReadOnlyCollection<Type> Dependencies { get; } = [typeof(ProjectModule)];
    public Guid? ActiveSessionId { get; private set; }
    public event EventHandler<WorkspaceAssistanceSessionChangedEventArgs>? ActiveSessionChanged;

    internal void SetActiveSession(Guid? sessionId)
    {
        if (ActiveSessionId == sessionId) return;

        var previous = ActiveSessionId;
        ActiveSessionId = sessionId;
        ActiveSessionChanged?.Invoke(this, new WorkspaceAssistanceSessionChangedEventArgs(previous, sessionId));
    }

    public bool CanSendMessage => _messageHandler is not null
        && Context?.Workspace.State == WorkspaceState.Running;

    /// <summary>
    /// Sends a message from another registered workspace module to the active assistance session
    /// and waits for the LLM response.
    /// </summary>
    public Task<WorkspaceAssistanceMessageResponse> SendMessageAsync(
        IWorkspaceModule sourceModule,
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceModule);
        if (Context is not null && !Context.Modules.Modules.Any(module => ReferenceEquals(module, sourceModule)))
            throw new InvalidOperationException($"Module '{sourceModule.Id}' is not registered in this workspace.");
        return SendMessageAsync(sourceModule.Id, message, cancellationToken);
    }

    /// <summary>
    /// Sends a message from a registered workspace module to the active assistance session
    /// and waits for the LLM response.
    /// </summary>
    public async Task<WorkspaceAssistanceMessageResponse> SendMessageAsync(
        string sourceModuleId,
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceModuleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (Context is null) throw new InvalidOperationException("The module is not initialized.");
        if (Context.Workspace.State != WorkspaceState.Running)
            throw new InvalidOperationException("The workspace must be running before assistance messages can be sent.");

        _ = Context.Modules.Get(sourceModuleId);
        var handler = _messageHandler
            ?? throw new InvalidOperationException("No assistance message handler is available.");

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var request = new WorkspaceAssistanceMessageRequest
        {
            SourceModuleId = sourceModuleId,
            Message = message,
        };
        var response = await handler(request, linkedCancellation.Token);
        return new WorkspaceAssistanceMessageResponse
        {
            SessionId = response.SessionId,
            SourceModuleId = sourceModuleId,
            Text = response.Text,
        };
    }

    internal void BindMessageHandler(
        Func<WorkspaceAssistanceMessageRequest, CancellationToken, Task<WorkspaceAssistanceMessageResponse>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _messageHandler = handler;
    }

    public async ValueTask<IReadOnlyList<string>> GetExtensionContextAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (Context is null) throw new InvalidOperationException("The module is not initialized.");

        var result = new List<string>();
        foreach (var provider in Context.Modules.Modules.OfType<IWorkspaceAssistanceContextProvider>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = await provider.GetAssistanceContextAsync(Context, sessionId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(value)) result.Add(value);
        }

        return result.AsReadOnly();
    }

    public override Task StopAsync(CancellationToken cancellationToken = default)
    {
        _lifetimeCancellation.Cancel();
        SetActiveSession(null);
        return Task.CompletedTask;
    }

    public override Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_lifetimeCancellation.IsCancellationRequested)
        {
            _lifetimeCancellation.Dispose();
            _lifetimeCancellation = new CancellationTokenSource();
        }
        return Task.CompletedTask;
    }

    protected override ValueTask DisposeAsyncCore()
    {
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        _messageHandler = null;
        return ValueTask.CompletedTask;
    }
}
