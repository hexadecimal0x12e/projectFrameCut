using System.Management.Automation;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.PowerShell;

[Cmdlet("Get", "ProjectFramePreview")]
public sealed class GetProjectFramePreviewCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetFramePreview;
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)] public uint FrameIndex { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public int? WidthPixels { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public int? HeightPixels { get; set; }
}

[Cmdlet("Get", "ProjectClipFramePreview")]
public sealed class GetProjectClipFramePreviewCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetClipFramePreview;
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true, Position = 1, ValueFromPipelineByPropertyName = true)] public uint FrameIndex { get; set; }
}
