using System.Collections;
using System.Management.Automation;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.PowerShell;

[Cmdlet("Get", "ProjectClipProperties")]
public sealed class GetProjectClipPropertiesCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetClipProperties;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
}

[Cmdlet("Set", "ProjectClipProperties", SupportsShouldProcess = true)]
public sealed class SetProjectClipPropertiesCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.SetClipProperties;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter] public string? Name { get; set; }
    [Parameter][AllowEmptyString] public string? ClipColor { get; set; }
    [Parameter] public bool? AllowFreeScaleResize { get; set; }
    [Parameter] public bool? ExtendToWholeDraft { get; set; }
    [Parameter] public string? TargetDecoder { get; set; }
    [Parameter][ValidateRange(-1d, 1d)] public double? HDRBrightnessOffset { get; set; }
    [Parameter] public bool? AudioEnabled { get; set; }
    [Parameter][ValidateRange(0d, 1d)] public double? AudioVolume { get; set; }
    [Parameter] public SwitchParameter DetachAudio { get; set; }
    [Parameter] public ushort? Red { get; set; }
    [Parameter] public ushort? Green { get; set; }
    [Parameter] public ushort? Blue { get; set; }
    [Parameter][ValidateRange(0d, 1d)] public float? Alpha { get; set; }
    [Parameter] public string? MaskSourceType { get; set; }
    [Parameter] public Hashtable? SourceArguments { get; set; }
    [Parameter][ValidateSet("Default", "None", "SSAA2x", "SSAA4x", "SSAA8x")] public string? AntiAliasMode { get; set; }
}

[Cmdlet("Get", "ProjectClipGeometry")]
public sealed class GetProjectClipGeometryCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetClipGeometry;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
}

[Cmdlet("Set", "ProjectClipGeometry", SupportsShouldProcess = true)]
public sealed class SetProjectClipGeometryCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.SetClipGeometry;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public int? WidthPixels { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public int? HeightPixels { get; set; }
    [Parameter] public int? XPixels { get; set; }
    [Parameter] public int? YPixels { get; set; }
    [Parameter] public float? RotationDegrees { get; set; }
    [Parameter] public bool? CropEnabled { get; set; }
    [Parameter][ValidateRange(0, int.MaxValue)] public int? CropXPixels { get; set; }
    [Parameter][ValidateRange(0, int.MaxValue)] public int? CropYPixels { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public int? CropWidthPixels { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public int? CropHeightPixels { get; set; }
    [Parameter] public float? CropRotationDegrees { get; set; }
}

public abstract class ProjectSourceClipCommand : ProjectWriteCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)][ValidateRange(0, int.MaxValue)] public int TrackId { get; set; }
    [Parameter] public string? Name { get; set; }
    [Parameter][ValidateRange(0, int.MaxValue)] public uint? StartFrame { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public uint? DurationFrames { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public int? WidthPixels { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public int? HeightPixels { get; set; }
}

[Cmdlet("Add", "ProjectSolidColorClip", SupportsShouldProcess = true)]
public sealed class AddProjectSolidColorClipCommand : ProjectSourceClipCommand
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.AddSolidColorClip;
    [Parameter] public ushort? Red { get; set; }
    [Parameter] public ushort? Green { get; set; }
    [Parameter] public ushort? Blue { get; set; }
    [Parameter][ValidateRange(0d, 1d)] public float? Alpha { get; set; }
}

[Cmdlet("Get", "ProjectMaskSourceType")]
public sealed class GetProjectMaskSourceTypeCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetMaskSourceType;
    [Parameter] public string? TypeName { get; set; }
}

[Cmdlet("Add", "ProjectMaskClip", SupportsShouldProcess = true)]
public sealed class AddProjectMaskClipCommand : ProjectSourceClipCommand
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.AddMaskClip;
    [Parameter(Mandatory = true)][ValidateNotNullOrEmpty] public string SourceType { get; set; } = "";
    [Parameter] public Hashtable? SourceArguments { get; set; }
}

[Cmdlet("Add", "ProjectEmptyVideoClip", SupportsShouldProcess = true)]
public sealed class AddProjectEmptyVideoClipCommand : ProjectSourceClipCommand
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.AddEmptyVideoClip;
}

[Cmdlet("Add", "ProjectExternalSourceClip", SupportsShouldProcess = true, DefaultParameterSetName = "Project")]
public sealed class AddProjectExternalSourceClipCommand : ProjectSourceClipCommand
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.AddExternalSourceClip;
    [Parameter(Mandatory = true, ParameterSetName = "Project", ValueFromPipelineByPropertyName = true)] public Guid ImportId { get; set; }
    [Parameter(Mandatory = true, ParameterSetName = "Rpc", ValueFromPipelineByPropertyName = true)] public Guid ClientId { get; set; }
    [Parameter(Mandatory = true)][ValidateNotNullOrEmpty] public string SourceId { get; set; } = "";
    [Parameter(Mandatory = true)][ValidateSet("Video", "Audio", "Mask")] public string SourceKind { get; set; } = "";
    [Parameter][ValidateRange(0, int.MaxValue)] public uint? SourceStartFrame { get; set; }
    [Parameter] public Hashtable? SourceArguments { get; set; }
}
