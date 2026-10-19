using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Context;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RPCProtocol;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace projectFrameCut.Render.Rendering;

public partial class Renderer
{
    private DeferredRenderCache? deferredCache;
    private Action? cleanupDeferredCache;
    private BlockingCollection<DeferredFrame>? deferredWrites;
    private long deferredBufferedBytes;
    private sealed record DeferredFrame(uint Frame, IPicture? Picture, bool Reusable, long Bytes);
    private int preparedFrames, restoredComposites;
    private readonly ConcurrentDictionary<uint, byte> restoredCompositeFrames = new();

    /// <summary>Remove this render's new disk frames after its output has been finalized.</summary>
    public void CleanupCreatedDeferredCache() => cleanupDeferredCache?.Invoke();

    private void InitializeDeferredCache(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string? signature = null;
        if (!string.IsNullOrWhiteSpace(RenderResumeSignature) && !string.IsNullOrWhiteSpace(DynamicPreviewCacheProjectRoot)
            && !Clips.Any(c => c is VideoClip { Decoder: EncodeAndDecode.DecoderContextPJFCProject }
                || Uri.TryCreate(c.FilePath, UriKind.Absolute, out var uri) && !uri.IsFile))
        {
            signature = FrameHashHelper.HashValue(new
            {
                DeferredRenderCache.Version, State = RenderResumeSignature, StartFrame, Duration, Options = HashOptions,
                EnableEffectAutoRetry,
                Clips = Clips.Select(c => new
                {
                    c.Id, Source = FrameHashHelper.SourceFingerprint(c.FilePath),
                    Font = c is TextClip text ? FrameHashHelper.SourceFingerprint(text.FontPath) : null,
                }).ToArray(),
                Modules = Clips.Select(c => c.GetType().Module.ModuleVersionId)
                    .Concat(Clips.SelectMany(c => c.EffectsInstances ?? []).Select(e => e.GetType().Module.ModuleVersionId))
                    .Concat(Clips.SelectMany(c => c.EffectProvidersInstances ?? []).Select(p => p.GetType().Module.ModuleVersionId))
                    .Concat(PluginManager.LoadedPlugins.Values.Select(p => p.GetType().Module.ModuleVersionId))
                    .Append(typeof(Renderer).Module.ModuleVersionId).Append(typeof(Picture8bpp).Module.ModuleVersionId)
                    .Distinct().OrderBy(x => x).ToArray(),
                ForcedImplementation = EffectHelper.ForcePreferToType,
                DefaultImplementations = EffectHelper.DefaultImplementsType,
                PreferredPlugins = IPluginBase.EffectImplementations.PreferredPlugins
                    .OrderBy(p => p.Key.TypeName, StringComparer.Ordinal).ThenBy(p => p.Key.ImplementType)
                    .Select(p => new { p.Key, p.Value }).ToArray(),
                ApproximateMixture = ClassicOverlayMixture.EnableApproximatePath,
                AntiAlias = IVectorContentClip.GlobalDefaultAntiAliasMode,
                ExternalSources = ProjectExternalSourceRuntime.Current.CacheToken,
            });
        }
        if (ResumeInterruptedRender && signature is null)
            Log("[Render] Using an isolated deferred job because a stable project/source signature is unavailable.");
        deferredCache = new(DynamicPreviewCacheProjectRoot, signature, ResumeInterruptedRender, VfdEncoder);
        Log($"[Render] Deferred cache: {deferredCache.DirectoryPath}, restoring={deferredCache.Restoring}, encoder={VfdEncoder.GetType().Name}.");
    }

    private ClipRequest[] GetDeferredRequests(uint frame, CancellationToken token)
    {
        var cache = deferredCache!;
        if (cache.Restoring && cache.TryRead(cache.ComposedPath(frame), token, out var info, out var picture))
        {
            picture?.Dispose();
            if (IsComposedFrame(info, frame) && info.Reusable)
            {
                restoredCompositeFrames[frame] = 0;
                Interlocked.Increment(ref restoredComposites);
                return [];
            }
        }
        return GetFrameRequests(frame);
    }

    private bool IsComposedFrame(DeferredRenderCache.FrameInfo info, uint frame) => info.Composed && info.Frame == frame
        && info.HasPicture && info.Width == TargetWidth && info.Height == TargetHeight
        && info.Bits == _ppb && info.Hdr == UseHDR;

