using Microsoft.Maui.Controls;
using projectFrameCut.DraftStuff;

namespace projectFrameCut;

public partial class DraftPage
{
    private readonly Dictionary<int, (ClipElementUI[] Clips, double[] Ends)> _clipPreviewTracks = [];
    private HashSet<Guid> _visibleClipPreviews = [];
    private bool _clipPreviewViewportDirty = true;
    private bool _clipPreviewViewportQueued;

    private void InvalidateClipPreviewViewport()
    {
        _clipPreviewViewportDirty = true;
        ScheduleClipPreviewViewportUpdate();
    }

    internal void ScheduleClipPreviewViewportUpdate()
    {
        if (_clipPreviewViewportQueued || AlreadyDisappeared) return;
        _clipPreviewViewportQueued = true;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(16), () =>
        {
            _clipPreviewViewportQueued = false;
            if (AlreadyDisappeared) return;
            try { RefreshClipPreviewViewport(); }
            catch (Exception ex) { Log(ex, "Refresh timeline preview viewport", this); }
        });
    }

    private void ClipPreviewViewport_SizeChanged(object? sender, EventArgs e)
        => ScheduleClipPreviewViewportUpdate();

    private void TracksAndClipsLayout_Scrolled(object? sender, ScrolledEventArgs e)
        => ScheduleClipPreviewViewportUpdate();

    private void RefreshClipPreviewViewport()
    {
#if DIAGHUB_ENABLE_TRACE_SYSTEM
        using var viewportMark = new UserMarkRange("Timeline.RefreshPreviewViewport", $"previews={_activeClipPreviews.Count}");
#endif
        if (TracksAndClipsLayout.Height <= 0 || TimelineScrollView.Width <= 0) return;
        if (_clipPreviewViewportDirty)
        {
            _clipPreviewViewportDirty = false;
            _clipPreviewTracks.Clear();
            foreach (var group in Clips.Values.Where(c => ShouldParticipateInTimelineLayout(c)
                && c.origTrack.HasValue && _activeClipPreviews.ContainsKey(c.Id)).GroupBy(c => c.origTrack!.Value))
            {
                var clips = group.OrderBy(c => c.Clip.TranslationX).ToArray();
                var ends = new double[clips.Length];
                double end = 0;
                for (int i = 0; i < clips.Length; i++)
                {
                    end = Math.Max(end, clips[i].Clip.TranslationX
                        + Math.Max(0, clips[i].Clip.WidthRequest > 0 ? clips[i].Clip.WidthRequest : clips[i].origLength));
                    ends[i] = end;
                }
                _clipPreviewTracks[group.Key] = (clips, ends);
            }
            LogDiagnostic($"Indexed {_activeClipPreviews.Count} timeline previews across {_clipPreviewTracks.Count} tracks.");
        }

        var visible = new HashSet<Guid>();
        foreach (var (trackId, index) in _clipPreviewTracks)
        {
            if (!Tracks.TryGetValue(trackId, out var track) || track.Parent is not VisualElement row) continue;
            double y = GetAbsolutePosition(row, TracksAndClipsLayout).Y - TracksAndClipsLayout.ScrollY;
            if (row.Height <= 0 || y + row.Height <= -ClipHeight || y >= TracksAndClipsLayout.Height + ClipHeight) continue;
            var scroll = trackId >= SubTrackOffset ? SubTimelineScrollView : TimelineScrollView;
            double left = scroll.ScrollX - 200;
            double right = scroll.ScrollX + scroll.Width + 200;
            int low = 0;
            int high = index.Ends.Length;
            // Prefix ends also handle overlapping clips and different sublayers.
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (index.Ends[mid] <= left) low = mid + 1;
                else high = mid;
            }
            for (int i = low; i < index.Clips.Length; i++)
            {
                var c = index.Clips[i];
                if (c.Clip.TranslationX >= right) break;
                if (c.Clip.Parent != track || !_activeClipPreviews.TryGetValue(c.Id, out var preview)) continue;
                if (preview.NotifyScrollChanged(scroll.ScrollX, scroll.Width))
                    visible.Add(c.Id);
                else
                {
                    preview.Dispose();
                    _activeClipPreviews.Remove(c.Id);
                    _clipPreviewViewportDirty = true;
                }
            }
        }
        foreach (var id in _visibleClipPreviews)
        {
            if (!visible.Contains(id) && _activeClipPreviews.TryGetValue(id, out var preview)) preview.Suspend();
        }
        _visibleClipPreviews = visible;
    }

    private void ClearClipPreviews()
    {
        foreach (var preview in _activeClipPreviews.Values) preview.Dispose();
        _activeClipPreviews.Clear();
        _clipPreviewTracks.Clear();
        _visibleClipPreviews.Clear();
    }
}
