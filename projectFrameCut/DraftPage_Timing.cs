using projectFrameCut.DraftStuff;
using projectFrameCut.ApplicationAPIBase.Views.TabbedView;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using System.Collections.Concurrent;
using projectFrameCut.ApplicationAPIBase.Project;

namespace projectFrameCut;

public partial class DraftPage
{
    private readonly ConcurrentDictionary<int, long> _timelineTimingVersions = new();
    internal event Action? TimelineTimingChanged;
    private readonly Dictionary<Guid, (uint Start, uint Length, uint SourceStart)> _resizeTiming = new();

    internal uint EditPixelToFrame(double px) => ClipTiming.RoundFrame(px * FramePerPixel * tracksZoomOffest);

    internal void InvalidateTimelineTiming(ClipElementUI clip)
    {
        if (clip.origTrack is int track) _timelineTimingVersions.AddOrUpdate(track, 1, (_, version) => version + 1);
    }

    internal (uint Start, uint Duration) GetClipFrameRange(ClipElementUI clip)
    {
        clip.AttachTiming(this);
        return clip.IsExtraDataOptionIsTrue("ExtendToWholeDraft")
            ? (0, GetTimelineDuration()) : (clip.TimelineStartFrame!.Value, clip.TimelineDuration);
    }

    private uint GetTimelineDuration()
    {
        ulong end = 0;
        foreach (var c in Clips.Values)
        {
            if (c.IsGhost || c.IsShadow || c.IsExtraDataOptionIsTrue("ExtendToWholeDraft")) continue;
            c.AttachTiming(this);
            end = Math.Max(end, c.TimelineEnd);
        }
        return (uint)Math.Min(uint.MaxValue, end);
    }

    private double ResolveGroupMove(IReadOnlyList<(ClipElementUI Clip, int Track, uint Start)> plans)
    {
        var ids = plans.Select(p => p.Clip.Id).ToHashSet();
        var occupied = Clips.Values.Where(c => ShouldParticipateInTimelineLayout(c) && !ids.Contains(c.Id))
            .Select(c => (Clip: c, Range: GetClipFrameRange(c))).ToArray();
        var forbidden = new List<(long Start, long End)>();
        long minimum = long.MinValue, maximum = long.MaxValue;
        foreach (var p in plans)
        {
            uint duration = GetClipFrameRange(p.Clip).Duration;
            minimum = Math.Max(minimum, -(long)p.Start);
            maximum = Math.Min(maximum, (long)uint.MaxValue - p.Start - duration);
            foreach (var other in occupied)
            {
                if (other.Clip.origTrack != p.Track || other.Clip.SubLayerIndex != p.Clip.SubLayerIndex || other.Range.Duration == 0) continue;
                forbidden.Add(((long)other.Range.Start - p.Start - duration + 1,
                    (long)other.Range.Start + other.Range.Duration - p.Start - 1));
            }
        }
        return ClipTiming.NearestPosition(0, minimum, maximum, forbidden) / (FramePerPixel * tracksZoomOffest);
    }

    private void BeginHandleResize(ClipElementUI clip)
    {
        clip.AttachTiming(this);
        _resizeTiming[clip.Id] = (clip.TimelineStartFrame!.Value, clip.lengthInFrame, clip.relativeStartFrame);
        clip.MovingStatus = ClipMovingStatus.Resize;
        clip.Clip.BatchBegin();
    }

