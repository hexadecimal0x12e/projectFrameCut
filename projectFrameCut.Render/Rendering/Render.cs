using projectFrameCut.Render.Contracts;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Drawing.Base.ReadWriteConvert;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Context;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using projectFrameCut.Render.RPCProtocol;
using System.Runtime.CompilerServices;
using static projectFrameCut.Render.Rendering.FrameComposition;

namespace projectFrameCut.Render.Rendering
{
    public partial class Renderer : IRenderContext
    {
        #region opts
        public const int SubTrackOffset = ClipLayerOrder.SubTrackOffset;

        public required IClip[] Clips { get; set; }
        public RenderClipSession? ClipSession { get; set; }
        public AudioSampleSource? AudioSamples { get; set; }
        public double FrameRate { get; set; } = 30;
        public uint Duration;
        public uint StartFrame = 0;
        /// <summary>
        /// Zero-based chunk ordinal when this renderer is used by a chunked render job.
        /// </summary>
        public int ChunkIndex { get; set; }
        /// <summary>
        /// Number of chunks in the owning render job. A value of one represents a normal render.
        /// </summary>
        public int ChunkCount { get; set; } = 1;
        /// <summary>
        /// Frames completed by earlier chunks. Used only for reporting global chunk progress.
        /// </summary>
        public uint CompletedFramesBeforeChunk { get; set; }
        /// <summary>
        /// Total frame count of the owning project. Defaults to this renderer's duration.
        /// </summary>
        public uint TotalProjectFrames { get; set; }
        public VideoBuilder? builder;
        public IRenderComposer? Composer { get; set; }
        public bool ComposeInTimelineOrder { get; set; } = true;

        public bool LogRenderState = false;
        public bool LogStaticsData = false;
        public bool LogProcessStack = false;
        public bool LogRenderSummary { get; set; } = true;
        public bool UseClipInitializationFallback { get; set; }
        public bool DiagnosticRender { get; set; }
        public bool LogFrameTimings { get; set; }
        public bool AutoCenterImplicitClip { get; set; } = false;

        public bool EnableThreadAffinity { get; set; } = false;
        public int[]? WorkerCPUCoreIndexs { get; set; }
        public int MaxThreads { get => field > 0 ? field : Environment.ProcessorCount; set; }
        /// <summary>Maximum hash preparation workers; nonpositive values use all available CPU cores.</summary>
        public int MaxHashThreads { get => field > 0 ? field : Environment.ProcessorCount; set; }
        public bool AllowWorkerOversubscription { get; set; } = true;
        public int GCOption = 0;

        public bool EnableEffectAutoRetry { get; set; } = true;
        public bool ProcessEffectFromCanvas { get; set; } = true;

        public bool ReuseDynamicPreviewCache { get; set; } = false;
        public bool EnableDeferredComposition { get; set; }
        /// <summary>Encodes deferred canvas frames in a format supported by the VFD decoder.</summary>
        public IPictureEncoder VfdEncoder { get; set; } = new VfdV2PictureEncoder(VfdCompression.Zstandard, zstandardLevel: 1);
        public bool ResumeInterruptedRender { get; set; } = true;
        public string? RenderResumeSignature { get; set; }
        public string? DynamicPreviewCacheProjectRoot { get; set; }
        public int DynamicPreviewCacheProjectWidth { get; set; }
        public int DynamicPreviewCacheProjectHeight { get; set; }

        public bool EnableMemoryBudget { get; set; } = false;
        public bool EnableGpuBufferPoolLimit { get; set; } = HwAccelPictureSession.DefaultEnableBufferPoolLimit;
        public long GpuBufferPoolMaxBytes { get; set; } = HwAccelPictureSession.DefaultBufferPoolMaxBytes;
        public int RenderAheadFrames { get => field > 0 ? field : Math.Clamp(MaxThreads, 2, 8); set; }
        public long MaxBufferedBytes { get => field > 0 ? field : RenderMemoryBudget.DefaultBytes; set; }

        public int ProjectRelativeWidth { get; set; }
        public int ProjectRelativeHeight { get; set; }
        public int TargetWidth { get; set; }
        public int TargetHeight { get; set; }
        public bool Use16Bit { get; set; } = true;
        public bool UseHDR { get; set; } = false;
        public int SDRClipsBrightnessInHDRMode { get; set; } = 203;
        public int MaximumHDRBrightness { get; set; } = 1000;

        Dictionary<Guid, int> PerClipHDRBrightness = new();

        public event Action<double, TimeSpan>? OnProgressChanged;
        public event Action<long, long>? OnHashPreparationProgressChanged;
        public event Action<ChunkRenderProgress>? OnChunkProgressChanged;
        private Stopwatch _renderTotalStopwatch = new();
        private readonly Queue<(TimeSpan Elapsed, long Finished)> progressSamples = new();
        private TimeSpan lastProgressSample;
        private double _currentFps = 0;

        public double CurrentFps => Interlocked.CompareExchange(ref _currentFps, 0, 0);
        public TimeSpan EstimatedRemaining => GetEstimatedRemaining(
            (EnableDeferredComposition ? 2d : 1d) * Duration - Volatile.Read(ref Finished)
                - (EnableDeferredComposition ? Volatile.Read(ref preparedFrames) : 0), CurrentFps);

        public RenderThreadProgress[] GetThreadProgress() => threadProgress.Values
            .OrderBy(p => p.ThreadId).Select(p => p.Snapshot()).ToArray();

        public TimeSpan OnePercentLowFrameTime => ComputeLowPercentileFrameTime(0.01);
        public double OnePercentLowFps
        {
            get
            {
                var ts = ComputeLowPercentileFrameTime(0.01);
                if (ts.TotalSeconds <= 0) return -1;
                return 1 / ts.TotalSeconds;
            }
        }

        public double CurrentSecondPerFrame => 1 / CurrentFps;
        public double CurrentFinishedPercentage => Duration > 0 ? (double)Volatile.Read(ref Finished) / Duration : 0;
        public int CurrentFinished => Finished;

        public bool AutoSetupRenderContext { get; set; } = true;
        public double Progress => EnableDeferredComposition && Duration > 0
            ? (Volatile.Read(ref preparedFrames) + (double)Volatile.Read(ref Finished)) / (2d * Duration)
            : CurrentFinishedPercentage;
        public string? AudioFilePath = null;
        public IAudioSource ComposedAudio { get { if (field is not null) return field; field = PluginManager.CreateAudioSource(AudioFilePath ?? throw new FileNotFoundException("Audio is not set.")); return field; } private set { field = value; } }
        double IRenderContext.TargetSecondPerFrame => 1d / (builder?.Writer?.FramePerSecond ?? FrameRate);

        public ConcurrentBag<TimeSpan> EachElapsed = new(), EachElapsedForPreparing = new();

        // Per-frame diagnostics (used for CSV reporting)
        public ConcurrentDictionary<uint, TimeSpan> FramePrepareElapsed { get; } = new();
        public ConcurrentDictionary<uint, TimeSpan> FrameRenderElapsed { get; } = new();
        public ConcurrentDictionary<uint, TimeSpan> FrameDirtyTime { get; } = new();
        public ConcurrentDictionary<uint, List<PictureProcessStack>> FrameProcessStacks { get; } = new();

        private readonly object cacheGate = new();
        private readonly object workerGate = new();
        private readonly object serialGate = new();
        private readonly ConcurrentDictionary<int, RenderThreadTracker> threadProgress = new();
        private readonly Dictionary<ClipCacheKey, CachedClipFrame> frameCache = new();
        private readonly Dictionary<Guid, ClipWorker> clipWorkers = new();
        private ClipWorker[] endpointWorkers = [];
        private IClip[] orderedClips = [];
        private readonly ConcurrentDictionary<Guid, IEffect[]> EffectCache = new();
        private readonly ClipArgumentFrameCache clipArgumentFrames = new();
        private readonly EffectRuntimeContext effectRuntime = new();
        private readonly FrameHashCache nodeCache = new();
        private readonly HwAccelPictureSession gpuSession = new();
        private FrameHashOptions? preparedHashOptions;
        private IClip[] _dynamicPreviewHashClips = [];
        private readonly ConcurrentDictionary<Guid, Dictionary<string, List<(string Path, int ProjectWidth, int ProjectHeight, int CanvasWidth, int CanvasHeight)>>> _dynamicPreviewFiles = new();
        private readonly ConcurrentDictionary<uint, FrameHashContext> pendingHashContexts = new();
        private CancellationTokenSource? renderCancellation;
        private IPicture BlankFrame = null!;
        private bool prepared, running, released;
        private bool serialRendering, workersStopping;
        private uint currentRenderFrame;
        private int[]? workerAffinity;
        private Action<Exception>? workerFailure;
        private int readers, ThreadWorking, Finished, _ppb;
        private long _renderElapsedTicksTotal, _prepareElapsedTicksTotal;
        private int _renderElapsedCount, _prepareElapsedCount;
        private int workerLimit, liveWorkers, sourceReadCount, effectProcessCount;
        private long sourceTicks, effectTicks, waitTicks, compositeTicks, writeTicks;

        private readonly record struct ClipCacheKey(Guid Clip, string Hash, int Width, int Height, int Bpp, bool Hdr, int SdrBrightness, int PeakBrightness);
        private sealed record ClipRequest(IClip Clip, uint Frame, ClipCacheKey Key);
        private sealed class RenderThreadTracker(string name, uint firstFrame, ulong endFrame, bool isCompositor)
        {
            private readonly object gate = new();
            private readonly Stopwatch elapsed = new();
            private RenderThreadState state = RenderThreadState.Paused;
            private uint? currentFrame;
            private ulong completedThrough = firstFrame;
            private long completedFrames;
            public int ThreadId;

            public void Start()
            {
                lock (gate) elapsed.Start();
            }

            public bool SetWorking(uint? frame, bool working)
            {
                lock (gate)
                {
                    bool wasWorking = state == RenderThreadState.Working;
                    currentFrame = frame;
                    state = working ? RenderThreadState.Working : RenderThreadState.Paused;
                    return wasWorking;
                }
            }

            public void CompleteFrame(uint frame)
            {
                lock (gate)
                {
                    completedFrames++;
                    // Cached frames may skip jobs, so progress follows the clip's timeline range.
                    completedThrough = Math.Max(completedThrough, (ulong)frame + 1);
                }
            }

            public void Stop(bool completed)
            {
                lock (gate)
                {
                    elapsed.Stop();
                    currentFrame = null;
                    state = completed ? RenderThreadState.Completed : RenderThreadState.Stopped;
                    if (completed) completedThrough = Math.Max(firstFrame, endFrame);
                }
            }

            public RenderThreadProgress Snapshot()
            {
                lock (gate)
                    return new(ThreadId, name, isCompositor, state, currentFrame,
                        endFrame > firstFrame ? Math.Clamp((completedThrough - (double)firstFrame) / (endFrame - firstFrame), 0, 1)
                            : state == RenderThreadState.Completed ? 1 : 0,
                        completedFrames, elapsed.Elapsed.TotalSeconds > 0 ? completedFrames / elapsed.Elapsed.TotalSeconds : 0);
            }
        }

