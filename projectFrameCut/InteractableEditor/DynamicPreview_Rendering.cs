using projectFrameCut.ApplicationAPIBase.Interaction;
using projectFrameCut.Drawing.Base;
using projectFrameCut.LivePreview;
using projectFrameCut.InteractableEditor;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using Path = System.IO.Path;

namespace projectFrameCut.InteractableEditor
{
    public sealed partial class DynamicPreview
    {
        private readonly FrameHashCache nodeCache = new();

        private Task<Dictionary<Guid, PreparedPreview>> PrepareLocalPreviewsAsync(IClip[] clips, int canvasWidth, int canvasHeight,
            int projectWidth, int projectHeight, uint frameIndex, CancellationToken token)
            => Task.Run(async () =>
            {
                clips = clips.Where(c => c.ClipType is not (ClipMode.AudioClip or ClipMode.MarkingClip)).ToArray();
                bool scRgb = OperatingSystem.IsWindows() && DefaultOutputMode != NativePreviewOutputMode.Disabled;
                var previews = new Dictionary<Guid, PreparedPreview>();
                using var cache = DynamicPreviewSettings.MemoryOnly ? null : new PreviewFrameCache(id => Path.Combine(DiskCacheRoot, id.ToString()), frameIndex,
                    canvasWidth, canvasHeight, projectWidth, projectHeight, scRgb, false, token, keepHwAccelPicture: scRgb,
                    findSource: key => GetLocalCacheIndex().FindPreviewSource(key), cacheSourceFrames: DynamicPreviewSettings.CacheSourceFrames);
                using var composer = new PreviewRenderComposer((frame, content, ct) =>
                {
                    PreviewFrameSource source;
                    if (cache is null)
                    {
                        var data = PreviewFrameData.Create(content.Picture, "", "", scRgb);
                        source = PreviewFrameMaterializer.FromData(new("", data.Width, data.Height,
                            scRgb ? PreviewPixelFormat.Rgba16FloatScRgb : PreviewPixelFormat.EncodedImage, false), data);
                    }
                    else
                    {
                        var identity = FrameHashHelper.GetClipHash(clips, content.Clip, frame,
                            FrameHashContext.Current!.Options with { Stage = "clip" }, ct);
                        var cached = cache.GetOrCreate(content.Clip, clips, identity.Hash, identity.CanReuse,
                            Path.Combine(DiskCacheRoot, "temporary", $"{Guid.NewGuid():N}.vfd2m"),
                            () => DynamicEffectGraph.CopyPicture(content.Picture),
                            () => clips.All(c => !ClipInitializationFailure.IsMarked(c) && Timeline.CanCacheClipSource(c)));
                        source = PreviewFrameMaterializer.FromData(new(cached.Path, cached.Data.Width, cached.Data.Height,
                            scRgb ? PreviewPixelFormat.Rgba16FloatScRgb : PreviewPixelFormat.EncodedImage, false)
                        { PreviewContainer = PreviewFrameContainer.GetContainer(cached.Path), PreviewEffectHash = cached.Data.EffectHash }, cached.Data);
                        if (!cached.Persistent) File.Delete(cached.Path);
                        else
                        {
                            try { GetLocalCacheIndex().Track(cached.Path, content.Clip.Id, frame, cached.Data.Width, cached.Data.Height); }
                            catch (Exception ex) { LogLocalCacheError(ex); }
                        }
                    }
                    previews[content.Clip.Id] = CreatePreparedClipPreview(content.Clip, source, content.IsCanvas);
                });
                var renderer = new Renderer
                {
                    Clips = clips,
                    Composer = composer,
                    StartFrame = frameIndex,
                    Duration = 1,
                    LogRenderSummary = false,
                    UseClipInitializationFallback = true,
                    TargetWidth = canvasWidth,
                    TargetHeight = canvasHeight,
                    ProjectRelativeWidth = projectWidth,
                    ProjectRelativeHeight = projectHeight,
                    ProcessEffectFromCanvas = Timeline.ProcessEffectFromCanvas,
                    MaxThreads = RenderSchedulingSettings.MaxParallelWorkers,
                    AllowWorkerOversubscription = RenderSchedulingSettings.AllowWorkerOversubscription,
                    EnableThreadAffinity = RenderSchedulingSettings.EnableThreadAffinity,
                    WorkerCPUCoreIndexs = RenderSchedulingSettings.WorkerCPUCoreIndexs,
                    EnableMemoryBudget = RenderSchedulingSettings.EnableMemoryBudget,
                    MaxBufferedBytes = RenderSchedulingSettings.MaxBufferedBytes,
                    EnableGpuBufferPoolLimit = RenderSchedulingSettings.EnableGpuBufferPoolLimit,
                    GpuBufferPoolMaxBytes = RenderSchedulingSettings.GpuBufferPoolMaxBytes,
                };
                renderer.PrepareRender(token);
                await renderer.GoRender(token).ConfigureAwait(false);
                MaintainLocalPreviewCache();
                return previews;
            }, token);

