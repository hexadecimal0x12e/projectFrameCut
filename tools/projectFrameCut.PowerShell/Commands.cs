using System.Collections;
using System.Management.Automation;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.PowerShell;

[Cmdlet("Get", "ProjectInfo")]
public sealed class GetProjectInfoCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetInfo;
}

[Cmdlet("Save", "Project", SupportsShouldProcess = true)]
public sealed class SaveProjectCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.Save;
}

[Cmdlet("Get", "ProjectClip")]
public sealed class GetProjectClipCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetClip;
    [Parameter(ValueFromPipelineByPropertyName = true)] public Guid? ClipId { get; set; }
    [Parameter] public string? Name { get; set; }
    [Parameter(ValueFromPipelineByPropertyName = true)][ValidateRange(0, int.MaxValue)] public int? TrackId { get; set; }
}

[Cmdlet("Add", "ProjectClip", SupportsShouldProcess = true)]
public sealed class AddProjectClipCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.AddClip;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)][ValidateRange(0, int.MaxValue)] public int TrackId { get; set; }
    [Parameter] public string? Name { get; set; }
    [Parameter] public uint? StartFrame { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public uint? DurationFrames { get; set; }
    [Parameter] public uint? SourceStartFrame { get; set; }
    [Parameter] public string? FilePath { get; set; }
    [Parameter(ValueFromPipelineByPropertyName = true)] public string? AssetId { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public int? WidthPixels { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public int? HeightPixels { get; set; }
}

[Cmdlet("Set", "ProjectClip", SupportsShouldProcess = true)]
public sealed class SetProjectClipCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.SetClip;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter] public string? Name { get; set; }
    [Parameter] public uint? StartFrame { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public uint? DurationFrames { get; set; }
    [Parameter] public uint? SourceStartFrame { get; set; }
    [Parameter(ValueFromPipelineByPropertyName = true)][ValidateRange(0, int.MaxValue)] public int? TrackId { get; set; }
    [Parameter] public string? FilePath { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public int? WidthPixels { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public int? HeightPixels { get; set; }
    [Parameter] public int? XPixels { get; set; }
    [Parameter] public int? YPixels { get; set; }
}

[Cmdlet("Remove", "ProjectClip", SupportsShouldProcess = true)]
public sealed class RemoveProjectClipCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.RemoveClip;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
}

[Cmdlet("Copy", "ProjectClip", SupportsShouldProcess = true)]
public sealed class CopyProjectClipCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.CopyClip;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter] public string? Name { get; set; }
    [Parameter(ValueFromPipelineByPropertyName = true)][ValidateRange(0, int.MaxValue)] public int? TrackId { get; set; }
    [Parameter] public uint? StartFrame { get; set; }
}

[Cmdlet("Get", "ProjectAsset")]
public sealed class GetProjectAssetCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetAsset;
    [Parameter(ValueFromPipelineByPropertyName = true)] public string? AssetId { get; set; }
    [Parameter] public string? Name { get; set; }
}

[Cmdlet("Add", "ProjectAsset", SupportsShouldProcess = true)]
public sealed class AddProjectAssetCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.AddAsset;
    [Parameter(Mandatory = true)][ValidateNotNullOrEmpty] public string FilePath { get; set; } = "";
    [Parameter] public string? Name { get; set; }
}

[Cmdlet("Remove", "ProjectAsset", SupportsShouldProcess = true)]
public sealed class RemoveProjectAssetCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.RemoveAsset;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)][ValidateNotNullOrEmpty] public string AssetId { get; set; } = "";
}

[Cmdlet("Get", "ProjectTrack")]
public sealed class GetProjectTrackCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetTrack;
    [Parameter(ValueFromPipelineByPropertyName = true)][ValidateRange(0, int.MaxValue)] public int? TrackId { get; set; }
}

[Cmdlet("Add", "ProjectTrack", SupportsShouldProcess = true)]
public sealed class AddProjectTrackCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.AddTrack;
    [Parameter(ValueFromPipelineByPropertyName = true)][ValidateRange(0, int.MaxValue)] public int? TrackId { get; set; }
}

[Cmdlet("Get", "ProjectTextStyle")]
public sealed class GetProjectTextStyleCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetTextStyle;
}

[Cmdlet("Get", "ProjectTextStyleField")]
public sealed class GetProjectTextStyleFieldCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetTextStyleField;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)][ValidateNotNullOrEmpty] public string StyleId { get; set; } = "";
}

[Cmdlet("Add", "ProjectTextClip", SupportsShouldProcess = true)]
public sealed class AddProjectTextClipCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.AddTextClip;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)][ValidateNotNullOrEmpty] public string StyleId { get; set; } = "";
    [Parameter(Mandatory = true)][ValidateNotNullOrEmpty] public string Text { get; set; } = "";
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)][ValidateRange(0, int.MaxValue)] public int TrackId { get; set; }
    [Parameter] public uint? StartFrame { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public uint? DurationFrames { get; set; }
    [Parameter] public Hashtable? Fields { get; set; }
}

[Cmdlet("Set", "ProjectTextClipStyle", SupportsShouldProcess = true)]
public sealed class SetProjectTextClipStyleCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.SetTextClipStyle;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true)] public Hashtable Fields { get; set; } = new();
}

[Cmdlet("Get", "ProjectEffectProviderType")]
public sealed class GetProjectEffectProviderTypeCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetEffectProviderType;
    [Parameter] public string? Name { get; set; }
}

[Cmdlet("Get", "ProjectEffectProviderField")]
public sealed class GetProjectEffectProviderFieldCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetEffectProviderField;
    [Parameter(Mandatory = true)][ValidateNotNullOrEmpty] public string TypeName { get; set; } = "";
}

[Cmdlet("Get", "ProjectClipEffectProvider")]
public sealed class GetProjectClipEffectProviderCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetClipEffectProvider;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(ValueFromPipelineByPropertyName = true)] public Guid? ProviderId { get; set; }
    [Parameter] public string? TypeName { get; set; }
}

[Cmdlet("Add", "ProjectClipEffectProvider", SupportsShouldProcess = true)]
public sealed class AddProjectClipEffectProviderCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.AddClipEffectProvider;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true)][ValidateNotNullOrEmpty] public string TypeName { get; set; } = "";
    [Parameter] public string? Name { get; set; }
    [Parameter] public bool? Enabled { get; set; }
    [Parameter] public Hashtable? Fields { get; set; }
    [Parameter(ValueFromPipelineByPropertyName = true)] public Guid? InputProviderId { get; set; }
    [Parameter] public bool? IsFinalOutput { get; set; }
}

[Cmdlet("Set", "ProjectClipEffectProvider", SupportsShouldProcess = true)]
public sealed class SetProjectClipEffectProviderCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.SetClipEffectProvider;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ProviderId { get; set; }
    [Parameter] public string? Name { get; set; }
    [Parameter] public bool? Enabled { get; set; }
    [Parameter] public Hashtable? Fields { get; set; }
    [Parameter] public SwitchParameter ResetToDefaults { get; set; }
    [Parameter(ValueFromPipelineByPropertyName = true)] public Guid? InputProviderId { get; set; }
    [Parameter] public bool? IsFinalOutput { get; set; }
}

[Cmdlet("Remove", "ProjectClipEffectProvider", SupportsShouldProcess = true)]
public sealed class RemoveProjectClipEffectProviderCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.RemoveClipEffectProvider;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ProviderId { get; set; }
}
