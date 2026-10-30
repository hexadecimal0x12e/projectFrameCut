using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Rendering;

namespace projectFrameCut.Render.RPCProtocol;

public sealed partial class RenderBackendService
{
    private async Task RenderFrameSequenceAsync(BackendSession session, RenderProjectRequest request, JobEntry entry, CancellationToken token)
    {
        await session.RenderGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string relativeRoot = $"Temp/FrameSequences/{entry.JobId:N}";
            using var writer = new PngSequenceVideoWriter
            {
                OutputPath = _artifacts.ResolveProjectPath(session.ProjectRoot, relativeRoot),
                Width = request.Width, Height = request.Height,
                FramePerSecond = session.FrameRate, StartFrame = request.StartFrame,
            };
            var options = request.FrameSequenceOptions ?? new();
            using var builder = new VideoBuilder(writer)
            {
                BlockWrite = true, Duration = request.FrameCount, DoGCAfterEachWrite = options.GCOption > 0,
            };
            uint chunkSize = options.ChunkFrames > 0 ? Math.Min(options.ChunkFrames, request.FrameCount) : request.FrameCount;
            Log($"Exporting {request.FrameCount} preview frames starting at {request.StartFrame}, workers={options.MaxThreads}, chunk frames={chunkSize}.");
            for (uint completed = 0; completed < request.FrameCount;)
            {
                uint length = Math.Min(chunkSize, request.FrameCount - completed);
                var renderer = new Renderer
                {
                    Clips = GetVisualClips(session.Clips), AudioSamples = session.AudioSamples,
                    StartFrame = request.StartFrame + completed, Duration = length, builder = builder,
                    ProjectRelativeWidth = session.Width, ProjectRelativeHeight = session.Height,
                    AutoCenterImplicitClip = true,
                    ChunkIndex = (int)(completed / chunkSize),
                    ChunkCount = (int)(((ulong)request.FrameCount + chunkSize - 1) / chunkSize),
                    CompletedFramesBeforeChunk = completed,
                    TotalProjectFrames = request.FrameCount,
                    DynamicPreviewCacheProjectRoot = session.ProjectRoot,
                    DynamicPreviewCacheProjectWidth = session.Width,
                    DynamicPreviewCacheProjectHeight = session.Height,
                    LogStaticsData = true,
                    MaxThreads = options.MaxThreads,
                    AllowWorkerOversubscription = options.AllowWorkerOversubscription,
                    RenderAheadFrames = options.RenderAheadFrames,
                    EnableMemoryBudget = options.EnableMemoryBudget,
                    MaxBufferedBytes = options.MaxBufferedBytes,
                    EnableThreadAffinity = options.EnableThreadAffinity,
                    WorkerCPUCoreIndexs = options.WorkerCPUCoreIndexs,
                    GCOption = options.GCOption,
                    ReuseDynamicPreviewCache = options.ReuseDynamicPreviewCache,
                    ResumeInterruptedRender = options.ResumeInterruptedRender,
                    LogProcessStack = options.LogProcessStack,
                    UseHDR = options.UseHDR,
                    MaximumHDRBrightness = options.MaximumHDRBrightness,
                    SDRClipsBrightnessInHDRMode = options.SDRClipsBrightnessInHDRMode,
                };
                renderer.OnProgressChanged += (progress, eta) => entry.Update(job =>
                {
                    job.Progress = (completed + progress * length) / request.FrameCount;
                    job.CurrentFps = renderer.CurrentFps;
                    job.CompletedFrames = completed + renderer.CurrentFinished;
                    job.TotalFrames = request.FrameCount;
                    job.EstimatedRemainingTicks = renderer.CurrentFps > 0
                        ? TimeSpan.FromSeconds((request.FrameCount - completed - renderer.CurrentFinished) / renderer.CurrentFps).Ticks : eta.Ticks;
                });
                foreach (var clip in renderer.Clips) _previewClipInitializations.Remove(clip);
                renderer.PrepareRender(token);
                await renderer.GoRender(token).ConfigureAwait(false);
                completed += length;
            }
            writer.Finish();
            if (writer.DurationWritten != request.FrameCount)
                throw new InvalidOperationException("The renderer did not write the requested number of frames.");
            var artifacts = Enumerable.Range(0, checked((int)request.FrameCount)).Select(i =>
                _artifacts.Register(session.Id, session.ProjectRoot,
                    $"{relativeRoot}/frame-{request.StartFrame + (uint)i:D10}.png", "image/png", false, true,
                    request.Width, request.Height, session.FrameRate)).ToList();
            entry.Update(job =>
            {
                job.State = RenderJobState.Completed;
                job.Progress = 1;
                job.FrameArtifacts = artifacts;
            });
            Log($"Exported {request.FrameCount} preview frames to {writer.OutputPath}.");
        }
        finally { session.RenderGate.Release(); }
    }
}