        private PreviewFrameSource PrepareLocalClipPreview(IClip clip, int canvasWidth, int canvasHeight,
            int projectWidth, int projectHeight, uint frameIndex, CancellationToken token)
        {
            bool scRgb = OperatingSystem.IsWindows() && DefaultOutputMode != NativePreviewOutputMode.Disabled;
            var mode = IPicture.PicturePixelMode.Float16Picture;
            using var audio = EffectProcessing.OpenAudioFrame(_previewer?.AudioSamples);
            using var gpuSession = new HwAccelPictureSession
            {
                EnableBufferPoolLimit = RenderSchedulingSettings.EnableGpuBufferPoolLimit,
                BufferPoolMaxBytes = RenderSchedulingSettings.GpuBufferPoolMaxBytes,
            };
            using var context = new FrameHashContext(new()
            {
                Width = canvasWidth,
                Height = canvasHeight,
                ProjectWidth = projectWidth,
                ProjectHeight = projectHeight,
                PixelMode = mode.Value,
                Stage = "clip",
                ProcessFromCanvas = Timeline.ProcessEffectFromCanvas,
            }, token, nodeCache, gpuSession)
            { Clips = _clips ?? [clip] };
            using var hashScope = context.Enter();
            var clips = _clips ?? [clip];
            clip.ReInit(mode);
            using var cache = DynamicPreviewSettings.MemoryOnly ? null : new PreviewFrameCache(id => Path.Combine(DiskCacheRoot, id.ToString()), frameIndex,
                canvasWidth, canvasHeight, projectWidth, projectHeight, scRgb, false, token, keepHwAccelPicture: scRgb,
                findSource: key => GetLocalCacheIndex().FindPreviewSource(key), cacheSourceFrames: DynamicPreviewSettings.CacheSourceFrames);
            IPicture? rendered = null;
            IPicture Render() => rendered ??= projectFrameCut.Render.RPCProtocol.ClipPreviewRenderer.Render(clip, clips,
                canvasWidth, canvasHeight, projectWidth, projectHeight, frameIndex, token, mode, initializeClips: false, keepHwAccelPicture: scRgb)
                ?? throw new InvalidOperationException($"Clip {clip.Id} did not produce a preview frame.");
            try
            {
                if (DynamicPreviewSettings.MemoryOnly)
                {
                    var data = PreviewFrameData.Create(Render(), "", "", scRgb);
                    return PreviewFrameMaterializer.FromData(new("", data.Width, data.Height,
                        scRgb ? PreviewPixelFormat.Rgba16FloatScRgb : PreviewPixelFormat.EncodedImage, false), data);
                }
                var identity = FrameHashHelper.GetClipHash(clips, clip, frameIndex, context.Options, token);
                var cached = cache!.GetOrCreate(clip, clips, identity.Hash, identity.CanReuse,
                    Path.Combine(DiskCacheRoot, "temporary", $"{Guid.NewGuid():N}.vfd2m"),
                    () => { var picture = Render(); rendered = null; return picture; },
                    () => clips.All(c => !ClipInitializationFailure.IsMarked(c) && Timeline.CanCacheClipSource(c)));
                var result = PreviewFrameMaterializer.FromData(new(cached.Path, cached.Data.Width, cached.Data.Height,
                    scRgb ? PreviewPixelFormat.Rgba16FloatScRgb : PreviewPixelFormat.EncodedImage, false)
                { PreviewContainer = PreviewFrameContainer.GetContainer(cached.Path), PreviewEffectHash = cached.Data.EffectHash }, cached.Data);
                if (!cached.Persistent) File.Delete(cached.Path);
                else
                {
                    try { GetLocalCacheIndex().Track(cached.Path, clip.Id, frameIndex, cached.Data.Width, cached.Data.Height); }
                    catch (Exception ex) { LogLocalCacheError(ex); }
                }
                MaintainLocalPreviewCache();
                return result;
            }
            finally { rendered?.Dispose(); }
        }
    }
}

