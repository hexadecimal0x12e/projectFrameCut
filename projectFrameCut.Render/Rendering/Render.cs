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
        public bool AutoCenterImplicitClip { get; set; } = false;

        public bool EnableThreadAffinity { get; set; } = false;
        public int[]? WorkerCPUCoreIndexs { get; set; }
        public int MaxThreads { get => field > 0 ? field : (int)(Environment.ProcessorCount * 1.75); set; }
        public int GCOption = 0;

        public bool EnableEffectAutoRetry { get; set; } = true;
        public bool ProcessEffectFromCanvas { get; set; } = true;
        public bool ReuseDynamicPreviewCache { get; set; } = false;
        public string? DynamicPreviewCacheProjectRoot { get; set; }
        public int DynamicPreviewCacheProjectWidth { get; set; }
        public int DynamicPreviewCacheProjectHeight { get; set; }

        public int RenderAheadFrames { get => field > 0 ? field : Math.Max(2, MaxThreads); set; }

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
        private readonly Dictionary<ClipCacheKey, CachedClipFrame> frameCache = new();
        private readonly Dictionary<uint, ClipRequest[]> frameRequests = new();
        private readonly Dictionary<Guid, IEffect[]> EffectCache = new();
        private readonly ClipArgumentFrameCache clipArgumentFrames = new();
        private readonly EffectRuntimeContext effectRuntime = new();
        private IClip[] _dynamicPreviewHashClips = [];
        private readonly ConcurrentDictionary<Guid, Dictionary<string, List<(string Path, int ProjectWidth, int ProjectHeight, int CanvasWidth, int CanvasHeight)>>> _dynamicPreviewFiles = new();
        private CancellationTokenSource? renderCancellation;
        private AutoResetEvent? cacheChanged;
        private IPicture BlankFrame = null!;
        private bool prepared, running, released;
        private int readers, ThreadWorking, Finished, _ppb;
        private long _renderElapsedTicksTotal, _prepareElapsedTicksTotal;
        private int _renderElapsedCount, _prepareElapsedCount;

        private readonly record struct ClipCacheKey(Guid Clip, string Hash, int Width, int Height, int Bpp, bool Hdr, int SdrBrightness, int PeakBrightness);
        private sealed record ClipRequest(IClip Clip, uint Frame, ClipCacheKey Key);
        private sealed record ClipContent(IClip Clip, IPicture Picture, ClipPositionTuple Position, bool PreserveAspect, bool IsCanvas = false);
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
                    clip.ReInit(_ppb);
                    var effectsForClip = EffectHelper.GetClipEffectsInstances(clip)
                        .Where(e => e.Enabled && e.TypeOfEffect.GetPipeline() == EffectPipeline.Picture);
                    if (HasExplicitTargetRect(clip)) effectsForClip = effectsForClip.Where(e => !IsLegacyInternalLayoutEffect(e));
                    EffectCache[clip.Id] = effectsForClip.ToArray();
                    if (clip.AlternativeSource is ISourceReplacementEffect replacement)
                        replacement.ProjectFrameRate = builder?.Writer.FramePerSecond ?? 30;
                    PerClipHDRBrightness[clip.Id] = clip.ExtraData.TryGetValue("HDRBrightness", out var brightness)
                        ? Convert.ToInt32(brightness) : SDRClipsBrightnessInHDRMode;
                    IsClipGeneratedByAI[clip.Id] = clip.ExtraData.TryGetValue("IsAI", out var ai) &&
                        (ai is bool b ? b : bool.TryParse(ai?.ToString(), out var value) && value);
                }
                BlankFrame = UseHDR ? HDRPicture16bpp.GenerateSolidColor(TargetWidth, TargetHeight, 0, 0, 0, 0, 0)
                    : Use16Bit ? Picture16bpp.GenerateSolidColor(TargetWidth, TargetHeight, 0, 0, 0, 0)
                    : Picture8bpp.GenerateSolidColor(TargetWidth, TargetHeight, 0, 0, 0, 0);
                BlankFrame.CanBeDisposed = false;
                for (ulong i = StartFrame; i < (ulong)StartFrame + Duration; i++)
                {
                    token.ThrowIfCancellationRequested();
                    uint frame = (uint)i;
                    frameRequests[frame] = Clips.Where(c => c.ContainsFrame(frame) || c.ExtendToWholeDraft && c.LayerIndex > SubTrackOffset)
                        .OrderBy(c => c, ClipLayerOrder.BottomToTop).Select(c => CreateRequest(c, frame)).ToArray();
                }
                prepared = true;
            }
            catch
            {
                released = true;
                ReleaseResources();
                throw;
            }
        }

        private ClipRequest CreateRequest(IClip clip, uint frame)
        {
            string hash = Timeline.CanCacheClipFrame(Clips, clip) ? Timeline.GetClipFrameHash(_dynamicPreviewHashClips, clip, frame) : "__error__";
            if (hash == "__error__") hash = Guid.NewGuid().ToString("N");
            return new(clip, frame, new(clip.Id, hash, TargetWidth, TargetHeight, _ppb, UseHDR,
                SDRClipsBrightnessInHDRMode, MaximumHDRBrightness));
        }

        public async Task GoRender(CancellationToken token, IPicture? cover = null)
        {
            lock (cacheGate)
            {
                ObjectDisposedException.ThrowIf(released, this);
                if (!prepared || running) throw new InvalidOperationException("Call PrepareRender before starting the renderer.");
                running = true;
            }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var changed = new AutoResetEvent(false);
            using var jobs = new BlockingCollection<CachedClipFrame>();
            using var registration = cancellation.Token.Register(() => changed.Set());
            var failures = new ConcurrentQueue<Exception>();
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var workers = new List<Thread>();
            var affinity = ResolveWorkerThreadAffinityTargetCores();
            renderCancellation = cancellation;
            cacheChanged = changed;
            _renderTotalStopwatch.Restart();
            Log($"[Render] Starting {MaxThreads} clip workers and one compositor, lookahead={RenderAheadFrames} frames.");

            void Fail(Exception ex)
            {
                Log(ex, "Render pipeline", this);
                failures.Enqueue(ex);
                cancellation.Cancel();
                changed.Set();
            }

            Thread StartThread(string name, Action action, bool worker = false)
            {
                var thread = new Thread(() =>
                {
                    using var effects = effectRuntime.Enter();
                    IRenderContext.ClearWorkerState();
                    IRenderContext.CurrentFrameBuffer = null;
                    if (AutoSetupRenderContext) IRenderContext.Current = this;
                    try
                    {
                        if (worker && affinity.Mask is { Length: > 0 })
                        {
                            try { ThreadAffinityHelper.SetCurrentThreadAffinity(affinity.Mask); }
                            catch (Exception ex) { Log(ex, "Set render worker affinity", this); }
                        }
                        action();
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                    catch (Exception ex) { Fail(ex); }
                    finally
                    {
                        IRenderContext.CurrentFrameBuffer = null;
                        IRenderContext.ClearWorkerState();
                        if (AutoSetupRenderContext) IRenderContext.Current = null;
                    }
                }) { Name = name, IsBackground = true };
                thread.Start();
                return thread;
            }

            try
            {
                for (int i = 0; i < MaxThreads; i++)
                    workers.Add(StartThread($"Clip render worker {i + 1}", () =>
                    {
                        foreach (var entry in jobs.GetConsumingEnumerable(cancellation.Token))
                        {
                            Interlocked.Increment(ref ThreadWorking);
                            try { ReadContent(entry); }
                            finally
                            {
                                IRenderContext.CurrentFrameBuffer = null;
                                IRenderContext.ClearWorkerState();
                                Interlocked.Decrement(ref ThreadWorking);
                                ReleaseFrame(entry);
                            }
                        }
                    }, worker: true));

                var compositor = StartThread("Render compositor", () =>
                {
                    var pending = new Dictionary<uint, CachedClipFrame[]>();
                    ulong scheduled = StartFrame, end = (ulong)StartFrame + Duration;
                    var lastLog = Stopwatch.StartNew();
                    try
                    {
                        if (cover is not null) builder?.Writer.Append(cover);
                        for (ulong i = StartFrame; i < end; i++)
                        {
                            cancellation.Token.ThrowIfCancellationRequested();
                            var sw = Stopwatch.StartNew();
                            uint frame = (uint)i;
                            while (scheduled < end && scheduled < i + (ulong)RenderAheadFrames)
                            {
                                var entries = frameRequests[(uint)scheduled].Select(r => AcquireFrame(r, cancellation.Token)).ToArray();
                                pending.Add((uint)scheduled, entries);
                                foreach (var entry in entries)
                                {
                                    if (Interlocked.CompareExchange(ref entry.Queued, 1, 0) != 0) continue;
                                    lock (cacheGate) entry.References++;
                                    try { jobs.Add(entry, cancellation.Token); }
                                    catch { ReleaseFrame(entry); throw; }
                                }
                                scheduled++;
                            }
                            var current = pending[frame];
                            IRenderContext.SetWorkerState(frame, RenderWorkerStage.Compositing, "Render compositor");
                            while (current.Any(e => Volatile.Read(ref e.Ready) == 0))
                            {
                                cancellation.Token.ThrowIfCancellationRequested();
                                changed.WaitOne(10);
                            }
                            cancellation.Token.ThrowIfCancellationRequested();
                            var prepareTime = TimeSpan.FromTicks(current.Sum(e => e.Elapsed.Ticks));
                            FramePrepareElapsed[frame] = prepareTime;
                            TrackPrepareElapsed(prepareTime);
                            WriteFrame(frame, Composite(current.Select(e => e.Content.Value), cancellation.Token), sw);
                            foreach (var entry in current) ReleaseFrame(entry);
                            pending.Remove(frame);
                            clipArgumentFrames.RemoveFrame(frame);
                            if (LogStaticsData && lastLog.Elapsed.TotalSeconds >= 10)
                            {
                                Log(GetRendererStatusInfo(), "STAT");
                                lastLog.Restart();
                            }
                        }
                    }
                    finally
                    {
                        foreach (var entries in pending.Values)
                            foreach (var entry in entries) ReleaseFrame(entry);
                        completed.TrySetResult();
                    }
                });
                await completed.Task.ConfigureAwait(false);
                compositor.Join();
            }
            finally
            {
                cancellation.Cancel();
                jobs.CompleteAdding();
                foreach (var worker in workers) worker.Join();
                while (jobs.TryTake(out var entry)) ReleaseFrame(entry);
                lock (cacheGate)
                {
                    running = false;
                    released = true;
                    while (readers > 0) Monitor.Wait(cacheGate);
                    cacheChanged = null;
                    renderCancellation = null;
                }
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
            Log($"[Render] Finished {Finished} frames in {_renderTotalStopwatch.Elapsed}.");
        }

        private CachedClipFrame AcquireFrame(ClipRequest request, CancellationToken token)
        {
            lock (cacheGate)
            {
                if (!frameCache.TryGetValue(request.Key, out var entry))
                {
                    entry = new(request, () => RenderClip(request.Clip, request.Frame, token));
                    frameCache.Add(request.Key, entry);
                }
                entry.References++;
                return entry;
            }
        }

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
            if (frameIndex < StartFrame || (ulong)frameIndex >= (ulong)StartFrame + Duration) return null;
            lock (cacheGate)
            {
                ObjectDisposedException.ThrowIf(released, this);
                if (!prepared || !frameRequests.TryGetValue(frameIndex, out _)) return null;
                readers++;
            }
            var entries = new List<CachedClipFrame>();
            using var effects = effectRuntime.Enter();
            var previousWorker = IRenderContext.WorkerState;
            var previousBuffer = IRenderContext.CurrentFrameBuffer;
            var previousContext = IRenderContext.Current;
            IRenderContext.ClearWorkerState();
            if (AutoSetupRenderContext) IRenderContext.Current = this;
            try
            {
                foreach (var request in frameRequests[frameIndex])
                {
                    token.ThrowIfCancellationRequested();
                    var entry = AcquireFrame(Timeline.CanCacheClipFrame(Clips, request.Clip) ? request : CreateRequest(request.Clip, frameIndex),
                        renderCancellation?.Token ?? token);
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
            token.ThrowIfCancellationRequested();
            using var masks = MaskFrameContext.Open(Clips, frameIndex, TargetWidth, TargetHeight,
                ProjectRelativeWidth, ProjectRelativeHeight, _ppb, token, forceNew: true);
            using var endpoints = ClipValuePassThrough.Open(Clips, frameIndex, TargetWidth, TargetHeight,
                ProjectRelativeWidth, ProjectRelativeHeight, _ppb, token);
            using var endpoint = ClipValueFrameContext.Current?.EnterClip(clip.Id, independent: true);
            clip = clipArgumentFrames.Get(clip, frameIndex);
            if (!ClipArgumentBinding.ShouldRender(clip)) return null;
            using var context = ValueProviderFrameContext.PushFrame(frameIndex,
                Math.Clamp(((float)frameIndex - clip.StartFrame) / Math.Max(1u, clip.GetEffectiveDuration()), 0, 1), false, token);
            IRenderContext.SetWorkerState(frameIndex, RenderWorkerStage.PreparingSource, Thread.CurrentThread.Name, clip);
            bool reusedPreview = TryLoadDynamicPreviewFrame(clip, frameIndex, _ppb, out var frame);
            if (!reusedPreview)
            {
                try
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
                catch
                {
                    frame?.Dispose();
                    throw;
                }
            }
            return ProcessClip(clip, frame, frameIndex, reusedPreview, token);
        }

        private ClipContent ProcessClip(IClip clip, IPicture frame, uint targetFrame, bool reusedPreview, CancellationToken token)
        {
            int layoutRelativeWidth = ProjectRelativeWidth, layoutRelativeHeight = ProjectRelativeHeight;
            try
            {
                IRenderContext.SetWorkerState(targetFrame, RenderWorkerStage.ProcessingEffects, Thread.CurrentThread.Name, clip);
                if (!reusedPreview && TransformProcessing.TryRender(clip, Clips, targetFrame, TargetWidth, TargetHeight,
                    layoutRelativeWidth, layoutRelativeHeight, _ppb, out var transition, SDRClipsBrightnessInHDRMode,
                    AutoCenterImplicitClip, initializeClips: false, preparedSource: frame, argumentFrames: clipArgumentFrames))
                {
                    if (!ReferenceEquals(frame, transition)) frame.Dispose();
                    return new(clip, transition!, new(0, 0, TargetWidth, TargetHeight, false), true, IsCanvas: true);
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
                        if (item is DynamicEffectGraph graph)
                        {
                            var result = graph.Evaluate(frame, clip, targetFrame, TargetWidth, TargetHeight,
                                layoutRelativeWidth, layoutRelativeHeight, targetPos, preserveAspect, ProcessEffectFromCanvas, token);
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
                                    frame = ReplaceFrame(frame, e.Render(frame, TargetWidth, TargetHeight));
                                    continue;
                                case EffectType.ContinuousEffect:
                                    if (item is not IContinuousEffect c) goto notdefined;
                                    int scopedStart = c.IsScoped ? c.StartPoint : (int)clip.StartFrame;
                                    int scopedEnd = c.IsScoped ? c.EndPoint : (int)(clip.StartFrame + clip.GetEffectiveDuration());
                                    if (scopedEnd <= scopedStart || targetFrame < scopedStart || targetFrame >= scopedEnd) continue;
                                    float continuousProgress = Math.Clamp((float)(targetFrame - scopedStart) / (scopedEnd - scopedStart), 0f, 1f);
                                    frame = ResizeForEffectIfNeeded(frame, item, targetPos.TargetWidth, targetPos.TargetHeight, preserveAspect);
                                    frame = ReplaceFrame(frame, c.Render(frame, continuousProgress, TargetWidth, TargetHeight));
                                    continue;
                                case EffectType.ContinuousClipPositionProvider:
                                    if (item is not IContinuousClipPositionProvider cp) goto notdefined;
                                    var pos = cp.GetPosition(clip, targetFrame, TargetWidth, TargetHeight, layoutRelativeWidth, layoutRelativeHeight);
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
                                    var pos1 = p.GetPosition(clip, TargetWidth, TargetHeight);
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
                            frame = ReplaceFrame(frame, n.Render(frame, TargetWidth, TargetHeight));
                        }
                        else if (item is IContinuousEffect c)
                        {
                            int scopedStart = c.IsScoped ? c.StartPoint : (int)clip.StartFrame;
                            int scopedEnd = c.IsScoped ? c.EndPoint : (int)(clip.StartFrame + clip.GetEffectiveDuration());
                            if (scopedEnd <= scopedStart || targetFrame < scopedStart || targetFrame >= scopedEnd) continue;
                            float continuousProgress = Math.Clamp((float)(targetFrame - scopedStart) / (scopedEnd - scopedStart), 0f, 1f);
                            frame = ResizeForEffectIfNeeded(frame, item, targetPos.TargetWidth, targetPos.TargetHeight, preserveAspect);
                            frame = ReplaceFrame(frame, c.Render(frame, continuousProgress, TargetWidth, TargetHeight));
                        }
                        else if (item is IClipPositionProvider p)
                        {
                            (var x, var y, var w, var h, bool delta, float rotation) = p.GetPosition(clip, TargetWidth, TargetHeight);
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
                            (var x, var y, var w, var h, bool delta, float rotation) = cp.GetPosition(clip, targetFrame, TargetWidth, TargetHeight, layoutRelativeWidth, layoutRelativeHeight);
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
                    if (frame is IHDRPicture<ushort> hdr)
                    {
                        if (!float.IsFinite(hdr.MaximumBrightness) || hdr.MaximumBrightness <= 0) hdr.MaximumBrightness = MaximumHDRBrightness;
                    }
                    else frame = ReplaceFrame(frame, HDRPicture16bpp.ToHDRPictureBySignal(frame, PerClipHDRBrightness[clip.Id]));
                }
                return new(clip, frame, targetPos, preserveAspect);
            }
            catch
            {
                frame.Dispose();
                throw;
            }
        }

        private static IPicture ReplaceFrame(IPicture source, IPicture result)
        {
            if (!ReferenceEquals(source, result)) source.Dispose();
            return result;
        }

        private IPicture Composite(IEnumerable<ClipContent?> clips, CancellationToken token)
        {
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
                    var frame = Track(DynamicEffectGraph.CopyPicture(clip.Picture));
                    int x = 0, y = 0;
                    if (!clip.IsCanvas)
                    {
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
                        var mixed = Track((clip.Clip.MixtureInstance ?? ClassicOverlayMixture.Default)
                            .Mix(result ?? BlankFrame, frame, _ppb, x, y, TargetWidth, TargetHeight));
                        if (result is not null && !ReferenceEquals(result, mixed)) Release(result);
                        if (!ReferenceEquals(frame, mixed)) Release(frame);
                        result = mixed;
                    }
                }
                if (result is not null && (result.Width != TargetWidth || result.Height != TargetHeight))
                    result = Replace(result, Resize(result, TargetWidth, TargetHeight, false));
                output = result ?? BlankFrame.Clone();
                return output;
            }
            finally
            {
                foreach (var frame in temporary)
                    if (!ReferenceEquals(frame, output)) frame.Dispose();
            }
        }

        private void WriteFrame(uint index, IPicture frame, Stopwatch sw)
        {
            bool written = false;
            try
            {
                if (LogProcessStack)
                {
                    FrameProcessStacks[index] = frame.ProcessStack.ToList();
                    FrameDirtyTime[index] = sw.Elapsed - TimeSpan.FromTicks(frame.ProcessStack.Where(c => c.Elapsed is not null).Sum(c => c.Elapsed!.Value.Ticks));
                }
                if (builder is not null)
                {
                    IRenderContext.SetWorkerState(index, RenderWorkerStage.WritingOutput, "Render compositor");
                    using var preview = builder.CapturePreviewFrame(frame);
                    builder.Writer.Append(frame);
                    written = true;
                    builder.NotifyFrameWritten(index, preview ?? frame);
                }
                Interlocked.Increment(ref Finished);
                FrameRenderElapsed[index] = sw.Elapsed;
                TrackRenderElapsed(sw.Elapsed);
                InvokeProgress();
                if (LogRenderState) Log($"[Render] Frame {index} written in {sw.Elapsed}.");
            }
            finally
            {
                if (!written || builder?.DisposeFrameAfterEachWrite != false) frame.Dispose();
                if (GCOption == 2) GC.Collect(2, GCCollectionMode.Forced, true, true);
                else if (GCOption == 1 && builder?.DoGCAfterEachWrite != true) GC.Collect();
            }
        }

        private IPicture ResizeForEffectIfNeeded(IPicture frame, IEffect effect, int width, int height, bool preserveAspect)
        {
            if (!ProcessEffectFromCanvas || !effect.CanProcessFromCanvas || width <= 0 || height <= 0 || frame.Width == width && frame.Height == height)
                return frame;
            var result = Resize(frame, width, height, preserveAspect);
            IRenderContext.CurrentFrameBuffer = result;
            return ReplaceFrame(frame, result);
        }

        private static IPicture Resize(IPicture frame, int width, int height, bool preserveAspect)
            => frame is IHDRPicture<ushort> hdr ? hdr.Resize(width, height, preserveAspect) : frame.Resize(width, height, preserveAspect);

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
            if (!Timeline.CanCacheClipFrame(Clips, clip)) return false;

            var directory = Path.Combine(DynamicPreviewCacheProjectRoot, "thumbs", "perClip", clip.Id.ToString(), "dynamic");
            if (!Directory.Exists(directory)) return false;

            var hashClip = _dynamicPreviewHashClips.FirstOrDefault(c => c.Id == clip.Id) ?? clip;
            var hash = Timeline.GetClipFrameHash(_dynamicPreviewHashClips, hashClip, frameIndex);
            if (hash == "__error__") return false;
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

        private Dictionary<string, List<(string Path, int ProjectWidth, int ProjectHeight, int CanvasWidth, int CanvasHeight)>> IndexDynamicPreviewFiles(string directory)
        {
            var files = new Dictionary<string, List<(string Path, int ProjectWidth, int ProjectHeight, int CanvasWidth, int CanvasHeight)>>(StringComparer.Ordinal);
            const string prefix = "dynamic_v8-mask-vfd_";
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

        private void InvokeProgress()
        {
            double prog = (double)Volatile.Read(ref Finished) / Duration;
            var elapsed = _renderTotalStopwatch.Elapsed;
            double fps = elapsed.TotalSeconds > 0 ? Volatile.Read(ref Finished) / elapsed.TotalSeconds : 0;
            Interlocked.Exchange(ref _currentFps, fps);
            OnProgressChanged?.Invoke(prog, GetEstimated(prog));
            uint projectFrames = TotalProjectFrames > 0 ? TotalProjectFrames : Duration;
            double globalProgress = projectFrames > 0
                ? Math.Clamp((CompletedFramesBeforeChunk + Volatile.Read(ref Finished)) / (double)projectFrames, 0, 1)
                : prog;
            OnChunkProgressChanged?.Invoke(new ChunkRenderProgress(
                ChunkIndex,
                Math.Max(1, ChunkCount),
                Volatile.Read(ref Finished),
                Duration,
                prog,
                globalProgress,
                GetEstimated(prog)));
        }

        private TimeSpan GetEstimated(double prog)
        {
            TimeSpan elapsed = _renderTotalStopwatch.Elapsed;
            TimeSpan etr = TimeSpan.Zero;
            if (prog > 0.005)
            {
                double totalEst = elapsed.TotalSeconds / prog;
                double remaining = totalEst - elapsed.TotalSeconds;
                if (remaining > 0) etr = TimeSpan.FromSeconds(remaining);
            }
            return etr;
        }

        public string GetRendererStatusInfo(bool includeQueueAndWriterStats = true)
        {
            int cached;
            lock (cacheGate) cached = frameCache.Count;
            return $"Finished {CurrentFinished}/{Duration} ({Progress:p2}), ETA: {GetEstimated(Progress)}, " +
                $"workers: {Volatile.Read(ref ThreadWorking)}/{MaxThreads}, cached clips: {cached}, " +
                $"written: {builder?.WrittenFramesCount ?? 0}, memory: {Environment.WorkingSet / 1024 / 1024:n0} MB, " +
                $"prepare: {GetAverageElapsed(_prepareElapsedTicksTotal, _prepareElapsedCount)}, " +
                $"render: {GetAverageElapsed(_renderElapsedTicksTotal, _renderElapsedCount)}, 1% low: {OnePercentLowFps:n1} FPS.";
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
            frameRequests.Clear();
            _dynamicPreviewFiles.Clear();
            foreach (var clip in Clips ?? [])
            {
                try { EffectHelper.ReleaseClipEffects(clip); }
                catch (Exception ex) { Log(ex, $"Release render effects for clip {clip.Id}", this); }
            }
            EffectCache.Clear();
            try { BlankFrame?.Dispose(true); }
            catch (Exception ex) { Log(ex, "Release render canvas", this); }
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
