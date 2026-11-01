using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.Rendering;

namespace projectFrameCut.Render.RPCProtocol;

public sealed partial class RenderBackendService
{
    private async ValueTask<ClipPreviewBatchResponse> RenderPreviewBatchAsync(ClipPreviewBatchRequest request, CancellationToken token)
    {
        var first = request.Requests[0];
        if (request.Requests.Any(r => r.SessionId != first.SessionId || r.FrameIndex != first.FrameIndex
            || r.CanvasWidth != first.CanvasWidth || r.CanvasHeight != first.CanvasHeight
            || r.ProjectWidth != first.ProjectWidth || r.ProjectHeight != first.ProjectHeight
            || r.BeforeLayout || !string.IsNullOrEmpty(r.VectorClipJson))
            || request.Requests.Select(r => r.ClipId).Distinct().Count() != request.Requests.Count)
            throw new ArgumentException("Preview composition requires distinct clips from the same timeline frame.", nameof(request));
        var session = GetSession(first.SessionId);
        using var operation = session.BeginOperation(token);
        token = operation.Token;
        await session.RenderGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var sources = ProjectExternalSourceRuntime.Use(session.ExternalSources);
            var clips = GetVisualClips(session.Clips);
            InitializePreviewClips(clips, IPicture.PicturePixelMode.Float16Picture, first.FrameIndex, token);
            using var clipSession = new RenderClipSession(clips, ownsClips: false);
            foreach (var clip in clips) clipSession.UsePreparedClip(clip);
            var requests = request.Requests.ToDictionary(r => r.ClipId);
            var artifacts = new Dictionary<Guid, RenderArtifact>();
            var response = new ClipPreviewBatchResponse();
            using var composer = new PreviewRenderComposer((frame, content, ct) =>
            {
                if (!requests.TryGetValue(content.Clip.Id, out var item)) return;
                try
                {
                    artifacts[item.ClipId] = RenderClipPreviewAsync(item, ct, previewSession: session, previewClips: clips,
                        preparedPreview: () => DynamicEffectGraph.CopyPicture(content.Picture)).AsTask().GetAwaiter().GetResult();
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { response.Errors.Add(new RemoteError(ex)); }
            });
            var options = request.RenderOptions ?? new();
            var renderer = new Renderer
            {
                Clips = clips, ClipSession = clipSession, Composer = composer, AudioSamples = session.AudioSamples,
                LogRenderSummary = false, UseClipInitializationFallback = true,
                StartFrame = first.FrameIndex, Duration = 1, FrameRate = session.FrameRate,
                TargetWidth = Math.Max(1, first.CanvasWidth), TargetHeight = Math.Max(1, first.CanvasHeight),
                ProjectRelativeWidth = first.ProjectWidth > 0 ? first.ProjectWidth : session.Width,
                ProjectRelativeHeight = first.ProjectHeight > 0 ? first.ProjectHeight : session.Height,
                ProcessEffectFromCanvas = Timeline.ProcessEffectFromCanvas,
                MaxThreads = options.MaxThreads, AllowWorkerOversubscription = options.AllowWorkerOversubscription,
                EnableThreadAffinity = options.EnableThreadAffinity, WorkerCPUCoreIndexs = options.WorkerCPUCoreIndexs,
                EnableMemoryBudget = options.EnableMemoryBudget, MaxBufferedBytes = options.MaxBufferedBytes,
                EnableGpuBufferPoolLimit = !(options.DisableGpuBufferPoolLimit || first.DisableGpuBufferPoolLimit),
                GpuBufferPoolMaxBytes = options.GpuBufferPoolMaxBytes > 0 ? options.GpuBufferPoolMaxBytes : first.GpuBufferPoolMaxBytes,
                ReuseDynamicPreviewCache = !first.DisableSourceFrameCache,
                DynamicPreviewCacheProjectRoot = session.ProjectRoot,
            };
            renderer.PrepareRender(token);
            await renderer.GoRender(token).ConfigureAwait(false);
            foreach (var item in request.Requests)
                if (artifacts.TryGetValue(item.ClipId, out var artifact)) response.Artifacts.Add(artifact);
            return response;
        }
        finally { session.RenderGate.Release(); }
    }
}