namespace projectFrameCut.LivePreview
{
    public partial class LivePreviewer
    {
        internal async Task<IReadOnlyList<PreviewFrameSource>> RenderClipFramesForDisplayAsync(IReadOnlyList<Guid> clips,
            uint frame, int width, int height, int projectWidth, int projectHeight, CancellationToken token)
        {
            var format = OperatingSystem.IsWindows() && DefaultOutputMode != NativePreviewOutputMode.Disabled
                ? PreviewPixelFormat.Rgba16FloatScRgb : PreviewPixelFormat.EncodedImage;
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(token);
            var pipes = new List<System.IO.Pipes.NamedPipeServerStream>();
            var receivers = new List<Task<PreviewFrameSource>>();
            try
            {
                var request = new ClipPreviewBatchRequest
                {
                    UseRenderer = true,
                    RenderOptions = new()
                    {
                        MaxThreads = RenderSchedulingSettings.MaxParallelWorkers,
                        AllowWorkerOversubscription = RenderSchedulingSettings.AllowWorkerOversubscription,
                        EnableThreadAffinity = RenderSchedulingSettings.EnableThreadAffinity,
                        WorkerCPUCoreIndexs = RenderSchedulingSettings.WorkerCPUCoreIndexs,
                        EnableMemoryBudget = RenderSchedulingSettings.EnableMemoryBudget,
                        MaxBufferedBytes = RenderSchedulingSettings.MaxBufferedBytes,
                        DisableGpuBufferPoolLimit = !RenderSchedulingSettings.EnableGpuBufferPoolLimit,
                        GpuBufferPoolMaxBytes = RenderSchedulingSettings.GpuBufferPoolMaxBytes,
                    },
                };
                foreach (var id in clips)
                {
                    var item = new ClipPreviewRequest
                    {
                        SessionId = RenderSessionId,
                        ClipId = id,
                        FrameIndex = frame,
                        CanvasWidth = width,
                        CanvasHeight = height,
                        ProjectWidth = projectWidth,
                        ProjectHeight = projectHeight,
                        PreferredPixelFormat = format,
                        UsePreviewMetadata = true,
                        DisableSourceFrameCache = !DynamicPreviewSettings.CacheSourceFrames,
                        DisableGpuBufferPoolLimit = !RenderSchedulingSettings.EnableGpuBufferPoolLimit,
                        GpuBufferPoolMaxBytes = RenderSchedulingSettings.GpuBufferPoolMaxBytes,
                    };
                    if (DynamicPreviewSettings.MemoryOnly)
                    {
                        item.PreviewPipeName = $"projectFrameCut-preview-{Guid.NewGuid():N}";
                        item.PreviewPipeToken = Guid.NewGuid().ToString("N");
                        var pipe = MemoryPreviewChannel.CreateServer(item.PreviewPipeName);
                        pipes.Add(pipe);
                        receivers.Add(ReceiveAsync(pipe, item.PreviewPipeToken));
                    }
                    request.Requests.Add(item);
                }
                var render = (RpcClient ?? RenderRpcBootstrap.Client).RenderClipPreviewBatchAsync(request, pending.Token).AsTask();
                var receive = Task.WhenAll(receivers);
                if (receivers.Count > 0 && await Task.WhenAny(render, receive).ConfigureAwait(false) == receive && !receive.IsCompletedSuccessfully)
                {
                    pending.Cancel();
                    try { await render.ConfigureAwait(false); } catch { }
                    return await receive.ConfigureAwait(false);
                }
                var response = await render.ConfigureAwait(false);
                if (response.Errors.Count > 0 || response.Artifacts.Count != clips.Count)
                    throw new InvalidOperationException(response.Errors.FirstOrDefault()?.Message ?? "The preview renderer did not return every clip.");
                if (receivers.Count > 0) return await receive.ConfigureAwait(false);
                var frames = new PreviewFrameSource[clips.Count];
                for (var i = 0; i < frames.Length; i++)
                {
                    var artifact = response.Artifacts[i];
                    if (artifact.PixelFormat is not (PreviewPixelFormat.VfdPicture or PreviewPixelFormat.PreviewFrame))
                        throw new InvalidDataException($"The render backend returned unsupported preview format {artifact.PixelFormat}.");
                    var path = await ResolveArtifactPathAsync(artifact, token).ConfigureAwait(false);
                    frames[i] = await Task.Run(() => PreviewFrameMaterializer.Prepare(new(path, artifact.Width, artifact.Height, format, false)
                    {
                        PreviewContainer = artifact.PreviewContainer,
                        PreviewEffectHash = artifact.PreviewEffectHash,
                    }), token).ConfigureAwait(false);
                }
                return frames;
            }
            finally
            {
                pending.Cancel();
                try { await Task.WhenAll(receivers).ConfigureAwait(false); } catch { }
                foreach (var pipe in pipes) pipe.Dispose();
            }

            async Task<PreviewFrameSource> ReceiveAsync(System.IO.Pipes.NamedPipeServerStream pipe, string secret)
            {
                await MemoryPreviewChannel.AuthenticateAsync(pipe, secret, pending.Token).ConfigureAwait(false);
                var (_, data) = await MemoryPreviewChannel.ReceiveAsync(pipe, pending.Token).ConfigureAwait(false);
                var source = PreviewFrameMaterializer.FromData(new("", data.Width, data.Height, format, false), data);
                await pipe.WriteAsync(new byte[] { 1 }, pending.Token).ConfigureAwait(false);
                return source;
            }
        }

