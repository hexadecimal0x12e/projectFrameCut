using System.Management.Automation;

namespace projectFrameCut.PowerShell;

[Cmdlet("Get", "ProjectTextClipArgument")]
public sealed class GetProjectTextClipArgumentCommand : GetProjectClipArgumentCommand
{
    protected override string? RequiredClipType => "TextClip";
}

[Cmdlet("Set", "ProjectTextClipArgument", SupportsShouldProcess = true)]
public sealed class SetProjectTextClipArgumentCommand : SetProjectClipArgumentCommand
{
    protected override string? RequiredClipType => "TextClip";
}

[Cmdlet("Get", "ProjectSubtitleClipArgument")]
public sealed class GetProjectSubtitleClipArgumentCommand : GetProjectClipArgumentCommand
{
    protected override string? RequiredClipType => "SubtitleClip";
}

[Cmdlet("Set", "ProjectSubtitleClipArgument", SupportsShouldProcess = true)]
public sealed class SetProjectSubtitleClipArgumentCommand : SetProjectClipArgumentCommand
{
    protected override string? RequiredClipType => "SubtitleClip";
}
