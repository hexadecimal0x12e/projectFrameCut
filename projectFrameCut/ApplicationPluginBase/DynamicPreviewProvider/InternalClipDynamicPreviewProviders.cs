using projectFrameCut.ApplicationAPIBase.DynamicPreviewProvider;
using CommunityToolkit.Maui.Views;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using System.Linq;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.Render.Effect;
using projectFrameCut.Shared;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using projectFrameCut.Render.Transform;
using IPicture = projectFrameCut.Drawing.Base.IPicture;
using RenderITransform = projectFrameCut.Render.RenderAPIBase.ClipAndTrack.ITransform;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Drawing.Base.Picture;

namespace projectFrameCut.ApplicationPluginBase.DynamicPreviewProvider;

internal abstract class InternalClipDynamicPreviewProviderBase : IClipDynamicPreviewProvider
{
    public abstract string TypeName { get; }

    public abstract bool IsAvailable(IClip target);

    public abstract View Generate(IClip target, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint targetFrame);

    protected static Label BuildFallbackLabel(string text)
    {
        return new Label
        {
            Text = text,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            TextColor = Colors.White,
            BackgroundColor = Color.FromArgb("#55000000"),
            Padding = new Thickness(8)
        };
    }
}

internal static class TransformClipDynamicPreviewRuntimeKeys
{
    public const string LeftClip = "__dynamicPreview_transform_left_clip";
    public const string RightClip = "__dynamicPreview_transform_right_clip";
}

internal sealed class VideoClipDynamicPreviewProvider : InternalClipDynamicPreviewProviderBase
{
    private const int PrefetchForwardCount = 8;
    private const int PrefetchBackwardCount = 1;

    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<VideoFrameCacheKey, CachedVideoFrame>> _perClipCache = new(StringComparer.Ordinal);
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

    public override bool IsAvailable(IClip target)
    {
        return target is VideoClip
            && target.FromPlugin == InternalPluginBase.InternalPluginBaseID
            && !string.IsNullOrWhiteSpace(target.FilePath)
            && File.Exists(target.FilePath);
    }

