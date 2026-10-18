using projectFrameCut.Render.Contracts;
using projectFrameCut.Drawing.Processing.Resizing;
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

namespace projectFrameCut.Render.Rendering
{
    public class Renderer : IRenderContext
    {
        #region opts
        public const int SubTrackOffset = ClipLayerOrder.SubTrackOffset;

        public required IClip[] Clips { get; set; }
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

        public bool LogRenderState = false;
        public bool LogStaticsData = false;
        public bool LogProcessStack = false;
        public bool DiagnosticRender { get; set; }
        public bool LogFrameTimings { get; set; }
        public bool AutoCenterImplicitClip { get; set; } = false;

        public bool EnableThreadAffinity { get; set; } = false;
        public int[]? WorkerCPUCoreIndexs { get; set; }
        public int MaxThreads { get => field > 0 ? field : Environment.ProcessorCount; set; }
        public bool AllowWorkerOversubscription { get; set; } = true;
        public int GCOption = 0;

        public bool EnableEffectAutoRetry { get; set; } = true;
        public bool ProcessEffectFromCanvas { get; set; } = true;
        public FrameHashMode FrameHashMode { get; set; } = FrameHashMode.Dynamic;
        public bool ReuseDynamicPreviewCache { get; set; } = false;
        public string? DynamicPreviewCacheProjectRoot { get; set; }
        public int DynamicPreviewCacheProjectWidth { get; set; }
        public int DynamicPreviewCacheProjectHeight { get; set; }

        public bool EnableMemoryBudget { get; set; } = false;
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
        Dictionary<Guid, bool> IsClipGeneratedByAI = new();

        public event Action<double, TimeSpan>? OnProgressChanged;
        public event Action<ChunkRenderProgress>? OnChunkProgressChanged;
        private Stopwatch _renderTotalStopwatch = new();
        private readonly Queue<(TimeSpan Elapsed, int Finished)> progressSamples = new();
        private TimeSpan lastProgressSample;
        private double _currentFps = 0;

        public double CurrentFps => Interlocked.CompareExchange(ref _currentFps, 0, 0);
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
        public double Progress => CurrentFinishedPercentage;
        public string? AudioFilePath = null;
        public IAudioSource ComposedAudio { get { if (field is not null) return field; field = PluginManager.CreateAudioSource(AudioFilePath ?? throw new FileNotFoundException("Audio is not set.")); return field; } private set { field = value; } }
        double IRenderContext.TargetSecondPerFrame => 1d / (double)(builder?.Writer?.FramePerSecond ?? 30.0);

        public ConcurrentBag<TimeSpan> EachElapsed = new(), EachElapsedForPreparing = new();

        // Per-frame diagnostics (used for CSV reporting)
        public ConcurrentDictionary<uint, TimeSpan> FramePrepareElapsed { get; } = new();
        public ConcurrentDictionary<uint, TimeSpan> FrameRenderElapsed { get; } = new();
        public ConcurrentDictionary<uint, TimeSpan> FrameDirtyTime { get; } = new();
        public ConcurrentDictionary<uint, List<PictureProcessStack>> FrameProcessStacks { get; } = new();

        private readonly object cacheGate = new();
        private readonly object workerGate = new();
        private readonly object serialGate = new();
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
        private AutoResetEvent? cacheChanged;
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
        private long sourceTicks, effectTicks, waitTicks, compositeTicks, writeTicks, bufferedBytes;

        private readonly record struct ClipCacheKey(Guid Clip, string Hash, int Width, int Height, int Bpp, bool Hdr, int SdrBrightness, int PeakBrightness);
        private sealed record ClipRequest(IClip Clip, uint Frame, ClipCacheKey Key);
        private sealed record ClipContent(IClip Clip, IPicture Picture, ClipPositionTuple Position, bool PreserveAspect, bool IsCanvas = false);
        private sealed record PendingRenderFrame(CachedClipFrame[] Entries, long EstimatedBytes, FrameHashContext Context);
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
            public Lazy<ClipContent?> Content { get; }
            // Queue tasks, timeline frames and temporary readers each hold a reference.
            public int References, Queued, Ready;
            public TimeSpan Elapsed;

