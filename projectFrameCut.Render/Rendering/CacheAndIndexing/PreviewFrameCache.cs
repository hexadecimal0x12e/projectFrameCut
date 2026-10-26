using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System.Collections.Concurrent;

namespace projectFrameCut.Render.Rendering;

public sealed class PreviewFrameCache : IDisposable
{
    private readonly Func<Guid, string> directory;
    private readonly uint frame;
    private readonly int canvasWidth, canvasHeight, projectWidth, projectHeight;
    private readonly bool scRgb, beforeLayout;
    private readonly bool readOnly;
    private readonly bool keepHwAccelPicture;
    private readonly bool metadataOnly;
    private readonly Func<string, string?>? findSource;
    private readonly CancellationToken token;
    private readonly PreviewSourceFrameContext scope;
    private readonly ConcurrentDictionary<string, Lazy<IPicture>> sources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> loadedSources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(Guid, uint, int, int, int), string> unstableHashes = new();
    private int sourceFailures;
    private static int sourceIndexErrorLogged;

    public PreviewFrameCache(Func<Guid, string> directory, uint frame, int canvasWidth, int canvasHeight,
        int projectWidth, int projectHeight, bool scRgb, bool beforeLayout, CancellationToken token, bool readOnly = false,
        bool keepHwAccelPicture = false, Func<string, string?>? findSource = null, bool metadataOnly = false)
    {
        this.directory = directory; this.frame = frame;
        this.canvasWidth = canvasWidth; this.canvasHeight = canvasHeight;
        this.projectWidth = projectWidth; this.projectHeight = projectHeight;
        this.scRgb = scRgb; this.beforeLayout = beforeLayout; this.token = token;
        this.readOnly = readOnly;
        this.keepHwAccelPicture = keepHwAccelPicture;
        this.metadataOnly = metadataOnly;
        this.findSource = findSource;
        scope = new(ReadSource);
    }

    private (string Hash, string Path) Locate(IClip clip, uint sourceFrame, int width, int height, IPicture.PicturePixelMode mode)
    {
        string hash;
        try { hash = FrameHashHelper.GetPreviewSourceHash(clip, sourceFrame, width, height, mode.Value); }
        catch (Exception ex)
        {
            Log(ex, $"Identify preview source for clip {clip.Id}", this);
            hash = unstableHashes.GetOrAdd((clip.Id, sourceFrame, width, height, mode.Value), _ => Guid.NewGuid().ToString("N"));
        }
        string frameKey = FrameHashHelper.IsFrameInvariant(clip, layout: true) ? "static" : frame.ToString();
        string name = $"dynamic_vfd2m_{hash}_{frameKey}_{projectWidth}x{projectHeight}_{canvasWidth}x{canvasHeight}_{(beforeLayout ? "before" : "clip")}_{(scRgb ? "scrgb16" : "png")}.vfd2m";
        return (hash, Path.Combine(Path.GetFullPath(directory(clip.Id)), name));
    }

    private static (PreviewFrameInfo Info, PreviewFrameData? Data) ReadCached(string path, string effectHash, bool scRgb, bool metadataOnly)
    {
        var data = metadataOnly ? null : PreviewFrameContainer.Read(path, effectHash, scRgb);
        return (data?.Info ?? PreviewFrameContainer.ReadInfo(path, effectHash, scRgb), data);
    }

    private static string? FindSourcePath(string key, Func<string, string?>? findSource, string? preferred = null)
    {
        if (preferred is not null && File.Exists(preferred)) return preferred;
        if (File.Exists(key)) return key;
        if (!Directory.Exists(Path.GetDirectoryName(key))) return null;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        try
        {
            if (findSource?.Invoke(key) is { } indexed && File.Exists(indexed)
                && string.Equals(PreviewFrameContainer.GetSourceKey(indexed), key, comparison)) return indexed;
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            if (Interlocked.Exchange(ref sourceIndexErrorLogged, 1) == 0) Log(ex, "Find preview source cache", nameof(PreviewFrameCache));
        }
        try
        {
            return Directory.EnumerateFiles(Path.GetDirectoryName(key)!, Path.GetFileName(PreviewFrameContainer.GetSourcePrefix(key)) + "*.vfd2m")
                .FirstOrDefault(path => string.Equals(PreviewFrameContainer.GetSourceKey(path), key, comparison));
        }
        catch (DirectoryNotFoundException) { return null; }
    }