    private void PreviewHandleResize(ClipElementUI clip, double delta, bool left)
    {
        if (!_resizeTiming.TryGetValue(clip.Id, out var before)) return;
        uint oldDuration = ClipTiming.EffectiveDuration(before.Length, clip.SpeedProvider);
        long end = (long)before.Start + oldDuration;
        double proposed = FrameToPixel(left ? before.Start : (uint)Math.Min(uint.MaxValue, end)) + delta;
        uint boundary = EditPixelToFrame(Math.Max(0, clip.CanSnapWhileResizing ? SnapPixels(proposed, clip.Id) : proposed));
        if (boundary == (left ? before.Start : end))
        {
            RestoreResizeTiming(clip, before);
            return;
        }
        var neighbors = Clips.Values.Where(c => c.Id != clip.Id && c.origTrack == clip.origTrack && c.SubLayerIndex == clip.SubLayerIndex
            && ShouldParticipateInTimelineLayout(c)).Select(GetClipFrameRange).ToArray();
        if (left)
        {
            uint limit = (uint)neighbors.Where(r => (ulong)r.Start + r.Duration <= before.Start)
                .Select(r => (ulong)r.Start + r.Duration).DefaultIfEmpty(0ul).Max();
            boundary = Math.Max(boundary, limit);
            if (clip.isInfiniteLength || clip.maxFrameCount == 0)
            {
                clip.lengthInFrame = ClipTiming.SourceDuration((uint)Math.Max(1, end - boundary), uint.MaxValue - before.SourceStart, clip.SpeedProvider);
                clip.SetTimelineStart((uint)Math.Max(0, end - clip.TimelineDuration));
                if (clip.TimelineStartFrame < limit || clip.TimelineEnd > uint.MaxValue) RestoreResizeTiming(clip, before);
                return;
            }
            long offset = (long)boundary - before.Start;
            long sourceOffset = offset == 0 ? 0 : offset > 0
                ? ClipTiming.SourceOffset((uint)Math.Min(oldDuration, offset), before.Length, clip.SpeedProvider)
                : -(long)ClipTiming.SourceDuration((uint)Math.Min(uint.MaxValue, -offset), before.SourceStart, clip.SpeedProvider);
            long sourceStart = Math.Clamp((long)before.SourceStart + sourceOffset, 0, (long)before.SourceStart + before.Length - 1);
            clip.relativeStartFrame = (uint)sourceStart;
            clip.lengthInFrame = (uint)((long)before.SourceStart + before.Length - sourceStart);
            clip.SetTimelineStart((uint)Math.Max(0, end - clip.TimelineDuration));
            if (clip.TimelineStartFrame < limit) RestoreResizeTiming(clip, before);
        }
        else
        {
            uint limit = neighbors.Where(r => r.Start >= end).Select(r => r.Start).DefaultIfEmpty(uint.MaxValue).Min();
            boundary = Math.Min(boundary, limit);
            uint maximum = clip.isInfiniteLength || clip.maxFrameCount == 0
                ? uint.MaxValue - before.SourceStart : Math.Max(1u, clip.maxFrameCount - Math.Min(clip.maxFrameCount, before.SourceStart));
            clip.lengthInFrame = ClipTiming.SourceDuration(boundary > before.Start ? boundary - before.Start : 1,
                maximum, clip.SpeedProvider);
            clip.SetTimelineStart(before.Start);
            if (clip.TimelineEnd > limit) RestoreResizeTiming(clip, before);
        }
        if (clip.TimelineEnd > uint.MaxValue) RestoreResizeTiming(clip, before);
        SetStatusText(Localized.DraftPage_WaitForUser);
    }

    private void RestoreResizeTiming(ClipElementUI clip, (uint Start, uint Length, uint SourceStart) before)
    {
        clip.relativeStartFrame = before.SourceStart;
        clip.lengthInFrame = before.Length;
        clip.SetTimelineStart(before.Start);
    }

    internal async Task RefreshRestoredSelectionAsync()
    {
        string? tab = (RightContentBorder.Content as TabbedView)?.SelectedItem?.Tag;
        var ids = _selectedClipIds.ToArray();
        Guid? selected = _selected?.Id;
        _selectedClipIds.Clear();
        _selectedOrigColorByClipId.Clear();
        _selected = null;
        foreach (var id in ids)
            if (Clips.TryGetValue(id, out var c)) AddClipToSelection(c);
        if (selected is Guid active && Clips.TryGetValue(active, out var clip) && _selectedClipIds.Contains(active))
            _selected = clip;
        OnPropertyChanged(nameof(SelectedAnyClip));
        OnPropertyChanged(nameof(_ShouldShowClipMoveControlInCenterInfoBar));
        OnPropertyChanged(nameof(_ShouldShowCenterCompactControlGrid));
        await RefreshSelectionUiAsync();
        if (tab is not null && RightContentBorder.Content is TabbedView panel) panel.SelectByTag(tab);
        if (_selected is not null && Popup.IsVisible && popupShowingDirection != "none") RefreshPropertyPanel(_selected);
    }
}
