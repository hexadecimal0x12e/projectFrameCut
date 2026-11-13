using projectFrameCut.ApplicationAPIBase.Interaction;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.LivePreview;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;
using System.Collections.Concurrent;
using System.Diagnostics;
using Path = System.IO.Path;

namespace projectFrameCut.InteractableEditor;

public sealed partial class DynamicPreview
{
    private const int MaxCachedFallbackFrames = 120;
    private const int MaxDiskCachedFallbackFrames = 1500;

    private static readonly ConcurrentDictionary<FallbackFrameCacheKey, CachedFallbackFrame> s_fallbackFrameCache = new();
    private static readonly ConcurrentDictionary<string, LocalPreviewCacheIndex> s_localCacheIndexes = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static int s_localCacheErrorLogged;
    private static LocalPreviewCacheIndex GetLocalCacheIndex(string? root = null)
        => s_localCacheIndexes.GetOrAdd(Path.GetFullPath(root ?? DiskCacheRoot), static path => new(path));

    private readonly object _preparedOverlayCacheGate = new();
    private IReadOnlyList<PreparedPreview> _lastPreparedOverlayPreviews = [];

    public void CacheOverlayPreparedPreviews(IReadOnlyList<PreparedPreview> prepared)
    {
        lock (_preparedOverlayCacheGate)
        {
            if (!CanCacheOverlayPreparedPreviews(prepared))
            {
                _lastPreparedOverlayPreviews = [];
                LogDiagnostic("[DynamicPreview] Skipped overlay cache for failed clip previews.");
                return;
            }
            _lastPreparedOverlayPreviews = prepared.Count == 0 ? [] : prepared.ToArray();
        }
    }

    private bool CanCacheOverlayPreparedPreviews(IReadOnlyList<PreparedPreview> prepared)
        => !prepared.Any(p => p.ErrorMessage is not null
            || p.Source is { } source && ClipInitializationFailure.IsMarked(source)
            || _clips?.Any(c => (p.IsCanvasPreview || c.Id == p.ClipId) && ClipInitializationFailure.IsMarked(c)) == true);

    private IReadOnlyList<PreparedPreview> GetCachedOverlayPreparedPreviews()
    {
        lock (_preparedOverlayCacheGate)
        {
            if (!CanCacheOverlayPreparedPreviews(_lastPreparedOverlayPreviews))
            {
                _lastPreparedOverlayPreviews = [];
                LogDiagnostic("[DynamicPreview] Cleared overlay cache containing failed clip previews.");
            }
            return _lastPreparedOverlayPreviews;
        }
    }

    private static long s_lastPreviewCacheCleanup;

    private static void MaintainLocalPreviewCache()
    {
        long now = Environment.TickCount64, last = Interlocked.Read(ref s_lastPreviewCacheCleanup);
        if (now - last < 60000 || Interlocked.CompareExchange(ref s_lastPreviewCacheCleanup, now, last) != last) return;
        _ = Task.Run(() =>
        {
            try
            {
                GetLocalCacheIndex().TrimPreviews(MaxDiskCachedFallbackFrames, TimeSpan.FromDays(10));
            }
            catch (IOException) { }
            catch (Exception ex) { Log(ex, "Clean local preview cache", typeof(DynamicPreview)); }
        });
    }

    private static bool TryGetCachedFallbackFrame(FallbackFrameCacheKey key, out IPicture frame, bool deepCopy = true)
    {
        if (s_fallbackFrameCache.TryGetValue(key, out var cached))
        {
            cached.Touch();
            frame = deepCopy ? cached.Frame.Clone() : cached.Frame;
            return true;
        }

        try
        {
            var index = GetLocalCacheIndex();
            var diskPath = index.FindFallback(key.ClipId, key.SourceFingerprint, key.FrameIndex, key.TargetWidth, key.TargetHeight);
            if (diskPath is not null && File.Exists(diskPath))
            {
                lock (PreviewFrameContainer.Gate(diskPath))
                {
                    frame = PreviewFrameMaterializer.LoadVfd(diskPath);
                    CacheFallbackFrame(key, frame);
                    try { index.Touch(diskPath); }
                    catch (Exception ex) { LogLocalCacheError(ex); }
                    return true;
                }
            }
        }
        catch (Exception ex) { LogLocalCacheError(ex); }

        frame = null!;
        return false;
    }

    private static bool TryGetResizedFallbackFrame(FallbackFrameCacheKey targetKey, out IPicture resizedFrame)
    {
        int bestDelta = int.MaxValue;
        int bestArea = -1;
        FallbackFrameCacheKey? bestKey = null;

        foreach (var kvp in s_fallbackFrameCache)
        {
            var k = kvp.Key;
            if (k.ClipId == targetKey.ClipId && k.SourceFingerprint == targetKey.SourceFingerprint && k.FrameIndex == targetKey.FrameIndex)
            {
                if (k.TargetWidth == targetKey.TargetWidth && k.TargetHeight == targetKey.TargetHeight)
                    continue;

                int delta = Math.Abs(k.TargetWidth - targetKey.TargetWidth) + Math.Abs(k.TargetHeight - targetKey.TargetHeight);
                int area = k.TargetWidth * k.TargetHeight;

                if (delta < bestDelta || (delta == bestDelta && area > bestArea))
                {
                    bestDelta = delta;
                    bestArea = area;
                    bestKey = k;
                }
            }
        }

        if (bestKey is null || !s_fallbackFrameCache.TryGetValue(bestKey.Value, out var cached))
        {
            resizedFrame = null!;
            return false;
        }

        cached.Touch();
        var source = cached.Frame.Clone();
        var resized = source.Resize(targetKey.TargetWidth, targetKey.TargetHeight, preserveAspect: true);
        if (ReferenceEquals(resized, source))
        {
            resizedFrame = source;
        }
        else
        {
            resizedFrame = resized;
            source.Dispose();
        }
        return true;
    }

