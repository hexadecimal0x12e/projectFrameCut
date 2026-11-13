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

[Cmdlet("Export", "ProjectFrameSequence")]
public sealed class ExportProjectFrameSequenceCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.ExportFrameSequence;
    [Parameter(Mandatory = true, Position = 0)] public uint StartFrame { get; set; }
    [Parameter(Mandatory = true, Position = 1)][ValidateRange(1, int.MaxValue)] public int FrameCount { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public int? WidthPixels { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public int? HeightPixels { get; set; }
}

[Cmdlet("Export", "ProjectAudioSamples")]
public sealed class ExportProjectAudioSamplesCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.ExportAudioSamples;
    [Parameter(Mandatory = true, Position = 0)] public uint StartFrame { get; set; }
    [Parameter(Mandatory = true, Position = 1)][ValidateRange(1, int.MaxValue)] public int FrameCount { get; set; }
    [Parameter][ValidateRange(8000, int.MaxValue)] public int? SampleRate { get; set; }
    [Parameter][ValidateRange(1, 8)] public int? Channels { get; set; }
}
