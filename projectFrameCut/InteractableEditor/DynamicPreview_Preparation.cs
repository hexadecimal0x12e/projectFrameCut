using projectFrameCut.ApplicationAPIBase.Interaction;
using projectFrameCut.Controls;
using projectFrameCut.DraftStuff;
using projectFrameCut.LivePreview;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace projectFrameCut.InteractableEditor;

public sealed partial class DynamicPreview
{
    private readonly EffectRuntimeContext effectRuntime = new();
    private const int ParallelPreparationThreshold = 2;
    private readonly SemaphoreSlim _preparationGate = new(1, 1);
    private long _prepareVersion;
    private long _lastSlowPreparationLog;

    /// <summary>
    /// Prepares a still frame at the requested output resolution, without viewport fitting.
    /// </summary>
    public async Task<IReadOnlyList<PreparedPreview>> PrepareFrameAsync(uint frameIndex, int targetWidth, int targetHeight, CancellationToken token, bool applyClipTargetLayout = true)
    {
#if DIAGHUB_ENABLE_TRACE_SYSTEM
        using var frameMark = new UserMarkRange("Preview.PrepareFrame", $"frame={frameIndex}, size={targetWidth}x{targetHeight}");
#endif
        var prepareVersion = Interlocked.Increment(ref _prepareVersion);
        targetWidth = Math.Max(1, targetWidth);
        targetHeight = Math.Max(1, targetHeight);
        var (projectWidth, projectHeight) = ResolveProjectDimensions(targetWidth, targetHeight);
        return await GetFinalRequests(frameIndex, projectWidth, projectHeight, prepareVersion, targetWidth, targetHeight, token, applyClipTargetLayout).ConfigureAwait(false);
    }

    /// <summary>
    /// Prepares interactive previews at the configured resolution; still frames retain the requested resolution.
    /// </summary>
    public Task<IReadOnlyList<PreparedPreview>> PrepareFrameAsync(uint frameIndex, int targetWidth, int targetHeight, double viewportWidth, double viewportHeight, CancellationToken token, bool applyClipTargetLayout = true, bool interactivePreview = false)
    {
        if (interactivePreview)
            (targetWidth, targetHeight) = ResolveDimensions(targetWidth, targetHeight, viewportWidth, viewportHeight);
        return PrepareFrameAsync(frameIndex, targetWidth, targetHeight, token, applyClipTargetLayout);
    }