            public CachedClipFrame(ClipRequest request, Func<ClipContent?> render)
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
            ObjectDisposedException.ThrowIf(released, this);
            if (prepared || running) throw new InvalidOperationException("The renderer has already been prepared.");
            using var effects = effectRuntime.Enter();
            try
            {
                if (AutoSetupRenderContext) IRenderContext.Current = this;
                if (Duration == 0 || (ulong)StartFrame + Duration > (ulong)uint.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(Duration));
                _ppb = Use16Bit ? 16 : 8;
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
                Log($"[Render] Preparing {Duration} frames, project={ProjectRelativeWidth}x{ProjectRelativeHeight}, output={TargetWidth}x{TargetHeight}.");
                foreach (var clip in Clips)
                {
                    token.ThrowIfCancellationRequested();
                    EffectHelper.ResolveClipEffects(clip);
                    clipWorkers.Add(clip.Id, new(clip,
                        clip.ExtendToWholeDraft ? StartFrame : Math.Max(StartFrame, clip.StartFrame),
                        clip.ExtendToWholeDraft ? (ulong)StartFrame + Duration
                            : Math.Min((ulong)StartFrame + Duration, (ulong)clip.StartFrame + clip.GetEffectiveDuration()),
                        () => InitializeClip(clip)));
                    PerClipHDRBrightness[clip.Id] = clip.ExtraData.TryGetValue("HDRBrightness", out var brightness)
                        ? Convert.ToInt32(brightness) : SDRClipsBrightnessInHDRMode;
                    IsClipGeneratedByAI[clip.Id] = clip.ExtraData.TryGetValue("IsAI", out var ai) &&
                        (ai is bool b ? b : bool.TryParse(ai?.ToString(), out var value) && value);
                }
                foreach (var worker in clipWorkers.Values)
                    worker.Dependencies = Timeline.CollectHashDependencies(Clips, worker.Clip, new HashSet<Guid> { worker.Clip.Id }).ToArray();
                endpointWorkers = clipWorkers.Values.Where(w => ClipValuePassThrough.HasEndpoints(w.Clip)).ToArray();
                BlankFrame = UseHDR ? HDRPicture16bpp.GenerateSolidColor(TargetWidth, TargetHeight, 0, 0, 0, 0, 0)
                    : Use16Bit ? Picture16bpp.GenerateSolidColor(TargetWidth, TargetHeight, 0, 0, 0, 0)
                    : Picture8bpp.GenerateSolidColor(TargetWidth, TargetHeight, 0, 0, 0, 0);
                BlankFrame.CanBeDisposed = false;
                preparedHashOptions = HashOptions;
                orderedClips = Clips.OrderBy(c => c, ClipLayerOrder.BottomToTop).ToArray();
                prepared = true;
            }
            catch
            {
                released = true;
                ReleaseResources();
                throw;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public async Task GoRender(CancellationToken token, IPicture? cover = null)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var changed = new AutoResetEvent(false);
            using var registration = cancellation.Token.Register(() =>
            {
                changed.Set();
                lock (workerGate) Monitor.PulseAll(workerGate);
            });
            var failures = new ConcurrentQueue<Exception>();
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            long memoryBudget = MaxBufferedBytes;

            void Fail(Exception ex)
            {
                Log(ex, $"Render pipeline ({Thread.CurrentThread.Name})", this);
                failures.Enqueue(ex);
                cancellation.Cancel();
                changed.Set();
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
                cacheChanged = changed;
                workerFailure = Fail;
                running = true;
            }
            _renderTotalStopwatch.Restart();
            progressSamples.Enqueue((TimeSpan.Zero, 0));

            try
            {
                if (serialRendering)
                {
                    Log(DiagnosticRender || LogFrameTimings
                        ? "[Render] Diagnostic mode: one serial render thread, no frame lookahead. Timings include GPU completion; nested timings are inclusive and self excludes child stages. Frame indices are zero-based."
                        : "[Render] Serial mode: one render thread, clips rendered bottom to top, no frame lookahead.");
                }
                else
                {
                    Log($"[Render] Clip-owned workers, running {(AllowWorkerOversubscription ? "soft limit" : "limit")}={workerLimit}, one compositor, lookahead<={RenderAheadFrames} frames, " +
                        (EnableMemoryBudget ? $"estimated buffer budget={memoryBudget / 1024 / 1024} MiB." : "no memory budget."));
                    if (LogFrameTimings)
                        Log("[Render] Detailed frame timings enabled. Frame total runs from scheduling through output; worker sums include overlapping work. Timings include GPU completion.");
                }

                Log($"[Render] Effect implementations: {EffectHelper.GetImplementationSummary(Clips)}.");
                var compositor = StartRenderThread("Render compositor", [MethodImpl(MethodImplOptions.AggressiveInlining)] () =>
                {
                    try
                    {
                        if (serialRendering)
                        {
                            Interlocked.Increment(ref ThreadWorking);
                            try { RenderSerialFrames(cancellation.Token, cover); }
                            finally { Interlocked.Decrement(ref ThreadWorking); }
                        }
                        else RenderFrames(changed, cancellation.Token, cover, memoryBudget);
                    }
                    finally
                    {
                        completed.TrySetResult();
                    }
                }, cancellation.Token, Fail, serialRendering ? workerAffinity : null);
                await completed.Task.ConfigureAwait(false);
                compositor.Join();
            }
            finally
            {
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
                    cacheChanged = null;
                    renderCancellation = null;
                }
                workerFailure = null;
                _renderTotalStopwatch.Stop();
                ReleaseResources();
            }
            if (!failures.IsEmpty)
            {
                if (failures.Count == 1 && failures.TryDequeue(out var failure))
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                throw new AggregateException("Rendering failed.", failures);
            }
            if (token.IsCancellationRequested) throw new TaskCanceledException("Rendering was cancelled.", null, token);
            Log($"[Render] Finished {Finished} frames in {_renderTotalStopwatch.Elapsed}. {GetPipelineTimingInfo()}");
        }

        #endregion

        #region worker

