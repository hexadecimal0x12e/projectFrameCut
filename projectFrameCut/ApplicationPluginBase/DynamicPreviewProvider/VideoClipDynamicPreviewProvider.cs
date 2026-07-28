using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.ApplicationAPIBase.Helpers;
using System.Collections.Concurrent;
using IPicture = projectFrameCut.Drawing.Base.IPicture;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;

namespace projectFrameCut.ApplicationPluginBase.DynamicPreviewProvider;

internal sealed class VideoClipDynamicPreviewProvider : InternalClipDynamicPreviewProviderBase
{
    private const int PrefetchForwardCount = 8;
    private const int PrefetchBackwardCount = 1;

    private static readonly ConcurrentDictionary<Guid, ConcurrentDictionary<VideoFrameCacheKey, CachedVideoFrame>> _perClipCache = new();
    private static readonly ConcurrentDictionary<VideoPrefetchContextKey, VideoPrefetchContext> _prefetchContexts = new();
    private static readonly ConcurrentDictionary<string, long> _diskFrameAccess = new(StringComparer.Ordinal);

    public static string DiskCacheRoot
    {
        get;
        set
        {
            if (Directory.Exists(value)) field = value;
        }
    } = Path.Combine(MauiProgram.DataPath, "RenderCache", "perClip");

    public override string TypeName => "VideoClip";

    public override bool IsPrepareGenerateDispatchable => true;

    public override bool IsAvailable(IClip target)
    {
        return target is VideoClip
            && target.FromPlugin == InternalPluginBase.InternalPluginBaseID
            && !string.IsNullOrWhiteSpace(target.FilePath)
            && File.Exists(target.FilePath);
    }

