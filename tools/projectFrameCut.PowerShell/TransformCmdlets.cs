using System.Collections;
using System.Management.Automation;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.PowerShell;

[Cmdlet("Get", "ProjectTransformType")]
public sealed class GetProjectTransformTypeCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetTransformType;
    [Parameter] public SwitchParameter Audio { get; set; }
    [Parameter] public string? TypeName { get; set; }
}

[Cmdlet("Get", "ProjectClipTransform")]
public sealed class GetProjectClipTransformCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetClipTransform;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter][ValidateSet("Left", "Right")] public string? Side { get; set; }
}

[Cmdlet("Set", "ProjectClipTransform", SupportsShouldProcess = true)]
public sealed class SetProjectClipTransformCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.SetClipTransform;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true)][ValidateSet("Left", "Right")] public string Side { get; set; } = "";
    [Parameter] public string? TypeName { get; set; }
    [Parameter][ValidateSet("OneInput", "TwoInput")] public string? InputMode { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public uint? DurationFrames { get; set; }
    [Parameter][ValidateSet("BeforeEffects", "AfterEffects")] public string? RenderOrder { get; set; }
    [Parameter] public bool? Enabled { get; set; }
    [Parameter] public Hashtable? Fields { get; set; }
    [Parameter] public SwitchParameter ResetToDefaults { get; set; }
}

[Cmdlet("Remove", "ProjectClipTransform", SupportsShouldProcess = true)]
public sealed class RemoveProjectClipTransformCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.RemoveClipTransform;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true)][ValidateSet("Left", "Right")] public string Side { get; set; } = "";
}

[Cmdlet("Get", "ProjectClipTransformPreview")]
public sealed class GetProjectClipTransformPreviewCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetClipTransformPreview;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true)][ValidateSet("Left", "Right")] public string Side { get; set; } = "";
    [Parameter] public string? TypeName { get; set; }
    [Parameter][ValidateSet("OneInput", "TwoInput")] public string? InputMode { get; set; }
    [Parameter] public Hashtable? Fields { get; set; }
}
