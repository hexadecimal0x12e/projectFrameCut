using System.Management.Automation;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.PowerShell;

[Cmdlet(VerbsLifecycle.Start, "ProjectBatch", SupportsShouldProcess = true)]
public sealed class StartProjectBatchCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.StartBatch;
    protected override bool ReturnResult => true;
}

[Cmdlet(VerbsLifecycle.Complete, "ProjectBatch", SupportsShouldProcess = true)]
public sealed class CompleteProjectBatchCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.CompleteBatch;
    protected override bool ReturnResult => true;
}

[Cmdlet(VerbsLifecycle.Stop, "ProjectBatch", SupportsShouldProcess = true)]
public sealed class StopProjectBatchCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.StopBatch;
    protected override bool ReturnResult => true;
}