    private static IReadOnlyList<(int Width, int Height)> ResolveFallbackDiskCacheSizes(Guid clipId, long sourceFingerprint, uint frame)
    {
        try { return GetLocalCacheIndex().ReadFallbackSizes(clipId, sourceFingerprint, frame); }
        catch (Exception ex) { LogLocalCacheError(ex); return []; }
    }

    private static bool TryGetResizedFallbackFromDisk(FallbackFrameCacheKey targetKey, out IPicture resizedFrame)
    {
        var sizes = ResolveFallbackDiskCacheSizes(targetKey.ClipId, targetKey.SourceFingerprint, targetKey.FrameIndex);
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
            if (w == targetKey.TargetWidth && h == targetKey.TargetHeight)
                continue;

            int delta = Math.Abs(w - targetKey.TargetWidth) + Math.Abs(h - targetKey.TargetHeight);
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

        var diskKey = new FallbackFrameCacheKey(targetKey.ClipId, bestSize.width, bestSize.height, targetKey.FrameIndex, targetKey.SourceFingerprint);
        try
        {
            var index = GetLocalCacheIndex();
            var diskPath = index.FindFallback(diskKey.ClipId, diskKey.SourceFingerprint, diskKey.FrameIndex, diskKey.TargetWidth, diskKey.TargetHeight);
            if (diskPath is not null && File.Exists(diskPath))
            {
                lock (PreviewFrameContainer.Gate(diskPath))
                {
                    using var source = PreviewFrameMaterializer.LoadVfd(diskPath);
                    resizedFrame = source.Resize(targetKey.TargetWidth, targetKey.TargetHeight, preserveAspect: true);
                    if (ReferenceEquals(resizedFrame, source)) resizedFrame = source.Clone();
                    try { index.Touch(diskPath); }
                    catch (Exception ex) { LogLocalCacheError(ex); }
                    return true;
                }
            }
        }
        catch (Exception ex) { LogLocalCacheError(ex); }
        resizedFrame = null!;
        return false;
    }

    private static void CacheFallbackFrame(FallbackFrameCacheKey key, IPicture frame)
    {
        s_fallbackFrameCache[key] = new CachedFallbackFrame(frame);
        TrimFallbackCacheIfNeeded();
    }

    private static void TrimFallbackCacheIfNeeded()
    {
        if (s_fallbackFrameCache.Count <= MaxCachedFallbackFrames)
        {
            return;
        }

        var removeCount = s_fallbackFrameCache.Count - MaxCachedFallbackFrames;
        foreach (var stale in s_fallbackFrameCache.OrderBy(x => x.Value.LastAccessTicks).Take(removeCount).ToArray())
        {
            if (s_fallbackFrameCache.TryRemove(stale.Key, out var removed))
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

    private static void EnqueueFallbackDiskPersist(FallbackFrameCacheKey key, IPicture frame)
    {
        string root = DiskCacheRoot;
        var diskPath = ResolveFallbackDiskCachePath(key);
        var snapshot = frame.Clone();
        _ = Task.Run(() =>
        {
            using var picture = snapshot;
            string temporary = diskPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                var index = GetLocalCacheIndex(root);
                lock (PreviewFrameContainer.Gate(diskPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);
                    picture.SaveToDisk(temporary, PictureExtensions.SharedVfdPictureEncoder);
                    File.Move(temporary, diskPath, true);
                    index.Track(diskPath, key.ClipId, key.FrameIndex, key.TargetWidth, key.TargetHeight, key.SourceFingerprint);
                }
                index.TrimFallback(MaxDiskCachedFallbackFrames);
            }
            catch (Exception ex) { LogLocalCacheError(ex); }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception ex) { LogLocalCacheError(ex); }
            }
        });
    }

    private static long ResolveFallbackSourceFingerprint(IClip clip)
    {
        var sourcePath = clip.FilePath;
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return clip.Id.GetHashCode();
        }
        if (projectFrameCut.Render.EncodeAndDecode.ProjectExternalVideoSource.IsPath(sourcePath))
            return StringComparer.Ordinal.GetHashCode(sourcePath + projectFrameCut.Render.RPCProtocol.ProjectExternalSourceRuntime.Current.CacheToken);

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

    private static string ResolveFallbackDiskCachePath(FallbackFrameCacheKey key)
    {
        var clipId = SanitizePathSegment(key.ClipId.ToString());
        var dimension = $"{key.TargetWidth}x{key.TargetHeight}";
        var fingerprint = key.SourceFingerprint.ToString("X16");
        var directory = Path.Combine(DiskCacheRoot, clipId, dimension, fingerprint);
        return Path.Combine(directory, $"{key.FrameIndex}.vfd");
    }

    [DebuggerStepThrough()]
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

    private static void LogLocalCacheError(Exception error)
    {
        if (Interlocked.Exchange(ref s_localCacheErrorLogged, 1) == 0)
            Log(error, "Access local preview cache index", typeof(DynamicPreview));
    }

    private sealed class CachedFallbackFrame
    {
        public CachedFallbackFrame(IPicture frame)
        {
            Frame = frame.Clone();
            Frame.CanBeDisposed = false;
            Touch();
        }

        public IPicture Frame { get; }
        public long LastAccessTicks { get; private set; }

        public void Touch() => LastAccessTicks = DateTime.UtcNow.Ticks;
    }

    private readonly record struct FallbackFrameCacheKey(Guid ClipId, int TargetWidth, int TargetHeight, uint FrameIndex, long SourceFingerprint);
}
