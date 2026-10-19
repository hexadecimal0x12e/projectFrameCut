using System.Collections;
using System.Management.Automation;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.PowerShell;

[Cmdlet("Get", "ProjectClipEffectStep")]
public sealed class GetProjectClipEffectStepCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetClipEffectStep;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ProviderId { get; set; }
    [Parameter][ValidateRange(0, int.MaxValue)] public int? StepIndex { get; set; }
}

[Cmdlet("Add", "ProjectClipEffectStep", SupportsShouldProcess = true)]
public sealed class AddProjectClipEffectStepCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.AddClipEffectStep;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ProviderId { get; set; }
    [Parameter] public Hashtable? Fields { get; set; }
}

[Cmdlet("Set", "ProjectClipEffectStep", SupportsShouldProcess = true)]
public sealed class SetProjectClipEffectStepCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.SetClipEffectStep;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ProviderId { get; set; }
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)][ValidateRange(0, int.MaxValue)] public int StepIndex { get; set; }
    [Parameter(Mandatory = true)] public Hashtable Fields { get; set; } = new();
}

[Cmdlet("Remove", "ProjectClipEffectStep", SupportsShouldProcess = true)]
public sealed class RemoveProjectClipEffectStepCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.RemoveClipEffectStep;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ProviderId { get; set; }
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)][ValidateRange(0, int.MaxValue)] public int StepIndex { get; set; }
}
