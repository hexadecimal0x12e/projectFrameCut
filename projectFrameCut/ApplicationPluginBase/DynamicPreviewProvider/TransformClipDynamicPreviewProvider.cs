using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.ApplicationAPIBase.Helpers;
using System.Collections.Concurrent;
using projectFrameCut.Render.Transform;
using IPicture = projectFrameCut.Drawing.Base.IPicture;
using RenderITransform = projectFrameCut.Render.RenderAPIBase.ClipAndTrack.ITransform;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Drawing.Base.Picture;

namespace projectFrameCut.ApplicationPluginBase.DynamicPreviewProvider;

internal sealed class TransformClipDynamicPreviewProvider : InternalClipDynamicPreviewProviderBase
{
    private const int MaxCachedSourceFrames = 160;
    private const int MaxDiskCachedSourceFrames = 2000;
    private static readonly ConcurrentDictionary<TransformSourceFrameCacheKey, CachedSourceFrame> _sourceFrameCache = new();
    private static readonly ConcurrentDictionary<string, long> _diskFrameAccess = new(StringComparer.Ordinal);
    private static readonly string _diskCacheRoot = Path.Combine(MauiProgram.DataPath, "RenderCache", "perClip");

    public override string TypeName => "TransformClip";

    public override bool IsPrepareGenerateDispatchable => true;

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

    public override Task<IPicture> PrepareSource(IClip target, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint targetFrame, CancellationToken cancellationToken)
    {
        if (target is not TransformContainer transformClip
            || transformClip.Transform is not IContinuousTransform continuousTransform)
        {
            throw new InvalidOperationException("Transform clip is unavailable.");
        }

        var renderWidth = Math.Max(1, canvasWidth > 0 ? canvasWidth : targetWidth);
        var renderHeight = Math.Max(1, canvasHeight > 0 ? canvasHeight : targetHeight);

        if (continuousTransform is ExternalSourceTransform externalSourceTransform)
        {
            return PrepareExternalSourceFrame(transformClip, externalSourceTransform, renderWidth, renderHeight, targetFrame, cancellationToken);
        }

        if (!TryResolveBoundClip(transformClip, continuousTransform.BindedLeftClip, TransformClipDynamicPreviewRuntimeKeys.LeftClip, out var leftClip)
            || !TryResolveBoundClip(transformClip, continuousTransform.BindedRightClip, TransformClipDynamicPreviewRuntimeKeys.RightClip, out var rightClip))
        {
            throw new InvalidOperationException("Transform source clips are unavailable.");
        }

        var leftFrameIndex = ClampFrameForClip(leftClip, targetFrame);
        var rightFrameIndex = ClampFrameForClip(rightClip, targetFrame);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryGetSourceFrame(leftClip, renderWidth, renderHeight, leftFrameIndex, out var leftFrame)
                || !TryGetSourceFrame(rightClip, renderWidth, renderHeight, rightFrameIndex, out var rightFrame))
            {
                throw new InvalidOperationException("Transform source frames are unavailable.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var progress = ResolveTransformProgress(continuousTransform, rightClip, targetFrame);
            var output = continuousTransform.GetFrame(
                leftFrame,
                rightFrame,
                progress,
                PluginManager.CreateComputer(continuousTransform.NeedComputer),
                renderWidth,
                renderHeight);

            cancellationToken.ThrowIfCancellationRequested();

            return output.BitPerPixel == IPicture.PicturePixelMode.BytePicture
                ? output
                : output.ToBitPerPixel(IPicture.PicturePixelMode.BytePicture);
        }, cancellationToken);
    }

    public override View Generate(IClip target, IPicture? preparedSource, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint targetFrame)
    {
        if (preparedSource != null)
        {
            return BuildImage(preparedSource.ToImageSource());
        }

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

    private Task<IPicture> PrepareExternalSourceFrame(TransformContainer transformClip, ExternalSourceTransform externalSourceTransform, int renderWidth, int renderHeight, uint targetFrame, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(externalSourceTransform.SourcePath) || !File.Exists(externalSourceTransform.SourcePath))
                throw new InvalidOperationException("External transform source is unavailable.");

            if (externalSourceTransform.source is null)
            {
                ((RenderITransform)externalSourceTransform).Init();
            }

            if (externalSourceTransform.source is null || externalSourceTransform.source.TotalFrames <= 0)
                throw new InvalidOperationException("External transform source has no frames.");

            cancellationToken.ThrowIfCancellationRequested();

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
                throw new InvalidOperationException("External transform source frame is unavailable.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return sourceFrame;
        }, cancellationToken);
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
