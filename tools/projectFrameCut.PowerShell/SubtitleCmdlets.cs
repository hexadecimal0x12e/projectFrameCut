using System.Collections;
using System.Management.Automation;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.PowerShell;

[Cmdlet("Get", "ProjectSubtitleClip")]
public sealed class GetProjectSubtitleClipCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetSubtitleClip;
    [Parameter(ValueFromPipelineByPropertyName = true)] public Guid? ClipId { get; set; }
    [Parameter(ValueFromPipelineByPropertyName = true)] public int? TrackId { get; set; }
}

[Cmdlet("Add", "ProjectSubtitleClip", SupportsShouldProcess = true)]
public sealed class AddProjectSubtitleClipCommand : ProjectSourceClipCommand
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.AddSubtitleClip;
    [Parameter] public string? FilePath { get; set; }
    [Parameter] public string? EncodingName { get; set; }
    [Parameter] public Hashtable[]? Cues { get; set; }
}

[Cmdlet("Set", "ProjectSubtitleClip", SupportsShouldProcess = true)]
public sealed class SetProjectSubtitleClipCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.SetSubtitleClip;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter] public string? FilePath { get; set; }
    [Parameter] public string? EncodingName { get; set; }
    [Parameter] public Hashtable[]? Cues { get; set; }
    [Parameter] public long? OffsetMilliseconds { get; set; }
    [Parameter] public SwitchParameter FitLength { get; set; }
}

[Cmdlet("Get", "ProjectSubtitleCue")]
public sealed class GetProjectSubtitleCueCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetSubtitleCue;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(ValueFromPipelineByPropertyName = true)] public Guid? CueId { get; set; }
}

[Cmdlet("Add", "ProjectSubtitleCue", SupportsShouldProcess = true)]
public sealed class AddProjectSubtitleCueCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.AddSubtitleCue;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true)][ValidateRange(0, long.MaxValue)] public long StartMilliseconds { get; set; }
    [Parameter(Mandatory = true)][ValidateRange(1, long.MaxValue)] public long EndMilliseconds { get; set; }
    [Parameter(Mandatory = true)][AllowEmptyString] public string Markdown { get; set; } = "";
}

[Cmdlet("Set", "ProjectSubtitleCue", SupportsShouldProcess = true)]
public sealed class SetProjectSubtitleCueCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.SetSubtitleCue;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid CueId { get; set; }
    [Parameter][ValidateRange(0, long.MaxValue)] public long? StartMilliseconds { get; set; }
    [Parameter][ValidateRange(1, long.MaxValue)] public long? EndMilliseconds { get; set; }
    [Parameter][AllowEmptyString] public string? Markdown { get; set; }
}

[Cmdlet("Remove", "ProjectSubtitleCue", SupportsShouldProcess = true)]
public sealed class RemoveProjectSubtitleCueCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.RemoveSubtitleCue;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid CueId { get; set; }
}
