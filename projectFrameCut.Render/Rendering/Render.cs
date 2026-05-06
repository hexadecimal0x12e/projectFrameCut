using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace projectFrameCut.Render.Rendering
{
    public class Renderer
    {
        #region opts
        public const int SubTrackOffset = 10000;

        public IClip[]? Clips;
        public uint Duration;
        public uint StartFrame = 0;
        public VideoBuilder? builder;

        public bool LogRenderState = false;
        public bool LogStaticsData = false;
        public bool LogProcessStack = false;
        public bool AutoCenterImplicitClip { get; set; } = false;

        public bool RenderByLayers { get; set; } = false;
        public int MaxThreads { get => field > 0 ? field : (int)(Environment.ProcessorCount * 1.75); set; }
        public int GCOption = 0;

        public int MaxRenderScheduleTimeout { get; set; } = 500;
        public int MinSchedulePreparedFrames { get => field > 0 ? field : MaxThreads; set; }
        public int RenderWatchdogNoProgressTimeoutMs { get => field > 0 ? field : 60_000; set; } = 60_000;
        public bool EnableRenderWatchdogForceStart { get; set; } = true;
        public double RenderWorkerLaunchUtilizationThreshold { get => field > 0 ? field : 1.0; set; } = 1.0;
        public int RenderSchedulerPreparePollDelayMs { get => field > 0 ? field : 5; set; } = 5;
        public int RenderSchedulerIdleDelayMs { get => field > 0 ? field : 10; set; } = 10;
        public int MinRemainingFramesForPreparedWait { get => field >= 0 ? field : Math.Max(0, MaxThreads / 2 - 2); set; } = -1;
        public int ThrottleThreshold { get => field > 0 ? field : Math.Max(MaxThreads * 4, MaxThreads + 8); set; }

        public int ProjectRelativeWidth { get; set; }
        public int ProjectRelativeHeight { get; set; }
        public int TargetWidth { get; set; }
        public int TargetHeight { get; set; }
        public bool Use16Bit { get; set; } = true;
        public bool UseHDR { get; set; } = false;
        public int SDRClipsBrightnessInHDRMode { get; set; } = 203;
        public int MaximumHDRBrightness { get; set; } = 1000;

        Dictionary<Guid, IClip> IndexedClipList = new();
        Dictionary<Guid, int> PerClipHDRBrightness = new();
        Dictionary<Guid, bool> IsClipGeneratedByAI = new();

        public void ClearCaches()
        {
            FrameCache.Clear();
            GC.Collect();
        }

        public event Action<double, TimeSpan>? OnProgressChanged;
        private Stopwatch _renderTotalStopwatch = new();
        private double _currentFps = 0;

        public double CurrentFps => Interlocked.CompareExchange(ref _currentFps, 0, 0);
        public double CurrentSecondPerFrame => 1 / CurrentFps;

        public ConcurrentBag<TimeSpan> EachElapsed = new(), EachElapsedForPreparing = new();

        // Per-frame diagnostics (used for CSV reporting)
        public ConcurrentDictionary<uint, TimeSpan> FramePrepareElapsed { get; } = new();
        public ConcurrentDictionary<uint, TimeSpan> FrameRenderElapsed { get; } = new();
        public ConcurrentDictionary<uint, TimeSpan> FrameDirtyTime { get; } = new();
        public ConcurrentDictionary<uint, TimeSpan> FrameQueuedTime { get; } = new();
        public ConcurrentDictionary<uint, List<PictureProcessStack>> FrameProcessStacks { get; } = new();

        public bool running { get; private set; } = false;

        ConcurrentDictionary<string, ConcurrentDictionary<uint, IPicture>> FrameCache = new();
        ConcurrentDictionary<uint, IClip[]> ClipNeedForFrame = new();

        // Layer-by-layer render: maps frame → ordered layer groups within that frame
        Dictionary<uint, LayerGroup[]>? FrameLayerGroups;
        // Layer-by-layer render: tracks per-frame layer completion state
        ConcurrentDictionary<uint, FrameLayerCompletion>? FrameLayerCompletions;
        // Layer-by-layer render: stores rendered layer results before merge
        ConcurrentDictionary<(uint Frame, int LayerOrdinal), IPicture>? LayerResults;

        ConcurrentDictionary<string, IEffect[]> EffectCache = new();
        ConcurrentDictionary<string, object> BindableEffectResultCache = new();
        ConcurrentDictionary<uint, Stopwatch> FramePrepareToRenderTimer = new();
        IComputer mixComputer = null!;

        int ThreadWorking = 0, Finished = 0;
        private SemaphoreSlim _threadLimiter = null!;

        // Thread-local computer cache to avoid contention
        private ThreadLocal<Dictionary<string, IComputer>> _threadLocalComputerCache =
            new ThreadLocal<Dictionary<string, IComputer>>(() => new Dictionary<string, IComputer>());

        public static bool IsProfilerAttached =>
            string.Equals(Environment.GetEnvironmentVariable("COR_ENABLE_PROFILING"), "1", StringComparison.Ordinal);


        ConcurrentQueue<uint> PreparedFrames = new(), BlankFrames = new();
        ConcurrentDictionary<uint, byte> PreparedFlag = new();

        int TotalEnqueued = 0;
        volatile bool PreparerFinished = false;


        private int _ppb;

        private IPicture BlankFrame = null!;

        // Thread-local: PlaceEffect_ImageSharp has mutable state and is not thread-safe
        private ThreadLocal<PlaceEffect_IPicture> _threadLocalBlankPlace =
            new(() => new PlaceEffect_IPicture { StartX = 0, StartY = 0 });

        // Per-clip lock objects to serialize effect processing for the same clip across threads
        // (IEffect instances in EffectCache are shared and may be stateful)
        private ConcurrentDictionary<string, object> _clipEffectLocks = new();

        static ClipEquabilityComparer clipEquabilityComparer = new();

        #endregion

        #region prepare
        public void PrepareRender(CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(Clips, nameof(Clips));
            InitializeRenderCaches();
            bool found = false;
            for (uint idx = StartFrame; idx < StartFrame + Duration; idx++)
            {
                found = false;
                if (token.IsCancellationRequested) return;
                foreach (var item in Clips)
                {
                    if (token.IsCancellationRequested) return;


                    if (IsFrameInClipRange(item, idx) || (item.ExtendToWholeDraft && item.LayerIndex > SubTrackOffset))
                    {
                        found = true;
                        ClipNeedForFrame.AddOrUpdate(
                            idx,
                            (_) => [item],
                            (_, old) => old
                                .Append(item)
                                .OrderBy(x => x.LayerIndex >= SubTrackOffset ? 1 : 0)
                                .ThenByDescending(x => x.LayerIndex)
                                .ThenByDescending(x => x.SubLayerIndex)
                                .ToArray());
                    }
                }

                if (!found)
                {
                    BlankFrames.Enqueue(idx);
                    Interlocked.Increment(ref TotalEnqueued);
                }

                if (idx % 50 == 0)
                {
                    Log($"[Preparer] source preparing finished {(float)(idx - StartFrame) / (float)Duration:p3} ({idx - StartFrame}/{Duration})");
                }

            }
            Log($"[Preparer] source preparing done.");

        }

        private void PrepareSource(CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(Clips, nameof(Clips));
            Stopwatch sw = new();

            var ppb = Use16Bit ? IPicture.PicturePixelMode.UShortPicture : IPicture.PicturePixelMode.BytePicture;
            foreach (var idx in ClipNeedForFrame.Keys.OrderBy(x => x))
            {
                // Throttling: limit only by prepared source-frame queue depth.
                // Do not use TotalEnqueued here because it includes blank frames and can deadlock with many blanks.
                while (!IsProfilerAttached && PreparedFrames.Count > ThrottleThreshold && !token.IsCancellationRequested)
                {
                    Log($"[Preparer] Waiting for more render slots... prepared source frames pending: {PreparedFrames.Count} (threshold: {ThrottleThreshold})");
                    Thread.Sleep(50);
                }

                if (token.IsCancellationRequested) return;
                sw.Restart();

                foreach (var item in Clips)
                {
                    if (token.IsCancellationRequested) return;

                    if (ClipNeedForFrame[idx].Contains(item, clipEquabilityComparer))
                    {
                        IPicture frame = null!;
                        int clipTargetWidth = ResolveClipOutputWidth(item, TargetWidth, ProjectRelativeWidth);
                        int clipTargetHeight = ResolveClipOutputHeight(item, TargetHeight, ProjectRelativeHeight);
                        if (item.ClipType == ClipMode.TransformClip && item is TransformContainer c)
                        {
                            if (c.Transform is not ITransform t) throw new NullReferenceException($"Transform for clip {c.Id} is null");
                            IClip? rightClip = null;

                            if (t.TransformType != TransformType.OneInputSingleFrameTransform)
                                if (!IndexedClipList.TryGetValue(t.BindedRightClip, out rightClip)) throw new NullReferenceException($"Transform {t.Name}({t.TypeName})'s left input for clip {c.Id} is null");

                            if (!IndexedClipList.TryGetValue(t.BindedLeftClip, out IClip? leftClip)) throw new NullReferenceException($"Transform {t.Name}({t.TypeName})'s left input for clip {c.Id} is null");


                            frame = TransformProcessing.ProcessTransform(leftClip, rightClip, t, clipTargetWidth, clipTargetHeight, idx, ppb);


                        }
                        else
                        {
                            frame = item.GetFrame(idx, clipTargetWidth, clipTargetHeight, true, ppb);
                        }
                        if (frame != null)
                        {
                            if (IsClipGeneratedByAI.TryGetValue(item.IdAsGUID, out var aiMark) && aiMark)
                            {
                                frame = EffectProcessing.ProcessAIWatermark(frame, null);
                            }
                            FrameCache.GetOrAdd(item.Id, (_) => new()).TryAdd(idx, frame);
                        }
                    }
                }
                if (PreparedFlag.TryAdd(idx, 0))
                {
                    PreparedFrames.Enqueue(idx);
                    Interlocked.Increment(ref TotalEnqueued);
                    FramePrepareToRenderTimer.AddOrUpdate(idx, Stopwatch.StartNew(), (_, c) => { c.Restart(); return c; });
                }
                sw.Stop();
                EachElapsedForPreparing.Add(sw.Elapsed);
                FramePrepareElapsed[idx] = sw.Elapsed;
                if (LogRenderState) Log($"[Preparer] Frame {idx} is ready to render, elapsed {sw.Elapsed}");

            }
            Log($"[Preparer] All frames are ready.");
        }

        /// <summary>
        /// Builds FrameLayerGroups from ClipNeedForFrame by grouping contiguous clips
        /// that share the same LayerIndex. Relies on the sort order established in PrepareRender.
        /// Frames without any clips are omitted (handled via BlankFrames).
        /// </summary>
        private void PrepareFrameLayerData()
        {
            FrameLayerGroups = new Dictionary<uint, LayerGroup[]>();

            foreach (var kv in ClipNeedForFrame)
            {
                uint frame = kv.Key;
                IClip[] clips = kv.Value;

                if (clips.Length == 0)
                    continue;

                var groups = new List<LayerGroup>();
                int i = 0;
                while (i < clips.Length)
                {
                    uint currentLayer = clips[i].LayerIndex;
                    int start = i;
                    while (i < clips.Length && clips[i].LayerIndex == currentLayer)
                        i++;

                    groups.Add(new LayerGroup
                    {
                        LayerIndex = currentLayer,
                        StartIndex = start,
                        Count = i - start,
                    });
                }

                FrameLayerGroups[frame] = groups.ToArray();
            }
        }
        #endregion

        #region render
        public async Task GoRender(CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(builder, nameof(builder));
            ArgumentNullException.ThrowIfNull(Clips, nameof(Clips));
            _renderTotalStopwatch.Restart();


            if (builder.BlockWrite)
            {
                await GoRenderSync(token);
                return;
            }

            if (RenderByLayers)
            {
                await GoRenderByLayer(token);
                return;
            }

            // Initialize thread limiter
            _threadLimiter = new SemaphoreSlim(MaxThreads, MaxThreads);
            ConcurrentQueue<Exception> exceptions = new();

            InitializeRenderCaches();
            if (ClipNeedForFrame.IsEmpty && BlankFrames.IsEmpty && Volatile.Read(ref TotalEnqueued) == 0)
            {
                PrepareRender(token);
                if (token.IsCancellationRequested)
                {
                    ReleaseResources();
                    return;
                }
            }


            running = true;
            if (LogStaticsData)
            {
                new Thread(() =>
                {
                    float d = Duration;
                    int finished = 0, wrote = 0, working = 0;
                    TimeSpan each = TimeSpan.Zero, eachPrepare = TimeSpan.Zero;
                    while (running)
                    {
                        try
                        {
                            if (!EachElapsed.IsEmpty)
                                each = new TimeSpan((long)EachElapsed.Average(x => x.Ticks));
                            if (!EachElapsedForPreparing.IsEmpty)
                                eachPrepare = new TimeSpan((long)EachElapsedForPreparing.Average(x => x.Ticks));

                            if (token.IsCancellationRequested) return;
                            finished = Volatile.Read(ref Finished);
                            wrote = builder.WrittenFramesCount;
                            working = Volatile.Read(ref ThreadWorking);

                            Log($"Overall finished {finished / d:p2}, and {TotalEnqueued / d:p2} is ready to render. ETA: {GetEstimated(finished / d)}, " +
                                $"Memory used by program: {Environment.WorkingSet / 1024 / 1024:n2} MB. \r\n" +
                                $"       (Already elapsed {_renderTotalStopwatch.Elapsed}, Total {TotalEnqueued}/{d} prepared and {finished}/{d} finished, " +
                                $"pending to render: {Volatile.Read(ref TotalEnqueued) - finished}, " +
                                $"total write frames: {wrote} wrote and {builder.TotalFramesCount - wrote} pended, " +
                                $"slots {Math.Max(0, MaxThreads - working)}/{MaxThreads}, active workers: {working}, " +
                                $"preparing elapsed average: {eachPrepare}, Each frame render elapsed average: {each}.)", "STAT");
                            Thread.Sleep(10000);
                        }
                        catch { }
                    }
                })
                {
                    Name = "Stat logger thread",
                    IsBackground = false
                }.Start();
            }

            Thread preparer = new(() =>
            {
                try
                {
                    PrepareSource(token);
                }
                catch (Exception ex)
                {
                    Log($"Error preparing source frames: {ex}", "error");
                    exceptions.Enqueue(ex);
                }
                finally
                {
                    PreparerFinished = true;
                }
            })
            {
                Name = "Preparer thread",
                IsBackground = true
            };
            preparer.Start();

            // Give the preparer a brief moment to queue the first frames, then start scheduling immediately
            await Task.Delay(50, token);

            int watchdogTimeoutMs = RenderWatchdogNoProgressTimeoutMs > 0 ? RenderWatchdogNoProgressTimeoutMs : 60_000;
            double launchUtilizationThreshold = RenderWorkerLaunchUtilizationThreshold;
            if (double.IsNaN(launchUtilizationThreshold) || double.IsInfinity(launchUtilizationThreshold) || launchUtilizationThreshold <= 0)
                launchUtilizationThreshold = 1.0;
            if (launchUtilizationThreshold > 1.0)
                launchUtilizationThreshold = 1.0;
            int preparePollDelayMs = RenderSchedulerPreparePollDelayMs > 0 ? RenderSchedulerPreparePollDelayMs : 5;
            int idleDelayMs = RenderSchedulerIdleDelayMs > 0 ? RenderSchedulerIdleDelayMs : 10;
            int minRemainingFramesForPreparedWait = MinRemainingFramesForPreparedWait >= 0
                ? MinRemainingFramesForPreparedWait
                : Math.Max(0, MaxThreads / 2 - 2);

            Stopwatch lastActivity = Stopwatch.StartNew();
            int lastManuallyStarted = 0;
            int lastFinished = Volatile.Read(ref Finished);

            while (true)
            {
                if (token.IsCancellationRequested)
                {
                    Log("Render cancelled by user.", "info");
                    break;
                }
                if (PreparerFinished && Volatile.Read(ref Finished) >= Duration)
                    break;

                int working = Volatile.Read(ref ThreadWorking);
                int availableSlots = Math.Max(0, MaxThreads - working);

                int preparedCount = PreparedFrames.Count;
                int currentFinished = Volatile.Read(ref Finished);
                if (currentFinished != lastFinished)
                {
                    lastFinished = currentFinished;
                    lastActivity.Restart();
                }

                bool forceStart = EnableRenderWatchdogForceStart
                    && watchdogTimeoutMs > 0
                    && lastActivity.ElapsedMilliseconds >= watchdogTimeoutMs;
                bool underLaunchUtilizationThreshold = availableSlots > 0
                    && (double)working / Math.Max(1, MaxThreads) < launchUtilizationThreshold;

                if (preparedCount > 0 && (forceStart || underLaunchUtilizationThreshold))
                {
                    int toStart = forceStart ? preparedCount : Math.Min(preparedCount, availableSlots);

                    if (forceStart)
                    {
                        Log($"[Watchdog] No rendered frame progress for {watchdogTimeoutMs} ms. prepared={preparedCount}, working={working}/{MaxThreads}, finished={Volatile.Read(ref Finished)}/{Duration}.", "warn");
                        if (availableSlots == 0)
                        {
                            Log($"[Watchdog] No available slots (all render threads busy). This often means a render thread is blocked (e.g. in effects/mixer) or the writer is stuck waiting for a missing frame index.", "warn");
                        }
                    }
                    else
                    {
                        // Add timeout to avoid infinite wait when preparer is slow (e.g., on Android with OpenGL main-thread bottleneck)
                        Stopwatch waitElapsed = Stopwatch.StartNew();
                        while (!PreparerFinished && Duration - Volatile.Read(ref Finished) > minRemainingFramesForPreparedWait && PreparedFrames.Count < MinSchedulePreparedFrames)
                        {
                            await Task.Delay(preparePollDelayMs, token);
                            if (MaxRenderScheduleTimeout > 0 && waitElapsed.ElapsedMilliseconds >= MaxRenderScheduleTimeout)
                            {
                                lastManuallyStarted += PreparedFrames.Count;
                                if (lastManuallyStarted % 50 == 0)
                                {
                                    Log($"[Render] Wait timeout reached for {lastManuallyStarted} times. (platform: {RuntimeInformation.RuntimeIdentifier})", "warn");
                                }
                                break;
                            }
                        }
                    }

                    lastActivity.Restart();

                    if (GCOption == 2)
                    {
                        GC.Collect(2, GCCollectionMode.Forced, true, true);
                        GC.WaitForFullGCComplete();
                    }
                    else if (GCOption == 1)
                    {
                        GC.Collect();
                    }

                    for (int i = 0; i < toStart; i++)
                    {
                        if (!PreparedFrames.TryDequeue(out var targetFrame))
                            break;

                        if (!_threadLimiter.Wait(0, token))
                        {
                            PreparedFrames.Enqueue(targetFrame);
                            break;
                        }

                        Interlocked.Increment(ref ThreadWorking);

                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try
                            {
                                Thread.CurrentThread?.Name = $"Render worker #{targetFrame}";
                                FlushBlankFramesBefore(targetFrame, token);
                                if(FramePrepareToRenderTimer.TryGetValue(targetFrame, out var timer))
                                {
                                    timer.Stop();
#if DEBUG
                                    LogDiagnostic($"Frame {targetFrame} took {timer.Elapsed} between prepared and render start.");
#endif
                                    FrameQueuedTime[targetFrame] = timer.Elapsed;
                                }
                                RenderAFrame(targetFrame, token);
                            }
                            catch (Exception ex)
                            {
                                Log(ex, $"rendering frame {targetFrame}", this);

#if DEBUG
                                throw;
#else
                                ex.Data["OrigStacktrace"] = ex.StackTrace;
                                exceptions.Enqueue(ex);
#endif
                            }
                            finally
                            {
                                Interlocked.Decrement(ref ThreadWorking);
                                try
                                {
                                    _threadLimiter.Release();
                                }
                                catch { }
                            }
                        }, null);
                    }

                }
                else
                {
                    if (PreparerFinished && PreparedFrames.IsEmpty && Volatile.Read(ref ThreadWorking) == 0 && !BlankFrames.IsEmpty)
                    {
                        FlushBlankFramesBefore(StartFrame + Duration, token);
                    }
                    await Task.Delay(idleDelayMs, token);
                }


                if (!exceptions.IsEmpty)
                {
                    Log("Exceptions occurred during rendering. Aborting.", "error");
                    var list = new List<Exception>();
                    while (exceptions.TryDequeue(out var ex)) list.Add(ex);
                    if (list.Count == 1) throw list[0];
                    throw new AggregateException("Multiple exceptions occurred during rendering.", list);
                }

            }
            if (token.IsCancellationRequested)
            {
                Log("Render cancelled by user.");
                ReleaseResources();
                return;
            }
            Log($"[Preparer] All frames are prepared and waiting for render done...");

            int waitCount = 0;
            while (Volatile.Read(ref ThreadWorking) > 0 && waitCount < 1000)
            {
                await Task.Delay(50, token);
                waitCount++;
            }

            ReleaseResources();

        }

        public async Task GoRenderSync(CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(builder, nameof(builder));
            ArgumentNullException.ThrowIfNull(Clips, nameof(Clips));

            _renderTotalStopwatch.Restart();
            Log("[Renderer] BlockWrite enabled: switching to single-threaded, synchronous render.", "info");

           

            if (LogStaticsData)
            {
                new Thread(() =>
                {
                    float d = Duration;
                    int finished = 0;
                    TimeSpan each = TimeSpan.Zero, eachPrepare = TimeSpan.Zero;
                    while (running)
                    {
                        try
                        {
                            if (!EachElapsed.IsEmpty)
                                each = new TimeSpan((long)EachElapsed.Average(x => x.Ticks));
                            if (!EachElapsedForPreparing.IsEmpty)
                                eachPrepare = new TimeSpan((long)EachElapsedForPreparing.Average(x => x.Ticks));

                            if (token.IsCancellationRequested) return;
                            finished = Volatile.Read(ref Finished);

                            Log($"Finished {finished / d:p2}. ETA: {GetEstimated(finished / d)}, " +
                                $"Memory used by program: {Environment.WorkingSet / 1024 / 1024:n2} MB. \r\n" +
                                $"       ({finished} of {d} finished, already elapsed {_renderTotalStopwatch.Elapsed}, " +
                                $"preparing elapsed average: {eachPrepare}, Each frame render elapsed average: {each}.)", "STAT");
                            Thread.Sleep(10000);
                        }
                        catch { }
                    }
                })
                {
                    Name = "Stat logger thread",
                    IsBackground = false
                }.Start();
            }



            for (uint idx = StartFrame; idx < StartFrame + Duration; idx++)
            {
                if (token.IsCancellationRequested)
                {
                    Log("Render cancelled by user.", "info");
                    break;
                }
                RenderOneFrameSync(idx, token);
            }

            if (token.IsCancellationRequested)
            {
                ReleaseResources();
                return;
            }

            ReleaseResources();
        }

        /// <summary>
        /// Renders the project scheduling work per-layer rather than per-frame.
        /// Each layer within a frame can be rendered in parallel; when all layers
        /// for a frame complete, they are merged and submitted to the builder.
        /// Falls back to GoRenderSync when BlockWrite is enabled.
        /// </summary>
        public async Task GoRenderByLayer(CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(builder, nameof(builder));
            ArgumentNullException.ThrowIfNull(Clips, nameof(Clips));

            if (builder.BlockWrite)
            {
                await GoRenderSync(token);
                return;
            }

            await GoRenderByLayerAsync(token);
        }

        /// <summary>
        /// Async implementation of GoRenderByLayer. Mirrors GoRender's structure but dispatches
        /// per-layer ThreadPool workers and assembles frames when all layers complete.
        /// </summary>
        private async Task GoRenderByLayerAsync(CancellationToken token)
        {
            _renderTotalStopwatch.Restart();

            _threadLimiter = new SemaphoreSlim(MaxThreads, MaxThreads);
            ConcurrentQueue<Exception> exceptions = new();

            InitializeRenderCaches();
            if (ClipNeedForFrame.IsEmpty && BlankFrames.IsEmpty && Volatile.Read(ref TotalEnqueued) == 0)
            {
                PrepareRender(token);
                if (token.IsCancellationRequested) { ReleaseResources(); return; }
            }

            PrepareFrameLayerData();
            FrameLayerCompletions = new();
            LayerResults = new();

            running = true;

            if (LogStaticsData)
            {
                new Thread(() =>
                {
                    float d = Duration;
                    int finished = 0, wrote = 0, working = 0;
                    TimeSpan each = TimeSpan.Zero, eachPrepare = TimeSpan.Zero;
                    while (running)
                    {
                        try
                        {
                            if (!EachElapsed.IsEmpty)
                                each = new TimeSpan((long)EachElapsed.Average(x => x.Ticks));
                            if (!EachElapsedForPreparing.IsEmpty)
                                eachPrepare = new TimeSpan((long)EachElapsedForPreparing.Average(x => x.Ticks));

                            if (token.IsCancellationRequested) return;
                            finished = Volatile.Read(ref Finished);
                            wrote = builder.WrittenFramesCount;
                            working = Volatile.Read(ref ThreadWorking);

                            Log($"Overall finished {finished / d:p2}, and {TotalEnqueued / d:p2} is ready to render. ETA: {GetEstimated(finished / d)}, " +
                                $"Memory used by program: {Environment.WorkingSet / 1024 / 1024:n2} MB. \r\n" +
                                $"       (Already elapsed {_renderTotalStopwatch.Elapsed}, Total {TotalEnqueued}/{d} prepared and {finished}/{d} finished, " +
                                $"pending to render: {Volatile.Read(ref TotalEnqueued) - finished}, " +
                                $"total write frames: {wrote} wrote and {builder.TotalFramesCount - wrote} pended, " +
                                $"slots {Math.Max(0, MaxThreads - working)}/{MaxThreads}, active workers: {working}, " +
                                $"preparing elapsed average: {eachPrepare}, Each frame render elapsed average: {each}.)", "STAT");
                            Thread.Sleep(10000);
                        }
                        catch { }
                    }
                })
                {
                    Name = "Stat logger thread",
                    IsBackground = false
                }.Start();
            }

            Thread preparer = new(() =>
            {
                try
                {
                    PrepareSource(token);
                }
                catch (Exception ex)
                {
                    Log($"Error preparing source frames: {ex}", "error");
                    exceptions.Enqueue(ex);
                }
                finally
                {
                    PreparerFinished = true;
                }
            })
            {
                Name = "Preparer thread",
                IsBackground = true
            };
            preparer.Start();

            // Give the preparer a brief moment to queue the first frames, then start scheduling immediately
            await Task.Delay(50, token);

            int watchdogTimeoutMs = RenderWatchdogNoProgressTimeoutMs > 0 ? RenderWatchdogNoProgressTimeoutMs : 60_000;
            double launchUtilizationThreshold = RenderWorkerLaunchUtilizationThreshold;
            if (double.IsNaN(launchUtilizationThreshold) || double.IsInfinity(launchUtilizationThreshold) || launchUtilizationThreshold <= 0)
                launchUtilizationThreshold = 1.0;
            if (launchUtilizationThreshold > 1.0)
                launchUtilizationThreshold = 1.0;
            int preparePollDelayMs = RenderSchedulerPreparePollDelayMs > 0 ? RenderSchedulerPreparePollDelayMs : 5;
            int idleDelayMs = RenderSchedulerIdleDelayMs > 0 ? RenderSchedulerIdleDelayMs : 10;
            int minRemainingFramesForPreparedWait = MinRemainingFramesForPreparedWait >= 0
                ? MinRemainingFramesForPreparedWait
                : Math.Max(0, MaxThreads / 2 - 2);

            Stopwatch lastActivity = Stopwatch.StartNew();
            int lastManuallyStarted = 0;
            int lastFinished = Volatile.Read(ref Finished);

            while (true)
            {
                if (token.IsCancellationRequested)
                {
                    Log("Render cancelled by user.", "info");
                    break;
                }
                if (PreparerFinished && Volatile.Read(ref Finished) >= Duration)
                    break;

                int working = Volatile.Read(ref ThreadWorking);
                int availableSlots = Math.Max(0, MaxThreads - working);

                int preparedCount = PreparedFrames.Count;
                int currentFinished = Volatile.Read(ref Finished);
                if (currentFinished != lastFinished)
                {
                    lastFinished = currentFinished;
                    lastActivity.Restart();
                }

                bool forceStart = EnableRenderWatchdogForceStart
                    && watchdogTimeoutMs > 0
                    && lastActivity.ElapsedMilliseconds >= watchdogTimeoutMs;
                bool underLaunchUtilizationThreshold = availableSlots > 0
                    && (double)working / Math.Max(1, MaxThreads) < launchUtilizationThreshold;

                if (preparedCount > 0 && (forceStart || underLaunchUtilizationThreshold))
                {
                    int toStart = forceStart ? preparedCount : Math.Min(preparedCount, availableSlots);

                    if (forceStart)
                    {
                        Log($"[Watchdog] No rendered frame progress for {watchdogTimeoutMs} ms. prepared={preparedCount}, working={working}/{MaxThreads}, finished={Volatile.Read(ref Finished)}/{Duration}.", "warn");
                        if (availableSlots == 0)
                        {
                            Log($"[Watchdog] No available slots (all render threads busy). This often means a render thread is blocked or the writer is stuck waiting for a missing frame index.", "warn");
                        }
                    }
                    else
                    {
                        Stopwatch waitElapsed = Stopwatch.StartNew();
                        while (!PreparerFinished && Duration - Volatile.Read(ref Finished) > minRemainingFramesForPreparedWait && PreparedFrames.Count < MinSchedulePreparedFrames)
                        {
                            await Task.Delay(preparePollDelayMs, token);
                            if (MaxRenderScheduleTimeout > 0 && waitElapsed.ElapsedMilliseconds >= MaxRenderScheduleTimeout)
                            {
                                lastManuallyStarted += PreparedFrames.Count;
                                if (lastManuallyStarted % 50 == 0)
                                {
                                    Log($"[Render] Wait timeout reached for {lastManuallyStarted} times. (platform: {RuntimeInformation.RuntimeIdentifier})", "warn");
                                }
                                break;
                            }
                        }
                    }

                    lastActivity.Restart();

                    if (GCOption == 2)
                    {
                        GC.Collect(2, GCCollectionMode.Forced, true, true);
                        GC.WaitForFullGCComplete();
                    }
                    else if (GCOption == 1)
                    {
                        GC.Collect();
                    }

                    for (int i = 0; i < toStart; i++)
                    {
                        if (!PreparedFrames.TryDequeue(out var targetFrame))
                            break;

                        FlushBlankFramesBefore(targetFrame, token);

                        if (!FrameLayerGroups!.TryGetValue(targetFrame, out var layerGroups) || layerGroups.Length == 0)
                        {
                            // No clips at this frame: treat as blank
                            builder!.Append(targetFrame, BlankFrame);
                            EachElapsed.Add(TimeSpan.Zero);
                            FramePrepareElapsed.TryAdd(targetFrame, TimeSpan.Zero);
                            FrameRenderElapsed.TryAdd(targetFrame, TimeSpan.Zero);
                            Interlocked.Increment(ref Finished);
                            InvokeProgress();
                            continue;
                        }

                        // Try to acquire semaphore slots for all layers (non-blocking, matching frame path)
                        int acquired = 0;
                        for (int li = 0; li < layerGroups.Length; li++)
                        {
                            if (!_threadLimiter.Wait(0))
                            {
                                for (int r = 0; r < acquired; r++)
                                    _threadLimiter.Release();
                                PreparedFrames.Enqueue(targetFrame);
                                break;
                            }
                            acquired++;
                        }

                        if (acquired < layerGroups.Length)
                            continue;

                        // Set up completion tracking for this frame
                        var completion = new FrameLayerCompletion
                        {
                            TotalLayers = layerGroups.Length,
                            CompletedLayers = 0,
                            LayerResults = new IPicture?[layerGroups.Length],
                        };
                        FrameLayerCompletions![targetFrame] = completion;

                        // Dispatch one worker per layer (slots already acquired above)
                        for (int layerIdx = 0; layerIdx < layerGroups.Length; layerIdx++)
                        {
                            Interlocked.Increment(ref ThreadWorking);
                            var capturedFrame = targetFrame;
                            var capturedLayerIdx = layerIdx;
                            var capturedGroup = layerGroups[layerIdx];

                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                try
                                {
                                    Thread.CurrentThread?.Name ??= $"Layer worker #{capturedFrame}.{capturedLayerIdx}";
                                    if (FramePrepareToRenderTimer.TryGetValue(capturedFrame, out var timer))
                                    {
#if DEBUG
                                        LogDiagnostic($"Frame {targetFrame}.{capturedLayerIdx} took {timer.Elapsed} between prepared and render start.");
#endif
                                        FrameQueuedTime.AddOrUpdate(capturedFrame, timer.Elapsed, (_, existing) => timer.Elapsed);
                                    }
                                    RenderALayer(capturedFrame, capturedLayerIdx, capturedGroup, token);
                                }
                                catch (Exception ex)
                                {
                                    Log($"Error rendering layer {capturedLayerIdx} of frame {capturedFrame}: {ex}", "error");
                                    ex.Data["OrigStacktrace"] = ex.StackTrace;
                                    exceptions.Enqueue(ex);
                                }
                                finally
                                {
                                    Interlocked.Decrement(ref ThreadWorking);
                                    try
                                    {
                                        _threadLimiter.Release();
                                    }
                                    catch { }
                                }
                            }, null);
                        }
                    }
                }
                else
                {
                    if (PreparerFinished && PreparedFrames.IsEmpty && Volatile.Read(ref ThreadWorking) == 0 && !BlankFrames.IsEmpty)
                    {
                        FlushBlankFramesBefore(StartFrame + Duration, token);
                    }
                    await Task.Delay(idleDelayMs, token);
                }

                if (!exceptions.IsEmpty)
                {
                    Log("Exceptions occurred during rendering. Aborting.", "error");
                    var list = new List<Exception>();
                    while (exceptions.TryDequeue(out var ex)) list.Add(ex);
                    if (list.Count == 1) throw list[0];
                    throw new AggregateException("Multiple exceptions occurred during rendering.", list);
                }
            }

            if (token.IsCancellationRequested)
            {
                Log("Render cancelled by user.");
                ReleaseResources();
                return;
            }

            Log($"[Preparer] All frames are prepared and waiting for render done...");

            int waitCount = 0;
            while (Volatile.Read(ref ThreadWorking) > 0 && waitCount < 1000)
            {
                await Task.Delay(50, token);
                waitCount++;
            }

            ReleaseResources();
        }

        #endregion

        #region inner render logic

        private void RenderAFrame(uint targetFrame, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(builder, nameof(builder));
            if (targetFrame >= StartFrame + Duration)
            {
                Log($"[Render] WARN: Target frame {targetFrame} exceeds project duration. Ignore.");
                return;
            }
            PreparedFlag.TryRemove(targetFrame, out _);

            if (!ClipNeedForFrame.Remove(targetFrame, out var ClipsNeed) || ClipsNeed.Length == 0)
            {
                RenderAFrameInternal(
                    targetFrame,
                    [],
                    null,
                    token);
                return;
            }
            var framesToRender = new List<(IClip Clip, IPicture? Frame)>(ClipsNeed.Length);
            foreach (var clip in ClipsNeed)
            {
                if (!FrameCache.TryGetValue(clip.Id, out var perClipCache))
                {
                    Log($"[Render] WARN: Frame cache not found for clip {clip.Id}.");
                    throw new NullReferenceException($"Frame cache not found for clip {clip.Id}. This should not happen because the preparer should have prepared it. Clip ID: {clip.Id}, Target Frame: {targetFrame}");
                }

                if (!perClipCache.TryGetValue(targetFrame, out var frame) || frame is null)
                {
                    Log($"[Render] WARN: Frame {targetFrame} not found in cache for clip {clip.Id}.");
                    throw new NullReferenceException($"Frame cache not found for clip {clip.Id}. This should not happen because the preparer should have prepared it. Clip ID: {clip.Id}, Target Frame: {targetFrame}");

                }

                framesToRender.Add((clip, frame));
            }

            RenderAFrameInternal(
                targetFrame,
                framesToRender,
                null,
                token);

            if (token.IsCancellationRequested) return;

            // DO NOT dispose cache frames here.
            // - Single-clip: result == the cache frame; builder has queued it for async writing.
            //   Disposing it now = use-after-free while the writer thread still holds a reference.
            //   The builder will dispose the frame (via PictureFlag) after the write completes.
            // - Multi-clip: individual clip frames were already disposed inside RenderAFrameInternal
            //   immediately after being merged (usedFrames == null branch). Result is a fresh
            //   allocation owned by builder.
            // Just remove the entries from cache so they don't leak if the clip appears in future frames.
            foreach (var clip in ClipsNeed)
            {
                if (FrameCache.TryGetValue(clip.Id, out var perClipCache))
                    perClipCache.TryRemove(targetFrame, out _);
            }
            return;
        }

        public void RenderOneFrameSync(uint targetFrame, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(builder, nameof(builder));
            if (targetFrame >= StartFrame + Duration)
            {
                Log($"[Render] WARN: Target frame {targetFrame} exceeds project duration. Ignore.");
                return;
            }
            Stopwatch prep = Stopwatch.StartNew();
            var clipsNeed = new List<IClip>();
            foreach (var item in Clips ?? Array.Empty<IClip>())
            {
                if (IsFrameInClipRange(item, targetFrame))
                {
                    clipsNeed.Add(item);
                }
            }
            clipsNeed = clipsNeed.OrderBy(x => x.LayerIndex).ToList();

            var usedFrames = new List<IPicture>();
            var framesToRender = new List<(IClip Clip, IPicture? Frame)>(clipsNeed.Count);
            foreach (var clip in clipsNeed)
            {
                IPicture frame = null!;
                int clipTargetWidth = ResolveClipOutputWidth(clip, TargetWidth, ProjectRelativeWidth);
                int clipTargetHeight = ResolveClipOutputHeight(clip, TargetHeight, ProjectRelativeHeight);
                if (clip.ClipType == ClipMode.TransformClip && clip is TransformContainer c)
                {
                    if (c.Transform == null)
                        c.ReInit(_ppb);
                    if (c.Transform is not ITransform t) throw new NullReferenceException($"Transform for clip {c.Id} is null");
                    IClip? rightClip = null;

                    if (t.TransformType != TransformType.OneInputSingleFrameTransform)
                        if (!IndexedClipList.TryGetValue(t.BindedRightClip, out rightClip)) throw new NullReferenceException($"Transform {t.Name}({t.TypeName})'s left input for clip {c.Id} is null");

                    if (!IndexedClipList.TryGetValue(t.BindedLeftClip, out IClip? leftClip)) throw new NullReferenceException($"Transform {t.Name}({t.TypeName})'s left input for clip {c.Id} is null");


                    frame = TransformProcessing.ProcessTransform(leftClip, rightClip, t, clipTargetWidth, clipTargetHeight, targetFrame, _ppb);
                }
                else
                {
                    frame = clip.GetFrame(targetFrame, clipTargetWidth, clipTargetHeight, true, _ppb);
                }
                if (frame == null)
                {
                    Log($"[Render] WARN: Frame {targetFrame} not found for clip {clip.Id}.");
                    framesToRender.Add((clip, null));
                    continue;
                }
                if (IsClipGeneratedByAI.TryGetValue(clip.IdAsGUID, out var aiMark) && aiMark)
                {
                    frame = EffectProcessing.ProcessAIWatermark(frame, null);

                }
                if (Use16Bit && frame.bitPerPixel != IPicture.PicturePixelMode.UShortPicture)
                {
                    frame = frame.ToBitPerPixel(IPicture.PicturePixelMode.UShortPicture);
                }
                else if (!Use16Bit && frame.bitPerPixel != IPicture.PicturePixelMode.BytePicture)
                {
                    frame = frame.ToBitPerPixel(IPicture.PicturePixelMode.BytePicture);
                }

                framesToRender.Add((clip, frame));
            }
            EachElapsedForPreparing.Add(prep.Elapsed);
            FramePrepareElapsed[targetFrame] = prep.Elapsed;

            RenderAFrameInternal(
                targetFrame,
                framesToRender,
                usedFrames: usedFrames,
                token);

            if (token.IsCancellationRequested) return;

            foreach (var pic in usedFrames)
            {
                try { pic?.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// Applies all effects to a single clip's frame, computes placement, handles HDR conversion,
        /// and composites the clip onto the accumulating result picture. Shared by frame-level and
        /// layer-level rendering paths.
        /// Returns the updated result (may be the same instance, a new picture, or null if cancelled).
        /// </summary>
        private IPicture? ProcessAndCompositeClip(
            IClip clip,
            IPicture frame,
            IPicture? currentResult,
            uint targetFrame,
            int layoutRelativeWidth,
            int layoutRelativeHeight,
            Dictionary<string, object> frameLocalCache,
            List<IPicture>? usedFrames,
            CancellationToken token)
        {
            if (token.IsCancellationRequested) return null;
            try
            {
                ClipPositionTuple targetPos = new(
                    clip.TargetX,
                    clip.TargetY,
                    clip.TargetWidth > 0 ? ScaleDimensionToTarget(clip.TargetWidth, layoutRelativeWidth, TargetWidth) : TargetWidth,
                    clip.TargetHeight > 0 ? ScaleDimensionToTarget(clip.TargetHeight, layoutRelativeHeight, TargetHeight) : TargetHeight,
                    false);

                if (EffectCache.TryGetValue(clip.Id, out var effects) && effects is not null)
                {
                    // Serialize per-clip effect processing: IEffect instances are shared across threads
                    // (stateful effects like ContinuousEffect would corrupt each other without this lock)
                    //var clipLock = _clipEffectLocks.GetOrAdd(clip.Id, _ => new object());
                    //lock (clipLock)
                    {
                        List<IPictureProcessStep> steps = new();
                        bool lastIsProcessStep = false, effectsChanged = false;
                        var effectCopy = effects.ToList();
                        foreach (var item in effects)
                        {
                            var computer = GetOrCreateComputer(item.NeedComputer);
                            if (item.YieldProcessStep != lastIsProcessStep && steps.Count > 0)
                            {
                                frame = PictureProcesser.Process(steps, frame, _ppb);
                                steps.Clear();
                            }

                            try
                            {
                                switch (item.TypeOfEffect)
                                {
                                    case EffectType.NormalEffect:
                                        if (item is not INormalEffect e) goto notdefined;
                                        EffectProcessing.ProcessEffect(ref frame, steps, ref lastIsProcessStep, e, computer, TargetWidth, TargetHeight);
                                        continue;
                                    case EffectType.ContinuousEffect:
                                        if (item is not IContinuousEffect c) goto notdefined;
                                        EffectProcessing.ProcessContinuousEffect(targetFrame, clip, computer, ref frame, steps, ref lastIsProcessStep, item, c, TargetWidth, TargetHeight);
                                        continue;
                                    case EffectType.BindableEffect:
                                        if (item is not IBindableArgumentEffect b) goto notdefined;
                                        if (EffectProcessing.ProcessBindableArgsEffect(targetFrame, ref frame, ref BindableEffectResultCache, frameLocalCache, clip, steps, ref lastIsProcessStep, b, computer, TargetWidth, TargetHeight))
                                        {
                                            effectCopy.Remove(item);
                                            effectsChanged = true;
                                        }
                                        continue;
                                    case EffectType.ContinuousClipPositionProvider:
                                        if (item is not IContinuousClipPositionProvider cp) goto notdefined;
                                        var pos = cp.GetPosition(clip, targetFrame, TargetWidth, TargetHeight);
                                        if (pos.IsDelta)
                                        {
                                            targetPos = new ClipPositionTuple(
                                                targetPos.TargetX + pos.TargetX,
                                                targetPos.TargetY + pos.TargetY,
                                                targetPos.TargetWidth + pos.TargetWidth,
                                                targetPos.TargetHeight + pos.TargetHeight,
                                                false);
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
                                                false);
                                        }
                                        else
                                        {
                                            targetPos = pos1;
                                        }
                                        continue;
                                    default:
                                        goto notdefined;
                                }
                            }
                            catch (NotSupportedException)
                            {
                                goto notdefined;
                            }
                            catch (NotImplementedException)
                            {
                                goto notdefined;
                            }
                            catch (InvalidOperationException)
                            {
                                goto notdefined;
                            }
                            catch (Exception ex)
                            {
                                Log(ex, $"Processing effect {item?.Name} ({item?.Id}) of clip {clip.Id}", this);
                                throw;
                            }



                        notdefined:
                            Log($"[Render] Effect {item.Name} of clip {clip.Id} has an not static defined type.", "warn");
                            if (item is IBindableArgumentEffect be)
                            {
                                if (EffectProcessing.ProcessBindableArgsEffect(targetFrame, ref frame, ref BindableEffectResultCache, frameLocalCache, clip, steps, ref lastIsProcessStep, be, computer, TargetWidth, TargetHeight))
                                {
                                    effectCopy.Remove(item);
                                    effectsChanged = true;
                                }
                            }
                            else if (item is IContinuousEffect c)
                            {
                                EffectProcessing.ProcessContinuousEffect(targetFrame, clip, computer, ref frame, steps, ref lastIsProcessStep, item, c, TargetWidth, TargetHeight);
                            }
                            else if (item is INormalEffect n)
                            {
                                EffectProcessing.ProcessEffect(ref frame, steps, ref lastIsProcessStep, n, computer, TargetWidth, TargetHeight);
                            }
                            else if (item is IClipPositionProvider p)
                            {
                                (var x, var y, var w, var h, bool delta) = p.GetPosition(clip, TargetWidth, TargetHeight);
                                if (delta)
                                {
                                    targetPos = new ClipPositionTuple(targetPos.TargetX + x, targetPos.TargetY + y, targetPos.TargetWidth + w, targetPos.TargetHeight + h, false);
                                }
                                else
                                {
                                    targetPos = new(x, y, w, h, false);
                                }
                            }
                            else if (item is IContinuousClipPositionProvider cp)
                            {
                                (var x, var y, var w, var h, bool delta) = cp.GetPosition(clip, targetFrame, TargetWidth, TargetHeight);
                                if (delta)
                                {
                                    targetPos = new ClipPositionTuple(targetPos.TargetX + x, targetPos.TargetY + y, targetPos.TargetWidth + w, targetPos.TargetHeight + h, false);
                                }
                                else
                                {
                                    targetPos = new(x, y, w, h, false);
                                }
                            }
                            else
                            {
                                throw new NotSupportedException($"The effect's ClipType {item.TypeOfEffect} {item.TypeName} of clip {clip.Id} is not supported by Render. Effect ID: {item.Id}");
                            }

                        }


                        if (steps.ListAny())
                        {
                            frame = PictureProcesser.Process(steps, frame, _ppb);
                        }

                        if (effectsChanged)
                        {
                            EffectCache[clip.Id] = effectCopy.OrderBy(c => c.Index).ToArray();
                        }
                    } // end lock (clipLock)
                }

                // Resize frame to match targetPos dimensions when they differ (replaces legacy __Internal_Resize__ effect)
                if (frame.Width != targetPos.TargetWidth || frame.Height != targetPos.TargetHeight)
                {
                    var old = frame;
                    frame = frame.Resize(targetPos.TargetWidth, targetPos.TargetHeight, true);
                    if (!ReferenceEquals(old, frame))
                    {
                        try { old.Dispose(); } catch { }
                    }
                }

                int clipX = ScaleCoordinateToTarget(targetPos.TargetX, layoutRelativeWidth, TargetWidth);
                int clipY = ScaleCoordinateToTarget(targetPos.TargetY, layoutRelativeHeight, TargetHeight);
                if (AutoCenterImplicitClip && ShouldAutoCenterImplicitClip(clip) && clipY == 0 && frame.Height < TargetHeight)
                {
                    clipY += (TargetHeight - frame.Height) / 2;
                }
                bool needsPlacement = clipX != 0 || clipY != 0 || frame.Width != TargetWidth || frame.Height != TargetHeight;
                if (UseHDR)
                {
                    if (frame is IHDRPicture<ushort> u)
                    {
                        // Preserve real HDR peak metadata. Only repair invalid values.
                        if (!float.IsFinite(u.MaximumBrightness) || u.MaximumBrightness <= 0)
                        {
                            u.MaximumBrightness = MaximumHDRBrightness;
                        }
                    }
                    else
                    {
                        frame = frame.ToHDRPictureBySignal(PerClipHDRBrightness.TryGetValue(clip.IdAsGUID, out var b) ? b : SDRClipsBrightnessInHDRMode);
                    }


                }
                if (currentResult is null)
                {
                    if (!needsPlacement)
                    {
                        // Single-clip fast path: ownership stays with builder queue.
                        return frame;
                    }
                    else
                    {
                        var mixer = clip.MixtureInstance ?? ClassicOverlayMixture.Default;
                        var computer = GetOrCreateComputer(mixer.NeedComputer ?? ClassicOverlayMixture.ComputerId);
                        var mixResult = mixer.Mix(BlankFrame, frame, computer, _ppb, clipX, clipY, TargetWidth, TargetHeight);
                        if (usedFrames is null)
                            try { frame.Dispose(); } catch { }
                        return mixResult;
                    }
                }
                else
                {
                    var mixer = clip.MixtureInstance ?? ClassicOverlayMixture.Default;
                    var computer = GetOrCreateComputer(mixer.NeedComputer ?? ClassicOverlayMixture.ComputerId);
                    var temp = mixer.Mix(currentResult, frame, computer, _ppb, clipX, clipY, TargetWidth, TargetHeight);
                    currentResult.Dispose();
                    if (usedFrames is null)
                        try { frame.Dispose(); } catch { }
                    return temp;
                }
            }
            catch (Exception ex)
            {
                Log(ex, $"internal render logic for {targetFrame}", this);
                throw;
            }
        }

        private void RenderAFrameInternal(
            uint targetFrame,
            List<(IClip Clip, IPicture? Frame)> clipsNeed,
            List<IPicture>? usedFrames,
            CancellationToken token)
        {
            Stopwatch sw = Stopwatch.StartNew();
            IPicture result = null!;
            Dictionary<string, object> frameLocalCache = new();
            int layoutRelativeWidth = ProjectRelativeWidth > 0 ? ProjectRelativeWidth : TargetWidth;
            int layoutRelativeHeight = ProjectRelativeHeight > 0 ? ProjectRelativeHeight : TargetHeight;

            foreach (var (clip, Frame) in clipsNeed)
            {
                var frame = Frame;
                if (token.IsCancellationRequested) return;

                if (frame == null)
                {
                    continue;
                }

                usedFrames?.Add(frame);

                result = ProcessAndCompositeClip(clip, frame, result, targetFrame, layoutRelativeWidth, layoutRelativeHeight, frameLocalCache, usedFrames, token);
                if (result is null) return; // cancelled
            }

            if (result is null)
            {
                result = BlankFrame;
            }
            else if (result.Width < TargetWidth || result.Height < TargetHeight)
            {
                // Bug fix: BlankPlace was a shared instance, not thread-safe under concurrent render
                result = _threadLocalBlankPlace.Value!.Render(result, null, TargetWidth, TargetHeight);
            }
            else if (result.Width > TargetWidth || result.Height > TargetHeight)
            {
                result = result.Resize(TargetWidth, TargetHeight, false);
            }

            builder!.Append(targetFrame, result);
            Interlocked.Increment(ref Finished);
            sw.Stop();
            if (LogProcessStack)
            {
                FrameProcessStacks[targetFrame] = result.ProcessStack;
                FrameDirtyTime[targetFrame] = sw.Elapsed - TimeSpan.FromTicks(result.ProcessStack.Where(c => c.Elapsed is not null).Sum(c => c.Elapsed!.Value.Ticks));
            }
            InvokeProgress();
            if (LogRenderState) Log($"[Render] Frame {targetFrame} render done, elapsed {sw.Elapsed}, dirty time {FrameDirtyTime[targetFrame]}");
            EachElapsed.Add(sw.Elapsed);
            FrameRenderElapsed[targetFrame] = sw.Elapsed;
        }

        #region layer-by-layer render helpers

        /// <summary>
        /// Renders all clips in a single layer (identified by layerOrdinal within a frame)
        /// by applying effects and compositing them together. Stores the result and checks
        /// whether the frame is fully assembled.
        /// </summary>
        private void RenderALayer(uint frame, int layerOrdinal, LayerGroup group, CancellationToken token)
        {
            if (!ClipNeedForFrame.TryGetValue(frame, out var allClips))
            {
                Log($"[LayerRender] WARN: Frame {frame} not found in ClipNeedForFrame.", "warn");
                OnLayerComplete(frame, layerOrdinal, null);
                return;
            }

            var layerClips = new List<(IClip Clip, IPicture? Frame)>(group.Count);
            for (int i = group.StartIndex; i < group.StartIndex + group.Count; i++)
            {
                if (i >= allClips.Length) break;
                var clip = allClips[i];

                if (!FrameCache.TryGetValue(clip.Id, out var perClipCache)
                    || !perClipCache.TryGetValue(frame, out var pic)
                    || pic is null)
                {
                    throw new NullReferenceException(
                        $"Frame cache not found for clip {clip.Id} at frame {frame}. Preparer should have prepared it.");
                }

                layerClips.Add((clip, pic));
            }

            IPicture? layerResult = RenderLayerClips(frame, layerClips, token);
            OnLayerComplete(frame, layerOrdinal, layerResult);
        }

        /// <summary>
        /// Composites a list of (clip, frame) pairs belonging to one layer into a single IPicture.
        /// Uses ProcessAndCompositeClip for per-clip effect processing and compositing.
        /// </summary>
        private IPicture? RenderLayerClips(
            uint frame,
            List<(IClip Clip, IPicture? Frame)> clipsNeed,
            CancellationToken token)
        {
            if (clipsNeed.Count == 0) return null;

            IPicture? result = null;
            Dictionary<string, object> frameLocalCache = new();
            int layoutRelativeWidth = ProjectRelativeWidth > 0 ? ProjectRelativeWidth : TargetWidth;
            int layoutRelativeHeight = ProjectRelativeHeight > 0 ? ProjectRelativeHeight : TargetHeight;

            foreach (var (clip, framePic) in clipsNeed)
            {
                if (token.IsCancellationRequested) return result;
                if (framePic == null) continue;

                result = ProcessAndCompositeClip(clip, framePic, result, frame,
                    layoutRelativeWidth, layoutRelativeHeight, frameLocalCache,
                    usedFrames: null, // layer rendering always disposes frames immediately
                    token);
                if (result is null) return null; // cancelled
            }

            return result;
        }

        /// <summary>
        /// Called when a layer finishes rendering. Stores the layer result and atomically
        /// checks if all layers for the frame are done. The last layer to complete
        /// triggers frame assembly and submission.
        /// </summary>
        private void OnLayerComplete(uint frame, int layerOrdinal, IPicture? layerResult)
        {
            if (!FrameLayerCompletions!.TryGetValue(frame, out var completion))
            {
                try { layerResult?.Dispose(); } catch { }
                return;
            }

            completion.LayerResults![layerOrdinal] = layerResult;
            int completed = Interlocked.Increment(ref completion.CompletedLayers);

            if (completed == completion.TotalLayers)
            {
                TryAssembleAndSubmitFrame(frame, completion);
            }
        }

        /// <summary>
        /// Merges all rendered layer pictures for a frame (in layer group order) and submits
        /// the final composited result to the builder. Called by the last layer to complete.
        /// </summary>
        private void TryAssembleAndSubmitFrame(uint frame, FrameLayerCompletion completion)
        {
            ArgumentNullException.ThrowIfNull(builder, nameof(builder));

            IPicture? merged = null;
            try
            {
                ClipNeedForFrame.TryGetValue(frame, out var allClips);
                FrameLayerGroups!.TryGetValue(frame, out var layerGroups);

                for (int layerIdx = 0; layerIdx < completion.LayerResults!.Length; layerIdx++)
                {
                    var layerPic = completion.LayerResults[layerIdx];
                    if (layerPic == null) continue;

                    var mixer = GetLayerMixer(allClips, layerGroups, layerIdx);
                    var computer = GetOrCreateComputer(mixer.NeedComputer ?? ClassicOverlayMixture.ComputerId);

                    if (merged == null)
                    {
                        merged = mixer.Mix(BlankFrame, layerPic, computer, _ppb);
                    }
                    else
                    {
                        var temp = mixer.Mix(merged, layerPic, computer, _ppb);
                        merged.Dispose();
                        merged = temp;
                    }
                }

                if (merged == null)
                {
                    merged = BlankFrame;
                }

                if (merged.Width < TargetWidth || merged.Height < TargetHeight)
                {
                    merged = _threadLocalBlankPlace.Value!.Render(merged, null, TargetWidth, TargetHeight);
                }
                else if (merged.Width > TargetWidth || merged.Height > TargetHeight)
                {
                    merged = merged.Resize(TargetWidth, TargetHeight, false);
                }

                builder.Append(frame, merged);

                // Clean up frame cache entries for this frame
                if (ClipNeedForFrame.TryGetValue(frame, out var clips))
                {
                    foreach (var clip in clips)
                    {
                        if (FrameCache.TryGetValue(clip.Id, out var perClipCache))
                            perClipCache.TryRemove(frame, out _);
                    }
                }
            }
            finally
            {
                // Dispose individual layer results (the merged result is owned by builder)
                foreach (var layerPic in completion.LayerResults!)
                {
                    if (!ReferenceEquals(layerPic, merged))
                        try { layerPic?.Dispose(); } catch { }
                }

                FrameLayerCompletions!.TryRemove(frame, out _);

                Interlocked.Increment(ref Finished);
                InvokeProgress();
            }
        }

        #endregion

        #endregion

        #region misc

        private static bool IsFrameInClipRange(IClip clip, uint targetFrame)
            => clip.ContainsFrame(targetFrame);

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

        private static int ResolveClipOutputX(IClip clip, int targetWidth, int projectRelativeWidth)
            => ScaleCoordinateToTarget(clip.TargetX, projectRelativeWidth, targetWidth);

        private static int ResolveClipOutputY(IClip clip, int targetHeight, int projectRelativeHeight)
            => ScaleCoordinateToTarget(clip.TargetY, projectRelativeHeight, targetHeight);

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

        public void InitializeRenderCaches()
        {
            ArgumentNullException.ThrowIfNull(builder, nameof(builder));

            _ppb = Use16Bit ? 16 : 8;
            TargetWidth = builder.Width;
            TargetHeight = builder.Height;
            ProjectRelativeWidth = ProjectRelativeWidth > 0 ? ProjectRelativeWidth : TargetWidth;
            ProjectRelativeHeight = ProjectRelativeHeight > 0 ? ProjectRelativeHeight : TargetHeight;

            if (UseHDR)
            {
                BlankFrame = HDRPicture16bpp.GenerateSolidColor(builder.Width, builder.Height, 0, 0, 0, 0, SDRClipsBrightnessInHDRMode);
            }
            else if (Use16Bit)
            {
                BlankFrame = Picture16bpp.GenerateSolidColor(builder.Width, builder.Height, 0, 0, 0, 0);
            }
            else
            {
                BlankFrame = Picture8bpp.GenerateSolidColor(builder.Width, builder.Height, 0, 0, 0, 0);
            }
            BlankFrame.CanBeDisposed = false;
            GC.KeepAlive(BlankFrame);

            if (MinSchedulePreparedFrames <= 0) MinSchedulePreparedFrames = MaxThreads;
            if (ThrottleThreshold <= 0) ThrottleThreshold = MaxThreads;
            if (MinSchedulePreparedFrames > ThrottleThreshold)
            {
                Log($"[Preparer] MinSchedulePreparedFrames ({MinSchedulePreparedFrames}) exceeds prepare throttle threshold ({ThrottleThreshold}); clamped to avoid scheduler/preparer deadlock.", "warn");
                MinSchedulePreparedFrames = ThrottleThreshold;
            }
            if (SDRClipsBrightnessInHDRMode > MaximumHDRBrightness) SDRClipsBrightnessInHDRMode = MaximumHDRBrightness;
            EffectCache.Clear();
            foreach (var item in Clips ?? Array.Empty<IClip>())
            {
                item.ReInit(_ppb);
                bool isAI = false;
                if (item.ExtraData.TryGetValue("IsAI", out var aiMark))
                {
                    if (aiMark is bool) isAI = (bool)aiMark;
                    else if (aiMark is string s && bool.TryParse(s, out var parsed)) isAI = parsed;
                    else if (aiMark is JsonElement je && je.ValueKind == JsonValueKind.True) isAI = true;
                }
                if (isAI) IsClipGeneratedByAI.TryAdd(item.IdAsGUID, isAI);
                var effectInstances = EffectHelper.GetEffectsInstances(item.Effects);

                if (HasExplicitTargetRect(item))
                {
                    effectInstances = effectInstances.Where(effect => effect is not null && !IsLegacyInternalLayoutEffect(effect)).ToArray();
                }

                EffectCache.AddOrUpdate(item.Id, effectInstances, (_, _) => effectInstances);
                foreach (var effect in effectInstances)
                {
                    if (effect.YieldProcessStep == true && effect.NeedComputer is not null)
                        throw new InvalidDataException("A effect can't both yield process step, and use a computer.");
                }

                Log($"[Preparer] Cached {effectInstances.Length} effects for clip {item.Id} ({string.Join(", ", effectInstances.Select(c => $"{c.TypeName}:'{c.Name}'"))})");

            }

            mixComputer = GetOrCreateComputer(ClassicOverlayMixture.ComputerId) ?? throw new NullReferenceException("Can't create computer for global mixer.");

            IndexedClipList = (Clips ?? Array.Empty<IClip>()).ToDictionary(c => Guid.TryParse(c.Id, out var result) ? result : throw new InvalidDataException($"Clip {c.Name}({c.Id}) has an invalid Id. Id should be a GUID."));
            PerClipHDRBrightness = (Clips ?? Array.Empty<IClip>()).ToDictionary(c => Guid.TryParse(c.Id, out var result) ? result : throw new InvalidDataException($"Clip {c.Name}({c.Id}) has an invalid Id. Id should be a GUID."), c => c.ExtraData.TryGetValue("HDRBrightness", out var value) ? Convert.ToInt32(value) : SDRClipsBrightnessInHDRMode);

        }

        private void InvokeProgress()
        {
            double prog = (double)Volatile.Read(ref Finished) / Duration;
            var elapsed = _renderTotalStopwatch.Elapsed;
            double fps = elapsed.TotalSeconds > 0 ? Volatile.Read(ref Finished) / elapsed.TotalSeconds : 0;
            Interlocked.Exchange(ref _currentFps, fps);
            OnProgressChanged?.Invoke(prog, GetEstimated(prog));
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

        private IComputer? GetOrCreateComputer(string? computerType)
        {
            if (computerType is null) return null;

            var cache = _threadLocalComputerCache.Value;
            if (cache != null && cache.TryGetValue(computerType, out var computer))
                return computer;

            // Create new computer for this thread
            var newComputer = PluginManager.CreateComputer(computerType, forceCreate: true);
            if (newComputer != null && cache != null)
            {
                cache[computerType] = newComputer;
            }
            return newComputer;
        }

        private void ReleaseResources()
        {
            try
            {
                running = false;
                Log("Release resources...");
                try
                {
                    // Dispose any cached frames that were prepared but never consumed.
                    foreach (var perClip in FrameCache.Values)
                    {
                        foreach (var pic in perClip.Values)
                        {
                            try { pic?.Dispose(); } catch { }
                        }
                        perClip.Clear();
                    }
                }
                catch { }

                FrameCache.Clear();
                ClipNeedForFrame.Clear();

                try
                {
                    foreach (var effects in EffectCache.Values)
                    {
                        foreach (var eff in effects)
                        {
                            if (eff is IDisposable d)
                            {
                                try { d.Dispose(); } catch { }
                            }
                        }
                    }
                }
                catch { }
                EffectCache.Clear();

                try { BlankFrame?.Dispose(); } catch { }

                // Clean up thread-local computer cache
                try { _threadLocalComputerCache?.Dispose(); } catch { }

                // Clean up thread-local BlankPlace
                try { _threadLocalBlankPlace?.Dispose(); } catch { }

                _clipEffectLocks.Clear();

                // Clean up thread limiter
                try { _threadLimiter?.Dispose(); } catch { }

                PreparedFlag.Clear();
                while (PreparedFrames.TryDequeue(out _)) { }
                while (BlankFrames.TryDequeue(out _)) { }

                // Clean up layer-by-layer render state
                if (LayerResults != null)
                {
                    foreach (var kv in LayerResults)
                    {
                        try { kv.Value?.Dispose(); } catch { }
                    }
                    LayerResults.Clear();
                }
                FrameLayerCompletions?.Clear();
                FrameLayerGroups?.Clear();
            }
            catch { }

        }

        private void FlushBlankFramesBefore(uint frameIndex, CancellationToken token)
        {
            try
            {
                ArgumentNullException.ThrowIfNull(builder, nameof(builder));
                while (!token.IsCancellationRequested && BlankFrames.TryPeek(out var head) && head < frameIndex)
                {
                    if (!BlankFrames.TryDequeue(out var blankIdx))
                        break;

                    if (blankIdx >= frameIndex)
                    {
                        BlankFrames.Enqueue(blankIdx);
                        break;
                    }

                    builder!.Append(blankIdx, BlankFrame);
                    EachElapsed.Add(TimeSpan.Zero);
                    FramePrepareElapsed.TryAdd(blankIdx, TimeSpan.Zero);
                    FrameRenderElapsed.TryAdd(blankIdx, TimeSpan.Zero);
                    Interlocked.Increment(ref Finished);
                    InvokeProgress();
                    Log($"[Render] Wrote blank frame {blankIdx} before starting frame {frameIndex}.");
                }
            }
            catch (Exception ex)
            {
                Log(ex, $"Write blank frames", this);
                throw;
            }
        }

        /// <summary>
        /// Returns the <see cref="IMixture"/> to use when compositing a layer onto the accumulated
        /// frame result. Uses the first non-null MixtureInstance from any clip in the layer;
        /// falls back to <see cref="ClassicOverlayMixture.Default"/>.
        /// </summary>
        private static IMixture GetLayerMixer(IClip[]? allClips, LayerGroup[]? layerGroups, int layerIdx)
        {
            if (allClips != null && layerGroups != null && layerIdx < layerGroups.Length)
            {
                var group = layerGroups[layerIdx];
                for (int i = group.StartIndex; i < group.StartIndex + group.Count && i < allClips.Length; i++)
                {
                    if (allClips[i].MixtureInstance != null)
                        return allClips[i].MixtureInstance;
                }
            }
            return ClassicOverlayMixture.Default;
        }

        /// <summary>
        /// Describes a contiguous group of clips sharing the same LayerIndex within a frame.
        /// </summary>
        internal struct LayerGroup
        {
            public uint LayerIndex;
            public int StartIndex;
            public int Count;
        }

        /// <summary>
        /// Tracks per-frame layer completion for atomic coordination across ThreadPool workers.
        /// </summary>
        internal class FrameLayerCompletion
        {
            public int TotalLayers;
            public int CompletedLayers;
            public IPicture?[] LayerResults = null!;
        }

        #endregion

    }
}