    public override Task<IPicture> PrepareSource(IClip target, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint targetFrame, CancellationToken cancellationToken)
    {
        if (target is not VideoClip clip || string.IsNullOrWhiteSpace(clip.FilePath) || !File.Exists(clip.FilePath))
            throw new InvalidOperationException("Video source is unavailable.");

        clip.Decoder?.EnableLock = true;

        var (renderWidth, renderHeight) = ResolveRenderSize(target, canvasWidth, canvasHeight, targetWidth, targetHeight);
        var sourceFingerprint = ResolveSourceFingerprint(clip.FilePath);
        var contextKey = new VideoPrefetchContextKey(clip.Id, renderWidth, renderHeight, sourceFingerprint);
        var frameKey = new VideoFrameCacheKey(clip.Id, renderWidth, renderHeight, targetFrame, sourceFingerprint);

        return Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (TryGetCachedFrame(frameKey, out var cachedFrame))
                {
                    EnqueuePrefetch(clip, contextKey, targetFrame);
                    TouchDiskEntry(ResolveDiskCachePath(frameKey));
                    return cachedFrame;
                }

                cancellationToken.ThrowIfCancellationRequested();

                using var frame = clip.GetFrameRelativeToStartPointOfSource(target.TryGetRelativeFrameIndex(targetFrame, target.StartFrame) ?? 0, renderWidth, renderHeight, true, 8);

                cancellationToken.ThrowIfCancellationRequested();

                var result = frame.BitPerPixel == IPicture.PicturePixelMode.BytePicture
                    ? frame.Clone()
                    : frame.ToBitPerPixel(IPicture.PicturePixelMode.BytePicture);

                var diskPath = ResolveDiskCachePath(frameKey);
                TryPersistFrameToDisk(result, diskPath);
                CacheFrame(frameKey, result, diskPath);
                EnqueuePrefetch(clip, contextKey, targetFrame);

                return result;
            }
            catch (OperationCanceledException) { return Picture8bpp.GenerateSolidColor(1, 1, 0, 0, 0, 1f); }
            catch { throw; }
        }, cancellationToken);
    }

    public override View Generate(IClip target, IPicture? preparedSource, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint targetFrame)
    {
        if (preparedSource != null)
        {
            return BuildImage(preparedSource.ToImageSource());
        }

        if (target is not VideoClip clip || string.IsNullOrWhiteSpace(clip.FilePath) || !File.Exists(clip.FilePath))
        {
            return BuildFallbackLabel("Video source is unavailable.");
        }
        clip.Decoder?.EnableLock = true;

        var (renderWidth, renderHeight) = ResolveRenderSize(target, canvasWidth, canvasHeight, targetWidth, targetHeight);
        var sourceFingerprint = ResolveSourceFingerprint(clip.FilePath);
        var contextKey = new VideoPrefetchContextKey(clip.Id, renderWidth, renderHeight, sourceFingerprint);
        var frameKey = new VideoFrameCacheKey(clip.Id, renderWidth, renderHeight, targetFrame, sourceFingerprint);

        if (TryGetCachedFrame(frameKey, out var cachedFrame))
        {
            EnqueuePrefetch(clip, contextKey, targetFrame);
            return BuildImage(cachedFrame.ToImageSource());
        }

        var decoded = RenderFrameAsImageSource(clip, renderWidth, renderHeight, targetFrame, contextKey, frameKey, persistToDisk: false);
        if (decoded is null)
        {
            return BuildFallbackLabel("Video source is unavailable.");
        }

        CacheFrame(frameKey, decoded.Value.Frame, decoded.Value.DiskPath);
        EnqueuePrefetch(clip, contextKey, targetFrame);

        return BuildImage(decoded.Value.Frame.ToImageSource());
    }

    private static Image BuildImage(ImageSource source)
    {
        return new Image
        {
            Source = source,
            Aspect = Aspect.Fill,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
        };
    }

    /// <summary>
    /// 计算视频帧的渲染尺寸。
    /// 使用 Clip 的逻辑尺寸（或项目尺寸）作为画布，使生成出来的帧能 1:1 填入
    /// PreviewHost；同时按 DynamicPreview 已经对画布施加的缩放因子等比缩小，
    /// 保留视频原始比例。这样就不会把按编辑器比例 letterbox 后的画布再拉伸到
    /// Clip 边界，从而避免预览画面与选择框错位。
    /// </summary>
    private static (int renderWidth, int renderHeight) ResolveRenderSize(IClip target, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight)
    {
        var clipW = target.TargetWidth > 0 ? target.TargetWidth : (targetWidth > 0 ? targetWidth : canvasWidth);
        var clipH = target.TargetHeight > 0 ? target.TargetHeight : (targetHeight > 0 ? targetHeight : canvasHeight);

        double scaleW = targetWidth > 0 ? (double)canvasWidth / targetWidth : 1.0;
        double scaleH = targetHeight > 0 ? (double)canvasHeight / targetHeight : 1.0;
        var scale = Math.Min(scaleW, scaleH);
        if (scale <= 0 || !double.IsFinite(scale)) scale = 1.0;

        var renderWidth = Math.Max(1, (int)Math.Round(clipW * scale));
        var renderHeight = Math.Max(1, (int)Math.Round(clipH * scale));

        return (renderWidth, renderHeight);
    }

    private static bool TryGetCachedFrame(VideoFrameCacheKey frameKey, out IPicture frame)
    {
        if (_perClipCache.TryGetValue(frameKey.ClipId, out var clipCache)
            && clipCache.TryGetValue(frameKey, out var cached))
        {
            cached.Touch();
            TouchDiskEntry(cached.DiskPath);
            frame = cached.Frame.Clone();
            return true;
        }

        var diskPath = ResolveDiskCachePath(frameKey);
        if (File.Exists(diskPath))
        {
            try
            {
                frame = new Picture8bpp(diskPath);
                CacheFrame(frameKey, frame, diskPath);
                return true;
            }
            catch
            {
                // Ignore broken disk cache entries and fall through to decode.
            }
        }

        frame = null!;
        return false;
    }

    private static void CacheFrame(VideoFrameCacheKey frameKey, IPicture frame, string? diskPath)
    {
        var clipCache = _perClipCache.GetOrAdd(frameKey.ClipId, static _ => new ConcurrentDictionary<VideoFrameCacheKey, CachedVideoFrame>());
        clipCache[frameKey] = new CachedVideoFrame(frame.Clone(), diskPath);
        TouchDiskEntry(diskPath);
    }

    private static void EnqueuePrefetch(VideoClip clip, VideoPrefetchContextKey contextKey, uint targetFrame)
    {
        var context = _prefetchContexts.GetOrAdd(contextKey, static _ => new VideoPrefetchContext());

        foreach (var frameIndex in EnumeratePrefetchFrames(targetFrame))
        {
            var frameKey = new VideoFrameCacheKey(contextKey.ClipId, contextKey.CanvasWidth, contextKey.CanvasHeight, frameIndex, contextKey.SourceFingerprint);
            if (IsFrameCached(frameKey))
            {
                continue;
            }

            if (context.PendingFrames.TryAdd(frameIndex, 0))
            {
                context.Queue.Enqueue(frameIndex);
            }
        }

        StartPrefetchWorkerIfNeeded(clip, contextKey, context);
    }

    private static IEnumerable<uint> EnumeratePrefetchFrames(uint centerFrame)
    {
        for (var i = 1; i <= PrefetchForwardCount; i++)
        {
            if (centerFrame <= uint.MaxValue - (uint)i)
            {
                yield return centerFrame + (uint)i;
            }
        }

        for (var i = 1; i <= PrefetchBackwardCount; i++)
        {
            if (centerFrame >= (uint)i)
            {
                yield return centerFrame - (uint)i;
            }
        }
    }

    private static void StartPrefetchWorkerIfNeeded(VideoClip clip, VideoPrefetchContextKey contextKey, VideoPrefetchContext context)
    {
        if (Interlocked.CompareExchange(ref context.WorkerRunning, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            while (true)
            {
                while (context.Queue.TryDequeue(out var frameIndex))
                {
                    context.PendingFrames.TryRemove(frameIndex, out _);

                    var frameKey = new VideoFrameCacheKey(contextKey.ClipId, contextKey.CanvasWidth, contextKey.CanvasHeight, frameIndex, contextKey.SourceFingerprint);
                    if (IsFrameCached(frameKey))
                    {
                        continue;
                    }

                    var decoded = RenderFrameAsImageSource(clip, contextKey.CanvasWidth, contextKey.CanvasHeight, frameIndex, contextKey, frameKey, persistToDisk: true);
                    if (decoded is not null)
                    {
                        CacheFrame(frameKey, decoded.Value.Frame, decoded.Value.DiskPath);
                    }
                }

                Interlocked.Exchange(ref context.WorkerRunning, 0);

                if (context.Queue.IsEmpty || Interlocked.CompareExchange(ref context.WorkerRunning, 1, 0) != 0)
                {
                    break;
                }
            }
        });
    }

    private static RenderedVideoFrame? RenderFrameAsImageSource(IClip clip, int renderWidth, int renderHeight, uint targetFrame, VideoPrefetchContextKey contextKey, VideoFrameCacheKey frameKey, bool persistToDisk)
    {
        // Double-check cache before decoding (handles race between main thread and prefetch worker).
        if (TryGetCachedFrame(frameKey, out var cached))
        {
            var cachedPath = ResolveDiskCachePath(frameKey);
            return new RenderedVideoFrame(cached, File.Exists(cachedPath) ? cachedPath : null);
        }

        clip.ReInit(8);
        using var frame = clip.GetFrameRelativeToStartPointOfSource(clip.TryGetRelativeFrameIndex(targetFrame, clip.StartFrame) ?? 0, renderWidth, renderHeight, true, 8);

        var result = frame.BitPerPixel == IPicture.PicturePixelMode.BytePicture
            ? frame.Clone()
            : frame.ToBitPerPixel(IPicture.PicturePixelMode.BytePicture);

        string? diskPath = null;
        if (persistToDisk)
        {
            diskPath = ResolveDiskCachePath(frameKey);
            if (TryPersistFrameToDisk(result, diskPath))
            {
                TouchDiskEntry(diskPath);
            }
            else
            {
                diskPath = null;
            }
        }

        return new RenderedVideoFrame(result, diskPath);
    }

    private static bool IsFrameCached(VideoFrameCacheKey frameKey)
    {
        if (_perClipCache.TryGetValue(frameKey.ClipId, out var clipCache) && clipCache.ContainsKey(frameKey))
        {
            return true;
        }

        var diskPath = ResolveDiskCachePath(frameKey);
        if (File.Exists(diskPath))
        {
            TouchDiskEntry(diskPath);
            return true;
        }

        return false;
    }

    private static bool TryPersistFrameToDisk(IPicture frame, string diskPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(diskPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            frame.SaveToPng(diskPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static long ResolveSourceFingerprint(string sourcePath)
    {
        try
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists)
            {
                return StringComparer.Ordinal.GetHashCode(sourcePath);
            }

            unchecked
            {
                return (info.Length * 397L) ^ info.LastWriteTimeUtc.Ticks;
            }
        }
        catch
        {
            return StringComparer.Ordinal.GetHashCode(sourcePath);
        }
    }

    private static string ResolveDiskCachePath(VideoFrameCacheKey frameKey)
    {
        var clipId = SanitizePathSegment(frameKey.ClipId.ToString());
        var dimension = $"{frameKey.CanvasWidth}x{frameKey.CanvasHeight}";
        var fingerprint = frameKey.SourceFingerprint.ToString("X16");
        return Path.Combine(DiskCacheRoot, clipId, dimension, fingerprint, $"{frameKey.FrameIndex}.png");
    }

    private static string SanitizePathSegment(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "_";
        }

        var chars = raw.ToCharArray();
        var invalids = Path.GetInvalidFileNameChars();
        for (var i = 0; i < chars.Length; i++)
        {
            if (invalids.Contains(chars[i]))
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }

    private static void TouchDiskEntry(string? diskPath)
    {
        if (string.IsNullOrWhiteSpace(diskPath))
        {
            return;
        }

        _diskFrameAccess[diskPath] = DateTime.UtcNow.Ticks;
    }

    private sealed class VideoPrefetchContext
    {
        public ConcurrentQueue<uint> Queue { get; } = new();
        public ConcurrentDictionary<uint, byte> PendingFrames { get; } = new();
        public int WorkerRunning;
    }

    private sealed class CachedVideoFrame
    {
        public CachedVideoFrame(IPicture frame, string? diskPath)
        {
            Frame = frame;
            DiskPath = diskPath;
            Touch();
        }

        public IPicture Frame { get; }
        public string? DiskPath { get; }
        public long LastAccessTicks { get; private set; }

        public void Touch()
        {
            LastAccessTicks = DateTime.UtcNow.Ticks;
        }
    }

    private readonly record struct RenderedVideoFrame(IPicture Frame, string? DiskPath);
    private sealed record VideoFrameCacheKey(Guid ClipId, int CanvasWidth, int CanvasHeight, uint FrameIndex, long SourceFingerprint);
    private sealed record VideoPrefetchContextKey(Guid ClipId, int CanvasWidth, int CanvasHeight, long SourceFingerprint);
}