        private sealed class ClipWorker
        {
            public IClip Clip { get; }
            public uint FirstFrame { get; }
            public ulong EndFrame { get; }
            public IClip[] Dependencies = [];
            public readonly BlockingCollection<CachedClipFrame> Jobs = new();
            public readonly ManualResetEventSlim Initialized = new();
            public Lazy<bool> Initialization { get; }
            public Thread? Thread;
            public uint InitializationFrame = uint.MaxValue;
            public bool InitializationUrgent;

            public ClipWorker(IClip clip, uint firstFrame, ulong endFrame, Action initialize)
            {
                Clip = clip;
                FirstFrame = firstFrame;
                EndFrame = endFrame;
                Initialization = new(() =>
                {
                    try { initialize(); return true; }
                    finally { Initialized.Set(); }
                });
            }
        }

        private sealed class CachedClipFrame
        {
            public ClipRequest Request { get; }
            public Lazy<RenderClipContent?> Content { get; }
            // Queue tasks, timeline frames and temporary readers each hold a reference.
            public int References, Queued, Ready;
            public TimeSpan Elapsed;
            public readonly List<Action<RenderClipContent?>> Subscribers = new();

            public CachedClipFrame(ClipRequest request, Func<RenderClipContent?> render)
            {
                Request = request;
                Content = new(() =>
                {
                    var sw = Stopwatch.StartNew();
                    var result = render();
                    Elapsed = sw.Elapsed;
                    return result;
                });
            }
        }

        public static bool IsProfilerAttached =>
            string.Equals(Environment.GetEnvironmentVariable("COR_ENABLE_PROFILING"), "1", StringComparison.Ordinal);

        #endregion

        #region init