        private Thread StartRenderThread(string name, Action action, CancellationToken token, Action<Exception> fail, int[]? affinityMask = null)
        {
            var thread = new Thread(() =>
            {
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
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex) { fail(ex); }
                finally
                {
                    IRenderContext.CurrentFrameBuffer = null;
                    IRenderContext.ClearWorkerState();
                    if (AutoSetupRenderContext) IRenderContext.Current = null;
                }
            })
            { Name = name, IsBackground = true };
            thread.Start();
            return thread;
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
                        EnsureClipDependencies(worker.Clip, entry.Request.Frame, token);
                        EnterWorker(worker, entry.Request.Frame, token);
                        try { ReadContent(entry); }
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
            using var diagnostic = RenderFrameDiagnostics.Current?.Measure($"Initialize clip {clip.Name} ({clip.Id})", "Initialize clip");
            clip.ReInit(_ppb);
            var effects = EffectHelper.GetClipEffectsInstances(clip)
                .Where(e => e.Enabled && e.TypeOfEffect.GetPipeline() == EffectPipeline.Picture);
            if (HasExplicitTargetRect(clip)) effects = effects.Where(e => !IsLegacyInternalLayoutEffect(e));
            EffectCache[clip.Id] = effects.ToArray();
            if (clip.AlternativeSource is ISourceReplacementEffect replacement)
                replacement.ProjectFrameRate = builder?.Writer.FramePerSecond ?? 30;
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
                            () => RenderClipJobs(worker, cancellation.Token), cancellation.Token, workerFailure!, workerAffinity);
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
            }
        }

        private void ExitWorker()
        {
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
                worker.Initialized.Wait(token);
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
        private ClipContent ProcessClip(IClip clip, IPicture frame, uint targetFrame, bool reusedPreview, CancellationToken token)
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
                        layoutRelativeWidth, layoutRelativeHeight, _ppb, out var transition, SDRClipsBrightnessInHDRMode,
                        AutoCenterImplicitClip, initializeClips: false, preparedSource: frame, argumentFrames: clipArgumentFrames, keepHwAccelPicture: true))
                    {
                        if (!ReferenceEquals(frame, transition)) frame.Dispose();
                        return new(clip, transition!, new(0, 0, TargetWidth, TargetHeight, false), true, IsCanvas: true);
                    }
                }
                ClipPositionTuple targetPos = new(
                    clip.TargetX,
                    clip.TargetY,
                    clip.TargetWidth > 0 ? ScaleDimensionToTarget(clip.TargetWidth, layoutRelativeWidth, TargetWidth) : TargetWidth,
                    clip.TargetHeight > 0 ? ScaleDimensionToTarget(clip.TargetHeight, layoutRelativeHeight, TargetHeight) : TargetHeight,
                    false, clip.Rotation);
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
                        using var effectTiming = RenderFrameDiagnostics.Current?.Measure($"Effect {item.Name} ({item.Id}, {item.GetType().Name})", $"Effect ({item.GetType().Name})");
                        if (item is DynamicEffectGraph graph)
                        {
                            var result = graph.Evaluate(frame, clip, targetFrame, TargetWidth, TargetHeight,
                                layoutRelativeWidth, layoutRelativeHeight, targetPos, preserveAspect, ProcessEffectFromCanvas, token, keepHwAccelPicture: true);
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
                                    if (pos.IsDelta)
                                    {
                                        targetPos = new ClipPositionTuple(
                                            targetPos.TargetX + pos.TargetX,
                                            targetPos.TargetY + pos.TargetY,
                                            targetPos.TargetWidth + pos.TargetWidth,
                                            targetPos.TargetHeight + pos.TargetHeight,
                                            false, targetPos.Rotation + pos.Rotation);
                                    }
                                    else
                                    {
                                        targetPos = pos;
                                    }
                                    continue;

                                case EffectType.ClipPositionProvider:
                                    if (item is not IClipPositionProvider p) goto notdefined;
                                    var pos1 = FrameHashContext.Compute(item, clip, targetFrame, null, TargetWidth, TargetHeight, layoutRelativeWidth, layoutRelativeHeight, () => p.GetPosition(clip, TargetWidth, TargetHeight));
                                    if (pos1.IsDelta)
                                    {
                                        targetPos = new ClipPositionTuple(
                                            targetPos.TargetX + pos1.TargetX,
                                            targetPos.TargetY + pos1.TargetY,
                                            targetPos.TargetWidth + pos1.TargetWidth,
                                            targetPos.TargetHeight + pos1.TargetHeight,
                                            false, targetPos.Rotation + pos1.Rotation);
                                    }
                                    else
                                    {
                                        targetPos = pos1;
                                    }
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
                            (var x, var y, var w, var h, bool delta, float rotation) = FrameHashContext.Compute(item, clip, targetFrame, null, TargetWidth, TargetHeight, layoutRelativeWidth, layoutRelativeHeight, () => p.GetPosition(clip, TargetWidth, TargetHeight));
                            if (delta)
                            {
                                targetPos = new ClipPositionTuple(targetPos.TargetX + x, targetPos.TargetY + y, targetPos.TargetWidth + w, targetPos.TargetHeight + h, false, targetPos.Rotation + rotation);
                            }
                            else
                            {
                                targetPos = new(x, y, w, h, false, rotation);
                            }
                        }
                        else if (item is IContinuousClipPositionProvider cp)
                        {
                            (var x, var y, var w, var h, bool delta, float rotation) = FrameHashContext.Compute(item, clip, targetFrame, null, TargetWidth, TargetHeight, layoutRelativeWidth, layoutRelativeHeight, () => cp.GetPosition(clip, targetFrame, TargetWidth, TargetHeight, layoutRelativeWidth, layoutRelativeHeight));
                            preserveAspect &= cp.PreserveAspectRatio;
                            if (delta)
                            {
                                targetPos = new ClipPositionTuple(targetPos.TargetX + x, targetPos.TargetY + y, targetPos.TargetWidth + w, targetPos.TargetHeight + h, false, targetPos.Rotation + rotation);
                            }
                            else
                            {
                                targetPos = new(x, y, w, h, false, rotation);
                            }
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

                if (UseHDR)
                {
                    using var hdrTiming = RenderFrameDiagnostics.Current?.Measure("HDR conversion");
                    if (frame is IHDRPicture<ushort> hdr)
                    {
                        if (!float.IsFinite(hdr.MaximumBrightness) || hdr.MaximumBrightness <= 0) hdr.MaximumBrightness = MaximumHDRBrightness;
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
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private IPicture Composite(IEnumerable<ClipContent?> clips, CancellationToken token)
        {
            using var diagnostic = RenderFrameDiagnostics.Current?.Measure("Compositing");
            var temporary = new HashSet<IPicture>(ReferenceEqualityComparer.Instance);
            IPicture? result = null, output = null;
            IPicture Track(IPicture picture) { temporary.Add(picture); return picture; }
            void Release(IPicture picture)
            {
                temporary.Remove(picture);
                picture.Dispose();
            }
            IPicture Replace(IPicture source, IPicture picture)
            {
                Track(picture);
                if (!ReferenceEquals(source, picture)) Release(source);
                return picture;
            }
            try
            {
                foreach (var clip in clips)
                {
                    token.ThrowIfCancellationRequested();
                    if (clip is null) continue;
                    using var clipTiming = RenderFrameDiagnostics.Current?.Measure($"Compose clip {clip.Clip.Name} ({clip.Clip.Id})", "Compose clip");
                    IPicture frame;
                    using (RenderFrameDiagnostics.Current?.Measure("Copy picture"))
                        frame = Track(DynamicEffectGraph.CopyPicture(clip.Picture));
                    int x = 0, y = 0;
                    if (!clip.IsCanvas)
                    {
                        using var layoutTiming = RenderFrameDiagnostics.Current?.Measure("Layout, resize and rotation");
                        var pos = clip.Position;
                        if (pos.TargetWidth > 0 && pos.TargetHeight > 0 && (frame.Width != pos.TargetWidth || frame.Height != pos.TargetHeight))
                            frame = Replace(frame, Resize(frame, pos.TargetWidth, pos.TargetHeight, clip.PreserveAspect));
                        x = ScaleCoordinateToTarget(pos.TargetX, ProjectRelativeWidth, TargetWidth);
                        y = ScaleCoordinateToTarget(pos.TargetY, ProjectRelativeHeight, TargetHeight);
                        if (AutoCenterImplicitClip && ShouldAutoCenterImplicitClip(clip.Clip) && y == 0 && frame.Height < TargetHeight)
                            y += (TargetHeight - frame.Height) / 2;
                        frame = Replace(frame, VideoClipRotation.Rotate(frame, pos.Rotation));
                        x += (int)Math.Round((pos.TargetWidth - frame.Width) / 2d);
                        y += (int)Math.Round((pos.TargetHeight - frame.Height) / 2d);
                    }
                    if (result is null && x == 0 && y == 0 && frame.Width == TargetWidth && frame.Height == TargetHeight)
                        result = frame;
                    else
                    {
                        using var blendTiming = RenderFrameDiagnostics.Current?.Measure($"Blend ({(clip.Clip.MixtureInstance ?? ClassicOverlayMixture.Default).GetType().Name})");
                        var mixed = Track(EffectPictureProcessing.Mix(clip.Clip.MixtureInstance ?? ClassicOverlayMixture.Default,
                            result ?? BlankFrame, frame, _ppb, x, y, TargetWidth, TargetHeight));
                        if (result is not null && !ReferenceEquals(result, mixed)) Release(result);
                        if (!ReferenceEquals(frame, mixed)) Release(frame);
                        result = mixed;
                    }
                }
                if (result is not null && (result.Width != TargetWidth || result.Height != TargetHeight))
                {
                    using var resizeTiming = RenderFrameDiagnostics.Current?.Measure("Resize output");
                    result = Replace(result, Resize(result, TargetWidth, TargetHeight, false));
                }
                using (RenderFrameDiagnostics.Current?.Measure("Materialize output / GPU readback"))
                    output = EffectPictureProcessing.Materialize(result ?? EffectPictureProcessing.Copy(BlankFrame));
                return output;
            }
            finally
            {
                foreach (var frame in temporary)
                    if (!ReferenceEquals(frame, output)) frame.Dispose();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void WriteFrame(uint index, IPicture frame, Stopwatch sw)
        {
            bool written = false;
            try
            {
                if (LogProcessStack)
                {
                    using var stackTiming = RenderFrameDiagnostics.Current?.Measure("Collect process stack");
                    FrameProcessStacks[index] = frame.ProcessStack.ToList();
                    FrameDirtyTime[index] = sw.Elapsed - TimeSpan.FromTicks(frame.ProcessStack.Where(c => c.Elapsed is not null).Sum(c => c.Elapsed!.Value.Ticks));
                }
                if (builder is not null)
                {
                    IRenderContext.SetWorkerState(index, RenderWorkerStage.WritingOutput, "Render compositor");
                    long writeStarted = Stopwatch.GetTimestamp();
                    try
                    {
                        IPicture? previewFrame;
                        using (RenderFrameDiagnostics.Current?.Measure("Capture preview"))
                            previewFrame = builder.CapturePreviewFrame(frame);
                        using var preview = previewFrame;
                        using (RenderFrameDiagnostics.Current?.Measure("Encode/write output"))
                            builder.Writer.Append(frame);
                        written = true;
                        using (RenderFrameDiagnostics.Current?.Measure("Publish preview"))
                            builder.NotifyFrameWritten(index, preview ?? frame);
                    }
                    finally { Interlocked.Add(ref writeTicks, Stopwatch.GetElapsedTime(writeStarted).Ticks); }
                }
                Interlocked.Increment(ref Finished);
                FrameRenderElapsed[index] = sw.Elapsed;
                TrackRenderElapsed(sw.Elapsed);
                using (RenderFrameDiagnostics.Current?.Measure("Progress notification"))
                    InvokeProgress();
                if (LogRenderState) Log($"[Render] Frame {index} written in {sw.Elapsed}.");
            }
            finally
            {
                using var cleanupTiming = RenderFrameDiagnostics.Current?.Measure("Dispose output and GC");
                if (!written || builder?.DisposeFrameAfterEachWrite != false) frame.Dispose();
                if (GCOption == 2) GC.Collect(2, GCCollectionMode.Forced, true, true);
                else if (GCOption == 1 && builder?.DoGCAfterEachWrite != true) GC.Collect();
            }
        }

        #endregion

        #region helper

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private void RenderFrames(AutoResetEvent changed, CancellationToken token, IPicture? cover, long memoryBudget)
        {
            var pending = new Dictionary<uint, PendingRenderFrame>();
            ClipRequest[]? nextRequests = null;
            ulong scheduled = StartFrame, end = (ulong)StartFrame + Duration;
            var lastLog = Stopwatch.StartNew();
            try
            {
                CompleteClipJobs(scheduled);
                if (cover is not null) builder?.Writer.Append(cover);
                for (ulong i = StartFrame; i < end; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var sw = Stopwatch.StartNew();
                    uint frame = (uint)i;
                    lock (workerGate)
                    {
                        currentRenderFrame = frame;
                        Monitor.PulseAll(workerGate);
                    }
                    while (scheduled < end && scheduled < i + (ulong)RenderAheadFrames)
                    {
                        nextRequests ??= GetFrameRequests((uint)scheduled);
                        long frameBytes = Math.Max(1, nextRequests.Sum(r => EstimateClipBytes(r.Clip)));
                        if (EnableMemoryBudget && pending.Count > 0 && bufferedBytes > memoryBudget - frameBytes) break;
                        var diagnostic = LogFrameTimings ? new RenderFrameDiagnostics((uint)scheduled) : null;
                        using (diagnostic?.Enter())
                        using (diagnostic?.Measure("Schedule frame"))
                        {
                            var context = new FrameHashContext(HashOptions, token, nodeCache, gpuSession) { Diagnostics = diagnostic };
                            pendingHashContexts[(uint)scheduled] = context;
                            var entries = nextRequests.Select(r => AcquireFrame(r, token, context)).ToArray();
                            pending.Add((uint)scheduled, new(entries, frameBytes, context));
                            Interlocked.Add(ref bufferedBytes, frameBytes);
                            diagnostic?.BeginWorkers();
                            QueueClipJobs(entries, token);
                        }
                        nextRequests = null;
                        scheduled++;
                        CompleteClipJobs(scheduled);
                    }

                    var current = pending[frame];
                    using var diagnosticScope = current.Context.Diagnostics?.Enter();
                    IPicture? picture = null;
                    try
                    {
                        picture = CompositeFrame(frame, current, changed, token);
                        using (current.Context.Diagnostics?.Measure("Release frame dependencies"))
                        {
                            foreach (var entry in current.Entries) ReleaseFrame(entry);
                            pending.Remove(frame);
                            Interlocked.Add(ref bufferedBytes, -current.EstimatedBytes);
                            current.Context.Dispose();
                            pendingHashContexts.TryRemove(frame, out _);
                            clipArgumentFrames.RemoveFrame(frame);
                        }
                        using (current.Context.Diagnostics?.Measure("Write frame"))
                        {
                            var output = picture;
                            picture = null;
                            WriteFrame(frame, output, sw);
                        }
                    }
                    finally
                    {
                        using (current.Context.Diagnostics?.Measure("Frame cleanup"))
                        {
                            picture?.Dispose();
                            IRenderContext.CurrentFrameBuffer = null;
                            IRenderContext.ClearWorkerState();
                        }
                    }
                    if (current.Context.Diagnostics is { } completed)
                    {
                        completed.Succeeded = true;
                        completed.Dispose();
                    }

                    if (LogStaticsData && lastLog.Elapsed.TotalSeconds >= 10)
                    {
                        Log(GetRendererStatusInfo(), "STAT");
                        lastLog.Restart();
                    }
                }
            }
            finally
            {
                foreach (var frame in pending.Values)
                    foreach (var entry in frame.Entries) ReleaseFrame(entry);
                Interlocked.Exchange(ref bufferedBytes, 0);
            }
        }

        private void RenderSerialFrames(CancellationToken token, IPicture? cover)
        {
            if (cover is not null && builder is not null)
            {
                var sw = Stopwatch.StartNew();
                builder.Writer.Append(cover);
                if (DiagnosticRender || LogFrameTimings) Log($"[Render diagnostic] Cover write: {sw.Elapsed.TotalMilliseconds:F3} ms", "diag");
            }
            for (ulong i = StartFrame; i < (ulong)StartFrame + Duration; i++)
            {
                token.ThrowIfCancellationRequested();
                lock (serialGate) RenderSerialFrame((uint)i, token);
            }
        }

        private void RenderSerialFrame(uint frame, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var diagnostic = DiagnosticRender || LogFrameTimings ? new RenderFrameDiagnostics(frame) : null;
            using var diagnosticScope = diagnostic?.Enter();
            var sw = Stopwatch.StartNew();
            var entries = new List<CachedClipFrame>();
            FrameHashContext? context = null;
            IPicture? picture = null;
            try
            {
                ClipRequest[] requests;
                using (diagnostic?.Measure("Schedule frame"))
                {
                    requests = GetFrameRequests(frame);
                    context = new FrameHashContext(HashOptions, token, nodeCache, gpuSession);
                }
                using (context.Enter())
                {
                    using (diagnostic?.Measure("Prepare clips serially"))
                    {
                        foreach (var request in requests)
                        {
                            token.ThrowIfCancellationRequested();
                            var entry = AcquireFrame(request, token, context);
                            entries.Add(entry);
                            ReadContent(entry);
                        }
                    }
                    var prepareTime = TimeSpan.FromTicks(entries.Sum(e => e.Elapsed.Ticks));
                    FramePrepareElapsed[frame] = prepareTime;
                    TrackPrepareElapsed(prepareTime);
                    IRenderContext.SetWorkerState(frame, RenderWorkerStage.Compositing, Thread.CurrentThread.Name);
                    long started = Stopwatch.GetTimestamp();
                    try { picture = Composite(entries.Select(e => e.Content.Value), token); }
                    finally { Interlocked.Add(ref compositeTicks, Stopwatch.GetElapsedTime(started).Ticks); }
                }
                using (diagnostic?.Measure("Release frame dependencies"))
                {
                    foreach (var entry in entries) ReleaseFrame(entry);
                    entries.Clear();
                    context.Dispose();
                    context = null;
                    clipArgumentFrames.RemoveFrame(frame);
                }
                using (diagnostic?.Measure("Write frame"))
                {
                    var output = picture!;
                    picture = null;
                    WriteFrame(frame, output, sw);
                }
                if (diagnostic is not null) diagnostic.Succeeded = true;
            }
            finally
            {
                using (diagnostic?.Measure("Frame cleanup"))
                {
                    picture?.Dispose();
                    foreach (var entry in entries) ReleaseFrame(entry);
                    context?.Dispose();
                    clipArgumentFrames.RemoveFrame(frame);
                    IRenderContext.CurrentFrameBuffer = null;
                    IRenderContext.ClearWorkerState();
                }
            }
        }

        private void QueueClipJobs(CachedClipFrame[] entries, CancellationToken token)
        {
            foreach (var entry in entries)
            {
                if (Interlocked.CompareExchange(ref entry.Queued, 1, 0) != 0) continue;
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

        private IPicture CompositeFrame(uint frame, PendingRenderFrame pending, AutoResetEvent changed, CancellationToken token)
        {
            IRenderContext.SetWorkerState(frame, RenderWorkerStage.Compositing, "Render compositor");
            long waitStarted = Stopwatch.GetTimestamp();
            using (RenderFrameDiagnostics.Current?.Measure("Wait for clip workers"))
            {
                while (pending.Entries.Any(e => Volatile.Read(ref e.Ready) == 0))
                {
                    token.ThrowIfCancellationRequested();
                    changed.WaitOne(10);
                }
            }
            Interlocked.Add(ref waitTicks, Stopwatch.GetElapsedTime(waitStarted).Ticks);
            token.ThrowIfCancellationRequested();
            pending.Context.Diagnostics?.CompleteWorkers();

            var prepareTime = TimeSpan.FromTicks(pending.Entries.Sum(e => e.Elapsed.Ticks));
            FramePrepareElapsed[frame] = prepareTime;
            TrackPrepareElapsed(prepareTime);

            long compositeStarted = Stopwatch.GetTimestamp();
            IPicture picture;
            using (pending.Context.Enter()) picture = Composite(pending.Entries.Select(e => e.Content.Value), token);
            Interlocked.Add(ref compositeTicks, Stopwatch.GetElapsedTime(compositeStarted).Ticks);
            return picture;
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
                        return RenderClip(request.Clip, request.Frame, token);
                    });
                    frameCache.Add(request.Key, entry);
                }
                entry.References++;
                return entry;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private ClipContent? ReadContent(CachedClipFrame entry)
        {
            var content = entry.Content.Value;
            Volatile.Write(ref entry.Ready, 1);
            lock (cacheGate) cacheChanged?.Set();
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
            using var frameContext = new FrameHashContext(HashOptions with
            {
                Mode = FrameHashContext.Current?.Options.Mode ?? HashOptions.Mode,
            }, token, nodeCache, gpuSession);
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
                return Composite(entries.Select(e => e.Content.Value), token);
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

        private ClipContent? RenderClip(IClip clip, uint frameIndex, CancellationToken token)
        {
            using var diagnostic = RenderFrameDiagnostics.Current?.Measure($"Clip {clip.Name} ({clip.Id}), frame {frameIndex}", "Clip");
            EnsureClipDependencies(clip, frameIndex, token);
            if (FrameHashContext.Current is { } context)
                return context.Evaluate((typeof(Renderer), clip.Id, frameIndex), () => RenderClipCore(clip, frameIndex, token),
                    c => c is null ? null : c with { Picture = DynamicEffectGraph.CopyPicture(c.Picture) }, c => c?.Picture.Dispose());
            return RenderClipCore(clip, frameIndex, token);
        }

        private IPicture ReadMaskBackdrop(IClip mask, uint frame, CancellationToken token)
        {
            var lower = new List<ClipContent?>();
            try
            {
                foreach (var clip in Clips.Where(c => (c.ContainsFrame(frame) || c.ExtendToWholeDraft && c.LayerIndex > SubTrackOffset)
                    && ClipLayerOrder.BottomToTop.Compare(c, mask) < 0).OrderBy(c => c, ClipLayerOrder.BottomToTop))
                    lower.Add(RenderClip(clip, frame, token));
                return Composite(lower, token);
            }
            finally { foreach (var item in lower) item?.Picture.Dispose(); }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private ClipContent? RenderClipCore(IClip clip, uint frameIndex, CancellationToken token)
        {
            using var diagnostic = RenderFrameDiagnostics.Current?.Measure("Clip hash/cache and setup");
            ClipContent? rendered = null;
            bool evaluated = false;
            try
            {
                var identity = FrameHashHelper.GetClipHash(Clips, clip, frameIndex, HashOptions with
                {
                    Mode = FrameHashContext.Current?.Options.Mode ?? HashOptions.Mode,
                    Stage = "render-clip",
                }, () =>
                {
                    evaluated = true;
                    rendered = RenderClipUncached(clip, frameIndex, token);
                    return rendered is null ? null : new { rendered.Picture, rendered.Position, rendered.PreserveAspect, rendered.IsCanvas };
                }, token);
                string key = "clip/" + identity.Hash;
                if (identity.CanReuse && nodeCache.TryRead(key, out var saved)
                    && saved is IReadOnlyDictionary<string, object?> values)
                {
                    rendered?.Picture.Dispose();
                    rendered = null;
                    return new((IClip)values["Clip"]!, (IPicture)values["Picture"]!,
                        (ClipPositionTuple)values["Position"]!, (bool)values["PreserveAspect"]!, (bool)values["IsCanvas"]!);
                }
                if (!evaluated) rendered = RenderClipUncached(clip, frameIndex, token);
                if (identity.CanReuse && rendered is not null && !ClipInitializationFailure.IsMarked(clip))
                    nodeCache.Write(key, new Dictionary<string, object?>
                    {
                        ["Clip"] = rendered.Clip,
                        ["Picture"] = rendered.Picture,
                        ["Position"] = rendered.Position,
                        ["PreserveAspect"] = rendered.PreserveAspect,
                        ["IsCanvas"] = rendered.IsCanvas,
                    });
                var result = rendered;
                rendered = null;
                return result;
            }
            finally { rendered?.Picture.Dispose(); }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private ClipContent? RenderClipUncached(IClip clip, uint frameIndex, CancellationToken token)
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
            IRenderContext.SetWorkerState(frameIndex, RenderWorkerStage.PreparingSource, Thread.CurrentThread.Name, clip);
            long sourceStarted = Stopwatch.GetTimestamp();
            IPicture frame = null!;
            bool reusedPreview;
            try
            {
                using var diagnostic = RenderFrameDiagnostics.Current?.Measure("Source read/decode, preview cache and resize");
                reusedPreview = TryLoadDynamicPreviewFrame(clip, frameIndex, _ppb, out frame);
                if (!reusedPreview)
                {
                    int width = ResolveClipOutputWidth(clip, TargetWidth, ProjectRelativeWidth);
                    int height = ResolveClipOutputHeight(clip, TargetHeight, ProjectRelativeHeight);
                    frame = ClipInitializationFailure.IsMarked(clip)
                        ? ClipInitializationFailure.CreateFallbackFrame(width, height, _ppb, clip.ExtraData)
                        : VideoClipRotation.ReadFrame(clip, clip.GetRelativeFrameIndex(frameIndex) ?? clip.Duration, width, height, _ppb);
                    if (!frame.CanBeDisposed) frame = DynamicEffectGraph.CopyPicture(frame);
                    if (frame.Width != width || frame.Height != height)
                        frame = ReplaceFrame(frame, Resize(frame, width, height, true));
                    if (IsClipGeneratedByAI.GetValueOrDefault(clip.Id))
                        frame = ReplaceFrame(frame, EffectProcessing.ProcessAIWatermark(frame, frameIndex));
                }
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
            return ProcessClip(clip, frame, frameIndex, reusedPreview, token);
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

            var directory = Path.Combine(DynamicPreviewCacheProjectRoot, "thumbs", "perClip", clip.Id.ToString(), "dynamic");
            if (!Directory.Exists(directory)) return false;

            var hashClip = _dynamicPreviewHashClips.FirstOrDefault(c => c.Id == clip.Id) ?? clip;
            var identity = FrameHashHelper.GetClipHash(_dynamicPreviewHashClips, hashClip, frameIndex,
                HashOptions with { AutoCenter = false });
            if (!identity.CanReuse) return false;
            var hash = identity.Hash;
            var format = ppb == IPicture.PicturePixelMode.UShortPicture ? "vfd16" : "vfd8";
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
            Mode = FrameHashMode,
            Width = TargetWidth,
            Height = TargetHeight,
            ProjectWidth = ProjectRelativeWidth,
            ProjectHeight = ProjectRelativeHeight,
            FrameRate = builder?.Writer.FramePerSecond ?? 30,
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

        private static int ScaleCoordinateToTarget(int value, int relativeValue, int targetValue)
        {
            if (value == 0)
            {
                return 0;
            }

            if (relativeValue > 0 && targetValue > 0 && relativeValue != targetValue)
            {
                return (int)Math.Round((double)value * targetValue / relativeValue, MidpointRounding.AwayFromZero);
            }

            return value;
        }

        private static bool ShouldAutoCenterImplicitClip(IClip clip)
        {
            if (HasExplicitTargetRect(clip))
            {
                return false;
            }

            return !HasLegacyInternalPlaceResizeEffects(clip);
        }

        private static bool HasExplicitTargetRect(IClip clip)
            => clip.TargetX != 0 || clip.TargetY != 0 || clip.TargetWidth > 0 || clip.TargetHeight > 0;

        private static bool HasLegacyInternalPlaceResizeEffects(IClip clip)
        {
            if (clip.Effects is null || clip.Effects.Length == 0)
            {
                return false;
            }

            return clip.Effects.Any(effect => effect is not null
                && (string.Equals(effect.Name, "__Internal_Place__", StringComparison.Ordinal)
                    || string.Equals(effect.Name, "__Internal_Resize__", StringComparison.Ordinal)
                    || (string.IsNullOrWhiteSpace(effect.Name)
                        && (string.Equals(effect.TypeName, "Place", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(effect.TypeName, "Resize", StringComparison.OrdinalIgnoreCase)))));
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

        #region misc

        private void InvokeProgress()
        {
            int finished = Volatile.Read(ref Finished);
            double prog = (double)finished / Duration;
            var elapsed = _renderTotalStopwatch.Elapsed;
            while (progressSamples.Count > 1 && elapsed - progressSamples.Peek().Elapsed > TimeSpan.FromSeconds(30))
                progressSamples.Dequeue();
            var sample = progressSamples.Peek();
            double seconds = (elapsed - sample.Elapsed).TotalSeconds;
            double fps = seconds > 0 ? (finished - sample.Finished) / seconds : 0;
            if (elapsed - lastProgressSample >= TimeSpan.FromSeconds(1))
            {
                progressSamples.Enqueue((elapsed, finished));
                lastProgressSample = elapsed;
            }
            Interlocked.Exchange(ref _currentFps, fps);
            var eta = GetEstimatedRemaining(Duration - (double)finished, fps);
            OnProgressChanged?.Invoke(prog, eta);
            uint projectFrames = TotalProjectFrames > 0 ? TotalProjectFrames : Duration;
            double globalProgress = projectFrames > 0
                ? Math.Clamp((CompletedFramesBeforeChunk + (double)finished) / projectFrames, 0, 1)
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
                $"cached clips: {cached}, estimated buffers: {Interlocked.Read(ref bufferedBytes) / 1024 / 1024} MiB, " +
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

    public readonly record struct ChunkRenderProgress(
        int ChunkIndex,
        int ChunkCount,
        int FinishedFrames,
        uint ChunkFrames,
        double ChunkProgress,
        double GlobalProgress,
        TimeSpan EstimatedRemaining);
}
