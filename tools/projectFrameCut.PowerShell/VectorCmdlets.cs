using System.Collections;
using System.Management.Automation;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.PowerShell;

[Cmdlet("Get", "ProjectVectorComponentType")]
public sealed class GetProjectVectorComponentTypeCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetVectorComponentType;
    [Parameter(ValueFromPipelineByPropertyName = true)] public string? TypeName { get; set; }
    [Parameter] public string? Name { get; set; }
}

[Cmdlet("Get", "ProjectVectorClip")]
public sealed class GetProjectVectorClipCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetVectorClip;
    [Parameter(ValueFromPipelineByPropertyName = true)] public Guid? ClipId { get; set; }
    [Parameter(ValueFromPipelineByPropertyName = true)][ValidateRange(0, int.MaxValue)] public int? TrackId { get; set; }
    [Parameter] public string? Name { get; set; }
}

public abstract class ProjectVectorClipWriteCmdlet : ProjectWriteCmdlet
{
    [Parameter] public string? Name { get; set; }
    [Parameter][ValidateRange(0, int.MaxValue)] public uint? StartFrame { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public uint? DurationFrames { get; set; }
    [Parameter][ValidateRange(0, int.MaxValue)] public uint? SourceStartFrame { get; set; }
    [Parameter][ValidateSet("Default", "None", "SSAA2x", "SSAA4x", "SSAA8x")] public string? AntiAliasMode { get; set; }
}

public abstract class ProjectVectorClipCreateCmdlet : ProjectVectorClipWriteCmdlet
{
    [Parameter][ValidateRange(1, int.MaxValue)] public int? WidthPixels { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public int? HeightPixels { get; set; }
    [Parameter] public int? XPixels { get; set; }
    [Parameter] public int? YPixels { get; set; }
    [Parameter] public float? RotationDegrees { get; set; }
}

[Cmdlet("Add", "ProjectVectorCanvasClip", SupportsShouldProcess = true)]
public sealed class AddProjectVectorCanvasClipCommand : ProjectVectorClipCreateCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.AddVectorCanvasClip;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)][ValidateRange(0, int.MaxValue)] public int TrackId { get; set; }
    [Parameter] public Hashtable[]? Components { get; set; }
}

[Cmdlet("Add", "ProjectVectorComponentClip", SupportsShouldProcess = true, DefaultParameterSetName = "Component")]
public sealed class AddProjectVectorComponentClipCommand : ProjectVectorClipCreateCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.AddVectorComponentClip;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)][ValidateRange(0, int.MaxValue)] public int TrackId { get; set; }
    [Parameter(Mandatory = true, ParameterSetName = "Component")][ValidateNotNullOrEmpty] public string TypeName { get; set; } = "";
    [Parameter(ParameterSetName = "Component")] public Hashtable? Parameters { get; set; }
    [Parameter(Mandatory = true, ParameterSetName = "File")][ValidateNotNullOrEmpty] public string FilePath { get; set; } = "";
    [Parameter][ValidateRange(1, int.MaxValue)] public int? CanvasWidthPixels { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public int? CanvasHeightPixels { get; set; }
    [Parameter][ValidateCount(4, 4)] public int[]? Viewport { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public uint? SourceLengthFrames { get; set; }
}

[Cmdlet("Set", "ProjectVectorClip", SupportsShouldProcess = true)]
public sealed class SetProjectVectorClipCommand : ProjectVectorClipWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.SetVectorClip;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(ValueFromPipelineByPropertyName = true)][ValidateRange(0, int.MaxValue)] public int? TrackId { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public int? CanvasWidthPixels { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public int? CanvasHeightPixels { get; set; }
    [Parameter][ValidateCount(4, 4)] public int[]? Viewport { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public uint? SourceLengthFrames { get; set; }
    [Parameter] public SwitchParameter UpdateBounds { get; set; }
}

[Cmdlet("Get", "ProjectVectorComponent")]
public sealed class GetProjectVectorComponentCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetVectorComponent;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(ValueFromPipelineByPropertyName = true)] public Guid? ComponentId { get; set; }
    [Parameter] public Guid? ParentComponentId { get; set; }
    [Parameter] public string? TypeName { get; set; }
}

[Cmdlet("Add", "ProjectVectorComponent", SupportsShouldProcess = true)]
public sealed class AddProjectVectorComponentCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.AddVectorComponent;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true)][ValidateNotNullOrEmpty] public string TypeName { get; set; } = "";
    [Parameter] public string? Name { get; set; }
    [Parameter] public Hashtable? Parameters { get; set; }
    [Parameter] public Guid? ParentComponentId { get; set; }
    [Parameter][ValidateRange(0, int.MaxValue)] public int? Index { get; set; }
    [Parameter] public bool? UpdateBounds { get; set; }
}

[Cmdlet("Set", "ProjectVectorComponent", SupportsShouldProcess = true)]
public sealed class SetProjectVectorComponentCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.SetVectorComponent;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ComponentId { get; set; }
    [Parameter] public string? Name { get; set; }
    [Parameter] public Hashtable? Parameters { get; set; }
    [Parameter][ValidateRange(0, int.MaxValue)] public int? Index { get; set; }
    [Parameter] public SwitchParameter ResetToDefaults { get; set; }
    [Parameter] public bool? UpdateBounds { get; set; }
}

[Cmdlet("Remove", "ProjectVectorComponent", SupportsShouldProcess = true)]
public sealed class RemoveProjectVectorComponentCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.RemoveVectorComponent;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ComponentId { get; set; }
    [Parameter] public bool? UpdateBounds { get; set; }
}