        public PreviewFrameSource RenderClipFrameForDisplay(Guid clipId, uint frameIndex, int canvasWidth, int canvasHeight, int projectWidth, int projectHeight, CancellationToken token, string? vectorClipJson = null)
            => RenderClipFrameForDisplayAsync(clipId, frameIndex, canvasWidth, canvasHeight, projectWidth, projectHeight, token, vectorClipJson).GetAwaiter().GetResult();

        public async Task<PreviewFrameSource> RenderClipFrameForDisplayAsync(Guid clipId, uint frameIndex, int canvasWidth, int canvasHeight, int projectWidth, int projectHeight, CancellationToken token, string? vectorClipJson = null)
        {
            if (DynamicPreviewSettings.MemoryOnly)
            {
                var name = $"projectFrameCut-preview-{Guid.NewGuid():N}";
                var secret = Guid.NewGuid().ToString("N");
                using var pipe = MemoryPreviewChannel.CreateServer(name);
                using var pending = CancellationTokenSource.CreateLinkedTokenSource(token);
                var receive = ReceiveAsync();
                try
                {
                    var render = (RpcClient ?? RenderRpcBootstrap.Client).RenderClipPreviewAsync(new()
                    {
                        SessionId = RenderSessionId,
                        ClipId = clipId,
                        FrameIndex = frameIndex,
                        CanvasWidth = canvasWidth,
                        CanvasHeight = canvasHeight,
                        ProjectWidth = projectWidth,
                        ProjectHeight = projectHeight,
                        PreferredPixelFormat = OperatingSystem.IsWindows() && DefaultOutputMode != NativePreviewOutputMode.Disabled
                            ? PreviewPixelFormat.Rgba16FloatScRgb : PreviewPixelFormat.EncodedImage,
                        VectorClipJson = vectorClipJson,
                        UsePreviewMetadata = true,
                        DisableSourceFrameCache = true,
                        DisableGpuBufferPoolLimit = !RenderSchedulingSettings.EnableGpuBufferPoolLimit,
                        GpuBufferPoolMaxBytes = RenderSchedulingSettings.GpuBufferPoolMaxBytes,
                        PreviewPipeName = name,
                        PreviewPipeToken = secret,
                    }, pending.Token).AsTask();
                    if (await Task.WhenAny(render, receive).ConfigureAwait(false) == receive && !receive.IsCompletedSuccessfully)
                    {
                        pending.Cancel();
                        try { await render.ConfigureAwait(false); } catch { }
                        return await receive.ConfigureAwait(false);
                    }
                    await render.ConfigureAwait(false);
                    return await receive.ConfigureAwait(false);
                }
                finally
                {
                    pending.Cancel();
                    try { await receive.ConfigureAwait(false); } catch { }
                }

                async Task<PreviewFrameSource> ReceiveAsync()
                {
                    await MemoryPreviewChannel.AuthenticateAsync(pipe, secret, pending.Token).ConfigureAwait(false);
                    var (_, data) = await MemoryPreviewChannel.ReceiveAsync(pipe, pending.Token).ConfigureAwait(false);
                    var source = PreviewFrameMaterializer.FromData(new("", data.Width, data.Height,
                        data.ScRgb ? PreviewPixelFormat.Rgba16FloatScRgb : PreviewPixelFormat.EncodedImage, false), data);
                    await pipe.WriteAsync(new byte[] { 1 }, pending.Token).ConfigureAwait(false);
                    return source;
                }
            }
            var format = DefaultOutputMode != NativePreviewOutputMode.Disabled && OperatingSystem.IsWindows()
                ? PreviewPixelFormat.Rgba16FloatScRgb : PreviewPixelFormat.EncodedImage;
            try { return await PrepareDisplayAsync(format).ConfigureAwait(false); }
            catch (Exception ex) when (format == PreviewPixelFormat.Rgba16FloatScRgb && ex is not OperationCanceledException && !token.IsCancellationRequested)
            {
                Log(ex, $"Render native preview for clip {clipId} at frame #{frameIndex}; using standard preview", this);
                return await PrepareDisplayAsync(PreviewPixelFormat.EncodedImage).ConfigureAwait(false);
            }

            async Task<PreviewFrameSource> PrepareDisplayAsync(PreviewPixelFormat target)
            {
                for (int attempt = 0; ; attempt++)
                {
                    var artifact = await (RpcClient ?? RenderRpcBootstrap.Client).RenderClipPreviewAsync(new ClipPreviewRequest
                    {
                        SessionId = RenderSessionId,
                        ClipId = clipId,
                        FrameIndex = frameIndex,
                        CanvasWidth = canvasWidth,
                        CanvasHeight = canvasHeight,
                        ProjectWidth = projectWidth,
                        ProjectHeight = projectHeight,
                        PreferredPixelFormat = target,
                        VectorClipJson = vectorClipJson,
                        UsePreviewMetadata = true,
                        DisableSourceFrameCache = !DynamicPreviewSettings.CacheSourceFrames,
                        DisableGpuBufferPoolLimit = !RenderSchedulingSettings.EnableGpuBufferPoolLimit,
                        GpuBufferPoolMaxBytes = RenderSchedulingSettings.GpuBufferPoolMaxBytes,
                    }, token).ConfigureAwait(false);
                    if (artifact.PixelFormat is not (PreviewPixelFormat.VfdPicture or PreviewPixelFormat.PreviewFrame))
                        throw new InvalidDataException($"The render backend returned unsupported preview format {artifact.PixelFormat}.");
                    try
                    {
                        var path = await ResolveArtifactPathAsync(artifact, token).ConfigureAwait(false);
                        return await Task.Run(() => PreviewFrameMaterializer.Prepare(new(path, artifact.Width, artifact.Height, target, false)
                        {
                            PreviewContainer = artifact.PreviewContainer,
                            PreviewEffectHash = artifact.PreviewEffectHash,
                        }), token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (attempt == 0 && !token.IsCancellationRequested && (ex is IOException or InvalidDataException)) { }
                }
            }
        }

        public bool CanCacheClipFrame(Guid clipId)
            => Clips?.FirstOrDefault(c => c.Id == clipId) is { } clip
                && !ClipInitializationFailure.IsMarked(clip)
                && Timeline.CanCacheClipFrame(Clips, clip);
    }
}