        public void PrepareRender(CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(Clips);
            if (ClipSession is not null && !ReferenceEquals(Clips, ClipSession.Clips))
                throw new InvalidOperationException("The renderer must use the clips owned by its clip session.");
            ObjectDisposedException.ThrowIf(released, this);
            if (prepared || running) throw new InvalidOperationException("The renderer has already been prepared.");
            using var effects = effectRuntime.Enter();
            try
            {
                if (AutoSetupRenderContext) IRenderContext.Current = this;
                gpuSession.EnableBufferPoolLimit = EnableGpuBufferPoolLimit;
                gpuSession.BufferPoolMaxBytes = GpuBufferPoolMaxBytes;
                Composer ??= EnableDeferredComposition ? new DeferredRenderComposer()
                    : new RenderComposer(builder) { ComposeInTimelineOrder = ComposeInTimelineOrder };
                EnableDeferredComposition = Composer is DeferredRenderComposer;
                if (EnableDeferredComposition && ChunkCount > 1)
                    throw new InvalidOperationException("Deferred composition and chunk rendering cannot be enabled together.");
                if (Duration == 0 || (ulong)StartFrame + Duration > (ulong)uint.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(Duration));
                _ppb = IPicture.PicturePixelMode.Float16Picture;
                if (LogRenderSummary) Log($"[Render] Working picture format: {(UseHDR ? "HDR FP16" : "FP16")}, RGB/alpha stored as Half.");
                if (builder is not null)
                {
                    TargetWidth = builder.Width;
                    TargetHeight = builder.Height;
                }
                if (TargetWidth <= 0 || TargetHeight <= 0) throw new InvalidOperationException("A valid target resolution is required.");
                ProjectRelativeWidth = ProjectRelativeWidth > 0 ? ProjectRelativeWidth : TargetWidth;
                ProjectRelativeHeight = ProjectRelativeHeight > 0 ? ProjectRelativeHeight : TargetHeight;
                SDRClipsBrightnessInHDRMode = Math.Min(SDRClipsBrightnessInHDRMode, MaximumHDRBrightness);
                _dynamicPreviewHashClips = Clips.ToArray();
                if (LogRenderSummary) Log($"[Render] Preparing {Duration} frames, project={ProjectRelativeWidth}x{ProjectRelativeHeight}, output={TargetWidth}x{TargetHeight}.");
                foreach (var clip in Clips)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (ClipSession is null) EffectHelper.ResolveClipEffects(clip);
                        else ClipSession.PrepareEffects(clip);
                    }
                    catch (Exception ex) when (UseClipInitializationFallback && ex is not OperationCanceledException)
                    {
                        ClipInitializationFailure.Mark(clip, "ResolveEffect", ex);
                        Log(ex, $"Initialize preview effects for clip {clip.Id}; using checkerboard fallback", this);
                    }
                    clipWorkers.Add(clip.Id, new(clip,
                        clip.ExtendToWholeDraft ? StartFrame : Math.Max(StartFrame, clip.StartFrame),
                        clip.ExtendToWholeDraft ? (ulong)StartFrame + Duration
                            : Math.Min((ulong)StartFrame + Duration, (ulong)clip.StartFrame + clip.GetEffectiveDuration()),
                        () => InitializeClip(clip)));
                    PerClipHDRBrightness[clip.Id] = clip.ExtraData.TryGetValue("HDRBrightness", out var brightness)
                        ? Convert.ToInt32(brightness) : SDRClipsBrightnessInHDRMode;
                }
                foreach (var worker in clipWorkers.Values)
                    worker.Dependencies = Timeline.CollectHashDependencies(Clips, worker.Clip, new HashSet<Guid> { worker.Clip.Id }).ToArray();
                endpointWorkers = clipWorkers.Values.Where(w => ClipValuePassThrough.HasEndpoints(w.Clip)).ToArray();
                BlankFrame = UseHDR ? HDRPictureFP16.GenerateSolidColor(TargetWidth, TargetHeight, Half.Zero, Half.Zero, Half.Zero, Half.Zero, Half.Zero)
                    : PictureFP16.GenerateSolidColor(TargetWidth, TargetHeight, Half.Zero, Half.Zero, Half.Zero, Half.Zero);
                BlankFrame.CanBeDisposed = false;
                preparedHashOptions = HashOptions;
                orderedClips = Clips.OrderBy(c => c, ClipLayerOrder.BottomToTop).ToArray();
                prepared = true;
            }
            catch
            {
                released = true;
                Composer?.Dispose();
                ReleaseResources();
                throw;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public async Task GoRender(CancellationToken token, IPicture? cover = null)
        {
            using var audio = EffectProcessing.OpenAudioFrame(AudioSamples, TotalProjectFrames > 0 ? TotalProjectFrames : AudioSamples?.TotalProjectFrames ?? Duration);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var registration = cancellation.Token.Register(() =>
            {
                lock (workerGate) Monitor.PulseAll(workerGate);
            });
            var failures = new ConcurrentQueue<Exception>();
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            long memoryBudget = MaxBufferedBytes;
            bool renderSucceeded = false;

            void Fail(Exception ex)
            {
                Log(ex, $"Render pipeline ({Thread.CurrentThread.Name})", this);
                lock (failures)
                    if (!failures.Any(e => ReferenceEquals(e, ex))) failures.Enqueue(ex);
                cancellation.Cancel();
            }
            lock (cacheGate)
            {
                ObjectDisposedException.ThrowIf(released, this);
                if (!prepared || running) throw new InvalidOperationException("Call PrepareRender before starting the renderer.");
                serialRendering = DiagnosticRender || MaxThreads == 1;
                workerAffinity = ResolveWorkerThreadAffinityTargetCores().Mask;
                workerLimit = serialRendering ? 1 : MaxThreads;
                currentRenderFrame = StartFrame;
                renderCancellation = cancellation;
                workerFailure = Fail;
                running = true;
            }
            _renderTotalStopwatch.Restart();
            progressSamples.Enqueue((TimeSpan.Zero, 0));

            try
            {
                if (EnableDeferredComposition)
                {
                    try
                    {
                        InitializeDeferredCache(cancellation.Token);
                    }
                    finally
                    {
                        OnHashPreparationProgressChanged?.Invoke(0, 0);
                    }
                    Log($"[Render] Deferred composition: cache composed frames for caller-owned encoding. Resume={ResumeInterruptedRender}.");
                }
                else if (LogRenderSummary && serialRendering)
                {
                    Log(DiagnosticRender || LogFrameTimings
                        ? "[Render] Diagnostic mode: one serial render thread, no frame lookahead. Timings include GPU completion; nested timings are inclusive and self excludes child stages. Frame indices are zero-based."
                        : "[Render] Serial mode: one render thread, clips rendered bottom to top, no frame lookahead.");
                }
                else if (LogRenderSummary)
                {
                    Log($"[Render] Clip-owned workers, running {(AllowWorkerOversubscription ? "soft limit" : "limit")}={workerLimit}, separate scheduler and compositor" +
                        $", lookahead<={RenderAheadFrames} frames, " +
                        (EnableMemoryBudget ? $"estimated buffer budget={memoryBudget / 1024 / 1024} MiB." : "no memory budget."));
                    if (LogFrameTimings)
                        Log("[Render] Detailed frame timings enabled. Frame total runs from scheduling through output; worker sums include overlapping work. Timings include GPU completion.");
                }

                if (LogRenderSummary) Log($"[Render] Effect implementations: {EffectHelper.GetImplementationSummary(Clips)}.");
                Composer!.FramePrepared += OnFramePrepared;
                Composer.FrameWritten += OnFrameWritten;
                Composer.Initialize(CompositionOptions with { RenderAheadFrames = serialRendering ? 1 : RenderAheadFrames, RunInline = serialRendering },
                    cancellation.Token, cover);
                var compositor = StartRenderThread(serialRendering ? "Render worker/compositor" : "Render scheduler", () =>
                {
                    if (serialRendering) Interlocked.Increment(ref ThreadWorking);
                    try { RenderFrames(cancellation.Token); }
                    finally
                    {
                        if (serialRendering) Interlocked.Decrement(ref ThreadWorking);
                        completed.TrySetResult();
                    }
                }, cancellation.Token, Fail, serialRendering ? workerAffinity : null, isCompositor: serialRendering);
                await completed.Task.ConfigureAwait(false);
                compositor.Join();
                renderSucceeded = failures.IsEmpty && !token.IsCancellationRequested;
            }
            finally
            {
                if (!renderSucceeded) Composer?.Cancel();
                cancellation.Cancel();
                lock (cacheGate) released = true;
                Thread[] workers;
                lock (workerGate)
                {
                    workersStopping = true;
                    foreach (var worker in clipWorkers.Values) worker.Jobs.CompleteAdding();
                    workers = clipWorkers.Values.Select(w => w.Thread).OfType<Thread>().ToArray();
                    Monitor.PulseAll(workerGate);
                }
                foreach (var worker in workers) worker.Join();
                foreach (var worker in clipWorkers.Values)
                    while (worker.Jobs.TryTake(out var entry)) ReleaseFrame(entry);
                lock (cacheGate)
                {
                    running = false;
                    released = true;
                    while (readers > 0) Monitor.Wait(cacheGate);
                    renderCancellation = null;
                }
                workerFailure = null;
                _renderTotalStopwatch.Stop();
                if (!EnableDeferredComposition || !renderSucceeded) Composer?.Dispose();
                ReleaseResources();
            }
            if (!failures.IsEmpty)
            {
                if (failures.Count == 1 && failures.TryDequeue(out var failure))
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                throw new AggregateException("Rendering failed.", failures);
            }
            if (token.IsCancellationRequested) throw new TaskCanceledException("Rendering was cancelled.", null, token);
            if (LogRenderSummary) Log($"[Render] Finished {(EnableDeferredComposition ? preparedFrames : Finished)} frames in {_renderTotalStopwatch.Elapsed}. {GetPipelineTimingInfo()}");
        }

        #endregion

        #region worker

        private Thread StartRenderThread(string name, Action action, CancellationToken token, Action<Exception> fail,
            int[]? affinityMask = null, bool isCompositor = false, uint? firstFrame = null, ulong? endFrame = null)
        {
            var progress = new RenderThreadTracker(name, firstFrame ?? StartFrame, endFrame ?? (ulong)StartFrame + Duration, isCompositor);
            var thread = new Thread(() =>
            {
                progress.Start();
                bool completed = false;
                using var effects = effectRuntime.Enter();
                IRenderContext.ClearWorkerState();
                IRenderContext.CurrentFrameBuffer = null;
                if (AutoSetupRenderContext) IRenderContext.Current = this;
                try
                {
                    if (affinityMask is { Length: > 0 })
                    {
                        try { ThreadAffinityHelper.SetCurrentThreadAffinity(affinityMask); }
                        catch (Exception ex) { Log(ex, "Set render worker affinity", this); }
                    }
                    action();
                    completed = true;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex) { fail(ex); }
                finally
                {
                    progress.Stop(completed || Volatile.Read(ref Finished) >= Duration);
                    IRenderContext.CurrentFrameBuffer = null;
                    IRenderContext.ClearWorkerState();
                    if (AutoSetupRenderContext) IRenderContext.Current = null;
                }
            })
            { Name = name, IsBackground = true };
            progress.ThreadId = thread.ManagedThreadId;
            threadProgress[thread.ManagedThreadId] = progress;
            thread.Start();
            return thread;
        }

        private bool SetThreadWorking(uint? frame, bool working)
            => threadProgress.TryGetValue(Environment.CurrentManagedThreadId, out var progress) && progress.SetWorking(frame, working);

        private void CompleteThreadFrame(uint frame)
        {
            if (threadProgress.TryGetValue(Environment.CurrentManagedThreadId, out var progress)) progress.CompleteFrame(frame);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private void RenderClipJobs(ClipWorker worker, CancellationToken token)
        {
            try
            {
                using (pendingHashContexts.TryGetValue(worker.InitializationFrame, out var context) ? context.Diagnostics?.Enter(worker: true) : null)
                {
                    EnterWorker(worker, null, token);
                    try { _ = worker.Initialization.Value; }
                    finally { ExitWorker(); }
                }
                foreach (var entry in worker.Jobs.GetConsumingEnumerable(token))
                {
                    try
                    {
                        SetThreadWorking(entry.Request.Frame, false);
                        EnsureClipDependencies(worker.Clip, entry.Request.Frame, token);
                        EnterWorker(worker, entry.Request.Frame, token);
                        try
                        {
                            ReadContent(entry);
                            CompleteThreadFrame(entry.Request.Frame);
                        }
                        finally { ExitWorker(); }
                    }
                    finally
                    {
                        IRenderContext.CurrentFrameBuffer = null;
                        IRenderContext.ClearWorkerState();
                        ReleaseFrame(entry);
                    }
                }
            }
            finally { Interlocked.Decrement(ref liveWorkers); }
        }

        private void InitializeClip(IClip clip)
        {
            if (UseClipInitializationFallback && ClipInitializationFailure.IsMarked(clip))
            {
                EffectCache[clip.Id] = [];
                return;
            }
            try { InitializeClipCore(clip); }
            catch (Exception ex) when (UseClipInitializationFallback && ex is not OperationCanceledException)
            {
                ClipInitializationFailure.Mark(clip, "Source or ResolveEffect", ex);
                EffectCache[clip.Id] = [];
                Log(ex, $"Initialize preview clip {clip.Id}; using checkerboard fallback", this);
            }
        }

        private void InitializeClipCore(IClip clip)
        {
            using var diagnostic = RenderFrameDiagnostics.Current?.Measure($"Initialize clip {clip.Name} ({clip.Id})", "Initialize clip");
            IEffect[] clipEffects;
            if (ClipSession?.IsInitialized(clip) == true)
                clipEffects = (clip.EffectsInstances ?? []).Where(e => e.Enabled).OrderBy(e => e.Index).ToArray();
            else
            {
                clip.ReInit(_ppb);
                clipEffects = EffectHelper.GetClipEffectsInstances(clip);
                ClipSession?.MarkInitialized(clip);
            }
            var effects = clipEffects
                .Where(e => e.Enabled && e.TypeOfEffect.GetPipeline() == EffectPipeline.Picture);
            if (HasExplicitTargetRect(clip)) effects = effects.Where(e => !IsLegacyInternalLayoutEffect(e));
            EffectCache[clip.Id] = effects.ToArray();
            if (clip.AlternativeSource is ISourceReplacementEffect replacement)
                replacement.ProjectFrameRate = (int)Math.Round(builder?.Writer.FramePerSecond ?? FrameRate);
        }

        private void StartClipWorker(ClipWorker worker, uint frame, CancellationToken token, bool urgent = false)
        {
            lock (workerGate)
            {
                token.ThrowIfCancellationRequested();
                var cancellation = renderCancellation!;
                cancellation.Token.ThrowIfCancellationRequested();
                if (workersStopping) throw new OperationCanceledException(cancellation.Token);
                worker.InitializationFrame = Math.Min(worker.InitializationFrame, frame);
                worker.InitializationUrgent |= urgent;
                if (worker.Thread is null)
                {
                    Interlocked.Increment(ref liveWorkers);
                    try
                    {
                        worker.Thread = StartRenderThread($"Clip render worker {worker.Clip.Name} ({worker.Clip.Id})",
                            () => RenderClipJobs(worker, cancellation.Token), cancellation.Token, workerFailure!, workerAffinity,
                            firstFrame: worker.FirstFrame, endFrame: worker.EndFrame);
                    }
                    catch
                    {
                        Interlocked.Decrement(ref liveWorkers);
                        throw;
                    }
                }
                Monitor.PulseAll(workerGate);
            }
        }

        private void EnterWorker(ClipWorker worker, uint? frame, CancellationToken token)
        {
            lock (workerGate)
            {
                // Current-frame work and explicitly requested initialization may bypass the soft limit when enabled.
                while (ThreadWorking >= workerLimit && (!AllowWorkerOversubscription
                    || (frame ?? worker.InitializationFrame) > currentRenderFrame && (frame.HasValue || !worker.InitializationUrgent)))
                {
                    token.ThrowIfCancellationRequested();
                    Monitor.Wait(workerGate);
                }
                token.ThrowIfCancellationRequested();
                Interlocked.Increment(ref ThreadWorking);
                SetThreadWorking(frame, true);
            }
        }

        private void ExitWorker()
        {
            SetThreadWorking(null, false);
            lock (workerGate)
            {
                Interlocked.Decrement(ref ThreadWorking);
                Monitor.PulseAll(workerGate);
            }
        }

        private void EnsureClipDependencies(IClip clip, uint frame, CancellationToken token, bool urgent = false)
        {
            var worker = clipWorkers[clip.Id];
            EnsureClipInitialized(worker, frame, token, urgent);
            foreach (var dependency in worker.Dependencies)
                EnsureClipInitialized(clipWorkers[dependency.Id], frame, token, urgent);
            foreach (var endpoint in endpointWorkers)
                if (frame >= endpoint.FirstFrame && frame < endpoint.EndFrame)
                    EnsureClipInitialized(endpoint, frame, token, urgent);
        }

        private void EnsureClipInitialized(ClipWorker worker, uint frame, CancellationToken token, bool urgent)
        {
            token.ThrowIfCancellationRequested();
            if (running && !serialRendering && !worker.Initialized.IsSet && worker.Thread != Thread.CurrentThread)
            {
                StartClipWorker(worker, frame, token, urgent);
                bool wasWorking = SetThreadWorking(frame, false);
                try { worker.Initialized.Wait(token); }
                finally { SetThreadWorking(frame, wasWorking); }
            }
            _ = worker.Initialization.Value;
        }

        private void CompleteClipJobs(ulong scheduled)
        {
            foreach (var worker in clipWorkers.Values)
                if (worker.EndFrame <= scheduled && !worker.Jobs.IsAddingCompleted) worker.Jobs.CompleteAdding();
        }

        #endregion

        #region render

        #region core render

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private RenderClipContent ProcessClip(IClip clip, IPicture frame, uint targetFrame, bool reusedPreview, CancellationToken token)
        {
            using var diagnostic = RenderFrameDiagnostics.Current?.Measure("Effects, transition and HDR");
            int layoutRelativeWidth = ProjectRelativeWidth, layoutRelativeHeight = ProjectRelativeHeight;
            long effectsStarted = Stopwatch.GetTimestamp();
            try
            {
                IRenderContext.SetWorkerState(targetFrame, RenderWorkerStage.ProcessingEffects, Thread.CurrentThread.Name, clip);
                using (RenderFrameDiagnostics.Current?.Measure("Transition"))
                {
                    if (!reusedPreview && TransformProcessing.TryRender(clip, Clips, targetFrame, TargetWidth, TargetHeight,
                        layoutRelativeWidth, layoutRelativeHeight, _ppb, out var transition, UseHDR ? SDRClipsBrightnessInHDRMode : 0,
                        AutoCenterImplicitClip, initializeClips: false, preparedSource: frame, argumentFrames: clipArgumentFrames, keepHwAccelPicture: true))
                    {
                        if (!ReferenceEquals(frame, transition)) frame.Dispose();
                        if (UseHDR && transition is not IHDRPicture<Half> and not IHwAccelPicture { IsHdr: true })
                            transition = ReplaceFrame(transition!, EffectPictureProcessing.ToHdr(transition!, PerClipHDRBrightness[clip.Id]));
                        return new(clip, transition!, new(0, 0, TargetWidth, TargetHeight, false), true, IsCanvas: true);
                    }
                }
                ClipPositionTuple targetPos = new(
                    clip.TargetX,
                    clip.TargetY,
                    clip.TargetWidth > 0 ? ScaleDimensionToTarget(clip.TargetWidth, layoutRelativeWidth, TargetWidth) : TargetWidth,
                    clip.TargetHeight > 0 ? ScaleDimensionToTarget(clip.TargetHeight, layoutRelativeHeight, TargetHeight) : TargetHeight,
                    false, clip.Rotation, clip.Opacity);
                bool preserveAspect = true;

                if (EffectCache.TryGetValue(clip.Id, out var effects) && effects is not null)
                {
                    var clipDuration = clip.GetEffectiveDuration();
                    var clipProgress = clipDuration > 0
                        ? Math.Clamp((float)((long)targetFrame - (long)clip.StartFrame) / clipDuration, 0f, 1f)
                        : 0f;
                    using var effectContext = ValueProviderFrameContext.PushFrame(targetFrame, clipProgress);
                    for (int _effectIdx = 0; _effectIdx < effects.Length; _effectIdx++)
                    {
                        token.ThrowIfCancellationRequested();
                        var item = effects[_effectIdx];
                        if (item is null) throw new InvalidOperationException($"Effect(index: #{_effectIdx}) is null in clip {clip.Id}. This indicates a logic error.");
                        using var effectTiming = RenderFrameDiagnostics.Current?.Measure($"Effect {item.Name} ({item.Id}, {item.GetType().Name})", $"Effect ({item.GetType().Name})");
                        if (item is DynamicEffectGraph graph)
                        {
                            var result = graph.Evaluate(frame, clip, targetFrame, TargetWidth, TargetHeight, layoutRelativeWidth, layoutRelativeHeight, targetPos, preserveAspect, ProcessEffectFromCanvas, token, keepHwAccelPicture: true);
                            if (!ReferenceEquals(frame, result.Picture)) frame.Dispose();
                            frame = result.Picture;
                            targetPos = result.Position;
                            preserveAspect = result.PreserveAspect;
                            continue;
                        }
                        if (reusedPreview && item is not IClipPositionProvider and not IContinuousClipPositionProvider)
                            continue;
                        IRenderContext.CurrentFrameBuffer = frame;

                        try
                        {
                            switch (item.TypeOfEffect)
                            {
                                case EffectType.NormalEffect:
                                    if (item is not INormalEffect e) goto notdefined;
                                    frame = ResizeForEffectIfNeeded(frame, item, targetPos.TargetWidth, targetPos.TargetHeight, preserveAspect);
                                    frame = ReplaceFrame(frame, FrameHashContext.Compute(item, clip, targetFrame, frame, TargetWidth, TargetHeight, layoutRelativeWidth, layoutRelativeHeight, () => EffectPictureProcessing.Render(item, frame, TargetWidth, TargetHeight)));
                                    continue;
                                case EffectType.ContinuousEffect:
                                    if (item is not IContinuousEffect c) goto notdefined;
                                    int scopedStart = c.IsScoped ? c.StartPoint : (int)clip.StartFrame;
                                    int scopedEnd = c.IsScoped ? c.EndPoint : (int)(clip.StartFrame + clip.GetEffectiveDuration());
                                    if (scopedEnd <= scopedStart || targetFrame < scopedStart || targetFrame >= scopedEnd) continue;
                                    float continuousProgress = Math.Clamp((float)(targetFrame - scopedStart) / (scopedEnd - scopedStart), 0f, 1f);
                                    frame = ResizeForEffectIfNeeded(frame, item, targetPos.TargetWidth, targetPos.TargetHeight, preserveAspect);
                                    frame = ReplaceFrame(frame, FrameHashContext.Compute(item, clip, targetFrame, frame, TargetWidth, TargetHeight, layoutRelativeWidth, layoutRelativeHeight, () => EffectPictureProcessing.Render(item, frame, TargetWidth, TargetHeight, continuousProgress), continuousProgress));
                                    continue;
                                case EffectType.ContinuousClipPositionProvider:
                                    if (item is not IContinuousClipPositionProvider cp) goto notdefined;
                                    var pos = FrameHashContext.Compute(item, clip, targetFrame, null, TargetWidth, TargetHeight, layoutRelativeWidth, layoutRelativeHeight, () => cp.GetPosition(clip, targetFrame, TargetWidth, TargetHeight, layoutRelativeWidth, layoutRelativeHeight));
                                    preserveAspect &= cp.PreserveAspectRatio;
                                    targetPos = targetPos.Apply(pos);
                                    continue;

                                case EffectType.ClipPositionProvider:
                                    if (item is not IClipPositionProvider p) goto notdefined;
                                    var pos1 = FrameHashContext.Compute(item, clip, targetFrame, null, TargetWidth, TargetHeight, layoutRelativeWidth, layoutRelativeHeight, () => p.GetPosition(clip, TargetWidth, TargetHeight, layoutRelativeWidth, layoutRelativeHeight));
                                    preserveAspect &= p.PreserveAspectRatio;
                                    targetPos = targetPos.Apply(pos1);
                                    continue;
                                case EffectType.NonIPictureOutputValueProvider:
                                    throw new InvalidOperationException($"Effect {item.Name} ({item.Id}) of clip {clip.Id} is a NonIPictureOutputValueProvider and should have been handled in the EffectBindingHelper.RebuildAllEffects or dynamically in the dynamic pipeline. This indicates a logic error.");

                                case EffectType.MixtureProvider:
                                case EffectType.Transform:
                                case EffectType.SpeedVarianceProvider:
                                case EffectType.VectorPictureEffect:
                                case EffectType.VectorComponentEffect:
                                case EffectType.TextEffect:
                                case EffectType.ContinuousTextEffect:
                                case EffectType.SourceReplacement:
                                    continue; //they've processed somewhere else

                                case EffectType.NotSpecified:
                                    throw new InvalidOperationException($"EffectType cannot be NotSpecified. Processing: {item.Name} of clip {clip.Id}");

                                default:
                                    Log($"[Render] Effect {item.Name} of clip {clip.Id} has an not defined type {item.TypeOfEffect}.", "warn");
                                    goto notdefined;
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Log(ex, $"Processing effect {item?.Name} ({item?.Id}) of clip {clip.Id}", this);
                            if (EnableEffectAutoRetry)
                            {
                                goto notdefined;
                            }
                            else
                            {
                                throw;
                            }
                        }

                    notdefined:
                        if (item is null) throw new InvalidOperationException($"Effect(index: #{_effectIdx}) is null in clip {clip.Id}. This indicates a logic error.");
                        if (item is INormalEffect n)
                        {
                            frame = ResizeForEffectIfNeeded(frame, item, targetPos.TargetWidth, targetPos.TargetHeight, preserveAspect);
                            frame = ReplaceFrame(frame, FrameHashContext.Compute(item, clip, targetFrame, frame, TargetWidth, TargetHeight, layoutRelativeWidth, layoutRelativeHeight, () => EffectPictureProcessing.Render(item, frame, TargetWidth, TargetHeight)));
                        }
                        else if (item is IContinuousEffect c)
                        {
                            int scopedStart = c.IsScoped ? c.StartPoint : (int)clip.StartFrame;
                            int scopedEnd = c.IsScoped ? c.EndPoint : (int)(clip.StartFrame + clip.GetEffectiveDuration());
                            if (scopedEnd <= scopedStart || targetFrame < scopedStart || targetFrame >= scopedEnd) continue;
                            float continuousProgress = Math.Clamp((float)(targetFrame - scopedStart) / (scopedEnd - scopedStart), 0f, 1f);
                            frame = ResizeForEffectIfNeeded(frame, item, targetPos.TargetWidth, targetPos.TargetHeight, preserveAspect);
                            frame = ReplaceFrame(frame, FrameHashContext.Compute(item, clip, targetFrame, frame, TargetWidth, TargetHeight, layoutRelativeWidth, layoutRelativeHeight, () => EffectPictureProcessing.Render(item, frame, TargetWidth, TargetHeight, continuousProgress), continuousProgress));
                        }
                        else if (item is IClipPositionProvider p)
                        {
                            targetPos = targetPos.Apply(FrameHashContext.Compute(item, clip, targetFrame, null, TargetWidth, TargetHeight, layoutRelativeWidth, layoutRelativeHeight, () => p.GetPosition(clip, TargetWidth, TargetHeight, layoutRelativeWidth, layoutRelativeHeight)));
                            preserveAspect &= p.PreserveAspectRatio;
                        }
                        else if (item is IContinuousClipPositionProvider cp)
                        {
                            targetPos = targetPos.Apply(FrameHashContext.Compute(item, clip, targetFrame, null, TargetWidth, TargetHeight, layoutRelativeWidth, layoutRelativeHeight, () => cp.GetPosition(clip, targetFrame, TargetWidth, TargetHeight, layoutRelativeWidth, layoutRelativeHeight)));
                            preserveAspect &= cp.PreserveAspectRatio;
                        }
                        else if (item is IValueProviderEffect)
                        {
                            throw new InvalidOperationException($"Effect {item.Name} ({item.Id}) of clip {clip.Id} is a IValueProviderEffect and should have been handled in the EffectBindingHelper.RebuildAllEffects. This indicates a logic error.");
                        }
                        else if (item is IMixture or ISpeedVarianceProvider or ITextEffect or IContinuousTextEffect or IVectorComponentEffect or IVectorPictureEffect or ISourceReplacementEffect)
                        {
                            //skip here, they've processed somewhere else
                            continue;
                        }
                        else
                        {
                            throw new NotSupportedException($"The effect's ClipType {item.TypeOfEffect} {item.TypeName} of clip {clip.Id} is not supported by Render. Effect ID: {item.Id}");
                        }

                    }
                }

                clip.EffectStagePlan?.Complete(clip, EffectStage.Render, targetFrame);
                if (UseHDR)
                {
                    using var hdrTiming = RenderFrameDiagnostics.Current?.Measure("HDR conversion");
                    if (frame is IHDRPicture<Half> hdr)
                    {
                        if (!Half.IsFinite(hdr.MaximumBrightness) || hdr.MaximumBrightness <= Half.Zero) hdr.MaximumBrightness = (Half)MaximumHDRBrightness;
                    }
                    else if (frame is not IHwAccelPicture { IsHdr: true }) frame = ReplaceFrame(frame, EffectPictureProcessing.ToHdr(frame, PerClipHDRBrightness[clip.Id]));
                }
                return new(clip, frame, targetPos, preserveAspect);
            }
            catch
            {
                frame.Dispose();
                throw;
            }
            finally
            {
                Interlocked.Add(ref effectTicks, Stopwatch.GetElapsedTime(effectsStarted).Ticks);
                Interlocked.Increment(ref effectProcessCount);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private static IPicture ReplaceFrame(IPicture source, IPicture result)
        {
            if (!ReferenceEquals(source, result)) source.Dispose();
            return result;
        }

        #endregion

        #region compose
        private RenderComposerOptions CompositionOptions => new()
        {
            StartFrame = StartFrame,
            Duration = Duration,
            TargetWidth = TargetWidth,
            TargetHeight = TargetHeight,
            ProjectRelativeWidth = ProjectRelativeWidth,
            ProjectRelativeHeight = ProjectRelativeHeight,
            UseHDR = UseHDR,
            AutoCenterImplicitClip = AutoCenterImplicitClip,
            RenderAheadFrames = RenderAheadFrames,
            EnableMemoryBudget = EnableMemoryBudget,
            MaxBufferedBytes = MaxBufferedBytes,
            Clips = Clips,
            AudioSamples = AudioSamples,
            RenderContext = AutoSetupRenderContext ? this : IRenderContext.Current,
            EnterRuntime = EnterComposerRuntime,
            LogProcessStack = LogProcessStack,
            LogRenderState = LogRenderState,
            GCOption = GCOption,
            SetWorking = (frame, working) => SetThreadWorking(frame, working),
            CompleteComposition = frame =>
            {
                if (!serialRendering && !EnableDeferredComposition) CompleteThreadFrame(frame);
            },
            EnterOutput = EnterComposerOutput,
        };

        // Source-dependent composition (masks and single-frame requests) stays in Renderer.
        private IPicture Composite(IEnumerable<RenderClipContent?> clips, uint frameIndex, CancellationToken token)
        {
            using var audio = EffectProcessing.OpenAudioFrame(AudioSamples, TotalProjectFrames > 0 ? TotalProjectFrames : AudioSamples?.TotalProjectFrames ?? Duration);
            using var endpoints = ClipValuePassThrough.Open(Clips, frameIndex, TargetWidth, TargetHeight,
                ProjectRelativeWidth, ProjectRelativeHeight, _ppb, token, source =>
                {
                    var dependency = RenderClip(source, frameIndex, token);
                    dependency?.Picture.Dispose();
                });
            return FrameComposition.Compose(clips, frameIndex, CompositionOptions, BlankFrame, token);
        }

        #endregion

        #region helper

        private void RenderFrames(CancellationToken token)
        {
            CompleteClipJobs(StartFrame);
            var lastLog = Stopwatch.StartNew();
            for (ulong i = StartFrame, end = (ulong)StartFrame + Duration; i < end; i++)
            {
                token.ThrowIfCancellationRequested();
                if (serialRendering) Monitor.Enter(serialGate);
                using var serial = serialRendering ? new RenderInputLease(() => Monitor.Exit(serialGate)) : null;
                uint frame = (uint)i;
                SetThreadWorking(frame, true);
                ClipRequest[] requests = Composer is DeferredRenderComposer deferred && deferred.TryRestoreFrame(frame, token)
                    ? [] : GetFrameRequests(frame);
                var diagnostic = DiagnosticRender || LogFrameTimings ? new RenderFrameDiagnostics(frame) : null;
                var context = new FrameHashContext(HashOptions, token, nodeCache, gpuSession) { Diagnostics = diagnostic, Clips = Clips };
                var entries = new List<CachedClipFrame>();
                var elapsed = Stopwatch.StartNew();
                var inputs = new RenderInputLease(() =>
                {
                    foreach (var entry in entries) ReleaseFrame(entry);
                    pendingHashContexts.TryRemove(frame, out _);
                    lock (workerGate)
                    {
                        currentRenderFrame = pendingHashContexts.Keys.DefaultIfEmpty(frame + 1).Min();
                        Monitor.PulseAll(workerGate);
                    }
                    clipArgumentFrames.RemoveFrame(frame);
                });
                var output = new RenderCompositionFrame(frame, requests.Select(r => r.Clip.Id).ToArray(), Math.Max(1, requests.Sum(r => EstimateClipBytes(r.Clip))), inputs, context)
                {
                    Elapsed = elapsed,
                    Diagnostics = diagnostic
                };
                bool registered = false;
                try
                {
                    pendingHashContexts[frame] = context;
                    lock (workerGate)
                    {
                        currentRenderFrame = pendingHashContexts.Keys.Min();
                        Monitor.PulseAll(workerGate);
                    }
                    foreach (var request in requests) entries.Add(AcquireFrame(request, token, context));
                    diagnostic?.BeginWorkers();
                    if (entries.Count == 0) RecordPreparation();
                    long started = Stopwatch.GetTimestamp();
                    Composer!.RegisterFrame(output, token);
                    registered = true;
                    Interlocked.Add(ref waitTicks, Stopwatch.GetElapsedTime(started).Ticks);
                    if (!serialRendering) QueueClipJobs(entries.ToArray(), token);
                    int remaining = entries.Count;
                    foreach (var entry in entries)
                    {
                        Subscribe(entry, content =>
                        {
                            if (Interlocked.Decrement(ref remaining) == 0) RecordPreparation();
                            Composer.Submit(frame, entry.Request.Clip.Id, content);
                        });
                        if (serialRendering) ReadContent(entry);
                    }
                    CompleteClipJobs(i + 1);
                    CompleteThreadFrame(frame);
                }
                finally
                {
                    if (!registered) output.Dispose();
                }
                if (LogStaticsData && lastLog.Elapsed.TotalSeconds >= 10)
                {
                    Log(GetRendererStatusInfo(), "STAT");
                    lastLog.Restart();
                }

                void RecordPreparation()
                {
                    diagnostic?.CompleteWorkers();
                    var time = TimeSpan.FromTicks(entries.Sum(e => e.Elapsed.Ticks));
                    FramePrepareElapsed[frame] = time;
                    TrackPrepareElapsed(time);
                }
            }
            Composer!.CompleteAsync(token).GetAwaiter().GetResult();
        }

        private IDisposable EnterComposerRuntime()
        {
            var runtime = effectRuntime.Enter();
            var project = ValueProviderFrameContext.PushProject(TotalProjectFrames > 0 ? TotalProjectFrames : AudioSamples?.TotalProjectFrames ?? Duration);
            var progress = EnterComposerProgress("Render composer");
            return new RenderInputLease(() => { progress.Dispose(); project.Dispose(); runtime.Dispose(); });
        }

        private IDisposable EnterComposerOutput()
        {
            if (EnableDeferredComposition) return EnterComposerProgress("Deferred video writer", true);
            var runtime = effectRuntime.Enter();
            var progress = EnterComposerProgress("Render output");
            return new RenderInputLease(() => { progress.Dispose(); runtime.Dispose(); });
        }

        private IDisposable EnterComposerProgress(string name, bool writing = false)
        {
            if (writing) _renderTotalStopwatch.Start();
            var tracker = new RenderThreadTracker(name, StartFrame, (ulong)StartFrame + Duration, true)
            { ThreadId = Environment.CurrentManagedThreadId };
            threadProgress[tracker.ThreadId] = tracker;
            tracker.Start();
            return new RenderInputLease(() =>
            {
                tracker.Stop(writing || !EnableDeferredComposition
                    ? Composer?.WrittenFrames >= Duration : Composer?.PreparedFrames >= Duration);
                if (writing) _renderTotalStopwatch.Stop();
            });
        }

        private void OnFramePrepared(RenderComposerFrameProgress progress)
        {
            Interlocked.Increment(ref preparedFrames);
            if (!serialRendering || !running) CompleteThreadFrame(progress.FrameIndex);
            Interlocked.Add(ref compositeTicks, progress.CompositionElapsed.Ticks);
            InvokeProgress();
        }

        private void OnFrameWritten(RenderComposerFrameProgress progress)
        {
            Interlocked.Increment(ref Finished);
            if (!serialRendering || !running) CompleteThreadFrame(progress.FrameIndex);
            if (!EnableDeferredComposition) Interlocked.Add(ref compositeTicks, progress.CompositionElapsed.Ticks);
            Interlocked.Add(ref writeTicks, progress.WriteElapsed.Ticks);
            FrameRenderElapsed[progress.FrameIndex] = progress.Elapsed;
            TrackRenderElapsed(progress.Elapsed);
            if (progress.ProcessStack is not null)
            {
                FrameProcessStacks[progress.FrameIndex] = progress.ProcessStack.ToList();
                FrameDirtyTime[progress.FrameIndex] = progress.DirtyTime;
            }
            InvokeProgress();
            if (Finished >= Duration) _renderTotalStopwatch.Stop();
        }

        private static void Subscribe(CachedClipFrame entry, Action<RenderClipContent?> submit)
        {
            lock (entry)
            {
                if (entry.Ready == 0)
                {
                    entry.Subscribers.Add(submit);
                    return;
                }
            }
            submit(entry.Content.Value);
        }

        private void QueueClipJobs(CachedClipFrame[] entries, CancellationToken token)
        {
            foreach (var entry in entries)
            {
                if (Volatile.Read(ref entry.Ready) != 0 || Interlocked.CompareExchange(ref entry.Queued, 1, 0) != 0) continue;
                var worker = clipWorkers[entry.Request.Clip.Id];
                StartClipWorker(worker, entry.Request.Frame, token);
                lock (cacheGate) entry.References++;
                try { worker.Jobs.Add(entry, token); }
                catch
                {
                    ReleaseFrame(entry);
                    throw;
                }
            }
        }

        private CachedClipFrame AcquireFrame(ClipRequest request, CancellationToken token, FrameHashContext context)
        {
            lock (cacheGate)
            {
                if (!frameCache.TryGetValue(request.Key, out var entry))
                {
                    entry = new(request, () =>
                    {
                        using var diagnosticScope = context.Diagnostics?.Enter(worker: true);
                        using var scope = context.Enter();
                        var content = RenderClip(request.Clip, request.Frame, token);
                        try
                        {
                            foreach (var id in ClipValuePassThrough.GetSourceClipIds(request.Clip))
                                if (id != request.Clip.Id && Clips.FirstOrDefault(c => c.Id == id) is { } source)
                                    RenderClip(source, request.Frame, token)?.Picture.Dispose();
                            return content;
                        }
                        catch { content?.Picture.Dispose(); throw; }
                    });
                    frameCache.Add(request.Key, entry);
                }
                entry.References++;
                return entry;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private RenderClipContent? ReadContent(CachedClipFrame entry)
        {
            var content = entry.Content.Value;
            Action<RenderClipContent?>[] subscribers;
            lock (entry)
            {
                if (entry.Ready != 0) return content;
                entry.Ready = 1;
                subscribers = entry.Subscribers.ToArray();
                entry.Subscribers.Clear();
            }
            foreach (var submit in subscribers) submit(content);
            return content;
        }

        private void ReleaseFrame(CachedClipFrame entry)
        {
            IPicture? picture;
            lock (cacheGate)
            {
                if (--entry.References != 0) return;
                frameCache.Remove(entry.Request.Key);
                picture = entry.Content.IsValueCreated ? entry.Content.Value?.Picture : null;
            }
            try { picture?.Dispose(); }
            catch (Exception ex) { Log(ex, "Release cached clip frame", this); }
        }

        public IPicture RenderSpecificFrame(uint frameIndex) => RenderSpecificFrame(frameIndex, CancellationToken.None)
            ?? throw new InvalidOperationException("The frame is outside the render range.");

        public IPicture? RenderSpecificFrame(uint frameIndex, CancellationToken token)
        {
            if (DiagnosticRender || MaxThreads == 1)
            {
                lock (serialGate) return RenderSpecificFrameCore(frameIndex, token);
            }
            return RenderSpecificFrameCore(frameIndex, token);
        }

        private IPicture? RenderSpecificFrameCore(uint frameIndex, CancellationToken token)
        {
            if (frameIndex < StartFrame || (ulong)frameIndex >= (ulong)StartFrame + Duration) return null;
            lock (cacheGate)
            {
                ObjectDisposedException.ThrowIf(released, this);
                if (!prepared) return null;
                readers++;
            }
            var entries = new List<CachedClipFrame>();
            using var frameContext = new FrameHashContext(HashOptions, token, nodeCache, gpuSession) { Clips = Clips };
            using var hashScope = frameContext.Enter();
            using var effects = effectRuntime.Enter();
            var previousWorker = IRenderContext.WorkerState;
            var previousBuffer = IRenderContext.CurrentFrameBuffer;
            var previousContext = IRenderContext.Current;
            IRenderContext.ClearWorkerState();
            if (AutoSetupRenderContext) IRenderContext.Current = this;
            try
            {
                foreach (var request in GetFrameRequests(frameIndex))
                {
                    token.ThrowIfCancellationRequested();
                    EnsureClipDependencies(request.Clip, frameIndex, renderCancellation?.Token ?? token, urgent: true);
                    var entry = AcquireFrame(request, renderCancellation?.Token ?? token, frameContext);
                    entries.Add(entry);
                    ReadContent(entry);
                }
                return Composite(entries.Select(e => e.Content.Value), frameIndex, token);
            }
            finally
            {
                foreach (var entry in entries) ReleaseFrame(entry);
                IRenderContext.WorkerState = previousWorker;
                IRenderContext.CurrentFrameBuffer = previousBuffer;
                if (AutoSetupRenderContext) IRenderContext.Current = previousContext;
                lock (cacheGate)
                {
                    readers--;
                    Monitor.PulseAll(cacheGate);
                }
            }
        }

        private RenderClipContent? RenderClip(IClip clip, uint frameIndex, CancellationToken token)
        {
            using var audio = EffectProcessing.OpenAudioFrame(AudioSamples, TotalProjectFrames > 0 ? TotalProjectFrames : AudioSamples?.TotalProjectFrames ?? Duration);
            using var diagnostic = RenderFrameDiagnostics.Current?.Measure($"Clip {clip.Name} ({clip.Id}), frame {frameIndex}", "Clip");
            EnsureClipDependencies(clip, frameIndex, token);
            if (FrameHashContext.Current is { } context)
                return context.Evaluate((typeof(Renderer), clip.Id, frameIndex), () => RenderDeferredClip(clip, frameIndex, token),
                    c => c is null ? null : c with { Picture = DynamicEffectGraph.CopyPicture(c.Picture) }, c => c?.Picture.Dispose());
            return RenderDeferredClip(clip, frameIndex, token);
        }

        private IPicture ReadMaskBackdrop(IClip mask, uint frame, CancellationToken token)
        {
            var lower = new List<RenderClipContent?>();
            try
            {
                foreach (var clip in Clips.Where(c => (c.ContainsFrame(frame) || c.ExtendToWholeDraft && c.LayerIndex > SubTrackOffset)
                    && ClipLayerOrder.BottomToTop.Compare(c, mask) < 0).OrderBy(c => c, ClipLayerOrder.BottomToTop))
                    lower.Add(RenderClip(clip, frame, token));
                return Composite(lower, frame, token);
            }
            finally { foreach (var item in lower) item?.Picture.Dispose(); }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private RenderClipContent? RenderClipUncached(IClip clip, uint frameIndex, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var masks = MaskFrameContext.Open(Clips, frameIndex, TargetWidth, TargetHeight,
                ProjectRelativeWidth, ProjectRelativeHeight, _ppb, token, readBelow: mask => ReadMaskBackdrop(mask, frameIndex, token), forceNew: true);
            using var endpoints = ClipValuePassThrough.Open(Clips, frameIndex, TargetWidth, TargetHeight,
                ProjectRelativeWidth, ProjectRelativeHeight, _ppb, token, source =>
                {
                    var dependency = RenderClip(source, frameIndex, token);
                    dependency?.Picture.Dispose();
                });
            using var endpoint = ClipValueFrameContext.Current?.EnterClip(clip.Id, independent: true);
            using (RenderFrameDiagnostics.Current?.Measure("Clip argument bindings"))
            {
                clip = clipArgumentFrames.Get(clip, frameIndex);
                if (!ClipArgumentBinding.ShouldRender(clip)) return null;
            }
            using var context = ValueProviderFrameContext.PushFrame(frameIndex,
                Math.Clamp(((float)frameIndex - clip.StartFrame) / Math.Max(1u, clip.GetEffectiveDuration()), 0, 1), false, token);
            using var clipScope = ValueProviderFrameContext.PushClip(clip);
            string? contentKey = null;
            // Graphs and endpoints keep their own node snapshots; layout is always evaluated for the current clip.
            if (clip is not MaskClip && !ClipValuePassThrough.HasEndpoints(clip)
                && !(clip.EffectsInstances ?? []).Any(e => e.Enabled && (DynamicEffectBindings.IsGraphEffect(e)
                    || e is IClipPositionProvider or IContinuousClipPositionProvider))
                && !TransformProcessing.HasActiveTransform(clip, Clips, frameIndex))
            {
                var identity = FrameHashHelper.GetClipHash(Clips, clip, frameIndex, HashOptions, token);
                if (identity.CanReuse)
                {
                    contentKey = "clip/" + FrameHashHelper.HashValue(new
                    {
                        identity.Hash,
                        Width = ResolveClipOutputWidth(clip, TargetWidth, ProjectRelativeWidth),
                        Height = ResolveClipOutputHeight(clip, TargetHeight, ProjectRelativeHeight),
                    });
                    if (nodeCache.TryRead(contentKey, out var saved) && saved is IPicture picture)
                        return ProcessClip(clip, picture, frameIndex, reusedPreview: true, token);
                }
            }
            IRenderContext.SetWorkerState(frameIndex, RenderWorkerStage.PreparingSource, Thread.CurrentThread.Name, clip);
            long sourceStarted = Stopwatch.GetTimestamp();
            IPicture frame = null!;
            bool reusedPreview;
            int width = ResolveClipOutputWidth(clip, TargetWidth, ProjectRelativeWidth);
            int height = ResolveClipOutputHeight(clip, TargetHeight, ProjectRelativeHeight);
            try
            {
                using var diagnostic = RenderFrameDiagnostics.Current?.Measure("Source read/decode, preview cache and resize");
                reusedPreview = TryLoadDynamicPreviewFrame(clip, frameIndex, _ppb, out frame);
                if (!reusedPreview)
                {
                    frame = ClipInitializationFailure.IsMarked(clip)
                        ? ClipInitializationFailure.CreateFallbackFrame(width, height, _ppb, clip.ExtraData)
                        : ReadPreviewCachedSource(clip, frameIndex, width, height, token);
                    if (!frame.CanBeDisposed) frame = DynamicEffectGraph.CopyPicture(frame);
                    if (frame.Width != width || frame.Height != height)
                        frame = ReplaceFrame(frame, Resize(frame, width, height, true));
                }
            }
            catch (Exception ex) when (UseClipInitializationFallback && ex is not OperationCanceledException)
            {
                frame?.Dispose();
                ClipInitializationFailure.Mark(clip, "SourceReading", ex);
                Log(ex, $"Read preview source for clip {clip.Id}; using checkerboard fallback", this);
                frame = ClipInitializationFailure.CreateFallbackFrame(width, height, _ppb, clip.ExtraData);
                reusedPreview = false;
            }
            catch
            {
                frame?.Dispose();
                throw;
            }
            finally
            {
                Interlocked.Add(ref sourceTicks, Stopwatch.GetElapsedTime(sourceStarted).Ticks);
                Interlocked.Increment(ref sourceReadCount);
            }
            var result = ProcessClip(clip, frame, frameIndex, reusedPreview, token);
            try
            {
                if (contentKey is not null && !ClipInitializationFailure.IsMarked(clip)) nodeCache.Write(contentKey, result.Picture);
                return result;
            }
            catch { result.Picture.Dispose(); throw; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private ClipRequest CreateRequest(IClip clip, uint frame)
            => new(clip, frame, new(clip.Id, Guid.NewGuid().ToString("N"), TargetWidth, TargetHeight, _ppb, UseHDR,
                SDRClipsBrightnessInHDRMode, MaximumHDRBrightness));

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private ClipRequest[] GetFrameRequests(uint frame) => orderedClips
            .Where(c => frame >= clipWorkers[c.Id].FirstFrame && frame < clipWorkers[c.Id].EndFrame)
            .Select(c => CreateRequest(c, frame)).ToArray();

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private long EstimateClipBytes(IClip clip) => RenderMemoryBudget.EstimateClipBytes(
            Math.Max(TargetWidth, ResolveClipOutputWidth(clip, TargetWidth, ProjectRelativeWidth)),
            Math.Max(TargetHeight, ResolveClipOutputHeight(clip, TargetHeight, ProjectRelativeHeight)));

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private IPicture ResizeForEffectIfNeeded(IPicture frame, IEffect effect, int width, int height, bool preserveAspect)
        {
            if (!ProcessEffectFromCanvas || !effect.CanProcessFromCanvas || width <= 0 || height <= 0 || frame.Width == width && frame.Height == height)
                return frame;
            var result = Resize(frame, width, height, preserveAspect);
            IRenderContext.CurrentFrameBuffer = result;
            return ReplaceFrame(frame, result);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private static IPicture Resize(IPicture frame, int width, int height, bool preserveAspect)
            => EffectPictureProcessing.Resize(frame, width, height, preserveAspect);

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private IPicture ReadPreviewCachedSource(IClip clip, uint frame, int width, int height, CancellationToken token)
        {
            using var cache = ReuseDynamicPreviewCache && !string.IsNullOrWhiteSpace(DynamicPreviewCacheProjectRoot)
                ? new PreviewFrameCache(id => Path.Combine(ProjectCachePaths.GetRoot(DynamicPreviewCacheProjectRoot), "thumbs", "perClip", id.ToString(), "dynamic"),
                    frame, TargetWidth, TargetHeight, ProjectRelativeWidth, ProjectRelativeHeight,
                    _ppb == IPicture.PicturePixelMode.Float16Picture, false, token, readOnly: true) : null;
            return VideoClipRotation.ReadFrame(clip, clip.GetRelativeFrameIndex(frame) ?? clip.Duration, width, height, _ppb);
        }

        private bool TryLoadDynamicPreviewFrame(IClip clip, uint frameIndex, IPicture.PicturePixelMode ppb, out IPicture frame)
        {
            frame = null!;
            if (!ReuseDynamicPreviewCache || string.IsNullOrWhiteSpace(DynamicPreviewCacheProjectRoot))
                return false;
            if (UseHDR || clip is MaskClip || VideoClipRotation.GetAngle(clip) != 0
                || clip.EffectsInstances?.Any(e => e.Enabled && e is (IClipPositionProvider or IContinuousClipPositionProvider or DynamicEffectGraph or NativeDynamicEffectGraph)) == true)
                return false;
            if (EncodeAndDecode.RemoteRpcVideoSource.IsExternalPath(clip.FilePath)) return false;
            if (TransformProcessing.HasActiveTransform(clip, Clips, frameIndex)) return false;

            var directory = Path.Combine(ProjectCachePaths.GetRoot(DynamicPreviewCacheProjectRoot), "thumbs", "perClip", clip.Id.ToString(), "dynamic");
            if (!Directory.Exists(directory)) return false;

            var hashClip = _dynamicPreviewHashClips.FirstOrDefault(c => c.Id == clip.Id) ?? clip;
            var identity = FrameHashHelper.GetClipOutputHash(_dynamicPreviewHashClips, hashClip, frameIndex,
                HashOptions with { AutoCenter = false });
            if (!identity.CanReuse) return false;
            var hash = identity.Hash;
            var format = "vfdfp16";
            var expectedWidth = ResolveClipOutputWidth(clip, TargetWidth, ProjectRelativeWidth);
            var expectedHeight = ResolveClipOutputHeight(clip, TargetHeight, ProjectRelativeHeight);
            try
            {
                var files = _dynamicPreviewFiles.GetOrAdd(clip.Id, _ => IndexDynamicPreviewFiles(directory));
                if (!files.TryGetValue($"{hash}_{format}", out var matches)) return false;
                foreach (var candidate in matches)
                {
                    if (candidate.ProjectWidth != (DynamicPreviewCacheProjectWidth > 0 ? DynamicPreviewCacheProjectWidth : ProjectRelativeWidth)
                        || candidate.ProjectHeight != (DynamicPreviewCacheProjectHeight > 0 ? DynamicPreviewCacheProjectHeight : ProjectRelativeHeight)
                        || candidate.CanvasWidth != TargetWidth || candidate.CanvasHeight != TargetHeight)
                        continue;
                    try
                    {
                        using var stream = new FileStream(candidate.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        if (!Drawing.Base.PictureExtensions.SharedVfdPictureDecoder.TryLoad(stream, out IPicture? loaded)
                            || loaded is null)
                            continue;
                        if (loaded.BitPerPixel != ppb || loaded.Width != expectedWidth || loaded.Height != expectedHeight)
                        {
                            loaded.Dispose();
                            continue;
                        }
                        frame = loaded;
                        LogDiagnostic($"[Render] Reused dynamic preview for clip {clip.Id}, frame {frameIndex}: {candidate.Path}");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Log(ex, $"Reading dynamic preview for clip {clip.Id}, frame {frameIndex}", this);
                    }
                }
            }
            catch (Exception ex)
            {
                Log(ex, $"Finding dynamic preview for clip {clip.Id}, frame {frameIndex}", this);
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private Dictionary<string, List<(string Path, int ProjectWidth, int ProjectHeight, int CanvasWidth, int CanvasHeight)>> IndexDynamicPreviewFiles(string directory)
        {
            var files = new Dictionary<string, List<(string Path, int ProjectWidth, int ProjectHeight, int CanvasWidth, int CanvasHeight)>>(StringComparer.Ordinal);
            const string prefix = $"dynamic_{RenderBackendService.ClipPreviewCacheVersion}_";
            foreach (var path in Directory.EnumerateFiles(directory, $"{prefix}*.vfd"))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var suffix = name[prefix.Length..];
                if (suffix.StartsWith("before-layout_", StringComparison.Ordinal)) suffix = suffix["before-layout_".Length..];
                var parts = suffix.Split('_');
                if (parts.Length != 4 || (parts[3] != "vfd8" && parts[3] != "vfd16")
                    || !TryParsePreviewSize(parts[1], out var projectWidth, out var projectHeight)
                    || !TryParsePreviewSize(parts[2], out var canvasWidth, out var canvasHeight))
                    continue;
                var key = $"{parts[0]}_{parts[3]}";
                if (!files.TryGetValue(key, out var group)) files[key] = group = new();
                group.Add((path, projectWidth, projectHeight, canvasWidth, canvasHeight));
            }
            return files;
        }

        private FrameHashOptions HashOptions => preparedHashOptions ?? new()
        {
            Width = TargetWidth,
            Height = TargetHeight,
            ProjectWidth = ProjectRelativeWidth,
            ProjectHeight = ProjectRelativeHeight,
            FrameRate = builder?.Writer.FramePerSecond ?? FrameRate,
            PixelMode = _ppb,
            Hdr = UseHDR,
            SdrBrightness = SDRClipsBrightnessInHDRMode,
            PeakBrightness = MaximumHDRBrightness,
            ProcessFromCanvas = ProcessEffectFromCanvas,
            AutoCenter = AutoCenterImplicitClip,
        };


        private static bool TryParsePreviewSize(string value, out int width, out int height)
        {
            width = height = 0;
            var separator = value.IndexOf('x');
            return separator > 0 && int.TryParse(value.AsSpan(0, separator), out width)
                && int.TryParse(value.AsSpan(separator + 1), out height)
                && width > 0 && height > 0;
        }

        private static int ResolveClipOutputWidth(IClip clip, int fallbackWidth, int projectRelativeWidth)
        {
            if (clip.TargetWidth > 0)
            {
                return ScaleDimensionToTarget(clip.TargetWidth, projectRelativeWidth, fallbackWidth);
            }

            return Math.Max(1, fallbackWidth);
        }

        private static int ResolveClipOutputHeight(IClip clip, int fallbackHeight, int projectRelativeHeight)
        {
            if (clip.TargetHeight > 0)
            {
                return ScaleDimensionToTarget(clip.TargetHeight, projectRelativeHeight, fallbackHeight);
            }

            return Math.Max(1, fallbackHeight);
        }

        private static int ScaleDimensionToTarget(int value, int relativeValue, int targetValue)
        {
            if (value <= 0)
            {
                return 0;
            }

            if (relativeValue > 0 && targetValue > 0 && relativeValue != targetValue)
            {
                return Math.Max(1, (int)Math.Round((double)value * targetValue / relativeValue, MidpointRounding.AwayFromZero));
            }

            return Math.Max(1, value);
        }

        private static bool IsLegacyInternalLayoutEffect(IEffect effect)
        {
            if (string.Equals(effect.Name, "__Internal_Place__", StringComparison.Ordinal)
                || string.Equals(effect.Name, "__Internal_Resize__", StringComparison.Ordinal))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(effect.Name)
                && (string.Equals(effect.TypeName, "Place", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(effect.TypeName, "Resize", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return false;
        }

        #endregion

        #endregion

        #region deferred/incremental rendering
        private int preparedFrames;
        private readonly ConcurrentDictionary<(Guid Clip, uint Frame), FrameHashResult> deferredClipHashes = new();
        private readonly ConcurrentDictionary<uint, FrameHashResult> deferredFrameHashes = new();

        public void CleanupCreatedDeferredCache() => (Composer as DeferredRenderComposer)?.CleanupCreatedCache();

        private void InitializeDeferredCache(CancellationToken token)
        {
            using var effects = effectRuntime.Enter();
            token.ThrowIfCancellationRequested();
            foreach (var worker in clipWorkers.Values)
            {
                token.ThrowIfCancellationRequested();
                _ = worker.Initialization.Value;
            }
            long totalHashes = Duration + clipWorkers.Values.Sum(w => Math.Max(0L, (long)w.EndFrame - w.FirstFrame));
            long completedHashes = 0;
            int hashThreads = MaxHashThreads;
            var progressTimer = Stopwatch.StartNew();
            OnHashPreparationProgressChanged?.Invoke(0, totalHashes);
            Log($"[Render] Preparing {totalHashes} deferred hashes for {Duration} frames with {hashThreads} workers.");
            void ReportHashProgress()
            {
                lock (progressTimer)
                {
                    completedHashes++;
                    if (completedHashes != totalHashes && progressTimer.ElapsedMilliseconds < 100) return;
                    OnHashPreparationProgressChanged?.Invoke(completedHashes, totalHashes);
                    progressTimer.Restart();
                }
            }
            FrameHashResult PrepareHash(FrameHashResult identity) => identity.CanReuse
                ? identity with { Hash = FrameHashHelper.HashValue(new { identity.Hash, EnableEffectAutoRetry, ReuseDynamicPreviewCache }) }
                : identity;
            Parallel.For((long)StartFrame, (long)StartFrame + Duration, new ParallelOptions
            {
                MaxDegreeOfParallelism = hashThreads,
                CancellationToken = token,
                TaskScheduler = TaskScheduler.Default,
            }, i =>
            {
                uint frame = (uint)i;
                token.ThrowIfCancellationRequested();
                deferredFrameHashes[frame] = PrepareHash(FrameHashHelper.GetFrameHash(Clips, frame, HashOptions, token));
                ReportHashProgress();
                foreach (var clip in orderedClips.Where(c => frame >= clipWorkers[c.Id].FirstFrame && frame < clipWorkers[c.Id].EndFrame))
                {
                    deferredClipHashes[(clip.Id, frame)] = PrepareHash(FrameHashHelper.GetClipOutputHash(Clips, clip, frame, HashOptions, token));
                    ReportHashProgress();
                }
            });
            string? signature = null;
            if (!string.IsNullOrWhiteSpace(RenderResumeSignature) && !string.IsNullOrWhiteSpace(DynamicPreviewCacheProjectRoot)
                && !Clips.Any(c => c is VideoClip { Decoder: EncodeAndDecode.DecoderContextPJFCProject }
                    || Uri.TryCreate(c.FilePath, UriKind.Absolute, out var uri) && !uri.IsFile))
            {
                signature = FrameHashHelper.HashValue(new
                {
                    DeferredRenderCache.Version,
                    State = RenderResumeSignature,
                    StartFrame,
                    Duration,
                    Options = HashOptions,
                    EnableEffectAutoRetry,
                    Clips = Clips.Select(c => new
                    {
                        c.Id,
                        Source = FrameHashHelper.SourceFingerprint(c.FilePath),
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
            ((DeferredRenderComposer)Composer!).InitializeCache(DynamicPreviewCacheProjectRoot, signature,
                ResumeInterruptedRender, VfdEncoder, deferredClipHashes, deferredFrameHashes, token);
        }

        private RenderClipContent? RenderDeferredClip(IClip clip, uint frame, CancellationToken token)
        {
            if (Composer is not DeferredRenderComposer deferred || !deferredClipHashes.TryGetValue((clip.Id, frame), out var identity))
                return RenderClipUncached(clip, frame, token);
            bool reusable = identity.CanReuse && !ClipValuePassThrough.HasEndpoints(clip);
            if (reusable && deferred.TryReadClip(clip, identity, token, out var info, out var picture))
            {
                try
                {
                    var frameClip = clipArgumentFrames.Get(clip, frame);
                    return picture is null ? null : new(frameClip, picture, info.Position, info.PreserveAspect,
                        info.IsCanvas, info.CenterImplicit);
                }
                catch { picture?.Dispose(); throw; }
            }
            var content = RenderClipUncached(clip, frame, token);
            try
            {
                deferred.WriteClip(clip, frame, identity, content, reusable && !ClipInitializationFailure.IsMarked(clip)
                    && !clipWorkers[clip.Id].Dependencies.Any(ClipInitializationFailure.IsMarked), token);
                return content;
            }
            catch { content?.Picture.Dispose(); throw; }
        }
        #endregion

        #region misc

        private void InvokeProgress()
        {
            int finished = Volatile.Read(ref Finished);
            long completedWork = finished + (EnableDeferredComposition ? Volatile.Read(ref preparedFrames) : 0L);
            double prog = Progress;
            var elapsed = _renderTotalStopwatch.Elapsed;
            while (progressSamples.Count > 1 && elapsed - progressSamples.Peek().Elapsed > TimeSpan.FromSeconds(30))
                progressSamples.Dequeue();
            var sample = progressSamples.Peek();
            double seconds = (elapsed - sample.Elapsed).TotalSeconds;
            double fps = seconds > 0 ? (completedWork - sample.Finished) / seconds : 0;
            if (elapsed - lastProgressSample >= TimeSpan.FromSeconds(1))
            {
                progressSamples.Enqueue((elapsed, completedWork));
                lastProgressSample = elapsed;
            }
            Interlocked.Exchange(ref _currentFps, fps);
            var eta = GetEstimatedRemaining((EnableDeferredComposition ? 2d : 1d) * Duration - completedWork, fps);
            OnProgressChanged?.Invoke(prog, eta);
            uint projectFrames = TotalProjectFrames > 0 ? TotalProjectFrames : AudioSamples?.TotalProjectFrames ?? Duration;
            double globalProgress = projectFrames > 0
                ? Math.Clamp((CompletedFramesBeforeChunk + prog * Duration) / projectFrames, 0, 1)
                : prog;
            OnChunkProgressChanged?.Invoke(new ChunkRenderProgress(
                ChunkIndex,
                Math.Max(1, ChunkCount),
                finished,
                Duration,
                prog,
                globalProgress,
                eta));
        }

        internal static TimeSpan GetEstimatedRemaining(double remainingFrames, double fps)
        {
            if (remainingFrames <= 0 || fps <= 0 || !double.IsFinite(fps)) return TimeSpan.Zero;
            double ticks = remainingFrames / fps * TimeSpan.TicksPerSecond;
            return ticks >= long.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)ticks);
        }

        public string GetRendererStatusInfo(bool includeQueueAndWriterStats = true)
        {
            int cached;
            lock (cacheGate) cached = frameCache.Count;
            return $"Finished {CurrentFinished}/{Duration} ({Progress:p2}), ETA: {GetEstimatedRemaining(Duration - (double)CurrentFinished, CurrentFps)}, {CurrentFps:n2} FPS, " +
                (serialRendering ? $"serial render threads: {Volatile.Read(ref ThreadWorking)}/1, "
                    : $"workers: {Volatile.Read(ref ThreadWorking)} running, {Volatile.Read(ref liveWorkers)} alive, {(AllowWorkerOversubscription ? "soft limit" : "limit")}: {workerLimit}, ") +
                $"cached clips: {cached}, estimated buffers: {(Composer?.BufferedBytes ?? 0) / 1024 / 1024} MiB, " +
                $"written: {builder?.WrittenFramesCount ?? 0}, memory: {Environment.WorkingSet / 1024 / 1024:n0} MB, " +
                $"prepare: {GetAverageElapsed(_prepareElapsedTicksTotal, _prepareElapsedCount)}, " +
                $"render: {GetAverageElapsed(_renderElapsedTicksTotal, _renderElapsedCount)}, 1% low: {OnePercentLowFps:n1} FPS. {GetPipelineTimingInfo()}";
        }

        public string GetPipelineTimingInfo()
        {
            double Average(long ticks, int count) => TimeSpan.FromTicks(ticks).TotalMilliseconds / Math.Max(1, count);
            int frames = Volatile.Read(ref Finished);
            return $"Source: {Average(Interlocked.Read(ref sourceTicks), Volatile.Read(ref sourceReadCount)):n1} ms/clip, " +
                $"effects: {Average(Interlocked.Read(ref effectTicks), Volatile.Read(ref effectProcessCount)):n1} ms/clip, " +
                $"wait: {Average(Interlocked.Read(ref waitTicks), frames):n1} ms/frame, " +
                $"compose: {Average(Interlocked.Read(ref compositeTicks), frames):n1} ms/frame, " +
                $"write: {Average(Interlocked.Read(ref writeTicks), frames):n1} ms/frame; {nodeCache.GetStatusInfo()}; {gpuSession.GetStatistics()}.";
        }

        private void TrackRenderElapsed(TimeSpan elapsed)
        {
            EachElapsed.Add(elapsed);
            Interlocked.Add(ref _renderElapsedTicksTotal, elapsed.Ticks);
            Interlocked.Increment(ref _renderElapsedCount);
        }

        /// <summary>
        /// Records a per-frame prepare elapsed time. See <see cref="TrackRenderElapsed"/>.
        /// </summary>
        private void TrackPrepareElapsed(TimeSpan elapsed)
        {
            EachElapsedForPreparing.Add(elapsed);
            Interlocked.Add(ref _prepareElapsedTicksTotal, elapsed.Ticks);
            Interlocked.Increment(ref _prepareElapsedCount);
        }

        private static TimeSpan GetAverageElapsed(long totalTicks, int count)
            => count > 0 ? new TimeSpan(totalTicks / count) : TimeSpan.Zero;

        /// <summary>
        /// 计算低百分位帧耗时：对 <see cref="EachElapsed"/> 中所有帧渲染耗时排序，
        /// 取最慢的 <paramref name="fraction"/> 比例帧的平均值。
        /// 例如 fraction=0.01 得到 1% low 帧耗时，fraction=0.001 得到 0.1% low。
        /// 返回 <see cref="TimeSpan.Zero"/> 表示尚无数据。
        /// </summary>
        /// <param name="fraction">要平均的最慢帧比例，如 0.01 表示最慢 1%。</param>
        public TimeSpan ComputeLowPercentileFrameTime(double fraction)
        {
            if (EachElapsed.IsEmpty) return TimeSpan.Zero;

            var sorted = EachElapsed.OrderBy(ts => ts.Ticks).ToArray();
            int count = Math.Max(1, (int)(sorted.Length * fraction));
            long totalTicks = 0;
            for (int i = sorted.Length - count; i < sorted.Length; i++)
                totalTicks += sorted[i].Ticks;
            return new TimeSpan(totalTicks / count);
        }

        public void ClearCaches()
        {
            lock (cacheGate)
            {
                foreach (var entry in frameCache.Values.Where(e => e.References == 0).ToArray())
                {
                    frameCache.Remove(entry.Request.Key);
                    if (entry.Content.IsValueCreated) entry.Content.Value?.Picture.Dispose();
                }
            }
        }

        private void ReleaseResources()
        {
            deferredClipHashes.Clear();
            deferredFrameHashes.Clear();
            IPicture[] pictures;
            lock (cacheGate)
            {
                pictures = frameCache.Values.Where(e => e.Content.IsValueCreated).Select(e => e.Content.Value?.Picture).OfType<IPicture>().ToArray();
                frameCache.Clear();
            }
            foreach (var picture in pictures)
            {
                try { picture.Dispose(); }
                catch (Exception ex) { Log(ex, "Release cached clip frame", this); }
            }
            clipArgumentFrames.Clear();
            orderedClips = [];
            _dynamicPreviewFiles.Clear();
            if (ClipSession is null)
                foreach (var clip in Clips ?? [])
                {
                    try { EffectHelper.ReleaseClipEffects(clip); }
                    catch (Exception ex) { Log(ex, $"Release render effects for clip {clip.Id}", this); }
                }
            EffectCache.Clear();
            try { BlankFrame?.Dispose(true); }
            catch (Exception ex) { Log(ex, "Release render canvas", this); }
            nodeCache.Dispose();
            foreach (var context in pendingHashContexts.Values) context.Dispose();
            pendingHashContexts.Clear();
            foreach (var worker in clipWorkers.Values)
            {
                worker.Jobs.Dispose();
                worker.Initialized.Dispose();
            }
            clipWorkers.Clear();
            endpointWorkers = [];
            gpuSession.Dispose();
            try { effectRuntime.Dispose(); }
            catch (Exception ex) { Log(ex, "Release render runtime", this); }
        }

        private (int[]? Mask, string? Description) ResolveWorkerThreadAffinityTargetCores()
        {
            if (WorkerCPUCoreIndexs is { Length: > 0 })
            {
                try
                {
                    var cpuIndexes = WorkerCPUCoreIndexs.Distinct().OrderBy(x => x).ToArray();
                    return (cpuIndexes, $"manual CPU cores: {string.Join(", ", cpuIndexes)}");
                }
                catch (Exception ex)
                {
                    Log($"[Renderer] Failed to resolve manual worker affinity: {ex.Message}", "warn");
                    return (null, null);
                }
            }

            if (!EnableThreadAffinity)
            {
                return (null, null);
            }

            try
            {
                var core = ThreadAffinityHelper.GetCpuCoreGroups()
                    .OrderBy(c => (c.Capacity ?? 0) + (c.EfficiencyClass ?? 0))
                    .LastOrDefault();

                if (core?.CpuIndexes is not { Count: > 0 })
                {
                    return (null, null);
                }

                var cores = core.CpuIndexes.ToArray();
                return (cores, $"auto-selected CPU cores: {string.Join(", ", cores)}");
            }
            catch (Exception ex)
            {
                Log($"[Renderer] Failed to resolve automatic worker affinity: {ex.Message}", "warn");
                return (null, null);
            }
        }
        #endregion

    }

    #region enums
    public enum RenderThreadState { Paused, Working, Completed, Stopped }

    public readonly record struct RenderThreadProgress(
        int ThreadId,
        string Name,
        bool IsCompositor,
        RenderThreadState State,
        uint? CurrentFrame,
        double Progress,
        long CompletedFrames,
        double FramesPerSecond);

    public readonly record struct ChunkRenderProgress(
        int ChunkIndex,
        int ChunkCount,
        int FinishedFrames,
        uint ChunkFrames,
        double ChunkProgress,
        double GlobalProgress,
        TimeSpan EstimatedRemaining);

    internal static class RenderMemoryBudget
    {
        public static long DefaultBytes => Math.Clamp(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 3,
            128L * 1024 * 1024, 1024L * 1024 * 1024);

        // Allow for the picture, dependency snapshots and temporary float channels.
        public static long EstimateClipBytes(int width, int height) => checked((long)width * height * 64);

        public static int LimitWorkers(int requested, long clipBytes, long budget)
            => (int)Math.Clamp(budget / Math.Max(1, clipBytes), 1, Math.Max(1, requested));
    }
    #endregion

    #region RenderClipSession
    public sealed class RenderClipSession : IDisposable
    {
        private readonly HashSet<Guid> preparedEffects = new();
        private readonly ConcurrentDictionary<Guid, byte> initializedClips = new();
        private readonly bool ownsClips;
        private bool disposed;

        public RenderClipSession(IClip[] clips, bool ownsClips = true)
        {
            Clips = clips ?? throw new ArgumentNullException(nameof(clips));
            this.ownsClips = ownsClips;
            if (ownsClips) Log($"[Render] Reusing {Clips.Length} clips across chunks.");
        }

        public IClip[] Clips { get; }

        internal void PrepareEffects(IClip clip)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (preparedEffects.Contains(clip.Id)) return;
            EffectHelper.ResolveClipEffects(clip);
            preparedEffects.Add(clip.Id);
        }

        internal bool IsInitialized(IClip clip) => initializedClips.ContainsKey(clip.Id) && !ClipInitializationFailure.IsMarked(clip);
        internal void MarkInitialized(IClip clip) => initializedClips.TryAdd(clip.Id, 0);
        internal void UsePreparedClip(IClip clip)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            preparedEffects.Add(clip.Id);
            if (!ClipInitializationFailure.IsMarked(clip)) MarkInitialized(clip);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (!ownsClips) return;
            foreach (var clip in Clips)
            {
                try { EffectHelper.ReleaseClipEffects(clip); }
                catch (Exception ex) { Log(ex, $"Release render effects for clip {clip.Id}", nameof(RenderClipSession)); }
                try { clip.Dispose(); }
                catch (Exception ex) { Log(ex, $"Release clip {clip.Id}", nameof(RenderClipSession)); }
            }
        }
    }

    #endregion
}
