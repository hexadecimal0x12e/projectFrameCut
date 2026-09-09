using System.Management.Automation;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.PowerShell;

public abstract class ProjectHistoryCmdlet : CancellableCmdlet
{
    [Parameter][ValidateRange(1, 3600)] public int TimeoutSeconds { get; set; } = 60;

    protected T InvokeRpc<T>(Func<RenderClient, CancellationToken, ValueTask<T>> action)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds + 2));
        return action(Connection.Current.Client, timeout.Token).AsTask().GetAwaiter().GetResult();
    }
}

[Cmdlet(VerbsCommon.Get, "ProjectHistory")]
[OutputType(typeof(ProjectHistory))]
public sealed class GetProjectHistoryCommand : ProjectHistoryCmdlet
{
    protected override void ProcessRecord()
    {
        try
        {
            WriteObject(InvokeRpc((client, ct) => client.GetProjectHistoryAsync(new() { TimeoutSeconds = TimeoutSeconds }, ct)));
        }
        catch (Exception ex) { Fail(ex); }
    }
}

public abstract class ProjectHistoryWriteCmdlet : ProjectHistoryCmdlet
{
    [Parameter] public SwitchParameter PassThru { get; set; }
    protected abstract string Action { get; }
    protected virtual string Target => $"Project session {Connection.Current.SessionId}";
    protected abstract ValueTask<ProjectHistoryState> Invoke(RenderClient client, CancellationToken cancellationToken);

    protected override void ProcessRecord()
    {
        try
        {
            if (!ShouldProcess(Target, Action)) return;
            var state = InvokeRpc((client, ct) => Invoke(client, ct));
            if (PassThru) WriteObject(state);
        }
        catch (Exception ex) { Fail(ex); }
    }
}

[Cmdlet(VerbsCommon.Undo, "ProjectHistory", SupportsShouldProcess = true)]
[OutputType(typeof(ProjectHistoryState))]
public sealed class UndoProjectHistoryCommand : ProjectHistoryWriteCmdlet
{
    protected override string Action => "Undo project history";
    protected override ValueTask<ProjectHistoryState> Invoke(RenderClient client, CancellationToken ct)
        => client.UndoProjectHistoryAsync(new() { TimeoutSeconds = TimeoutSeconds }, ct);
}

[Cmdlet(VerbsCommon.Redo, "ProjectHistory", SupportsShouldProcess = true)]
[OutputType(typeof(ProjectHistoryState))]
public sealed class RedoProjectHistoryCommand : ProjectHistoryWriteCmdlet
{
    protected override string Action => "Redo project history";
    protected override ValueTask<ProjectHistoryState> Invoke(RenderClient client, CancellationToken ct)
        => client.RedoProjectHistoryAsync(new() { TimeoutSeconds = TimeoutSeconds }, ct);
}

[Cmdlet(VerbsData.Restore, "ProjectHistory", SupportsShouldProcess = true)]
[OutputType(typeof(ProjectHistoryState))]
public sealed class RestoreProjectHistoryCommand : ProjectHistoryWriteCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    public Guid SnapshotId { get; set; }
    protected override string Action => "Restore project history snapshot";
    protected override string Target => $"Project history snapshot {SnapshotId}";
    protected override ValueTask<ProjectHistoryState> Invoke(RenderClient client, CancellationToken ct)
        => client.RestoreProjectHistoryAsync(new() { SnapshotId = SnapshotId, TimeoutSeconds = TimeoutSeconds }, ct);
}
