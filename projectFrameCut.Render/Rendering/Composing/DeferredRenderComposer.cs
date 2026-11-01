using System.Collections.Concurrent;
using System.Diagnostics;
using projectFrameCut.Drawing.Base.ReadWriteConvert;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Context;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.Rendering;

public sealed class DeferredRenderComposer : RenderComposerBase
{
    private DeferredRenderCache? cache;
    private Dictionary<uint, FrameHashResult> hashes = new();
    private readonly ConcurrentDictionary<uint, byte> restored = new();
    private int writing;
    private bool completed;
    public string? CacheDirectory => cache?.DirectoryPath;

    internal void InitializeCache(string? projectRoot, string? signature, bool resume, IPictureEncoder encoder,
        IReadOnlyDictionary<(Guid Clip, uint Frame), FrameHashResult> clipHashes,
        IReadOnlyDictionary<uint, FrameHashResult> frameHashes, CancellationToken token)
    {
        if (cache is not null) throw new InvalidOperationException("The deferred cache is already initialized.");
        hashes = frameHashes.ToDictionary(p => p.Key, p => p.Value);
        cache = new(projectRoot, signature, resume, encoder);
        cache.WriteHashes(clipHashes.Select(p => (p.Key.Clip, p.Key.Frame, p.Value))
            .Concat(hashes.Select(p => (Guid.Empty, p.Key, p.Value))), token);
        cache.IndexReusableFrames(clipHashes.Values.Concat(hashes.Values)
            .Where(h => h.CanReuse).Select(h => h.Hash).ToHashSet(StringComparer.Ordinal), token);
        Log($"[Composer] Deferred cache: {cache.DirectoryPath}, restoring={cache.Restoring}, encoder={encoder.GetType().Name}.");
    }

    internal bool TryRestoreFrame(uint frame, CancellationToken token)
    {
        var identity = hashes[frame];
        if (identity.CanReuse && cache!.TryReadReusable(cache.ComposedPath(frame), identity.Hash, true, token, out var info, out var picture))
        {
            picture?.Dispose();
            if (IsComposedFrame(info, frame) && info.Reusable)
            {
                restored[frame] = 0;
                return true;
            }
        }
        return false;
    }

    internal bool TryReadClip(IClip clip, FrameHashResult identity, CancellationToken token,
        out DeferredRenderCache.FrameInfo info, out IPicture? picture)
    {
        info = new();
        picture = null;
        if (cache is null || !cache.TryReadReusable(cache.ClipPath(clip.Id, identity.Hash), identity.Hash, false,
            token, out info, out picture)) return false;
        if (info.Clip == clip.Id) return true;
        picture?.Dispose();
        picture = null;
        return false;
    }

    internal void WriteClip(IClip clip, uint frame, FrameHashResult identity, RenderClipContent? content,
        bool reusable, CancellationToken token)
    {
        using var downloaded = content?.Picture is IHwAccelPicture gpu ? gpu.Download() : null;
        var saved = downloaded ?? content?.Picture;
        cache!.Write(cache.ClipPath(clip.Id, identity.Hash), new()
        {
            Clip = clip.Id, Frame = frame, Hash = identity.Hash, HasPicture = saved is not null,
            Reusable = reusable, Position = content?.Position ?? default,
            PreserveAspect = content?.PreserveAspect ?? false, IsCanvas = content?.IsCanvas ?? false,
            CenterImplicit = content?.CenterImplicit ?? FrameComposition.ShouldAutoCenterImplicitClip(content?.Clip ?? clip),
            Width = saved?.Width ?? 0, Height = saved?.Height ?? 0,
            Bits = saved is null ? (int)IPicture.PicturePixelMode.Float16Picture : (int)saved.BitPerPixel,
            Hdr = saved is IHDRPicture<Half>,
        }, saved, token);
    }

    protected override bool RestoreFrame(uint frame, CancellationToken token) => restored.ContainsKey(frame);

    private bool IsComposedFrame(DeferredRenderCache.FrameInfo info, uint frame) => info.Composed && info.Frame == frame
        && info.HasPicture && info.Width == Options.TargetWidth && info.Height == Options.TargetHeight
        && info.Bits == (int)IPicture.PicturePixelMode.Float16Picture && info.Hdr == Options.UseHDR;

    protected override void BeginOutput(CancellationToken token)
    {
        if (cache is null) throw new InvalidOperationException("Initialize the deferred cache before rendering.");
        cache.SetStage("rendering");
    }

