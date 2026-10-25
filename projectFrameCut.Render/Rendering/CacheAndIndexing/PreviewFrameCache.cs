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
    private readonly CancellationToken token;
    private readonly PreviewSourceFrameContext scope;
    private readonly ConcurrentDictionary<string, Lazy<IPicture>> sources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> loadedSources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(Guid, uint, int, int, int), string> unstableHashes = new();
    private int sourceFailures;

    public PreviewFrameCache(Func<Guid, string> directory, uint frame, int canvasWidth, int canvasHeight,
        int projectWidth, int projectHeight, bool scRgb, bool beforeLayout, CancellationToken token, bool readOnly = false)
    {
        this.directory = directory; this.frame = frame;
        this.canvasWidth = canvasWidth; this.canvasHeight = canvasHeight;
        this.projectWidth = projectWidth; this.projectHeight = projectHeight;
        this.scRgb = scRgb; this.beforeLayout = beforeLayout; this.token = token;
        this.readOnly = readOnly;
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
        string name = $"dynamic_vfd2m_{hash}_{frame}_{projectWidth}x{projectHeight}_{canvasWidth}x{canvasHeight}_{(beforeLayout ? "before" : "clip")}_{(scRgb ? "scrgb16" : "png")}.vfd2m";
        return (hash, Path.Combine(directory(clip.Id), name));
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
            IPicture? picture = null;
            bool reuseSource = clip is VideoClip or PhotoClip || clip is IVectorContentClip
                && clip.FromPlugin == Plugin.InternalPluginBase.InternalPluginBaseID && !FrameHashHelper.NeedsRendering(clip);
            if (reuseSource && Timeline.CanCacheClipSource(clip) && File.Exists(entry.Path))
            {
                try
                {
                    picture = PreviewFrameContainer.LoadSource(entry.Path);
                    if (picture.Width <= 0 || picture.Height <= 0 || picture.BitPerPixel != mode)
                    {
                        picture.Dispose();
                        picture = null;
                    }
                    else loadedSources.TryAdd(entry.Path, 0);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
                {
                    Log(ex, $"Read preview source for clip {clip.Id}; regenerating", this);
                }
            }
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
            if (picture is IHwAccelPicture gpu)
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
        using var frameScope = ValueProviderFrameContext.PushFrame(frame,
            Math.Clamp(((float)frame - clip.StartFrame) / Math.Max(1u, clip.GetEffectiveDuration()), 0, 1), false, token);
        clip = ClipArgumentBinding.InitializeFrame(clip, frame);
        int width = Math.Max(1, clip.TargetWidth <= 0 ? canvasWidth
            : (int)Math.Round((double)clip.TargetWidth * canvasWidth / projectWidth, MidpointRounding.AwayFromZero));
        int height = Math.Max(1, clip.TargetHeight <= 0 ? canvasHeight
            : (int)Math.Round((double)clip.TargetHeight * canvasHeight / projectHeight, MidpointRounding.AwayFromZero));
        var mode = scRgb ? IPicture.PicturePixelMode.UShortPicture : IPicture.PicturePixelMode.BytePicture;
        uint sourceFrame = clip.GetRelativeFrameIndex(frame) ?? clip.StartFrame + clip.GetEffectiveDuration();
        var entry = Locate(clip, sourceFrame, width, height, mode);
        lock (PreviewFrameContainer.Gate(entry.Path))
        {
            token.ThrowIfCancellationRequested();
            if (allowCaching && File.Exists(entry.Path))
            {
                try
                {
                    var cached = PreviewFrameContainer.Read(entry.Path, effectHash, scRgb);
                    if (cached.SourceHash == entry.Hash)
                    {
                        File.SetLastWriteTimeUtc(entry.Path, DateTime.UtcNow);
                        return (entry.Path, cached, true, true);
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
            using var output = render();
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
                    else source = VectorPictureEffectProcessing.ReadFrame(clip, sourceFrame, width, height, mode);
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
            var data = PreviewFrameData.Create(output, entry.Hash, effectHash, scRgb);
            bool persistent = allowCaching && sourceFailures == 0 && unstableHashes.IsEmpty && canPersist() && !ClipInitializationFailure.IsMarked(clip);
            string path = persistent ? entry.Path : temporaryPath;
            if (persistent && loadedSources.ContainsKey(entry.Path)) PreviewFrameContainer.ReplaceMetadata(path, savedSource.Value, data, token);
            else PreviewFrameContainer.Save(path, savedSource.Value, data, token);
            return (path, data, false, persistent);
        }
    }

    public void Dispose()
    {
        scope.Dispose();
        foreach (var source in sources.Values)
            if (source.IsValueCreated) source.Value.Dispose();
    }
}
