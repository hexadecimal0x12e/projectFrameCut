using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Render.ClipsAndTracks.Text;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Shared;
using System.Text.Json;
using SubtitleDocument = projectFrameCut.Render.RenderAPIBase.ClipAndTrack.SubtitleData;

namespace projectFrameCut.Render.ClipsAndTracks;

public class SubtitleClip : TextClip, IClip
{
    public override ClipMode ClipType => ClipMode.SubtitleClip;
    public SubtitleData SubtitleData { get; set; } = new();

    private readonly object subtitleLock = new();
    private SubtitleData? preparedData;
    private SubtitleCue[] cues = [];
    private RichTextEntry[] entries = [];

    uint? IClip.TryGetRelativeFrameIndex(uint targetFrame, uint? onFail)
    {
        // 全项目模式下，字幕时间直接对应项目时间。
        if (ExtendToWholeDraft) return targetFrame;

        long offset = (long)targetFrame - StartFrame;
        uint duration = ((IClip)this).GetEffectiveDuration();
        if (offset == duration) return null;
        if (offset < 0 || offset >= duration) return onFail;

        ulong frame = (ulong)RelativeStartFrame
            + ClipTiming.SourceOffset((uint)offset, Duration, SpeedVarianceProviderInstance, duration);
        return frame <= uint.MaxValue ? (uint)frame : onFail;
    }

    public override void ReInit(IPicture.PicturePixelMode targetPPB)
    {
        lock (subtitleLock)
        {
            if (ExtraData?.TryGetValue(SubtitleDocument.MetadataKey, out var raw) == true)
                SubtitleData = SubtitleDocument.Restore(raw);
            preparedData = null;
            Prepare();
            Log($"SubtitleClip {Name} ({Id}): prepared {cues.Length} cues, ExtendToWholeDraft={ExtendToWholeDraft}.");
        }
        base.ReInit(targetPPB);
    }

    private void Prepare()
    {
        if (ReferenceEquals(preparedData, SubtitleData)) return;
        SubtitleData.Validate();
        cues = SubtitleData.Cues.OrderBy(c => c.StartMilliseconds).ToArray();
        entries = cues.Select(SubtitleData.BuildEntry).ToArray();
        preparedData = SubtitleData;
    }

    protected override IReadOnlyList<TextEntry> GetRawEntries(uint frameIndex)
    {
        lock (subtitleLock)
        {
            Prepare();
            double fps = ResolveFrameRate();
            decimal milliseconds = frameIndex * 1000m / (decimal)fps;
            int left = 0, right = cues.Length - 1, index = -1;
            while (left <= right)
            {
                int middle = left + (right - left) / 2;
                if (cues[middle].StartMilliseconds <= milliseconds)
                {
                    index = middle;
                    left = middle + 1;
                }
                else right = middle - 1;
            }
            if (index < 0 || milliseconds >= cues[index].EndMilliseconds) return [];
            float width = TargetWidth > 0 ? TargetWidth : 1920;
            var entry = entries[index] with { ExtraData = new(entries[index].ExtraData) };
            entry.X += entry.Alignment switch
            {
                TextAlignment.Center => width / 2,
                TextAlignment.Right => width * .95f,
                _ => width * .05f
            };
            entry.Y -= SubtitleData.BottomMargin;
            entry.SetLayoutMode(TextClipLayoutMode.FixedWidth);
            entry.SetVerticalAlignment(ClipVerticalAlignment.Bottom);
            if (entry.GetWrappingWidth() is not > 0) entry.SetWrappingWidth(width * .9f);
            return [entry];
        }
    }

    private double ResolveFrameRate()
    {
        if (ExtraData?.TryGetValue(ClipDraftDTO.ProjectFrameRateMetaKey, out var raw) == true)
        {
            double fps = raw is JsonElement json ? json.GetDouble() : Convert.ToDouble(raw);
            if (double.IsFinite(fps) && fps > 0) return fps;
        }
        if (FrameTime > 0 && float.IsFinite(FrameTime)) return Math.Round(1d / FrameTime, 6);
        throw new InvalidDataException("Subtitle clip requires a valid project frame rate.");
    }
}