    private IPicture? TryLoadSource(IClip clip, string key, IPicture.PicturePixelMode mode)
    {
        bool reuse = clip is VideoClip or PhotoClip || clip is IVectorContentClip
            && clip.FromPlugin == Plugin.InternalPluginBase.InternalPluginBaseID && FrameHashHelper.CanPersistClipHash([clip], clip);
        if (!reuse || !Timeline.CanCacheClipSource(clip)) return null;
        lock (PreviewFrameContainer.Gate(key))
        {
            if (FindSourcePath(key, findSource) is not { } path) return null;
            try
            {
                var picture = PreviewFrameContainer.LoadSource(path);
                if (picture.Width <= 0 || picture.Height <= 0 || picture.BitPerPixel != mode)
                {
                    picture.Dispose();
                    return null;
                }
                loadedSources[key] = path;
                return picture;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
            {
                Log(ex, $"Read preview source for clip {clip.Id}; regenerating", this);
                return null;
            }
        }
    }

    private IPicture ReadSource(IClip clip, uint sourceFrame, int width, int height, IPicture.PicturePixelMode mode, Func<IPicture> compute)
    {
        token.ThrowIfCancellationRequested();
        bool native = clip is IVectorContentClip or MaskClip
            || clip.FromPlugin != Plugin.InternalPluginBase.InternalPluginBaseID;
        if (readOnly && (native || clip is not (VideoClip or PhotoClip))) return compute();
        var entry = Locate(clip, sourceFrame, width, height, mode);
        var saved = sources.GetOrAdd(entry.Path, _ => new Lazy<IPicture>(() =>
        {
            IPicture? picture = TryLoadSource(clip, entry.Path, mode);
            if (picture is null)
            {
                using var sourceScope = PreviewSourceFrameContext.Suspend();
                using var raw = native ? PreviewSourceFrameContext.Raw() : null;
                using var mask = native && MaskFrameContext.Current is { } m
                    ? MaskFrameContext.Open(m.Clips, m.Frame, m.Width, m.Height, m.ProjectWidth, m.ProjectHeight, m.PixelMode, token, forceNew: true) : null;
                try { picture = compute(); }
                catch (Exception ex) when (native && ex is not OperationCanceledException)
                {
                    Interlocked.Increment(ref sourceFailures);
                    Log(ex, $"Capture raw preview source for clip {clip.Id}; using a temporary preview", this);
                    picture = Timeline.CreateTransparentFrame(width, height, mode);
                }
            }
            if (!keepHwAccelPicture && picture is IHwAccelPicture gpu)
            {
                var downloaded = gpu.Download();
                picture.Dispose();
                picture = downloaded;
            }
            return picture;
        }));
        IPicture source;
        try { source = saved.Value; }
        catch { sources.TryRemove(entry.Path, out _); throw; }
        // Native effects need their original text/vector representation, not a rasterized substitute.
        return native ? compute() : DynamicEffectGraph.CopyPicture(source);
    }

    public (string Path, PreviewFrameData Data, bool CacheHit, bool Persistent) GetOrCreate(IClip clip,
        IReadOnlyList<IClip> clips, string effectHash, bool allowCaching, string temporaryPath,
        Func<IPicture> render, Func<bool> canPersist)
    {
        using var pending = Prepare(clip, clips, effectHash, allowCaching, temporaryPath, render, canPersist);
        return pending.Complete();
    }

    public PendingFrame Prepare(IClip clip, IReadOnlyList<IClip> clips, string effectHash, bool allowCaching,
        string temporaryPath, Func<IPicture> render, Func<bool> canPersist)
    {
        using var frameScope = ValueProviderFrameContext.PushFrame(frame,
            Math.Clamp(((float)frame - clip.StartFrame) / Math.Max(1u, clip.GetEffectiveDuration()), 0, 1), false, token);
        clip = ClipArgumentBinding.InitializeFrame(clip, frame);
        var outputIdentity = FrameHashHelper.GetClipOutputHash(clips, clip, frame, FrameHashContext.Current?.Options ?? new()
        {
            Width = canvasWidth, Height = canvasHeight, ProjectWidth = projectWidth, ProjectHeight = projectHeight,
            PixelMode = scRgb ? 16 : 8, Stage = beforeLayout ? "before-layout" : "clip",
            ProcessFromCanvas = Timeline.ProcessEffectFromCanvas,
        }, token);
        allowCaching &= outputIdentity.CanReuse;
        effectHash = FrameHashHelper.HashValue(new { Content = effectHash, Layout = outputIdentity.Hash });
        int width = Math.Max(1, clip.TargetWidth <= 0 ? canvasWidth
            : (int)Math.Round((double)clip.TargetWidth * canvasWidth / projectWidth, MidpointRounding.AwayFromZero));
        int height = Math.Max(1, clip.TargetHeight <= 0 ? canvasHeight
            : (int)Math.Round((double)clip.TargetHeight * canvasHeight / projectHeight, MidpointRounding.AwayFromZero));
        var mode = scRgb ? IPicture.PicturePixelMode.UShortPicture : IPicture.PicturePixelMode.BytePicture;
        uint sourceFrame = clip.GetRelativeFrameIndex(frame) ?? clip.StartFrame + clip.GetEffectiveDuration();
        var entry = Locate(clip, sourceFrame, width, height, mode);
        string path = PreviewFrameContainer.GetSourcePrefix(entry.Path) + effectHash + ".vfd2m";
        lock (PreviewFrameContainer.Gate(entry.Path))
        {
            token.ThrowIfCancellationRequested();
            string? existing = File.Exists(path) ? path : FindSourcePath(entry.Path, findSource);
            if (allowCaching && existing is not null)
            {
                try
                {
                    var cached = ReadCached(existing, effectHash, scRgb, metadataOnly);
                    if (cached.Info.SourceHash == entry.Hash)
                    {
                        File.SetLastWriteTimeUtc(existing, DateTime.UtcNow);
                        return new(path, entry.Path, existing, cached.Info, cached.Data, findSource, token);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
                {
                    // A different effect fingerprint is an ordinary cache miss.
                    if (ex is not InvalidDataException) Log(ex, $"Read preview metadata for clip {clip.Id}; regenerating", this);
                }
            }
            using var mask = MaskFrameContext.Open(clips, frame, canvasWidth, canvasHeight, projectWidth, projectHeight, mode, token);
            using var endpoints = ClipValuePassThrough.Open(clips, frame, canvasWidth, canvasHeight, projectWidth, projectHeight, mode, token);
            using var endpointClip = ClipValueFrameContext.Current?.EnterClip(clip.Id, independent: true);
            var output = render();
            try
            {
                token.ThrowIfCancellationRequested();
                if (!sources.TryGetValue(entry.Path, out var savedSource))
                {
                    using var sourceScope = PreviewSourceFrameContext.Suspend();
                    bool native = clip is IVectorContentClip or MaskClip || clip.FromPlugin != Plugin.InternalPluginBase.InternalPluginBaseID;
                    using var raw = native ? PreviewSourceFrameContext.Raw() : null;
                    using var rawMask = native && MaskFrameContext.Current is { } m
                        ? MaskFrameContext.Open(m.Clips, m.Frame, m.Width, m.Height, m.ProjectWidth, m.ProjectHeight, m.PixelMode, token, forceNew: true) : null;
                    IPicture source;
                    try
                    {
                        if (ClipInitializationFailure.IsMarked(clip) || !ClipArgumentBinding.ShouldRender(clip))
                        {
                            Interlocked.Increment(ref sourceFailures);
                            source = Timeline.CreateTransparentFrame(width, height, mode);
                        }
                        else source = TryLoadSource(clip, entry.Path, mode)
                            ?? VectorPictureEffectProcessing.ReadFrame(clip, sourceFrame, width, height, mode);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Interlocked.Increment(ref sourceFailures);
                        Log(ex, $"Capture raw preview source for clip {clip.Id}; using a temporary preview", this);
                        source = Timeline.CreateTransparentFrame(width, height, mode);
                    }
                    savedSource = new(() => source);
                    sources.TryAdd(entry.Path, savedSource);
                }
                loadedSources.TryGetValue(entry.Path, out var loadedPath);
                // Keep a reusable source even when its new derived output cannot be reused.
                bool persistent = (allowCaching || loadedPath is not null) && sourceFailures == 0 && unstableHashes.IsEmpty
                    && canPersist() && !ClipInitializationFailure.IsMarked(clip);
                var pending = new PendingFrame(persistent ? path : temporaryPath, entry.Path, loadedPath, output, savedSource.Value,
                    entry.Hash, effectHash, scRgb, persistent, findSource, token, metadataOnly);
                sources.TryRemove(entry.Path, out _);
                return pending;
            }
            catch { output.Dispose(); throw; }
        }
    }

    /// <summary>Owns source and output pictures until preview packaging completes.</summary>
    public sealed class PendingFrame : IDisposable
    {
        private readonly string path, sourceKey, sourceHash = string.Empty, effectHash = string.Empty;
        private readonly string? loadedPath;
        private readonly bool scRgb;
        private readonly bool metadataOnly;
        private readonly Func<string, string?>? findSource;
        private readonly CancellationToken token;
        private IPicture? output, source;
        private PreviewFrameData? data;
        private PreviewFrameInfo? info;
        private bool completed;
        public bool CacheHit { get; private set; }
        public bool Persistent { get; }

        internal PendingFrame(string path, string sourceKey, string loadedPath, PreviewFrameInfo info, PreviewFrameData? data,
            Func<string, string?>? findSource, CancellationToken token)
        {
            this.path = path; this.sourceKey = sourceKey; this.loadedPath = loadedPath;
            this.info = info; this.data = data; this.findSource = findSource; this.token = token;
            CacheHit = Persistent = true;
        }

        internal PendingFrame(string path, string sourceKey, string? loadedPath, IPicture output, IPicture source,
            string sourceHash, string effectHash, bool scRgb, bool persistent, Func<string, string?>? findSource,
            CancellationToken token, bool metadataOnly)
        {
            this.path = path; this.output = output; this.source = source;
            this.sourceKey = sourceKey; this.loadedPath = loadedPath; this.findSource = findSource;
            this.sourceHash = sourceHash; this.effectHash = effectHash;
            this.scRgb = scRgb; Persistent = persistent; this.token = token; this.metadataOnly = metadataOnly;
        }

        public (string Path, PreviewFrameData Data, bool CacheHit, bool Persistent) Complete()
        {
            CompleteInfo();
            data ??= PreviewFrameContainer.Read(path, info!.EffectHash, info.ScRgb);
            return (path, data, CacheHit, Persistent);
        }

        public (string Path, PreviewFrameInfo Info, bool CacheHit, bool Persistent) CompleteInfo()
        {
            token.ThrowIfCancellationRequested();
            if (!completed)
            {
                lock (PreviewFrameContainer.Gate(path))
                {
                    if (CacheHit)
                    {
                        if (loadedPath != path && loadedPath is not null && File.Exists(loadedPath))
                            File.Move(loadedPath, path, overwrite: true);
                        if (!File.Exists(path))
                            throw new FileNotFoundException("The cached preview was removed before packaging.", path);
                    }
                    else
                    {
                        // Another frame may have published this same static output while it was queued.
                        if (Persistent && File.Exists(path))
                        {
                            try
                            {
                                var cached = ReadCached(path, effectHash, scRgb, metadataOnly);
                                if (cached.Info.SourceHash == sourceHash)
                                {
                                    info = cached.Info;
                                    data = cached.Data;
                                    CacheHit = completed = true;
                                    return (path, info, CacheHit, Persistent);
                                }
                            }
                            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { }
                        }
                        using var downloaded = output is IHwAccelPicture gpu ? gpu.Download() : null;
                        var prepared = PreviewFrameData.Create(downloaded ?? output!, sourceHash, effectHash, scRgb);
                        token.ThrowIfCancellationRequested();
                        string? existing = Persistent && loadedPath is not null ? FindSourcePath(sourceKey, findSource, loadedPath) : null;
                        if (existing is not null)
                        {
                            PreviewFrameContainer.ReplaceMetadata(existing, prepared, token);
                            if (!string.Equals(existing, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                                File.Move(existing, path, overwrite: true);
                        }
                        else if (source is not null) PreviewFrameContainer.Save(path, source, prepared, token);
                        else throw new FileNotFoundException("The cached preview source was removed before packaging.", loadedPath);
                        data = prepared;
                        info = prepared.Info;
                    }
                }
                completed = true;
            }
            return (path, info!, CacheHit, Persistent);
        }

        public void Dispose()
        {
            try { Interlocked.Exchange(ref output, null)?.Dispose(); }
            finally
            {
                data = null;
                Interlocked.Exchange(ref source, null)?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        scope.Dispose();
        foreach (var source in sources.Values)
            if (source.IsValueCreated) source.Value.Dispose();
    }
}
