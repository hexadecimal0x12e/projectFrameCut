using System.Diagnostics;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Context;

namespace projectFrameCut.Render.Rendering;

public interface IRenderComposer : IDisposable
{
    long BufferedBytes { get; }
    int PreparedFrames { get; }
    int WrittenFrames { get; }
    event Action<RenderComposerFrameProgress>? FramePrepared;
    event Action<RenderComposerFrameProgress>? FrameWritten;
    void Initialize(RenderComposerOptions options, CancellationToken token, IPicture? cover = null);
    /// <summary>On success, owns the frame lease. Clip IDs are ordered from bottom to top.</summary>
    void RegisterFrame(RenderCompositionFrame frame, CancellationToken token);
    /// <summary>Pictures are borrowed from the registered frame lease; null also completes a clip.</summary>
    void Submit(uint frame, Guid clip, RenderClipContent? content);
    Task CompleteAsync(CancellationToken token);
    void Cancel();
}

public sealed record RenderComposerOptions
{
    public required uint StartFrame { get; init; }
    public required uint Duration { get; init; }
    public required int TargetWidth { get; init; }
    public required int TargetHeight { get; init; }
    public required int ProjectRelativeWidth { get; init; }
    public required int ProjectRelativeHeight { get; init; }
    public bool UseHDR { get; init; }
    public bool AutoCenterImplicitClip { get; init; }
    public int RenderAheadFrames { get; init; } = 4;
    public bool EnableMemoryBudget { get; init; }
    public long MaxBufferedBytes { get; init; } = RenderMemoryBudget.DefaultBytes;
    public bool LogProcessStack { get; init; }
    public bool LogRenderState { get; init; }
    public int GCOption { get; init; }
    public IClip[] Clips { get; init; } = [];
    public AudioSampleSource? AudioSamples { get; init; }
    public IRenderContext? RenderContext { get; init; }
    public Func<IDisposable>? EnterRuntime { get; init; }
    internal bool RunInline { get; init; }
    internal Action<uint?, bool>? SetWorking { get; init; }
    internal Action<uint>? CompleteComposition { get; init; }
    internal Func<IDisposable>? EnterOutput { get; init; }
}

public sealed record RenderClipContent(IClip Clip, IPicture Picture, ClipPositionTuple Position,
    bool PreserveAspect, bool IsCanvas = false, bool? CenterImplicit = null);

/// <summary>The input lease keeps clip pictures and dependency snapshots alive until composition ends.</summary>
public sealed class RenderCompositionFrame(uint frameIndex, Guid[] clips, long estimatedBytes,
    IDisposable inputs, FrameHashContext? context = null) : IDisposable
{
    private IDisposable? inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));
    private int disposed;
    public uint FrameIndex { get; } = frameIndex;
    public IReadOnlyList<Guid> Clips { get; } = clips.ToArray();
    public long EstimatedBytes { get; } = estimatedBytes;
    public FrameHashContext? Context { get; } = context;
    public Stopwatch Elapsed { get; init; } = Stopwatch.StartNew();
    internal RenderFrameDiagnostics? Diagnostics { get; init; }
    internal TimeSpan CompositionElapsed;
    internal IReadOnlyList<PictureProcessStack>? ProcessStack;
    internal TimeSpan DirtyTime;
    internal RenderClipContent[]? PreviewClips;

    public void ReleaseInputs()
    {
        var lease = Interlocked.Exchange(ref inputs, null);
        if (lease is null) return;
        lease.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { ReleaseInputs(); }
        finally
        {
            foreach (var clip in PreviewClips ?? [])
            {
                try { clip.Picture.Dispose(); }
                catch (Exception ex) { Log(ex, "Release composed preview clip", nameof(RenderCompositionFrame)); }
            }
            PreviewClips = null;
            try { Context?.Dispose(); }
            finally { Diagnostics?.Dispose(); }
        }
    }
}

public sealed record RenderComposerFrameProgress(uint FrameIndex, TimeSpan Elapsed, TimeSpan CompositionElapsed,
    TimeSpan WriteElapsed, IReadOnlyList<PictureProcessStack>? ProcessStack, TimeSpan DirtyTime);

internal sealed class RenderInputLease(Action release) : IDisposable
{
    private Action? release = release;
    public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
}