    // Takes ownership of the composed picture, including when enqueueing fails.
    private void SaveDeferredFrame(uint frame, IPicture? picture, CancellationToken token)
    {
        long bytes = 0;
        try
        {
            if (picture is not null)
            {
                bytes = EstimateDeferredFrameBytes(picture.Width, picture.Height);
                Interlocked.Add(ref deferredBufferedBytes, bytes);
            }
            using (RenderFrameDiagnostics.Current?.Measure("Queue composed frame"))
                deferredWrites!.Add(new(frame, picture, !Clips.Any(ClipInitializationFailure.IsMarked), bytes), token);
            picture = null;
            bytes = 0;
            CompleteThreadFrame(frame);
        }
        finally
        {
            picture?.Dispose();
            Interlocked.Add(ref deferredBufferedBytes, -bytes);
        }
    }

    private long EstimateDeferredFrameBytes(int width, int height) =>
        checked((long)width * height * (3 * (_ppb / 8) + sizeof(float) * (UseHDR ? 2 : 1)));

    private void WriteDeferredFrames(BlockingCollection<DeferredFrame> frames)
    {
        // Drain accepted canvases on cancellation so they remain available for resume.
        foreach (var frame in frames.GetConsumingEnumerable())
        {
            SetThreadWorking(frame.Frame, true);
            try
            {
                if (frame.Picture is { } picture)
                    deferredCache!.Write(deferredCache.ComposedPath(frame.Frame), new()
                    {
                        Frame = frame.Frame, Composed = true, HasPicture = true, Reusable = frame.Reusable,
                        Width = picture.Width, Height = picture.Height, Bits = (int)picture.BitPerPixel,
                        Hdr = picture is IHDRPicture<ushort>,
                    }, picture, CancellationToken.None);
                Interlocked.Increment(ref preparedFrames);
                CompleteThreadFrame(frame.Frame);
                InvokeProgress();
            }
            finally
            {
                frame.Picture?.Dispose();
                Interlocked.Add(ref deferredBufferedBytes, -frame.Bytes);
                SetThreadWorking(null, false);
            }
        }
    }

    private void RenderDeferredFrames(AutoResetEvent changed, CancellationToken token, IPicture? cover, long memoryBudget)
    {
        var cache = deferredCache!;
        cache.SetStage("rendering");
        int capacity = (int)Math.Clamp((EnableMemoryBudget ? memoryBudget / 4 : memoryBudget)
            / Math.Max(1, EstimateDeferredFrameBytes(TargetWidth, TargetHeight)), 1, RenderAheadFrames);
        using var frames = new BlockingCollection<DeferredFrame>(capacity);
        deferredWrites = frames;
        var writer = StartRenderThread("VFD cache writer", () => WriteDeferredFrames(frames),
            token, workerFailure!);
        Log($"[Render] VFD cache writer started; queue capacity={capacity} frames.");
        try
        {
            if (serialRendering)
            {
                Interlocked.Increment(ref ThreadWorking);
                try { RenderSerialFrames(token, null, cacheOutput: true); }
                finally { Interlocked.Decrement(ref ThreadWorking); }
            }
            else RenderFrames(changed, token, null, memoryBudget, cacheOutput: true);
            CompleteClipJobs((ulong)StartFrame + Duration);
            SetThreadWorking(null, false);
            foreach (var worker in clipWorkers.Values) worker.Thread?.Join();
        }
        finally
        {
            frames.CompleteAdding();
            writer.Join();
            deferredWrites = null;
            while (frames.TryTake(out var frame))
            {
                frame.Picture?.Dispose();
                Interlocked.Add(ref deferredBufferedBytes, -frame.Bytes);
            }
        }
        token.ThrowIfCancellationRequested();

        Log($"[Render] All composed frames cached; restored {restoredComposites} frames. Writing video in timeline order.");
        cache.SetStage("encoding");
        if (cover is not null) builder?.Writer.Append(cover);
        for (ulong i = StartFrame, end = (ulong)StartFrame + Duration; i < end; i++)
        {
            token.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            SetThreadWorking((uint)i, true);
            Interlocked.Increment(ref ThreadWorking);
            try { WriteFrame((uint)i, ReadDeferredComposedFrame((uint)i, token), sw); }
            finally
            {
                SetThreadWorking(null, false);
                Interlocked.Decrement(ref ThreadWorking);
                IRenderContext.CurrentFrameBuffer = null;
                IRenderContext.ClearWorkerState();
            }
        }
        token.ThrowIfCancellationRequested();
        cache.SetStage("completed");
        cleanupDeferredCache = cache.CleanupCreatedFiles;
    }

    private IPicture ReadDeferredComposedFrame(uint frame, CancellationToken token)
    {
        if (!deferredCache!.TryRead(deferredCache.ComposedPath(frame), token, out var info, out var picture)
            || !IsComposedFrame(info, frame) || picture is null)
        {
            picture?.Dispose();
            throw new InvalidDataException($"Deferred composed frame {frame} is missing or invalid.");
        }
        return picture;
    }
}