    public async Task<IReadOnlyList<PreparedPreview>> GetFinalRequests(uint frameIndex, int projectWidth, int projectHeight, long prepareVersion, int canvasWidth, int canvasHeight, CancellationToken token, bool applyClipTargetLayout = true)
    {
        var clipsSnapshot = AcquireClipsSnapshot();
        try
        {
            var requests = ResolveRequests(clipsSnapshot, frameIndex, projectWidth, projectHeight);
            var prepared = await PrepareRequestsAsync(requests, canvasWidth, canvasHeight, projectWidth, projectHeight, frameIndex, applyClipTargetLayout, checkVersion: true, prepareVersion, token).ConfigureAwait(false);
            if (prepared is not null) return prepared;
            if (token.IsCancellationRequested) return null!;
            return GetCachedOverlayPreparedPreviews();
        }
        finally { ReleaseClipsSnapshot(); }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public async Task<IReadOnlyList<PreparedPreview>?> PrepareRequestsAsync(IReadOnlyList<PreviewRequest> requests, int canvasWidth, int canvasHeight, int projectWidth, int projectHeight, uint frameIndex, bool applyClipTargetLayout, bool checkVersion, long prepareVersion, CancellationToken token)
    {
        CancelClipWarmups();
#if DIAGHUB_ENABLE_TRACE_SYSTEM
        using var prepareMark = new UserMarkRange("Preview.PrepareRequests", $"frame={frameIndex}, requests={requests.Count}, size={canvasWidth}x{canvasHeight}");
#endif
        if (requests.Count == 0)
        {
            return [];
        }
        var clips = AcquireClipsSnapshot();
        try
        {
            if (clips.Length == 0) clips = requests.Select(r => r.Clip).ToArray();
            await _preparationGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (checkVersion && prepareVersion != Volatile.Read(ref _prepareVersion)) return null;
                using var effectScope = effectRuntime.Enter();
                var ordered = requests.Reverse().ToArray();
                var prepared = new PreparedPreview[ordered.Length];
                try
                {
                    if (_previewer is not null)
                    {
                        var frames = await _previewer.RenderClipFramesForDisplayAsync(ordered.Select(r => r.Clip.Id).ToArray(),
                            frameIndex, canvasWidth, canvasHeight, projectWidth, projectHeight, token).ConfigureAwait(false);
                        for (var i = 0; i < ordered.Length; i++)
                            prepared[i] = CreatePreparedClipPreview(ClipArgumentBinding.InitializeFrame(ordered[i].Clip, frameIndex), frames[i],
                                ProjectReferenceRuntime.IsCanvasClip(ordered[i].Clip)
                                || PreviewRenderComposer.RequiresBackdrop(ordered[i].Clip)
                                || TransformProcessing.HasActiveTransform(ordered[i].Clip, clips, frameIndex));
                    }
                    else
                    {
                        var previews = await PrepareLocalPreviewsAsync(clips, canvasWidth, canvasHeight,
                            projectWidth, projectHeight, frameIndex, token).ConfigureAwait(false);
                        for (var i = 0; i < ordered.Length; i++)
                            prepared[i] = previews.GetValueOrDefault(ordered[i].Clip.Id)
                                ?? new PreparedPreview(ordered[i].Clip.Id, null, null, ClipArgumentBinding.InitializeFrame(ordered[i].Clip, frameIndex));
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log(ex, $"Compose dynamic preview at frame {frameIndex}; retrying clips separately", this);
                    for (var i = 0; i < ordered.Length; i++)
                        prepared[i] = await GenerateClipPreviewPreparedAsync(ordered[i], canvasWidth, canvasHeight,
                            projectWidth, projectHeight, frameIndex, applyClipTargetLayout, token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                if (checkVersion && prepareVersion != Volatile.Read(ref _prepareVersion)) return null;
                SuppressCompositedPreviews(prepared);
                CacheOverlayPreparedPreviews(prepared);
                return prepared;
            }
            finally { _preparationGate.Release(); }
        }
        finally { ReleaseClipsSnapshot(); }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public async Task<PreparedPreview> PrepareVectorClipSnapshotAsync(ClipDraftDTO clip, uint frameIndex, int width, int height, CancellationToken token)
    {
        using var effectScope = effectRuntime.Enter();
        var json = JsonSerializer.Serialize(clip);
        var (projectWidth, projectHeight) = ResolveProjectDimensions(width, height);
        return await Task.Run(async () =>
        {
            using var document = JsonDocument.Parse(json);
            using var source = PluginManager.CreateClip(document.RootElement);
            var preview = await GenerateClipPreviewPreparedAsync(new PreviewRequest(source), width, height, projectWidth, projectHeight, frameIndex, false, token, json).ConfigureAwait(false);
            return new PreparedPreview(preview.ClipId, preview.ViewFactory, preview.ErrorMessage, null, isTransparentAt: preview.IsTransparentAt);
        }, token).ConfigureAwait(false);
    }

    private async Task<PreparedPreview> GenerateClipPreviewPreparedAsync(PreviewRequest request, int canvasWidth, int canvasHeight, int projectWidth, int projectHeight, uint frameIndex, bool _, CancellationToken token, string? vectorClipJson = null)
    {
        var clip = request.Clip;
        var startedAt = Stopwatch.GetTimestamp();
        long renderedAt = 0;
        try
        {
            bool canvasPreview = ProjectReferenceRuntime.IsCanvasClip(request.Clip)
                || PreviewRenderComposer.RequiresBackdrop(request.Clip)
                || TransformProcessing.HasActiveTransform(request.Clip, _clips ?? [request.Clip], frameIndex);
            if (_previewer is not null)
            {
                clip = ClipArgumentBinding.InitializeFrame(clip, frameIndex);
                var displayFrame = await _previewer.RenderClipFrameForDisplayAsync(request.Clip.Id, frameIndex, canvasWidth, canvasHeight, projectWidth, projectHeight, token, vectorClipJson).ConfigureAwait(false);
                renderedAt = Stopwatch.GetTimestamp();
                return CreatePreparedClipPreview(clip, displayFrame, canvasPreview);
            }
            else
            {
                var displayFrame = await Task.Run(() => PrepareLocalClipPreview(clip, canvasWidth, canvasHeight,
                    projectWidth, projectHeight, frameIndex, token), token).ConfigureAwait(false);
                clip = ClipArgumentBinding.InitializeFrame(clip, frameIndex);
                renderedAt = Stopwatch.GetTimestamp();
                return CreatePreparedClipPreview(clip, displayFrame, canvasPreview);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log(ex, $"Render dynamic preview for clip {request.Clip.Name} ({request.Clip.Id})", this);
            return new PreparedPreview(request.Clip.Id, () => new PreviewErrorView(ex.Message), ex.Message, clip);
        }
        finally
        {
            var now = Stopwatch.GetTimestamp();
            if (renderedAt == 0) renderedAt = now;
            var elapsedMs = Stopwatch.GetElapsedTime(startedAt, now).TotalMilliseconds;
            var lastLog = Interlocked.Read(ref _lastSlowPreparationLog);
            if (!token.IsCancellationRequested && elapsedMs >= 250 && now - lastLog >= Stopwatch.Frequency * 5
                && Interlocked.CompareExchange(ref _lastSlowPreparationLog, now, lastLog) == lastLog)
                LogDiagnostic($"[DynamicPreview] Slow preparation: clip={clip.Id}, frame={frameIndex}, size={canvasWidth}x{canvasHeight}, total={elapsedMs:F1}ms, render/RPC={Stopwatch.GetElapsedTime(startedAt, renderedAt).TotalMilliseconds:F1}ms, materialize={Stopwatch.GetElapsedTime(renderedAt, now).TotalMilliseconds:F1}ms.");
        }
    }
}
