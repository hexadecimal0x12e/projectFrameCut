using System.Collections;
using System.Management.Automation;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.PowerShell;

[Cmdlet("Get", "ProjectSettings")]
public sealed class GetProjectSettingsCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetSettings;
}

[Cmdlet("Set", "ProjectSettings", SupportsShouldProcess = true)]
public sealed class SetProjectSettingsCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.SetSettings;
    [Parameter][ValidateRange(1, uint.MaxValue)] public uint? FrameRate { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public int? WidthPixels { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public int? HeightPixels { get; set; }
    [Parameter] public bool? EnableHDR { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public uint? HdrMaximumBrightness { get; set; }
    [Parameter][ValidateRange(0, int.MaxValue)] public uint? SdrClipBrightness { get; set; }
    [Parameter] public string? CoverFilePath { get; set; }
    [Parameter] public SwitchParameter ClearCover { get; set; }
    [Parameter] public bool? WriteCoverAsFirstFrame { get; set; }
    [Parameter] public Hashtable? UserDefinedProperties { get; set; }
}
