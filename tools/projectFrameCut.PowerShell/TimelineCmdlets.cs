using System.Management.Automation;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.PowerShell;

[Cmdlet("Split", "ProjectClip", SupportsShouldProcess = true)]
public sealed class SplitProjectClipCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.SplitClip;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true)][ValidateRange(0, int.MaxValue)] public uint FrameIndex { get; set; }
}

[Cmdlet("Join", "ProjectClip", SupportsShouldProcess = true)]
public sealed class JoinProjectClipCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.JoinClip;
    [Parameter(Mandatory = true)][ValidateCount(2, int.MaxValue)] public Guid[] ClipIds { get; set; } = [];
    [Parameter] public string? Name { get; set; }
}

[Cmdlet("Split", "ProjectClipGroup", SupportsShouldProcess = true)]
public sealed class SplitProjectClipGroupCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.SplitClipGroup;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
}

[Cmdlet("Add", "ProjectSubTrack", SupportsShouldProcess = true)]
public sealed class AddProjectSubTrackCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.AddSubTrack;
    [Parameter(ValueFromPipelineByPropertyName = true)][ValidateRange(10000, int.MaxValue)] public int? TrackId { get; set; }
}

[Cmdlet("Remove", "ProjectTrack", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.High)]
public sealed class RemoveProjectTrackCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.RemoveTrack;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)][ValidateRange(0, int.MaxValue)] public int TrackId { get; set; }
}