    public override View Generate(IClip target, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint targetFrame)
    {
        if (target is not VideoClip clip || string.IsNullOrWhiteSpace(clip.FilePath) || !File.Exists(clip.FilePath))
        {
            return BuildFallbackLabel("Video source is unavailable.");
        }
        clip.Decoder?.EnableLock = true;

        var sourceFingerprint = ResolveSourceFingerprint(clip.FilePath);
        var contextKey = new VideoPrefetchContextKey(clip.Id, canvasWidth, canvasHeight, sourceFingerprint);
        var frameKey = new VideoFrameCacheKey(clip.Id, canvasWidth, canvasHeight, targetFrame, sourceFingerprint);

        if (TryGetCachedFrame(frameKey, out var cachedSource))
        {
            EnqueuePrefetch(clip, contextKey, targetFrame);
            return BuildImage(cachedSource);
        }

        var decoded = RenderFrameAsImageSource(clip, canvasWidth, canvasHeight, targetFrame, contextKey, frameKey, persistToDisk: false);
        if (decoded is null)
        {
            return BuildFallbackLabel("Video source is unavailable.");
        }

        CacheFrame(frameKey, decoded.Value.Source, decoded.Value.DiskPath);
        EnqueuePrefetch(clip, contextKey, targetFrame);

        return BuildImage(decoded.Value.Source);
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

    private static bool TryGetCachedFrame(VideoFrameCacheKey frameKey, out ImageSource source)
    {
        if (_perClipCache.TryGetValue(frameKey.ClipId, out var clipCache)
            && clipCache.TryGetValue(frameKey, out var cached))
        {
            cached.Touch();
            TouchDiskEntry(cached.DiskPath);
            source = cached.Source;
            return true;
        }

        var diskPath = ResolveDiskCachePath(frameKey);
        if (File.Exists(diskPath))
        {
            try
            {
                source = ImageSource.FromFile(diskPath);
                CacheFrame(frameKey, source, diskPath);
                return true;
            }
            catch
            {
                // Ignore broken disk cache entries and fall through to decode.
            }
        }

        source = null!;
        return false;
    }

    private static void CacheFrame(VideoFrameCacheKey frameKey, ImageSource source, string? diskPath)
    {
        var clipCache = _perClipCache.GetOrAdd(frameKey.ClipId, static _ => new ConcurrentDictionary<VideoFrameCacheKey, CachedVideoFrame>());
        clipCache[frameKey] = new CachedVideoFrame(source, diskPath);
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
                        CacheFrame(frameKey, decoded.Value.Source, decoded.Value.DiskPath);
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

    private static RenderedVideoFrame? RenderFrameAsImageSource(IClip clip, int canvasWidth, int canvasHeight, uint targetFrame, VideoPrefetchContextKey contextKey, VideoFrameCacheKey frameKey, bool persistToDisk)
    {
        // Double-check cache before decoding (handles race between main thread and prefetch worker).
        if (TryGetCachedFrame(frameKey, out var cached))
        {
            var cachedPath = ResolveDiskCachePath(frameKey);
            return new RenderedVideoFrame(cached, File.Exists(cachedPath) ? cachedPath : null);
        }

        using var frame = clip.GetFrameRelativeToStartPointOfSource(clip.TryGetRelativeFrameIndex(targetFrame, clip.StartFrame) ?? 0, canvasWidth, canvasHeight, true, 8);

        if (persistToDisk)
        {
            var diskPath = ResolveDiskCachePath(frameKey);
            if (TryPersistFrameToDisk(frame, diskPath))
            {
                var diskSource = ImageSource.FromFile(diskPath);
                TouchDiskEntry(diskPath);
                return new RenderedVideoFrame(diskSource, diskPath);
            }
        }

        return new RenderedVideoFrame(frame.ToImageSource(), null);
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

    private static bool TryPersistFrameToDisk(projectFrameCut.Drawing.Base.IPicture frame, string diskPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(diskPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            frame.SaveAsPng8bpp(diskPath, null);
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
        var clipId = SanitizePathSegment(frameKey.ClipId);
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
        public CachedVideoFrame(ImageSource source, string? diskPath)
        {
            Source = source;
            DiskPath = diskPath;
            Touch();
        }

        public ImageSource Source { get; }
        public string? DiskPath { get; }
        public long LastAccessTicks { get; private set; }

        public void Touch()
        {
            LastAccessTicks = DateTime.UtcNow.Ticks;
        }
    }

    private readonly record struct RenderedVideoFrame(ImageSource Source, string? DiskPath);
    private sealed record VideoFrameCacheKey(string ClipId, int CanvasWidth, int CanvasHeight, uint FrameIndex, long SourceFingerprint);
    private sealed record VideoPrefetchContextKey(string ClipId, int CanvasWidth, int CanvasHeight, long SourceFingerprint);
}

internal sealed class PhotoClipDynamicPreviewProvider : InternalClipDynamicPreviewProviderBase
{
    public override string TypeName => "PhotoClip";

    public override bool IsAvailable(IClip target)
    {
        return target is PhotoClip
            && target.FromPlugin == InternalPluginBase.InternalPluginBaseID
            && !string.IsNullOrWhiteSpace(target.FilePath)
            && File.Exists(target.FilePath);
    }

    public override View Generate(IClip target, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint targetFrame)
    {
        if (target is not PhotoClip clip || string.IsNullOrWhiteSpace(clip.FilePath) || !File.Exists(clip.FilePath))
        {
            return BuildFallbackLabel("Image source is unavailable.");
        }

        return new Image
        {
            Source = ImageSource.FromFile(clip.FilePath),
            Aspect = Aspect.Fill,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
        };
    }
}

internal sealed class SolidColorClipDynamicPreviewProvider : InternalClipDynamicPreviewProviderBase
{
    public override string TypeName => "SolidColorClip";

    public override bool IsAvailable(IClip target)
    {
        return target is SolidColorClip
            && target.FromPlugin == InternalPluginBase.InternalPluginBaseID;
    }

    public override View Generate(IClip target, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint targetFrame)
    {
        if (target is not SolidColorClip clip)
        {
            return BuildFallbackLabel("Solid color clip is unavailable.");
        }

        var resolvedWidth = clip.TargetWidth > 0 ? clip.TargetWidth : clip.EffectiveOutputWidth;
        var resolvedHeight = clip.TargetHeight > 0 ? clip.TargetHeight : clip.EffectiveOutputHeight;

        if (targetWidth > 0)
        {
            resolvedWidth = Math.Min(resolvedWidth, targetWidth);
        }

        if (targetHeight > 0)
        {
            resolvedHeight = Math.Min(resolvedHeight, targetHeight);
        }

        var previewWidth = Math.Max(1, resolvedWidth > 0 ? resolvedWidth : (targetWidth > 0 ? targetWidth : canvasWidth));
        var previewHeight = Math.Max(1, resolvedHeight > 0 ? resolvedHeight : (targetHeight > 0 ? targetHeight : canvasHeight));
        var alpha = clip.A.HasValue ? Math.Clamp(clip.A.Value, 0f, 1f) : 1f;
        return new Grid
        {
            WidthRequest = previewWidth,
            HeightRequest = previewHeight,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
            Children =
            {
                new BoxView
                {
                    Color = Color.FromRgba(clip.R / 65535f, clip.G / 65535f, clip.B / 65535f, alpha),
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Fill,
                }
            }
        };
    }
}

internal sealed class TransformClipDynamicPreviewProvider : InternalClipDynamicPreviewProviderBase
{
    private const int MaxCachedSourceFrames = 160;
    private const int MaxDiskCachedSourceFrames = 2000;
    private static readonly ConcurrentDictionary<TransformSourceFrameCacheKey, CachedSourceFrame> _sourceFrameCache = new();
    private static readonly ConcurrentDictionary<string, long> _diskFrameAccess = new(StringComparer.Ordinal);
    private static readonly string _diskCacheRoot = Path.Combine(MauiProgram.DataPath, "RenderCache", "perClip");

    public override string TypeName => "TransformClip";

    public override bool IsAvailable(IClip target)
    {
        if (target is not TransformContainer transformClip
            || target.FromPlugin != InternalPluginBase.InternalPluginBaseID
            || transformClip.Transform is not IContinuousTransform continuousTransform)
        {
            return false;
        }

        if (continuousTransform is ExternalSourceTransform externalSourceTransform)
        {
            return !string.IsNullOrWhiteSpace(externalSourceTransform.SourcePath)
                && File.Exists(externalSourceTransform.SourcePath);
        }

        return true;
    }

    public override View Generate(IClip target, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint targetFrame)
    {
        if (target is not TransformContainer transformClip
            || transformClip.Transform is not IContinuousTransform continuousTransform)
        {
            return BuildFallbackLabel("Transform clip is unavailable.");
        }

        var renderWidth = Math.Max(1, canvasWidth > 0 ? canvasWidth : targetWidth);
        var renderHeight = Math.Max(1, canvasHeight > 0 ? canvasHeight : targetHeight);

        if (continuousTransform is ExternalSourceTransform externalSourceTransform)
        {
            return GenerateExternalSourcePreview(transformClip, externalSourceTransform, renderWidth, renderHeight, targetFrame);
        }

        if (!TryResolveBoundClip(transformClip, continuousTransform.BindedLeftClip, TransformClipDynamicPreviewRuntimeKeys.LeftClip, out var leftClip)
            || !TryResolveBoundClip(transformClip, continuousTransform.BindedRightClip, TransformClipDynamicPreviewRuntimeKeys.RightClip, out var rightClip))
        {
            return BuildFallbackLabel("Transform source clips are unavailable.");
        }

        var leftFrameIndex = ClampFrameForClip(leftClip, targetFrame);
        var rightFrameIndex = ClampFrameForClip(rightClip, targetFrame);
        if (!TryGetSourceFrame(leftClip, renderWidth, renderHeight, leftFrameIndex, out var leftFrame)
            || !TryGetSourceFrame(rightClip, renderWidth, renderHeight, rightFrameIndex, out var rightFrame))
        {
            return BuildFallbackLabel("Transform source frames are unavailable.");
        }

        var progress = ResolveTransformProgress(continuousTransform, rightClip, targetFrame);
        var output = continuousTransform.GetFrame(
            leftFrame,
            rightFrame,
            progress,
            PluginManager.CreateComputer(continuousTransform.NeedComputer),
            renderWidth,
            renderHeight);

        return BuildImage(output.ToImageSource());
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

    private static View GenerateExternalSourcePreview(TransformContainer transformClip, ExternalSourceTransform externalSourceTransform, int renderWidth, int renderHeight, uint targetFrame)
    {
        if (string.IsNullOrWhiteSpace(externalSourceTransform.SourcePath) || !File.Exists(externalSourceTransform.SourcePath))
        {
            return BuildFallbackLabel("External transform source is unavailable.");
        }

        if (externalSourceTransform.source is null)
        {
            ((RenderITransform)externalSourceTransform).Init();
        }

        if (externalSourceTransform.source is null || externalSourceTransform.source.TotalFrames <= 0)
        {
            return BuildFallbackLabel("External transform source has no frames.");
        }

        double progress = ResolveProgressForExternalSource(transformClip, externalSourceTransform, targetFrame);
        var frameCount = externalSourceTransform.source.TotalFrames;
        var sourceFrameIndex = (uint)Math.Clamp((long)(progress * frameCount), 0L, Math.Max(0L, frameCount - 1));
        if (!TryGetFrameFromCache(
            clipId: $"{transformClip.Id}:external-source",
            sourcePath: externalSourceTransform.SourcePath,
            targetWidth: renderWidth,
            targetHeight: renderHeight,
            frameIndex: sourceFrameIndex,
            frameResolver: static (frame, state) => state.External.source.GetFrame(frame, false).Resize(state.TargetWidth, state.TargetHeight, true).ToBitPerPixel(IPicture.PicturePixelMode.BytePicture),
            state: (External: externalSourceTransform, TargetWidth: renderWidth, TargetHeight: renderHeight),
            out var sourceFrame))
        {
            return BuildFallbackLabel("External transform source frame is unavailable.");
        }

        return BuildImage(sourceFrame.ToImageSource());
    }

    private static double ResolveProgressForExternalSource(TransformContainer transformClip, ExternalSourceTransform externalSourceTransform, uint targetFrame)
    {
        if (TryResolveBoundClip(transformClip, externalSourceTransform.BindedRightClip, TransformClipDynamicPreviewRuntimeKeys.RightClip, out var rightClip))
        {
            return ResolveTransformProgress(externalSourceTransform, rightClip, targetFrame);
        }

        var duration = Math.Max(1u, ((IClip)transformClip).GetEffectiveDuration());
        if (duration <= 1)
        {
            return 0d;
        }

        var offset = Math.Clamp((long)targetFrame - transformClip.StartFrame, 0L, duration - 1L);
        return offset / (double)(duration - 1);
    }

    private static bool TryResolveBoundClip(TransformContainer transformClip, Guid expectedClipId, string runtimeKey, out IClip clip)
    {
        if (transformClip.ExtraData.TryGetValue(runtimeKey, out var runtimeValue)
            && runtimeValue is IClip runtimeClip
            && runtimeClip.IdAsGUID == expectedClipId)
        {
            clip = runtimeClip;
            return true;
        }

        clip = null!;
        return false;
    }

    private static bool TryGetSourceFrame(IClip clip, int targetWidth, int targetHeight, uint frameIndex, out IPicture frame)
    {
        if (clip is VideoClip videoClip
            && !string.IsNullOrWhiteSpace(videoClip.FilePath)
            && File.Exists(videoClip.FilePath))
        {
            return TryGetFrameFromCache(
                clipId: clip.Id,
                sourcePath: videoClip.FilePath,
                targetWidth: targetWidth,
                targetHeight: targetHeight,
                frameIndex: frameIndex,
                frameResolver: static (frame, state) => state.Clip.GetFrame(frame, state.TargetWidth, state.TargetHeight, true, IPicture.PicturePixelMode.BytePicture),
                state: (Clip: clip, TargetWidth: targetWidth, TargetHeight: targetHeight),
                out frame);
        }

        try
        {
            frame = clip.GetFrame(frameIndex, targetWidth, targetHeight, true, IPicture.PicturePixelMode.BytePicture);
            return true;
        }
        catch
        {
            frame = null!;
            return false;
        }
    }

    private static bool TryGetFrameFromCache<TState>(string clipId, string sourcePath, int targetWidth, int targetHeight, uint frameIndex, Func<uint, TState, IPicture> frameResolver, TState state, out IPicture frame)
    {
        var sourceFingerprint = ResolveSourceFingerprint(sourcePath);
        var key = new TransformSourceFrameCacheKey(clipId, targetWidth, targetHeight, frameIndex, sourceFingerprint);
        if (TryGetCachedFrame(key, out frame))
        {
            return true;
        }

        var diskPath = ResolveDiskCachePath(key);
        if (File.Exists(diskPath))
        {
            try
            {
                frame = new Picture8bpp(diskPath);
                CacheFrame(key, frame, diskPath);
                return true;
            }
            catch
            {
            }
        }

        // Multi-resolution: try resize from a cached frame at a different size (memory first, then disk)
        if (!TryGetResizedFromCache(clipId, sourceFingerprint, frameIndex, targetWidth, targetHeight, out frame)
            && !TryGetResizedFromDisk(clipId, sourceFingerprint, frameIndex, targetWidth, targetHeight, out frame))
        {
            try
            {
                var resolved = frameResolver(frameIndex, state);
                frame = resolved.BitPerPixel == IPicture.PicturePixelMode.BytePicture
                    ? resolved
                    : resolved.ToBitPerPixel(IPicture.PicturePixelMode.BytePicture);
            }
            catch
            {
                frame = null!;
                return false;
            }
        }

        string? persistedDiskPath = null;
        if (TryPersistFrameToDisk(frame, diskPath))
        {
            persistedDiskPath = diskPath;
        }

        CacheFrame(key, frame, persistedDiskPath);
        return true;
    }

    private static bool TryGetCachedFrame(TransformSourceFrameCacheKey key, out IPicture frame)
    {
        if (_sourceFrameCache.TryGetValue(key, out var cached))
        {
            cached.Touch();
            TouchDiskEntry(cached.DiskPath);
            frame = cached.Frame.Clone();
            return true;
        }

        frame = null!;
        return false;
    }

    private static bool TryGetResizedFromCache(string clipId, long sourceFingerprint, uint frameIndex, int targetWidth, int targetHeight, out IPicture resizedFrame)
    {
        int bestDelta = int.MaxValue;
        int bestArea = -1;
        TransformSourceFrameCacheKey? bestKey = null;

        foreach (var kvp in _sourceFrameCache)
        {
            var k = kvp.Key;
            if (k.ClipId == clipId && k.SourceFingerprint == sourceFingerprint && k.FrameIndex == frameIndex)
            {
                if (k.CanvasWidth == targetWidth && k.CanvasHeight == targetHeight)
                    continue;

                int delta = Math.Abs(k.CanvasWidth - targetWidth) + Math.Abs(k.CanvasHeight - targetHeight);
                int area = k.CanvasWidth * k.CanvasHeight;

                if (delta < bestDelta || (delta == bestDelta && area > bestArea))
                {
                    bestDelta = delta;
                    bestArea = area;
                    bestKey = k;
                }
            }
        }

        if (bestKey is null || !_sourceFrameCache.TryGetValue(bestKey.Value, out var cached))
        {
            resizedFrame = null!;
            return false;
        }

        cached.Touch();
        using var source = cached.Frame.Clone();
        var resized = source.Resize(targetWidth, targetHeight, preserveAspect: true);
        if (ReferenceEquals(resized, source))
        {
            resizedFrame = source;
        }
        else
        {
            resizedFrame = resized.BitPerPixel == IPicture.PicturePixelMode.BytePicture
                ? resized
                : resized.ToBitPerPixel(IPicture.PicturePixelMode.BytePicture);
        }
        return true;
    }

    private static List<(int width, int height)> ResolveDiskCacheSizes(string clipId, long sourceFingerprint)
    {
        var sizes = new List<(int width, int height)>();
        var baseDir = Path.Combine(_diskCacheRoot, SanitizePathSegment(clipId));
        if (!Directory.Exists(baseDir))
            return sizes;

        var fingerprintStr = sourceFingerprint.ToString("X16");
        try
        {
            foreach (var subDir in Directory.EnumerateDirectories(baseDir))
            {
                var name = Path.GetFileName(subDir);
                var parts = name.Split('x');
                if (parts.Length == 2
                    && int.TryParse(parts[0], out var w)
                    && int.TryParse(parts[1], out var h)
                    && w > 0 && h > 0
                    && Directory.Exists(Path.Combine(subDir, fingerprintStr)))
                {
                    sizes.Add((w, h));
                }
            }
        }
        catch
        {
        }

        return sizes;
    }

    private static bool TryGetResizedFromDisk(string clipId, long sourceFingerprint, uint frameIndex, int targetWidth, int targetHeight, out IPicture resizedFrame)
    {
        var sizes = ResolveDiskCacheSizes(clipId, sourceFingerprint);
        if (sizes.Count == 0)
        {
            resizedFrame = null!;
            return false;
        }

        int bestDelta = int.MaxValue;
        int bestArea = -1;
        (int width, int height) bestSize = default;

        foreach (var (w, h) in sizes)
        {
            if (w == targetWidth && h == targetHeight)
                continue;

            int delta = Math.Abs(w - targetWidth) + Math.Abs(h - targetHeight);
            int area = w * h;

            if (delta < bestDelta || (delta == bestDelta && area > bestArea))
            {
                bestDelta = delta;
                bestArea = area;
                bestSize = (w, h);
            }
        }

        if (bestDelta == int.MaxValue)
        {
            resizedFrame = null!;
            return false;
        }

        var diskKey = new TransformSourceFrameCacheKey(clipId, bestSize.width, bestSize.height, frameIndex, sourceFingerprint);
        var diskPath = ResolveDiskCachePath(diskKey);
        if (!File.Exists(diskPath))
        {
            resizedFrame = null!;
            return false;
        }

        try
        {
            using var loaded = new Picture8bpp(diskPath);
            var resized = loaded.Resize(targetWidth, targetHeight, preserveAspect: true);
            if (ReferenceEquals(resized, loaded))
            {
                resizedFrame = loaded;
            }
            else
            {
                resizedFrame = resized.BitPerPixel == IPicture.PicturePixelMode.BytePicture
                    ? resized
                    : resized.ToBitPerPixel(IPicture.PicturePixelMode.BytePicture);
            }
            TouchDiskEntry(diskPath);
            return true;
        }
        catch
        {
            resizedFrame = null!;
            return false;
        }
    }

    private static void CacheFrame(TransformSourceFrameCacheKey key, IPicture frame, string? diskPath)
    {
        _sourceFrameCache[key] = new CachedSourceFrame(frame.Clone(), diskPath);
        TouchDiskEntry(diskPath);
        TrimCacheIfNeeded();
        TrimDiskCacheIfNeeded();
    }

    private static void TrimCacheIfNeeded()
    {
        if (_sourceFrameCache.Count <= MaxCachedSourceFrames)
        {
            return;
        }

        var removeCount = _sourceFrameCache.Count - MaxCachedSourceFrames;
        foreach (var stale in _sourceFrameCache.OrderBy(x => x.Value.LastAccessTicks).Take(removeCount).ToArray())
        {
            if (_sourceFrameCache.TryRemove(stale.Key, out var removed))
            {
                try
                {
                    removed.Frame.Dispose();
                }
                catch
                {
                }
            }
        }
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

            frame.SaveAsPng8bpp(diskPath, null);
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

    private static string ResolveDiskCachePath(TransformSourceFrameCacheKey key)
    {
        var clipId = SanitizePathSegment(key.ClipId);
        var dimension = $"{key.CanvasWidth}x{key.CanvasHeight}";
        var fingerprint = key.SourceFingerprint.ToString("X16");
        return Path.Combine(_diskCacheRoot, clipId, dimension, fingerprint, $"{key.FrameIndex}.png");
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

    private static void TrimDiskCacheIfNeeded()
    {
        if (_diskFrameAccess.Count <= MaxDiskCachedSourceFrames)
        {
            return;
        }

        var removeCount = _diskFrameAccess.Count - MaxDiskCachedSourceFrames;
        foreach (var stale in _diskFrameAccess.OrderBy(entry => entry.Value).Take(removeCount).ToArray())
        {
            if (_diskFrameAccess.TryRemove(stale.Key, out _))
            {
                try
                {
                    if (File.Exists(stale.Key))
                    {
                        File.Delete(stale.Key);
                    }
                }
                catch
                {
                }
            }
        }
    }

    private static uint ClampFrameForClip(IClip clip, uint globalFrame)
    {
        ulong endExclusive = (ulong)clip.StartFrame + clip.GetEffectiveDuration();
        if (endExclusive <= clip.StartFrame)
        {
            return clip.StartFrame;
        }

        uint lastFrame = (uint)Math.Min((ulong)uint.MaxValue, endExclusive - 1);
        if (globalFrame < clip.StartFrame)
        {
            return clip.StartFrame;
        }

        if (globalFrame > lastFrame)
        {
            return lastFrame;
        }

        return globalFrame;
    }

    private static double ResolveTransformProgress(IContinuousTransform source, IClip rightClip, uint frameIndex)
    {
        long transformStart = (long)rightClip.StartFrame - source.Duration;
        long indexInTransform = (long)frameIndex - transformStart;
        if (indexInTransform < 0)
        {
            indexInTransform = 0;
        }

        if (indexInTransform >= source.Duration && source.Duration > 0)
        {
            indexInTransform = source.Duration - 1;
        }

        if (source.Duration <= 1)
        {
            return 0d;
        }

        var progress = indexInTransform / (double)(source.Duration - 1);
        return Math.Clamp(progress, 0d, 1d);
    }

    private sealed class CachedSourceFrame
    {
        public CachedSourceFrame(IPicture frame, string? diskPath)
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

    private readonly record struct TransformSourceFrameCacheKey(string ClipId, int CanvasWidth, int CanvasHeight, uint FrameIndex, long SourceFingerprint);
}

internal sealed class TextClipDynamicPreviewProvider : InternalClipDynamicPreviewProviderBase
{
    public override string TypeName => "TextClip";

    public override bool IsAvailable(IClip target)
    {
        return target is TextClip
            && target.FromPlugin == InternalPluginBase.InternalPluginBaseID;
    }

    public override View Generate(IClip target, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint targetFrame)
    {
        if (target is not TextClip clip)
        {
            return BuildFallbackLabel("Text clip is unavailable.");
        }

        var renderW = targetWidth > 0 ? targetWidth : canvasWidth;
        var renderH = targetHeight > 0 ? targetHeight : canvasHeight;
        var context = DynamicPreviewRenderContext.Current;
        var projectW = context is { ProjectRelativeWidth: > 0 } ? context.Value.ProjectRelativeWidth : renderW;
        var projectH = context is { ProjectRelativeHeight: > 0 } ? context.Value.ProjectRelativeHeight : renderH;
        // Text entries (x/y/fontSize/wrappingWidth) are stored in project coordinate space.
        // Keep text rasterization in that same space to avoid geometry drifting with preview resolution.
        var clipW = target.TargetWidth > 0 ? Math.Max(1, target.TargetWidth) : Math.Max(1, projectW);
        var clipH = target.TargetHeight > 0 ? Math.Max(1, target.TargetHeight) : Math.Max(1, projectH);

        var frame = clip.GetFrameRelativeToStartPointOfSource(0, clipW, clipH, true, IPicture.PicturePixelMode.BytePicture);

        LogDiagnostic($"TextClip {clip.Name}: target {clip.TargetWidth}*{clip.TargetHeight}, resolved: {renderW}*{renderH}, text: {TextMeasureHelper.MeasureBounds(clip)}");

        return new Image
        {
            Source = frame.ToImageSource(),
            Aspect = Aspect.Fill,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
        };
    }
}