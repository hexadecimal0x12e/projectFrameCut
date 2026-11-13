using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Shared;
using System.Collections.Concurrent;

namespace projectFrameCut.InteractableEditor;

public sealed partial class DynamicPreview
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _clipWarmups = new();

    public bool PreviewWarmupEnabled
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            if (!value) CancelClipWarmups();
        }
    } = true;

    public int PreviewWarmupSeconds { get; set { field = Math.Clamp(value, 1, 60); } } = 5;

    public void CancelClipWarmup(Guid clipId)
    {
        if (_clipWarmups.TryGetValue(clipId, out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    public void CancelClipWarmups()
    {
        foreach (var clipId in _clipWarmups.Keys) CancelClipWarmup(clipId);
    }

    public void WarmClipFrames(Guid clipId, uint playhead, int frameRate, int canvasWidth, int canvasHeight, int projectWidth, int projectHeight)
    {
        CancelClipWarmup(clipId);
        if (!PreviewWarmupEnabled || _previewer is null || _clips?.FirstOrDefault(c => c.Id == clipId) is not { } clip) return;
        if (ClipInitializationFailure.IsMarked(clip))
        {
            LogDiagnostic($"[DynamicPreview] Skipped warmup for failed clip {clipId}.");
            return;
        }
        if (!_previewer.CanCacheClipFrame(clipId))
        {
            LogDiagnostic($"[DynamicPreview] Skipped warmup for changing source in clip {clipId}.");
            return;
        }

        var cts = new CancellationTokenSource();
        _clipWarmups[clipId] = cts;
        var previewer = _previewer;
        var seconds = PreviewWarmupSeconds;
        _ = Task.Run(async () =>
        {
            try
            {
                cts.Token.ThrowIfCancellationRequested();
                if (clip.ClipType == ClipMode.AudioClip) return;

                var radius = Math.Max(1, frameRate) * seconds;
                var rendered = 0;
                for (var distance = 0; distance <= radius; distance++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    if (ClipInitializationFailure.IsMarked(clip)) return;
                    var index = (long)playhead + distance;
                    if (index > uint.MaxValue || !clip.ContainsFrame((uint)index)) continue;
                    await previewer.RenderClipFrameForDisplayAsync(clipId, (uint)index, canvasWidth, canvasHeight, projectWidth, projectHeight, cts.Token).ConfigureAwait(false);
                    rendered++;
                }
                for (var distance = 1; distance <= radius; distance++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    if (ClipInitializationFailure.IsMarked(clip)) return;
                    var index = (long)playhead - distance;
                    if (index < 0 || !clip.ContainsFrame((uint)index)) continue;
                    await previewer.RenderClipFrameForDisplayAsync(clipId, (uint)index, canvasWidth, canvasHeight, projectWidth, projectHeight, cts.Token).ConfigureAwait(false);
                    rendered++;
                }
                LogDiagnostic($"[DynamicPreview] Warmed {rendered} frames for clip {clipId} around {playhead}.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log(ex, $"Warm dynamic preview for clip {clipId}", this); }
            finally
            {
                if (_clipWarmups.TryGetValue(clipId, out var current) && ReferenceEquals(current, cts))
                    _clipWarmups.TryRemove(clipId, out _);
                cts.Dispose();
            }
        });
    }
}
