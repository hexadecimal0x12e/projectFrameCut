using System.Collections.Concurrent;
using System.Diagnostics;
using projectFrameCut.Render.RenderAPIBase.Context;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.Rendering;

public abstract class RenderComposerBase : IRenderComposer
{
    private sealed class BufferedFrame(RenderCompositionFrame frame)
    {
        public RenderCompositionFrame Frame { get; } = frame;
        public readonly Dictionary<Guid, RenderClipContent?> Results = new();
        public bool InputsReleased;
        public bool Ready => Results.Count == Frame.Clips.Count;
    }

    private readonly object gate = new();
    private readonly SortedDictionary<uint, BufferedFrame> frames = new();
    private readonly Queue<uint> ready = new();
    private CancellationTokenSource? cancellation;
    private Task? completion;
    private BlockingCollection<(RenderCompositionFrame Frame, IPicture? Picture, long Bytes)>? outputQueue;
    private Task? outputCompletion;
    private long queuedOutputBytes;
    private Exception? failure;
    private bool inputCompleted, disposed;
    private ulong nextFrame, registeredThrough;
    private long bufferedBytes;
    private int inputFrames;
    private IPicture? inlineBlank;
    private int preparedFrames, writtenFrames;
    protected RenderComposerOptions Options { get; private set; } = null!;
    protected bool IsInitialized => cancellation is not null;
    protected IPicture? Cover { get; private set; }
    protected virtual bool Ordered => true;
    protected virtual bool SeparateOutput => false;
    protected virtual long OutputBufferedBytes => 0;
    protected virtual int PendingOutputs => 0;
    public long BufferedBytes => Interlocked.Read(ref bufferedBytes) + Interlocked.Read(ref queuedOutputBytes) + OutputBufferedBytes;
    public int PreparedFrames => Volatile.Read(ref preparedFrames);
    public int WrittenFrames => Volatile.Read(ref writtenFrames);
    public event Action<RenderComposerFrameProgress>? FramePrepared;
    public event Action<RenderComposerFrameProgress>? FrameWritten;