    protected override void StoreFrame(RenderCompositionFrame frame, IPicture? picture, CancellationToken token)
    {
        try
        {
            if (picture is not null)
            {
                var identity = hashes[frame.FrameIndex];
                cache!.Write(cache.ComposedPath(frame.FrameIndex), new()
                {
                    Frame = frame.FrameIndex, Composed = true, HasPicture = true,
                    Reusable = identity.CanReuse && !Options.Clips.Any(ClipInitializationFailure.IsMarked),
                    Hash = identity.Hash, Width = picture.Width, Height = picture.Height,
                    Bits = (int)picture.BitPerPixel, Hdr = picture is IHDRPicture<Half>,
                }, picture, CancellationToken.None);
            }
            NotifyPrepared(frame);
            if (frame.Diagnostics is not null) frame.Diagnostics.Succeeded = true;
        }
        finally { picture?.Dispose(); frame.Dispose(); }
    }

    public override async Task CompleteAsync(CancellationToken token)
    {
        await base.CompleteAsync(token).ConfigureAwait(false);
        cache!.SetStage("prepared");
        Log($"[Composer] Cached {PreparedFrames} composed frames; restored {restored.Count}. Ready for encoding.");
    }

    public async Task WriteVideoAsync(VideoBuilder? builder, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref writing, 1, 0) != 0)
            throw new InvalidOperationException("Deferred video output has already started.");
        try
        {
            await base.CompleteAsync(token).ConfigureAwait(false);
            await Task.Run(() =>
            {
                using var progress = Options.EnterOutput?.Invoke();
                var previous = IRenderContext.Current;
                IRenderContext.Current = Options.RenderContext ?? previous;
                using var context = new RenderInputLease(() =>
                {
                    IRenderContext.CurrentFrameBuffer = null;
                    IRenderContext.ClearWorkerState();
                    IRenderContext.Current = previous;
                });
                cache!.SetStage("encoding");
                token.ThrowIfCancellationRequested();
                if (Cover is not null) builder?.Writer.Append(Cover);
                for (ulong i = Options.StartFrame, end = (ulong)Options.StartFrame + Options.Duration; i < end; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var frame = new RenderCompositionFrame((uint)i, [], 0, new RenderInputLease(() => { }));
                    IPicture? picture = null;
                    bool written = false;
                    long started = Stopwatch.GetTimestamp();
                    try
                    {
                        Options.SetWorking?.Invoke((uint)i, true);
                        picture = ReadFrame((uint)i, token);
                        IRenderContext.CurrentFrameBuffer = picture;
                        IRenderContext.SetWorkerState((uint)i, RenderWorkerStage.WritingOutput, "Deferred video writer");
                        if (Options.LogProcessStack)
                        {
                            frame.ProcessStack = picture.ProcessStack.ToArray();
                            frame.DirtyTime = frame.Elapsed.Elapsed - TimeSpan.FromTicks(frame.ProcessStack
                                .Where(p => p.Elapsed is not null).Sum(p => p.Elapsed!.Value.Ticks));
                        }
                        using var preview = builder?.CapturePreviewFrame(picture);
                        if (builder is not null)
                        {
                            builder.Writer.Append(picture);
                            written = true;
                            builder.NotifyFrameWritten((uint)i, preview ?? picture);
                        }
                        NotifyWritten(frame, Stopwatch.GetElapsedTime(started));
                    }
                    finally
                    {
                        if (!written || builder?.DisposeFrameAfterEachWrite != false) picture?.Dispose();
                        frame.Dispose();
                        Options.SetWorking?.Invoke(null, false);
                        IRenderContext.CurrentFrameBuffer = null;
                        IRenderContext.ClearWorkerState();
                        if (Options.GCOption == 2) GC.Collect(2, GCCollectionMode.Forced, true, true);
                        else if (Options.GCOption == 1 && builder?.DoGCAfterEachWrite != true) GC.Collect();
                    }
                }
                token.ThrowIfCancellationRequested();
                cache.SetStage("completed");
                completed = true;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { cache?.Dispose(); }
    }

    private IPicture ReadFrame(uint frame, CancellationToken token)
    {
        bool found = restored.ContainsKey(frame)
            ? cache!.TryReadReusable(cache.ComposedPath(frame), hashes[frame].Hash, true, token, out var info, out var picture)
            : cache!.TryRead(cache.ComposedPath(frame), token, out info, out picture);
        if (found && IsComposedFrame(info, frame) && picture is not null) return picture;
        picture?.Dispose();
        throw new InvalidDataException($"Deferred composed frame {frame} is missing or invalid.");
    }

    public void CleanupCreatedCache()
    {
        if (completed) cache?.CleanupCreatedFiles();
    }

    public override void Dispose()
    {
        base.Dispose();
        cache?.Dispose();
    }
}