    public void Initialize(RenderComposerOptions options, CancellationToken token, IPicture? cover = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completion is not null) throw new InvalidOperationException("The composer has already been initialized.");
        if (options.Duration == 0 || (ulong)options.StartFrame + options.Duration > uint.MaxValue
            || options.TargetWidth <= 0 || options.TargetHeight <= 0 || options.RenderAheadFrames <= 0
            || options.EnableMemoryBudget && options.MaxBufferedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        Options = options;
        Cover = cover;
        nextFrame = registeredThrough = options.StartFrame;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        completion = options.RunInline ? Task.CompletedTask
            : Task.Factory.StartNew(() => Process(cancellation.Token), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    // Registration is ordered even when clip completion and composition are not.
    public void RegisterFrame(RenderCompositionFrame frame, CancellationToken token)
    {
        if (cancellation is null) throw new InvalidOperationException("Initialize the composer first.");
        using var registration = token.Register(Wake);
        lock (gate)
        {
            while (true)
            {
                ThrowIfStopped(token);
                if (inputCompleted) throw new InvalidOperationException("Composer input is complete.");
                if (frame.FrameIndex != registeredThrough || registeredThrough >= (ulong)Options.StartFrame + Options.Duration)
                    throw new InvalidOperationException("Frames must be registered once in timeline order.");
                if (inputFrames + PendingOutputs < Options.RenderAheadFrames
                    && (!Options.EnableMemoryBudget || inputFrames == 0
                        || BufferedBytes <= Options.MaxBufferedBytes - frame.EstimatedBytes)) break;
                Monitor.Wait(gate, 50);
            }
            if (frame.Clips.Distinct().Count() != frame.Clips.Count)
                throw new ArgumentException("A frame cannot contain duplicate clip IDs.", nameof(frame));
            if (frame.EstimatedBytes < 0) throw new ArgumentOutOfRangeException(nameof(frame));
            var buffered = new BufferedFrame(frame);
            frames.Add(frame.FrameIndex, buffered);
            inputFrames++;
            registeredThrough++;
            Interlocked.Add(ref bufferedBytes, frame.EstimatedBytes);
            if (!Ordered && !Options.RunInline && buffered.Ready) ready.Enqueue(frame.FrameIndex);
            Monitor.PulseAll(gate);
        }
        if (Options.RunInline) ProcessInline();
    }

    public void Submit(uint frame, Guid clip, RenderClipContent? content)
    {
        if (cancellation is null) throw new InvalidOperationException("Initialize the composer first.");
        lock (gate)
        {
            ThrowIfStopped(CancellationToken.None);
            if (!frames.TryGetValue(frame, out var buffered) || !buffered.Frame.Clips.Contains(clip))
                throw new InvalidOperationException($"Unexpected clip {clip} for frame {frame}.");
            if (!buffered.Results.TryAdd(clip, content))
                throw new InvalidOperationException($"Clip {clip} was already submitted for frame {frame}.");
            if (!Ordered && !Options.RunInline && buffered.Ready) ready.Enqueue(frame);
            Monitor.PulseAll(gate);
        }
        if (Options.RunInline) ProcessInline();
    }

    public virtual async Task CompleteAsync(CancellationToken token)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (completion is null) throw new InvalidOperationException("Initialize the composer first.");
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            inputCompleted = true;
            if (Options.RunInline && (frames.Count != 0 || registeredThrough != (ulong)Options.StartFrame + Options.Duration))
                throw new InvalidOperationException("Composer input is incomplete.");
            Monitor.PulseAll(gate);
        }
        using var registration = token.Register(Cancel);
        await completion.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    private void Process(CancellationToken token)
    {
        IPicture? blank = null;
        var previous = IRenderContext.Current;
        try
        {
            using var runtime = Options.EnterRuntime?.Invoke();
            IRenderContext.Current = Options.RenderContext ?? previous;
            blank = CreateBlank();
            BeginOutput(token);
            if (SeparateOutput)
            {
                outputQueue = new(1);
                outputCompletion = Task.Factory.StartNew(() => WriteOutputs(token), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            while (true)
            {
                BufferedFrame? buffered = null;
                lock (gate)
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        if (Ordered && nextFrame <= uint.MaxValue && frames.TryGetValue((uint)nextFrame, out var next) && next.Ready)
                        {
                            buffered = next;
                            break;
                        }
                        if (!Ordered && ready.TryDequeue(out uint frame))
                        {
                            buffered = frames[frame];
                            break;
                        }
                        if (inputCompleted && frames.Count == 0)
                        {
                            if (registeredThrough != (ulong)Options.StartFrame + Options.Duration)
                                throw new InvalidOperationException("Composer input ended before all frames were registered.");
                            break;
                        }
                        Monitor.Wait(gate, 50);
                    }
                }
                if (buffered is null) break;
                ProcessFrame(buffered, blank, token);
                lock (gate)
                {
                    frames.Remove(buffered.Frame.FrameIndex);
                    if (Ordered) nextFrame++;
                    Monitor.PulseAll(gate);
                }
            }
            outputQueue?.CompleteAdding();
            outputCompletion?.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Fail(ex);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure!).Throw();
        }
        finally
        {
            outputQueue?.CompleteAdding();
            try { outputCompletion?.GetAwaiter().GetResult(); }
            catch (Exception) when (failure is not null || token.IsCancellationRequested) { }
            if (outputQueue is not null)
                while (outputQueue.TryTake(out var output))
                {
                    try { output.Picture?.Dispose(); }
                    catch (Exception ex) { Log(ex, "Release queued render picture", this); }
                    try { output.Frame.Dispose(); }
                    catch (Exception ex) { Log(ex, "Release queued render frame", this); }
                    Interlocked.Add(ref queuedOutputBytes, -output.Bytes);
                }
            blank?.Dispose(true);
            IRenderContext.CurrentFrameBuffer = null;
            IRenderContext.ClearWorkerState();
            IRenderContext.Current = previous;
        }
    }

    private IPicture CreateBlank()
    {
        IPicture blank = Options.UseHDR
            ? HDRPictureFP16.GenerateSolidColor(Options.TargetWidth, Options.TargetHeight, Half.Zero, Half.Zero, Half.Zero, Half.Zero, Half.Zero)
            : PictureFP16.GenerateSolidColor(Options.TargetWidth, Options.TargetHeight, Half.Zero, Half.Zero, Half.Zero, Half.Zero);
        blank.CanBeDisposed = false;
        return blank;
    }

    private void ProcessInline()
    {
        try
        {
            if (inlineBlank is null)
            {
                inlineBlank = CreateBlank();
                BeginOutput(cancellation!.Token);
            }
            if (nextFrame > uint.MaxValue || !frames.TryGetValue((uint)nextFrame, out var frame) || !frame.Ready) return;
            ProcessFrame(frame, inlineBlank, cancellation!.Token);
            frames.Remove(frame.Frame.FrameIndex);
            nextFrame++;
        }
        catch (Exception ex)
        {
            failure = ex;
            Cancel();
            throw;
        }
    }

    private void ProcessFrame(BufferedFrame buffered, IPicture blank, CancellationToken token)
    {
        var frame = buffered.Frame;
        IPicture? picture = null;
        try
        {
            Options.SetWorking?.Invoke(frame.FrameIndex, true);
            using var diagnostic = frame.Diagnostics?.Enter();
            using (frame.Context?.Enter())
            using (EffectProcessing.OpenAudioFrame(Options.AudioSamples))
            using (ClipValuePassThrough.Open(Options.Clips, frame.FrameIndex, Options.TargetWidth, Options.TargetHeight,
                Options.ProjectRelativeWidth, Options.ProjectRelativeHeight, IPicture.PicturePixelMode.Float16Picture, token,
                source => throw new InvalidOperationException($"Clip dependency {source.Id} was not prepared for composition.")))
            {
                IRenderContext.SetWorkerState(frame.FrameIndex, RenderWorkerStage.Compositing, "Render composer");
                long started = Stopwatch.GetTimestamp();
                if (!RestoreFrame(frame.FrameIndex, token))
                    picture = ComposeFrame(frame, buffered.Results, blank, token);
                frame.CompositionElapsed = Stopwatch.GetElapsedTime(started);
            }
            Options.CompleteComposition?.Invoke(frame.FrameIndex);
            if (picture is not null && Options.LogProcessStack)
            {
                frame.ProcessStack = picture.ProcessStack.ToArray();
                frame.DirtyTime = frame.Elapsed.Elapsed - TimeSpan.FromTicks(frame.ProcessStack
                    .Where(p => p.Elapsed is not null).Sum(p => p.Elapsed!.Value.Ticks));
            }
            // Output buffers own the materialized picture; they no longer need clip inputs.
            ReleaseInputs(frame);
            var output = picture;
            picture = null;
            QueueOutput(frame, output, token);
        }
        catch
        {
            picture?.Dispose();
            ReleaseInputs(frame);
            frame.Dispose();
            throw;
        }
        finally
        {
            Options.SetWorking?.Invoke(null, false);
            IRenderContext.CurrentFrameBuffer = null;
            IRenderContext.ClearWorkerState();
        }
    }

    private void QueueOutput(RenderCompositionFrame frame, IPicture? picture, CancellationToken token)
    {
        if (outputQueue is null)
        {
            WriteOutput(frame, picture, token);
            return;
        }
        long bytes = 0;
        try
        {
            bytes = picture is not null ? PictureBytes(picture)
                : (frame.PreviewClips ?? []).Sum(c => PictureBytes(c.Picture));
            Interlocked.Add(ref queuedOutputBytes, bytes);
            outputQueue.Add((frame, picture, bytes), token);
        }
        catch
        {
            Interlocked.Add(ref queuedOutputBytes, -bytes);
            try { picture?.Dispose(); }
            finally { frame.Dispose(); }
            throw;
        }
    }

    private static long PictureBytes(IPicture picture)
        => checked((long)picture.Width * picture.Height * (picture is IHDRPicture<Half>
            or IHwAccelPicture { IsHdr: true } ? 10 : 8));

    private void WriteOutput(RenderCompositionFrame frame, IPicture? picture, CancellationToken token)
    {
        bool transferred = false;
        try
        {
            using var diagnostic = frame.Diagnostics?.Enter();
            using var context = frame.PreviewClips is not null ? frame.Context?.Enter() : null;
            using var audio = EffectProcessing.OpenAudioFrame(Options.AudioSamples);
            using var endpoints = frame.PreviewClips is not null ? ClipValuePassThrough.Open(Options.Clips, frame.FrameIndex,
                Options.TargetWidth, Options.TargetHeight, Options.ProjectRelativeWidth, Options.ProjectRelativeHeight,
                IPicture.PicturePixelMode.Float16Picture, token,
                source => throw new InvalidOperationException($"Clip dependency {source.Id} was not prepared for output.")) : null;
            transferred = true;
            StoreFrame(frame, picture, token);
        }
        finally
        {
            if (!transferred)
            {
                try { picture?.Dispose(); }
                finally { frame.Dispose(); }
            }
        }
    }

    private void WriteOutputs(CancellationToken token)
    {
        var previous = IRenderContext.Current;
        try
        {
            using var runtime = Options.EnterOutput?.Invoke();
            IRenderContext.Current = Options.RenderContext ?? previous;
            foreach (var output in outputQueue!.GetConsumingEnumerable(token))
            {
                try
                {
                    Options.SetWorking?.Invoke(output.Frame.FrameIndex, true);
                    WriteOutput(output.Frame, output.Picture, token);
                }
                finally
                {
                    Interlocked.Add(ref queuedOutputBytes, -output.Bytes);
                    Options.SetWorking?.Invoke(null, false);
                    IRenderContext.CurrentFrameBuffer = null;
                    IRenderContext.ClearWorkerState();
                    Wake();
                }
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
            throw;
        }
        finally
        {
            IRenderContext.CurrentFrameBuffer = null;
            IRenderContext.ClearWorkerState();
            IRenderContext.Current = previous;
        }
    }

    private void Fail(Exception ex)
    {
        lock (gate)
        {
            failure ??= ex;
            Monitor.PulseAll(gate);
        }
        cancellation!.Cancel();
    }

    private void ReleaseInputs(RenderCompositionFrame frame)
    {
        frame.ReleaseInputs();
        if (frame.PreviewClips is null) frame.Context?.Dispose();
        lock (gate)
        {
            if (frames.TryGetValue(frame.FrameIndex, out var buffered) && !buffered.InputsReleased)
            {
                buffered.InputsReleased = true;
                inputFrames--;
                Interlocked.Add(ref bufferedBytes, -frame.EstimatedBytes);
            }
            Monitor.PulseAll(gate);
        }
    }

    protected virtual void BeginOutput(CancellationToken token) { }
    protected virtual bool RestoreFrame(uint frame, CancellationToken token) => false;
    protected virtual IPicture? ComposeFrame(RenderCompositionFrame frame, IReadOnlyDictionary<Guid, RenderClipContent?> clips,
        IPicture blank, CancellationToken token)
        => FrameComposition.Compose(frame.Clips.Select(id => clips[id]), frame.FrameIndex, Options, blank, token);
    // Takes ownership of the picture and frame, including when output fails.
    protected abstract void StoreFrame(RenderCompositionFrame frame, IPicture? picture, CancellationToken token);
    protected void NotifyPrepared(RenderCompositionFrame frame)
    {
        Interlocked.Increment(ref preparedFrames);
        FramePrepared?.Invoke(Progress(frame, TimeSpan.Zero));
    }
    protected void NotifyWritten(RenderCompositionFrame frame, TimeSpan elapsed)
    {
        Interlocked.Increment(ref writtenFrames);
        FrameWritten?.Invoke(Progress(frame, elapsed));
        if (Options.LogRenderState) Log($"[Composer] Frame {frame.FrameIndex} written in {frame.Elapsed.Elapsed}.");
    }
    private static RenderComposerFrameProgress Progress(RenderCompositionFrame frame, TimeSpan writeElapsed)
        => new(frame.FrameIndex, frame.Elapsed.Elapsed, frame.CompositionElapsed, writeElapsed, frame.ProcessStack, frame.DirtyTime);

    private void ThrowIfStopped(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        token.ThrowIfCancellationRequested();
        cancellation!.Token.ThrowIfCancellationRequested();
    }
    private void Wake() { lock (gate) Monitor.PulseAll(gate); }
    public void Cancel()
    {
        if (disposed) return;
        cancellation?.Cancel();
        Wake();
    }

    public virtual void Dispose()
    {
        if (disposed) return;
        Cancel();
        try { completion?.GetAwaiter().GetResult(); }
        catch (Exception) when (failure is not null || cancellation?.IsCancellationRequested == true) { }
        disposed = true;
        foreach (var frame in frames.Values) frame.Frame.Dispose();
        frames.Clear();
        ready.Clear();
        outputQueue?.Dispose();
        inlineBlank?.Dispose(true);
        Interlocked.Exchange(ref bufferedBytes, 0);
        cancellation?.Dispose();
    }
}
