using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Context;
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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace projectFrameCut.Render.Rendering
{
    public readonly record struct ChunkRenderProgress(
        int ChunkIndex,
        int ChunkCount,
        int FinishedFrames,
        uint ChunkFrames,
        double ChunkProgress,
        double GlobalProgress,
        TimeSpan EstimatedRemaining);

    public class Renderer : IRenderContext
    {
        #region opts
        public const int SubTrackOffset = 10000;

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

        public bool OneByOneRender { get; set; } = false;
        public bool RenderByLayers { get; set; } = false;
        public bool PrepareInWorkerThreads { get; set; } = false;
        public bool EnableThreadAffinity { get; set; } = false;
        public int[]? WorkerCPUCoreIndexs { get; set; }
        public int MaxThreads { get => field > 0 ? field : (int)(Environment.ProcessorCount * 1.75); set; }
        public int GCOption = 0;

        public bool EnableGPUBatchProcess { get; set; } = true;
        public bool AllowReorderEffect { get; set; } = true;
        public bool EnableEffectAutoRetry { get; set; } = true;

        public int MaxRenderScheduleTimeout { get; set; } = 500;
        public int MinSchedulePreparedFrames { get => field > 0 ? field : MaxThreads; set; }
        public int RenderWatchdogNoProgressTimeoutMs { get => field > 0 ? field : 60_000; set; } = 60_000;
        public bool EnableRenderWatchdogForceStart { get; set; } = true;
        public double RenderWorkerLaunchUtilizationThreshold { get => field > 0 ? field : 1.0; set; } = 1.0;
        public int RenderSchedulerPreparePollDelayMs { get => field > 0 ? field : 5; set; } = 5;
        public int RenderSchedulerIdleDelayMs { get => field > 0 ? field : 10; set; } = 10;
        public int MinRemainingFramesForPreparedWait { get => field >= 0 ? field : Math.Max(0, MaxThreads / 2 - 2); set; } = -1;
        public int ThrottleThreshold { get => field > 0 ? field : Math.Max(MaxThreads * 4, MaxThreads + 8); set; }
        public int MaxPendingWriteFrames { get => field > 0 ? field : Math.Max(ThrottleThreshold * 2, 32); set; }
        public bool BlockPreparingBeforeRendering { get; set; } = false;
        public bool DisableAllThrottleOptions { get; set; } = false;

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

        private bool running { get; set; } = false;

        ConcurrentDictionary<Guid, ConcurrentDictionary<uint, IPicture>> FrameCache = new();
        ConcurrentDictionary<string, IPicture> ImmutableContentCache = new();
        ConcurrentDictionary<uint, IClip[]> ClipNeedForFrame = new();

        // Layer-by-layer render: maps frame → ordered layer groups within that frame
        Dictionary<uint, LayerGroup[]>? FrameLayerGroups;
        // Layer-by-layer render: tracks per-frame layer completion state
        ConcurrentDictionary<uint, FrameLayerCompletion>? FrameLayerCompletions;
        // Layer-by-layer render: stores rendered layer results before merge
        ConcurrentDictionary<(uint Frame, int LayerOrdinal), IPicture>? LayerResults;

        ConcurrentDictionary<Guid, IEffect[]> EffectCache = new();
        ConcurrentDictionary<string, object> BindableEffectResultCache = new();

        int ThreadWorking = 0, Finished = 0;
        private SemaphoreSlim _threadLimiter = null!;

        ConcurrentQueue<uint> PreparedFrames = new(), BlankFrames = new();
        /// <summary>
        /// Per-frame prepared flag array indexed by (frame - StartFrame).
        /// 0 = not prepared, 1 = prepared-and-queued, 2 = pre-cached by RenderSpecificFrame.
        /// Uses <see cref="Interlocked.CompareExchange"/> for atomic access.
        /// </summary>
        int[] _preparedFlagArray = [];

        int TotalEnqueued = 0;
        volatile bool PreparerFinished = false;

        private int _ppb;

        private IPicture BlankFrame = null!;

        // Thread-local: PlaceEffect_HwAccel has mutable state and is not thread-safe
        private ThreadLocal<PlaceEffect_HwAccel> _threadLocalBlankPlace =
            new(() => new PlaceEffect_HwAccel { StartX = 0, StartY = 0 });

        // Thread-local pool for frame-level cache dictionaries to reduce GC pressure
        private ThreadLocal<Stack<Dictionary<string, object>>> _frameLocalCachePool =
            new(() => new Stack<Dictionary<string, object>>(4));

        // Caches whether a computer type supports GPU batching, avoiding per-frame computer lookups
        private static readonly ConcurrentDictionary<string, bool> ComputerBatchSupportCache = new();

        // Running totals for O(1) average elapsed statistics (avoids scanning the bags on every stat log)
        private long _renderElapsedTicksTotal; 
        private int _renderElapsedCount;
        private long _prepareElapsedTicksTotal; 
        private int _prepareElapsedCount;

        public static bool IsProfilerAttached =>
            string.Equals(Environment.GetEnvironmentVariable("COR_ENABLE_PROFILING"), "1", StringComparison.Ordinal);

        #endregion

        #region prepare
        public void PrepareRender(CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(Clips, nameof(Clips));
            if (AutoSetupRenderContext) IRenderContext.Current = this;
            Log($"[Preparer] Calculating clip visibility for {Duration} frames...");
            var clipsForFrame = new List<IClip>(Clips.Length);
            for (uint idx = StartFrame; idx < StartFrame + Duration; idx++)
            {
                clipsForFrame.Clear();
                if (token.IsCancellationRequested) return;
                foreach (var item in Clips)
                {
                    if (token.IsCancellationRequested) return;

                    if (item.ContainsFrame(idx) || (item.ExtendToWholeDraft && item.LayerIndex > SubTrackOffset))
                    {
                        clipsForFrame.Add(item);
                    }
                }

                if (clipsForFrame.Count > 0)
                {
                    // Sort once per frame instead of re-sorting on every AddOrUpdate
                    ClipNeedForFrame[idx] = clipsForFrame
                        .OrderBy(x => x.LayerIndex >= SubTrackOffset ? 1 : 0)
                        .ThenByDescending(x => x.LayerIndex)
                        .ThenByDescending(x => x.SubLayerIndex)
                        .ToArray();
                }
                else
                {
                    BlankFrames.Enqueue(idx);
                    Interlocked.Increment(ref TotalEnqueued);
                }

            }
            InitializeRenderCaches();
            Log($"[Preparer] source preparing done.");
            if (builder is null) Log("Builder is null, nothing will be written to output file.", "warn");

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

                // Update per-thread state: preparer is working on this frame
                var _wsPrep = IRenderContext.WorkerState;
                if (_wsPrep is not null)
                {
                    _wsPrep.CurrentFrame = idx;
                    _wsPrep.CurrentClip = null;
                }

                sw.Restart();

                foreach (var item in ClipNeedForFrame[idx])
                {
                    if (token.IsCancellationRequested) return;

                    var frame = DecodeClipSourceFrame(item, idx, ppb);
                    if (frame != null)
                    {
                        FrameCache.GetOrAdd(item.Id, (_) => new()).TryAdd(idx, frame);
                    }
                }
                int prepFlagArrayIdx = (int)(idx - StartFrame);
                if (prepFlagArrayIdx >= 0 && prepFlagArrayIdx < _preparedFlagArray.Length && Interlocked.CompareExchange(ref _preparedFlagArray[prepFlagArrayIdx], 1, 0) == 0)
                {
                    PreparedFrames.Enqueue(idx);
                    Interlocked.Increment(ref TotalEnqueued);
                }
                sw.Stop();
                TrackPrepareElapsed(sw.Elapsed);
                FramePrepareElapsed[idx] = sw.Elapsed;
                if (LogRenderState || BlockPreparingBeforeRendering) Log($"[Preparer] Frame {idx} is ready to render, elapsed {sw.Elapsed}");

            }
            Log($"[Preparer] All frames are ready.");
        }

        private void InitializeRenderCaches()
        {
            _preparedFlagArray = new int[Duration];
            _ppb = Use16Bit ? 16 : 8;
            if (builder is not null)
            {
                TargetWidth = builder.Width;
                TargetHeight = builder.Height;
            }
            ProjectRelativeWidth = ProjectRelativeWidth > 0 ? ProjectRelativeWidth : TargetWidth;
            ProjectRelativeHeight = ProjectRelativeHeight > 0 ? ProjectRelativeHeight : TargetHeight;

            if (UseHDR)
            {
                BlankFrame = HDRPicture16bpp.GenerateSolidColor(TargetWidth, TargetHeight, 0, 0, 0, 0, 0);
            }
            else if (Use16Bit)
            {
                BlankFrame = Picture16bpp.GenerateSolidColor(TargetWidth, TargetHeight, 0, 0, 0, 0);
            }
            else
            {
                BlankFrame = Picture8bpp.GenerateSolidColor(TargetWidth, TargetHeight, 0, 0, 0, 0);
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

            // Convert TextClip instances without IContinuousTextEffect to ImmutableContentTextClip
            // so their frame content can be cached and reused across frames.
            for (int clipIdx = 0; clipIdx < (Clips?.Length ?? 0); clipIdx++)
            {
                if (Clips[clipIdx] is TextClip textClip && textClip is not ImmutableContentTextClip)
                {
                    // Ensure EffectsInstances is populated so we can check for IContinuousTextEffect
                    textClip.ReInit(_ppb);
                    bool hasContinuousTextEffect = textClip.EffectsInstances?.Any(e => e is IContinuousTextEffect) == true;
                    if (!hasContinuousTextEffect)
                    {
                        Clips[clipIdx] = new ImmutableContentTextClip
                        {
                            Id = textClip.Id,
                            Name = textClip.Name,
                            LayerIndex = textClip.LayerIndex,
                            SubLayerIndex = textClip.SubLayerIndex,
                            StartFrame = textClip.StartFrame,
                            RelativeStartFrame = textClip.RelativeStartFrame,
                            Duration = textClip.Duration,
                            FrameTime = textClip.FrameTime,
                            SecondPerFrameRatio = textClip.SecondPerFrameRatio,
                            Effects = textClip.Effects,
                            EffectProviders = textClip.EffectProviders,   // 必须带上，否则转换后 provider 绑定数据丢失
                            ExtraData = textClip.ExtraData,
                            ExtendToWholeDraft = textClip.ExtendToWholeDraft,
                            BindedSoundTrack = textClip.BindedSoundTrack,
                            TextEntries = textClip.TextEntries,
                            FontPath = textClip.FontPath,
                            TargetWidth = textClip.TargetWidth,
                            TargetHeight = textClip.TargetHeight,
                            TargetX = textClip.TargetX,
                            TargetY = textClip.TargetY,
                            MixtureInstance = textClip.MixtureInstance,
                            SpeedVarianceProviderInstance = textClip.SpeedVarianceProviderInstance,
                            ClipAntiAliasMode = textClip.ClipAntiAliasMode,
                        };
                        Log($"[Preparer] Converted TextClip '{textClip.Name}' ({textClip.Id}) to ImmutableContentTextClip (no IContinuousTextEffect).");
                    }
                }
            }

            foreach (var item in Clips ?? Array.Empty<IClip>())
            {
                item.ReInit(_ppb);
                bool isAI = false;
                if (item.ExtraData.TryGetValue("IsAI", out var aiMark))
                {
                    if (aiMark is bool) isAI = (bool)aiMark;
                    else if (aiMark is string s && bool.TryParse(s, out var parsed)) isAI = parsed;
                    else if (aiMark is JsonElement je && je.ValueKind == JsonValueKind.True) isAI = true;
                    Log($"[Preparer] Clip {item.Id} ({item.Name}) is marked as AI-generated.");
                }
                if (isAI) IsClipGeneratedByAI.TryAdd(item.Id, isAI);
                // 优先从 EffectProviders 重建（保留动态绑定，值提供器被内联进消费者字段），无 provider 时回退静态 Effects。
                var effectInstances = EffectHelper.GetClipEffectsInstances(item);

                if (HasExplicitTargetRect(item))
                {
                    effectInstances = effectInstances.Where(effect => effect is not null && !IsLegacyInternalLayoutEffect(effect)).ToArray();
                }

                if (AllowReorderEffect)
                    effectInstances = ReorderEffectsForGpuBatching(effectInstances);

                if (item.AlternativeSource is ISourceReplacementEffect sre)
                {
                    sre.ProjectFrameRate = builder?.Writer.FramePerSecond ?? 0;
                }

                EffectCache.AddOrUpdate(item.Id, effectInstances, (_, _) => effectInstances);

                Log($"[Preparer] Cached {effectInstances.Length} effects for clip {item.Id} ({string.Join(", ", effectInstances.Select(c => $"{c.TypeName}:'{c.Name}'@{c.ImplementType}"))})");

            }

            IndexedClipList = (Clips ?? Array.Empty<IClip>()).ToDictionary(c => c.Id);
            PerClipHDRBrightness = (Clips ?? Array.Empty<IClip>()).ToDictionary(c => c.Id, c => c.ExtraData.TryGetValue("HDRBrightness", out var value) ? Convert.ToInt32(value) : SDRClipsBrightnessInHDRMode);

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

            ArgumentNullException.ThrowIfNull(Clips, nameof(Clips));
            if ((ClipNeedForFrame.IsEmpty && BlankFrames.IsEmpty) || Duration <= 0)
            {
                throw new InvalidOperationException("Either the project is empty, Duration is not set, or PrepareRender is not called yet. Please ensure that the project has clips and call PrepareRender before rendering.");
            }
            _renderTotalStopwatch.Restart();
            if (AutoSetupRenderContext) IRenderContext.Current = this;
            if (OneByOneRender || builder?.BlockWrite == true || MaxThreads == 1)
            {
                await GoRenderSync(token);
            }
            else if (PrepareInWorkerThreads)
            {
                await GoRenderWithWorkerDecode(token);
            }
            else if (RenderByLayers)
            {
                await GoRenderByLayerAsync(token);
            }
            else
            {
                await GoBackgroundPreparerRender(token);
            }
        }

        /// <summary>
        /// Writer backpressure throttle: if <see cref="VideoBuilder"/> has accumulated too many
        /// unwritten frames (<see cref="MaxPendingWriteFrames"/>), pause the render scheduler
        /// until at least 50% of the threshold have been flushed to disk by the write thread.
        /// Returns <c>false</c> if the caller should abort due to cancellation.
        /// No-op when <see cref="DisableAllThrottleOptions"/> is true, <see cref="builder"/> is null,
        /// or the pending count is already within the limit.
        /// </summary>
        private async Task<bool> CheckWriterBackpressureAsync(int pollDelayMs, CancellationToken token)
        {
            if (DisableAllThrottleOptions || builder is null)
                return true;

            int pending = builder.PendingWriteCount;
            int maxPending = MaxPendingWriteFrames;
            if (pending <= maxPending)
                return true;

            Log($"[Render] Writer backpressure: {pending} frames pending write (limit {maxPending}). Pausing render until write thread catches up...", "warn");

            while (!token.IsCancellationRequested)
            {
                await Task.Delay(pollDelayMs, token);
                if (builder.PendingWriteCount <= 10)
                {
                    Log($"[Render] Writer backpressure resolved: {builder.PendingWriteCount} frames pending, resuming render.", "info");
                    return true;
                }
            }
            return false;
        }

        #region scheduler helpers

        private readonly record struct SchedulerConfig(
            int WatchdogTimeoutMs,
            double LaunchUtilizationThreshold,
            int PreparePollDelayMs,
            int IdleDelayMs,
            int MinRemainingFramesForPreparedWait,
            int MaxRenderScheduleTimeoutValue);

        private SchedulerConfig ResolveSchedulerConfig()
        {
            int watchdog = RenderWatchdogNoProgressTimeoutMs > 0 ? RenderWatchdogNoProgressTimeoutMs : 60_000;
            double util = RenderWorkerLaunchUtilizationThreshold;
            if (double.IsNaN(util) || double.IsInfinity(util) || util <= 0) util = 1.0;
            if (util > 1.0) util = 1.0;
            int preparePollMs = RenderSchedulerPreparePollDelayMs > 0 ? RenderSchedulerPreparePollDelayMs : 5;
            int idleMs = RenderSchedulerIdleDelayMs > 0 ? RenderSchedulerIdleDelayMs : 10;
            int minPrepared = MinRemainingFramesForPreparedWait >= 0
                ? MinRemainingFramesForPreparedWait
                : Math.Max(0, MaxThreads / 2 - 2);
            int maxTimeout = MaxRenderScheduleTimeout > 0 ? MaxRenderScheduleTimeout : 500;
            return new(watchdog, util, preparePollMs, idleMs, minPrepared, maxTimeout);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void HandleGarbageCollection()
        {
            if (GCOption == 2)
            {
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForFullGCComplete();
            }
            else if (GCOption == 1)
            {
                GC.Collect();
            }
        }

        private static void HandleSchedulerExceptions(ConcurrentQueue<Exception> exceptions, string contextMessage)
        {
            if (exceptions.IsEmpty) return;
            var list = new List<Exception>();
            while (exceptions.TryDequeue(out var ex)) list.Add(ex);
            if (list.Count == 1) throw list.First();
            throw new AggregateException(contextMessage, list);
        }

        private void StartStatLoggerThread(CancellationToken token, bool includeQueueAndWriterStats)
        {
            new Thread(() =>
            {
                while (running)
                {
                    try
                    {
                        if (token.IsCancellationRequested) return;
                        Log(GetRendererStatusInfo(includeQueueAndWriterStats), "STAT");
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

        #endregion

        private async Task GoBackgroundPreparerRender(CancellationToken token)
        {
            // Initialize thread limiter
            _threadLimiter = new SemaphoreSlim(MaxThreads, MaxThreads);
            ConcurrentQueue<Exception> exceptions = new();

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
            if (LogStaticsData) StartStatLoggerThread(token, includeQueueAndWriterStats: true);

            Thread preparer = new(() =>
            {
                if (AutoSetupRenderContext) IRenderContext.Current = this;
                IRenderContext.SetWorkerState(0, RenderWorkerStage.PreparingSource, "Preparer");
                if (EnableThreadAffinity)
                {
                    try
                    {
                        var core = ThreadAffinityHelper.GetCpuCoreGroups().OrderBy(c => (c.EfficiencyClass ?? 0) + (c.Capacity ?? 0)).FirstOrDefault();
                        if (EnableThreadAffinity && core != null)
                        {
                            ThreadAffinityHelper.SetCurrentThreadAffinity(core.CpuIndexes.ToArray());
                            Log($"[Preparer] Set preparer thread affinity to CPU cores: {string.Join(", ", core.CpuIndexes)} (efficiency class: {core.EfficiencyClass}, capacity: {core.Capacity})");
                        }
                    }
                    catch { }
                }
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
                    IRenderContext.ClearWorkerState();
                    if (AutoSetupRenderContext) IRenderContext.Current = null;
                }
            })
            {
                Name = "Preparer thread",
                IsBackground = true
            };
            preparer.Start();

            if (BlockPreparingBeforeRendering)
            {
                Log($"[Render] Blocking until preparer finishes before starting rendering.");
                preparer.Join();
                if (token.IsCancellationRequested)
                {
                    ReleaseResources();
                    return;
                }
            }

            var workerThreadAffinity = ResolveWorkerThreadAffinity();
            if (workerThreadAffinity.Mask is not null)
            {
                Log($"Using thread affinity for worker threads ({workerThreadAffinity.Description}).");
            }

            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
            void worker(uint targetFrame)
            {
                if (AutoSetupRenderContext) IRenderContext.Current = this;
                IRenderContext.SetWorkerState(targetFrame, RenderWorkerStage.Compositing, $"Render worker #{targetFrame}");
                try
                {
                    FlushBlankFramesBefore(targetFrame, token);
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
                    IRenderContext.ClearWorkerState();
                    if (AutoSetupRenderContext) IRenderContext.Current = null;
                    Interlocked.Decrement(ref ThreadWorking);
                    try
                    {
                        _threadLimiter.Release();
                    }
                    catch { }
                }
            }

            if (DisableAllThrottleOptions)
            {
                Log("Throttling disabled for both preparer and render workers. This may lead to high memory usage and potential deadlocks if the preparer is slower than the render workers.");


                Parallel.For(StartFrame, checked(StartFrame + Duration), new ParallelOptions { MaxDegreeOfParallelism = MaxThreads, CancellationToken = token }, i =>
                {
                    Interlocked.Increment(ref ThreadWorking);
                    StartWorkerThread($"Render worker #{i}", () => worker((uint)i), workerThreadAffinity.Mask);
                });

                goto done;
            }

            // Give the preparer a brief moment to queue the first frames, then start scheduling immediately
            await Task.Delay(50, token);

            var cfg = ResolveSchedulerConfig();
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
                    && cfg.WatchdogTimeoutMs > 0
                    && lastActivity.ElapsedMilliseconds >= cfg.WatchdogTimeoutMs;
                bool underLaunchUtilizationThreshold = availableSlots > 0
                    && (double)working / Math.Max(1, MaxThreads) < cfg.LaunchUtilizationThreshold;
                if (preparedCount > 0 && (forceStart || underLaunchUtilizationThreshold))
                {
                    int toStart = forceStart ? preparedCount : Math.Min(preparedCount, availableSlots);

                    if (forceStart)
                    {
                        Log($"[Watchdog] No rendered frame progress for {cfg.WatchdogTimeoutMs} ms. prepared={preparedCount}, working={working}/{MaxThreads}, finished={Volatile.Read(ref Finished)}/{Duration}.", "warn");
                        if (availableSlots == 0)
                        {
                            Log($"[Watchdog] No available slots (all render threads busy). This often means a render thread is blocked (e.g. in effects/mixer) or the writer is stuck waiting for a missing frame index.", "warn");
                        }
                    }
                    else
                    {
                        // Add timeout to avoid infinite wait when preparer is slow (e.g., on Android with OpenGL main-thread bottleneck)
                        Stopwatch waitElapsed = Stopwatch.StartNew();
                        while (!PreparerFinished && Duration - Volatile.Read(ref Finished) > cfg.MinRemainingFramesForPreparedWait && PreparedFrames.Count < MinSchedulePreparedFrames)
                        {
                            await Task.Delay(cfg.PreparePollDelayMs, token);
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

                    HandleGarbageCollection();

                    // Writer backpressure throttle
                    if (!await CheckWriterBackpressureAsync(cfg.PreparePollDelayMs, token))
                        break;
                    lastActivity.Restart();
                    toStart = forceStart ? PreparedFrames.Count : Math.Min(PreparedFrames.Count, Math.Max(0, MaxThreads - Volatile.Read(ref ThreadWorking)));
                    if (toStart <= 0) continue;

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
                        StartWorkerThread($"Render worker #{targetFrame}", () => worker(targetFrame), workerThreadAffinity.Mask);
                    }

                }
                else
                {
                    if (PreparerFinished && PreparedFrames.IsEmpty && Volatile.Read(ref ThreadWorking) == 0 && !BlankFrames.IsEmpty)
                    {
                        FlushBlankFramesBefore(StartFrame + Duration, token);
                    }
                    await Task.Delay(cfg.IdleDelayMs, token);
                }


                HandleSchedulerExceptions(exceptions, "Multiple exceptions occurred during rendering.");

            }
            if (token.IsCancellationRequested)
            {
                Log("Render cancelled by user.");
                ReleaseResources();
                return;
            }
        done:
            Log($"[Preparer] All frames are prepared and waiting for render done...");

            int waitCount = 0;
            while (Volatile.Read(ref ThreadWorking) > 0 && waitCount < 1000)
            {
                await Task.Delay(50, token);
                waitCount++;
            }

            ReleaseResources();

        }

        private async Task GoRenderWithWorkerDecode(CancellationToken token)
        {
            Log("Starting worker-decoded render...");
            _renderTotalStopwatch.Restart();

            _threadLimiter = new SemaphoreSlim(MaxThreads, MaxThreads);
            ConcurrentQueue<Exception> exceptions = new();

            if (ClipNeedForFrame.IsEmpty && BlankFrames.IsEmpty && Volatile.Read(ref TotalEnqueued) == 0)
            {
                PrepareRender(token);
                if (token.IsCancellationRequested)
                {
                    ReleaseResources();
                    return;
                }
            }

            if (RenderByLayers)
            {
                PrepareFrameLayerData();
                FrameLayerCompletions = new();
            }

            running = true;

            var (Mask, Description) = ResolveWorkerThreadAffinityTargetCores();
            var workerAffinityMask = ResolveWorkerThreadAffinity().Mask; // ulong? for StartWorkerThread compatibility
            int[] reversedMask = [];
            if (Mask?.Any() ?? false)
            {
                Log($"Using thread affinity for worker threads ({Description}).");
                reversedMask = Enumerable.Range(0, Environment.ProcessorCount).Except(Mask).ToArray();
            }

            var frameQueue = new ConcurrentQueue<uint>();
            uint nextFrameToEnqueue = StartFrame;
            uint renderEndFrame = StartFrame + Duration;
            int maxQueuedFrames = Math.Max(MaxThreads, ThrottleThreshold);

            void enqueueFramesToThrottle()
            {
                while (nextFrameToEnqueue < renderEndFrame && frameQueue.Count < maxQueuedFrames)
                {
                    frameQueue.Enqueue(nextFrameToEnqueue++);
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
            void worker(uint targetFrame)
            {
                if (AutoSetupRenderContext) IRenderContext.Current = this;
                IRenderContext.SetWorkerState(targetFrame, RenderWorkerStage.PreparingSource, $"Worker-Decode render #{targetFrame}");
                try
                {
                    if (reversedMask.Length > 0)
                    {
                        ThreadAffinityHelper.SetCurrentThreadAffinity(reversedMask); //let preparing work can eat all small cores
                    }

                    FlushBlankFramesBefore(targetFrame, token);

                    if (!ClipNeedForFrame.TryGetValue(targetFrame, out var clips) || clips == null || clips.Length == 0)
                    {
                        builder?.Append(targetFrame, BlankFrame.Clone());
                        TrackRenderElapsed(TimeSpan.Zero);
                        FramePrepareElapsed.TryAdd(targetFrame, TimeSpan.Zero);
                        FrameRenderElapsed.TryAdd(targetFrame, TimeSpan.Zero);
                        Interlocked.Increment(ref Finished);
                        InvokeProgress();
                        return;
                    }

                    var sw = Stopwatch.StartNew();
                    var ppb = Use16Bit ? IPicture.PicturePixelMode.UShortPicture : IPicture.PicturePixelMode.BytePicture;
                    foreach (var item in clips)
                    {
                        if (token.IsCancellationRequested) break;

                        // Update per-thread state to reflect the clip being decoded
                        var _wsDecode = IRenderContext.WorkerState;
                        if (_wsDecode is not null) _wsDecode.CurrentClip = item;

                        try
                        {
                            var frame = DecodeClipSourceFrame(item, targetFrame, ppb);
                            if (frame != null)
                            {
                                FrameCache.GetOrAdd(item.Id, (_) => new()).TryAdd(targetFrame, frame);
                            }
                        }
                        catch (Exception exInner)
                        {
                            Log($"Error preparing source for frame {targetFrame}, clip {item.Id}: {exInner}", "error");
                            throw;
                        }
                    }
                    sw.Stop();
                    TrackPrepareElapsed(sw.Elapsed);
                    FramePrepareElapsed[targetFrame] = sw.Elapsed;

                    if (Mask?.Any() ?? false)
                    {
                        ThreadAffinityHelper.SetCurrentThreadAffinity(Mask); //let preparing work can eat all big cores
                        Thread.Sleep(0); // reschedule the thread to make sure new mask applies
                    }

                    if (RenderByLayers)
                    {
                        RenderPreparedFrameByLayer(targetFrame, token);
                    }
                    else
                    {
                        RenderAFrame(targetFrame, token);
                    }
                }
                catch (Exception ex)
                {
                    ex.Data["OrigStacktrace"] = ex.StackTrace;
                    Log(ex, $"worker decoding/rendering frame {targetFrame}", this);
                }
                finally
                {
                    IRenderContext.ClearWorkerState();
                    if (AutoSetupRenderContext) IRenderContext.Current = null;
                    Interlocked.Decrement(ref ThreadWorking);
                    try
                    {
                        _threadLimiter.Release();
                    }
                    catch { }
                }
            }

            if (DisableAllThrottleOptions)
            {
                Log("Throttling disabled for worker-decoded render. This may lead to high memory usage and potential deadlocks if the preparer is slower than the render workers.");
                Parallel.For(StartFrame, checked(StartFrame + Duration), new ParallelOptions { MaxDegreeOfParallelism = MaxThreads, CancellationToken = token }, i =>
                {
                    Interlocked.Increment(ref ThreadWorking);
                    StartWorkerThread($"Worker-Decode render #{i}", () => worker((uint)i), workerAffinityMask);
                });
                goto done;
            }

            var cfg = ResolveSchedulerConfig();

            Stopwatch lastActivity = Stopwatch.StartNew();
            int lastFinished = Volatile.Read(ref Finished);

            enqueueFramesToThrottle();
            while (true)
            {
                if (token.IsCancellationRequested)
                {
                    Log("Render cancelled by user.", "info");
                    break;
                }

                int finished = Volatile.Read(ref Finished);
                if (finished != lastFinished)
                {
                    lastFinished = finished;
                    lastActivity.Restart();
                }

                int working = Volatile.Read(ref ThreadWorking);
                int availableSlots = Math.Max(0, MaxThreads - working);
                int queuedFrames = frameQueue.Count;

                bool queueDrained = nextFrameToEnqueue >= renderEndFrame && queuedFrames == 0;
                if (queueDrained && working == 0)
                    break;

                bool forceStart = EnableRenderWatchdogForceStart
                    && cfg.WatchdogTimeoutMs > 0
                    && lastActivity.ElapsedMilliseconds >= cfg.WatchdogTimeoutMs;
                bool underLaunchUtilizationThreshold = availableSlots > 0
                    && (double)working / Math.Max(1, MaxThreads) < cfg.LaunchUtilizationThreshold;

                if (queuedFrames > 0 && (forceStart || underLaunchUtilizationThreshold))
                {
                    int toStart = forceStart ? queuedFrames : Math.Min(queuedFrames, availableSlots);
                    if (forceStart)
                    {
                        Log($"[Watchdog] No rendered frame progress for {cfg.WatchdogTimeoutMs} ms. queued={queuedFrames}, working={working}/{MaxThreads}, finished={finished}/{Duration}.", "warn");
                    }

                    HandleGarbageCollection();

                    // Writer backpressure throttle
                    if (!await CheckWriterBackpressureAsync(cfg.IdleDelayMs, token))
                        break;
                    lastActivity.Restart();
                    toStart = forceStart ? frameQueue.Count : Math.Min(frameQueue.Count, Math.Max(0, MaxThreads - Volatile.Read(ref ThreadWorking)));
                    if (toStart <= 0) { enqueueFramesToThrottle(); continue; }

                    for (int i = 0; i < toStart; i++)
                    {
                        if (!frameQueue.TryDequeue(out var targetFrame))
                            break;

                        if (!_threadLimiter.Wait(0, token))
                        {
                            frameQueue.Enqueue(targetFrame);
                            break;
                        }

                        Interlocked.Increment(ref ThreadWorking);

                        if (Mask is not null)
                        {
                            // With affinity: must create dedicated threads (two-phase affinity would pollute ThreadPool)
                            new Thread(() => { worker(targetFrame); })
                            {
                                Name = $"Worker-Decode render #{targetFrame}",
                                IsBackground = false,
                                Priority = ThreadPriority.Highest
                            }.Start();
                        }
                        else
                        {
                            // No affinity: reuse ThreadPool to avoid per-frame thread creation cost
                            ThreadPool.QueueUserWorkItem(_ => worker(targetFrame));
                        }

                    }

                    lastActivity.Restart();
                    enqueueFramesToThrottle();
                    continue;
                }

                enqueueFramesToThrottle();
                if (nextFrameToEnqueue >= renderEndFrame && frameQueue.IsEmpty && Volatile.Read(ref ThreadWorking) == 0 && !BlankFrames.IsEmpty)
                {
                    FlushBlankFramesBefore(StartFrame + Duration, token);
                }
                await Task.Delay(cfg.IdleDelayMs, token);
                HandleSchedulerExceptions(exceptions, "Multiple exceptions occurred during worker-decoded rendering.");
            }

        done:
            int waitCount = 0;
            while (Volatile.Read(ref ThreadWorking) > 0 && waitCount < 1000)
            {
                await Task.Delay(50, token);
                waitCount++;
            }

            ReleaseResources();
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void RenderPreparedFrameByLayer(uint targetFrame, CancellationToken token)
        {


            int pfIdx = (int)(targetFrame - StartFrame);
            if ((uint)pfIdx < _preparedFlagArray.Length)
                Interlocked.CompareExchange(ref _preparedFlagArray[pfIdx], 0, 1);

            if (!ClipNeedForFrame.TryGetValue(targetFrame, out var clipsNeed) || clipsNeed.Length == 0)
            {
                var sw = Stopwatch.StartNew();
                var rendered = RenderAFrameInternal(targetFrame, [], token);
                if (rendered is not null) SubmitAndFinishFrame(targetFrame, rendered, sw);
                return;
            }

            if (FrameLayerGroups is null || !FrameLayerGroups.TryGetValue(targetFrame, out var layerGroups) || layerGroups.Length == 0)
            {
                RenderAFrame(targetFrame, token);
                return;
            }

            FrameLayerCompletions![targetFrame] = new FrameLayerCompletion
            {
                TotalLayers = layerGroups.Length,
                CompletedLayers = 0,
                LayerResults = new IPicture?[layerGroups.Length],
                RenderStopwatch = Stopwatch.StartNew(),
            };

            for (int layerIdx = 0; layerIdx < layerGroups.Length; layerIdx++)
            {
                RenderALayer(targetFrame, layerIdx, layerGroups[layerIdx], token);
            }
        }

        private async Task GoRenderSync(CancellationToken token)
        {
            Log("[Renderer] OneByOne enabled/MaxThread is 1: switching to single-threaded, synchronous render.", "info");

            if (LogStaticsData) StartStatLoggerThread(token, includeQueueAndWriterStats: false);

            running = true;

            for (uint idx = StartFrame; idx < StartFrame + Duration; idx++)
            {
                if (token.IsCancellationRequested)
                {
                    Log("Render cancelled by user.", "info");
                    break;
                }
                IRenderContext.SetWorkerState(idx, RenderWorkerStage.Compositing, "Sync render");
                try
                {
                    RenderOneFrameSync(idx, token);
                }
                finally
                {
                    IRenderContext.ClearWorkerState();
                }
            }

            if (token.IsCancellationRequested)
            {
                ReleaseResources();
                return;
            }

            ReleaseResources();
        }

        /// <summary>
        /// Async implementation of GoRenderByLayer. Mirrors GoRender's structure but dispatches
        /// per-layer ThreadPool workers and assembles frames when all layers complete.
        /// </summary>
        private async Task GoRenderByLayerAsync(CancellationToken token)
        {
            _threadLimiter = new SemaphoreSlim(MaxThreads, MaxThreads);
            ConcurrentQueue<Exception> exceptions = new();

            if (ClipNeedForFrame.IsEmpty && BlankFrames.IsEmpty && Volatile.Read(ref TotalEnqueued) == 0)
            {
                PrepareRender(token);
                if (token.IsCancellationRequested) { ReleaseResources(); return; }
            }

            PrepareFrameLayerData();
            FrameLayerCompletions = new();
            LayerResults = new();

            running = true;

            var workerThreadAffinity = ResolveWorkerThreadAffinity();
            if (workerThreadAffinity.Mask.HasValue)
            {
                Log($"Using thread affinity for worker threads ({workerThreadAffinity.Description}).");
            }

            if (LogStaticsData) StartStatLoggerThread(token, includeQueueAndWriterStats: true);

            Thread preparer = new(() =>
            {
                if (AutoSetupRenderContext) IRenderContext.Current = this;
                IRenderContext.SetWorkerState(0, RenderWorkerStage.PreparingSource, "Layer-Preparer");
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
                    IRenderContext.ClearWorkerState();
                    if (AutoSetupRenderContext) IRenderContext.Current = null;
                }
            })
            {
                Name = "Preparer thread",
                IsBackground = true
            };
            preparer.Start();

            if(BlockPreparingBeforeRendering)
            {
                Log($"[Render] Blocking until preparer finishes before starting rendering.");
                preparer.Join();
                if (token.IsCancellationRequested) { ReleaseResources(); return; }
            }

            // Give the preparer a brief moment to queue the first frames, then start scheduling immediately
            await Task.Delay(50, token);

            var cfg = ResolveSchedulerConfig();
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
                    && cfg.WatchdogTimeoutMs > 0
                    && lastActivity.ElapsedMilliseconds >= cfg.WatchdogTimeoutMs;
                bool underLaunchUtilizationThreshold = availableSlots > 0
                    && (double)working / Math.Max(1, MaxThreads) < cfg.LaunchUtilizationThreshold;

                if (preparedCount > 0 && (forceStart || underLaunchUtilizationThreshold))
                {
                    int toStart = forceStart ? preparedCount : Math.Min(preparedCount, availableSlots);

                    if (forceStart)
                    {
                        Log($"[Watchdog] No rendered frame progress for {cfg.WatchdogTimeoutMs} ms. prepared={preparedCount}, working={working}/{MaxThreads}, finished={Volatile.Read(ref Finished)}/{Duration}.", "warn");
                        if (availableSlots == 0)
                        {
                            Log($"[Watchdog] No available slots (all render threads busy). This often means a render thread is blocked or the writer is stuck waiting for a missing frame index.", "warn");
                        }
                    }
                    else
                    {
                        Stopwatch waitElapsed = Stopwatch.StartNew();
                        while (!PreparerFinished && Duration - Volatile.Read(ref Finished) > cfg.MinRemainingFramesForPreparedWait && PreparedFrames.Count < MinSchedulePreparedFrames)
                        {
                            await Task.Delay(cfg.PreparePollDelayMs, token);
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

                    HandleGarbageCollection();

                    // Writer backpressure throttle
                    if (!await CheckWriterBackpressureAsync(cfg.PreparePollDelayMs, token))
                        break;
                    lastActivity.Restart();
                    toStart = forceStart ? PreparedFrames.Count : Math.Min(PreparedFrames.Count, Math.Max(0, MaxThreads - Volatile.Read(ref ThreadWorking)));
                    if (toStart <= 0) continue;

                    for (int i = 0; i < toStart; i++)
                    {
                        if (!PreparedFrames.TryDequeue(out var targetFrame))
                            break;

                        FlushBlankFramesBefore(targetFrame, token);

                        if (!FrameLayerGroups!.TryGetValue(targetFrame, out var layerGroups) || layerGroups.Length == 0)
                        {
                            // No clips at this frame: treat as blank
                            builder?.Append(targetFrame, BlankFrame.Clone());
                            TrackRenderElapsed(TimeSpan.Zero);
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
                            RenderStopwatch = Stopwatch.StartNew(),
                        };
                        FrameLayerCompletions![targetFrame] = completion;

                        // Dispatch one worker per layer (slots already acquired above)
                        for (int layerIdx = 0; layerIdx < layerGroups.Length; layerIdx++)
                        {
                            Interlocked.Increment(ref ThreadWorking);
                            var capturedFrame = targetFrame;
                            var capturedLayerIdx = layerIdx;
                            var capturedGroup = layerGroups[layerIdx];

                            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
                            void worker()
                            {
                                if (AutoSetupRenderContext) IRenderContext.Current = this;
                                IRenderContext.SetWorkerState(capturedFrame, RenderWorkerStage.Compositing, $"Layer worker #{capturedFrame}.{capturedLayerIdx}");
                                try
                                {
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
                                    IRenderContext.ClearWorkerState();
                                    if (AutoSetupRenderContext) IRenderContext.Current = null;
                                    Interlocked.Decrement(ref ThreadWorking);
                                    try
                                    {
                                        _threadLimiter.Release();
                                    }
                                    catch { }
                                }
                            }
                            ;

                            StartWorkerThread($"Layer worker #{capturedFrame}.{capturedLayerIdx}", worker, workerThreadAffinity.Mask);
                        }
                    }
                }
                else
                {
                    if (PreparerFinished && PreparedFrames.IsEmpty && Volatile.Read(ref ThreadWorking) == 0 && !BlankFrames.IsEmpty)
                    {
                        FlushBlankFramesBefore(StartFrame + Duration, token);
                    }
                    await Task.Delay(cfg.IdleDelayMs, token);
                }

                HandleSchedulerExceptions(exceptions, "Multiple exceptions occurred during rendering.");
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

        [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
        private void RenderAFrame(uint targetFrame, CancellationToken token)
        {

            if (targetFrame >= StartFrame + Duration)
            {
                Log($"[Render] WARN: Target frame {targetFrame} exceeds project duration. Ignore.");
                return;
            }
            int pfIdx = (int)(targetFrame - StartFrame);
            if ((uint)pfIdx < _preparedFlagArray.Length)
                Interlocked.CompareExchange(ref _preparedFlagArray[pfIdx], 0, 1);

            if (!ClipNeedForFrame.Remove(targetFrame, out var ClipsNeed) || ClipsNeed.Length == 0)
            {
                // 帧已被 RenderSpecificFrame 预写入 builder，Finished 已在其路径中递增
                if (builder?.FramePendedToWrite?.ContainsKey(targetFrame) == true)
                {
                    if (LogRenderState) Log($"[Render] Frame {targetFrame} was already pre-cached; skipping.");
                    return;
                }

                var sw1 = Stopwatch.StartNew();
                SubmitAndFinishFrame(targetFrame, RenderAFrameInternal(targetFrame, [], token), sw1);
                return;
            }
            // 二次检查：在 Remove 成功之后、开始合成之前，确认帧没有被 RenderSpecificFrame 预写入
            // 这覆盖了：Worker 早已消费了 PreparedFlag，RenderSpecificFrame 的 TryAdd/TryPreAppend
            // 竞争成功写入 builder 但 Worker 还未开始合成的场景。
            if (builder?.TryGetCachedFrame(targetFrame, out _) == true)
            {
                if (LogRenderState) Log($"[Render] Frame {targetFrame} was pre-cached before composition; skipping.");
                foreach (var clip in ClipsNeed)
                    if (FrameCache.TryGetValue(clip.Id, out var perClipCache))
                        perClipCache.TryRemove(targetFrame, out _);
                // Finished 已在 RenderSpecificFrame 中递增，不再重复
                return;
            }

            var framesToRender = new List<ClipFrameTuple>(ClipsNeed.Length);
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

            var sw = Stopwatch.StartNew();
            var rendered = RenderAFrameInternal(
                targetFrame,
                framesToRender,
                token);

            if (rendered is null) // cancelled
            {
                foreach (var clip in ClipsNeed)
                    if (FrameCache.TryGetValue(clip.Id, out var perClipCache))
                        perClipCache.TryRemove(targetFrame, out _);
                return;
            }

            SubmitAndFinishFrame(targetFrame, rendered, sw);

            // DO NOT dispose cache frames here.
            // - Single-clip: result == the cache frame; builder has queued it for async writing.
            //   The builder will dispose the frame (via PictureFlag) after the write completes.
            // - Multi-clip: individual clip frames were already disposed inside RenderAFrameInternal
            //   immediately after being merged (usedFrames == null branch).
            // Just remove the entries from the cache dictionary.
            foreach (var clip in ClipsNeed)
            {
                if (FrameCache.TryGetValue(clip.Id, out var perClipCache))
                    perClipCache.TryRemove(targetFrame, out _);
            }
            return;
        }

        private void RenderOneFrameSync(uint targetFrame, CancellationToken token)
        {

            if (targetFrame >= StartFrame + Duration)
            {
                Log($"[Render] WARN: Target frame {targetFrame} exceeds project duration. Ignore.");
                return;
            }
            Stopwatch prep = Stopwatch.StartNew();
            var clipsNeed = new List<IClip>();
            foreach (var item in Clips ?? [])
            {
                if (item.ContainsFrame(targetFrame) || (item.ExtendToWholeDraft && item.LayerIndex > SubTrackOffset))
                {
                    clipsNeed.Add(item);
                }
            }
            clipsNeed = clipsNeed.OrderBy(x => x.LayerIndex >= SubTrackOffset ? 1 : 0)
                                 .ThenByDescending(x => x.LayerIndex)
                                 .ThenByDescending(x => x.SubLayerIndex)
                                 .ToList();

            var framesToRender = new List<ClipFrameTuple>(clipsNeed.Count);
            var syncPpb = Use16Bit ? IPicture.PicturePixelMode.UShortPicture : IPicture.PicturePixelMode.BytePicture;
            foreach (var clip in clipsNeed)
            {
                // Update per-thread state: which clip is being processed in sync mode
                var _wsSync = IRenderContext.WorkerState;
                if (_wsSync is not null) _wsSync.CurrentClip = clip;

                var frame = DecodeClipSourceFrame(clip, targetFrame, syncPpb);
                if (frame == null)
                {
                    Log($"[Render] WARN: Frame {targetFrame} not found for clip {clip.Id}.");
                    framesToRender.Add((clip, null));
                    continue;
                }
                if (frame.BitPerPixel != syncPpb)
                {
                    frame = frame.ToBitPerPixel(syncPpb);
                }

                framesToRender.Add((clip, frame));
            }
            TrackPrepareElapsed(prep.Elapsed);
            FramePrepareElapsed[targetFrame] = prep.Elapsed;

            var renderSw = Stopwatch.StartNew();
            var rendered = RenderAFrameInternal(targetFrame, framesToRender, token);

            if (rendered is not null)
            {
                SubmitAndFinishFrame(targetFrame, rendered, renderSw);
            }
        }

        #region get frame during render

        public IPicture RenderSpecificFrame(uint frameIndex) => RenderSpecificFrame(frameIndex, CancellationToken.None) ?? throw new InvalidOperationException("Render does not return valid data.");

        /// <summary>
        /// 在渲染过程中获取项目某一帧的最终画面。
        /// 优先从 <see cref="VideoBuilder"/> 的未写入缓存取（O(1)），
        /// 如果该帧已写入则从源帧重新合成。
        /// </summary>
        /// <param name="frameIndex">目标帧索引</param>
        /// <param name="token">取消令牌</param>
        /// <returns>
        /// 渲染完成的画面，调用者负责调用 <see cref="IPicture.Dispose()"/>；
        /// 如果索引超出范围返回 null。
        /// </returns>
        public IPicture? RenderSpecificFrame(uint frameIndex, CancellationToken token)
        {


            if (frameIndex < StartFrame || frameIndex >= StartFrame + Duration)
            {
                Log($"[GetPictureForFrame] WARN: Frame {frameIndex} out of range [{StartFrame}, {StartFrame + Duration}).");
                return null;
            }

            // 1) 优先从 builder 的未写入缓存取（帧已合成但尚未写入视频文件）
            if (builder?.TryGetCachedFrame(frameIndex, out var cachedFrame) == true && cachedFrame is not null)
            {
                Log($"[GetPictureForFrame] Got frame {frameIndex} from VideoBuilder cache.");
                return cachedFrame.Clone();
            }

            // 2) 帧已写入或尚未渲染 → 从源帧重新合成
            Log($"[GetPictureForFrame] Frame {frameIndex} not in VideoBuilder cache; re-rendering from source.");
            var result = ReRenderFrame(frameIndex, token);
            if (result is null) return null;

            // 3) 如果该帧尚未进入渲染管线（还在 PreparedFrames 中待调度），
            //    预写入 builder 缓存，让后续正常管线直接跳过它。
            //    使用 PreparedFlag.TryAdd 作为原子认领：
            //    - 成功 = 管线尚未处理这帧（Preparer 还没设 flag，或设了但还没入队）
            //    - Preparer 稍后遇到此帧时 TryAdd 会失败，跳过入队，避免重复调度
            //    - 注意：不能移除 ClipNeedForFrame——Preparer 正在用索引器遍历它
            //    - 如果 Worker 已出队此帧（TryRemove 消费了 flag），TryAdd 也会成功，
            //      TryPreAppend 写入 Cache，Worker 结束后 RenderAFrame 会检测到并跳过
            if (builder?.FramePendedToWrite.ContainsKey(frameIndex) == false
                && TryClaimPreparedFlag(frameIndex))
            {
                var clone = result.Clone();
                if (builder?.TryPreAppend(frameIndex, clone) == true)
                {
                    // 不删除 ClipNeedForFrame：Preparer 正在遍历它，移除 key 会导致 KeyNotFoundException
                    // 但 Preparer 的 PreparedFlag.TryAdd 会失败 → 不会入队 PreparedFrames
                    Interlocked.Increment(ref Finished);
                    InvokeProgress();
                    Log($"[GetPictureForFrame] Pre-cached frame {frameIndex} to VideoBuilder.");
                }
                else
                {
                    // 认领竞争失败（另一线程先写入了），清理 flag
                    int pfIdx2 = (int)(frameIndex - StartFrame);
                    if ((uint)pfIdx2 < _preparedFlagArray.Length)
                        Interlocked.CompareExchange(ref _preparedFlagArray[pfIdx2], 0, 1);
                    try { clone.Dispose(); } catch { }
                }
            }

            return result;
        }

        /// <summary>
        /// 从源帧重新合成指定帧的画面，不写入 builder。
        /// 会尝试从 <see cref="FrameCache"/> 取已解码的源帧，不存在时重新从 <see cref="IClip.GetFrame"/> 解码。
        /// </summary>
        private IPicture? ReRenderFrame(uint frameIndex, CancellationToken token)
        {
            // 获取该帧涉及的所有 clip
            var clipsNeed = new List<IClip>();
            if (ClipNeedForFrame.TryGetValue(frameIndex, out var existingClips) && existingClips.Length > 0)
            {
                clipsNeed.AddRange(existingClips);
            }
            else
            {
                // 如果 ClipNeedForFrame 已清理（帧已渲染完毕），从 Clips 重新扫描
                foreach (var clip in Clips ?? Array.Empty<IClip>())
                {
                    if (clip.ContainsFrame(frameIndex)
                        || (clip.ExtendToWholeDraft && clip.LayerIndex > SubTrackOffset))
                    {
                        clipsNeed.Add(clip);
                    }
                }

                if (clipsNeed.Count > 0)
                {
                    clipsNeed = clipsNeed
                        .OrderBy(x => x.LayerIndex >= SubTrackOffset ? 1 : 0)
                        .ThenByDescending(x => x.LayerIndex)
                        .ThenByDescending(x => x.SubLayerIndex)
                        .ToList();
                }
            }

            if (clipsNeed.Count == 0)
            {
                // 空白帧
                Log($"[ReRenderFrame] Frame {frameIndex} has no clips, returning blank frame.");
                return BlankFrame.Clone();
            }

            var ppb = Use16Bit ? IPicture.PicturePixelMode.UShortPicture : IPicture.PicturePixelMode.BytePicture;
            var framesToRender = new List<ClipFrameTuple>(clipsNeed.Count);

            foreach (var clip in clipsNeed)
            {
                IPicture? frame = null;

                // 优先从 FrameCache 取已解码的源帧（不干扰原始缓存，clone 副本用于合成）
                if (FrameCache.TryGetValue(clip.Id, out var perClipCache)
                    && perClipCache.TryGetValue(frameIndex, out var cachedFrame)
                    && cachedFrame is not null)
                {
                    frame = cachedFrame.Clone();
                    LogDiagnostic($"[ReRenderFrame] Frame {frameIndex}: clip {clip.Id} from FrameCache.");
                }
                else
                {
                    // 从源重新解码（lenient：输入缺失记录警告并跳过）
                    frame = DecodeClipSourceFrame(clip, frameIndex, ppb, throwOnMissingTransformInput: false);
                    // 共享的不可变内容不允许被合成管线 dispose，clone 副本
                    if (frame is not null && !frame.CanBeDisposed)
                    {
                        frame = frame.Clone();
                    }
                    if (frame is not null)
                        LogDiagnostic($"[ReRenderFrame] Frame {frameIndex}: clip {clip.Id} decoded from source.");
                }

                if (frame is not null)
                {
                    framesToRender.Add((clip, frame));
                }
            }

            if (framesToRender.Count == 0)
            {
                Log($"[ReRenderFrame] Frame {frameIndex}: no source frames available, returning blank.");
                return BlankFrame.Clone();
            }

            // 合成帧（直接复用 RenderAFrameInternal 的合成逻辑）
            return RenderAFrameInternal(frameIndex, framesToRender, token);
        }

        #endregion

        /// <summary>
        /// 解码某个 clip 在指定帧的源画面：处理 Transform clip、不可变内容 clip（共享缓存）
        /// 与普通 clip，并在需要时叠加 AI 水印。四条渲染路径（后台预备、Worker 解码、同步渲染、重渲染）共用。
        /// </summary>
        /// <param name="item">目标 clip</param>
        /// <param name="frameIndex">目标帧索引</param>
        /// <param name="ppb">目标像素位深</param>
        /// <param name="throwOnMissingTransformInput">
        /// Transform 输入缺失时的行为：true 抛出异常（渲染管线路径）；false 记录警告并返回 null（重渲染路径）。
        /// </param>
        /// <returns>解码后的画面；输入缺失且不抛异常时返回 null</returns>
        private IPicture? DecodeClipSourceFrame(IClip item, uint frameIndex, IPicture.PicturePixelMode ppb, bool throwOnMissingTransformInput = true)
        {
            IPicture? frame;
            int clipTargetWidth = ResolveClipOutputWidth(item, TargetWidth, ProjectRelativeWidth);
            int clipTargetHeight = ResolveClipOutputHeight(item, TargetHeight, ProjectRelativeHeight);

            if (item.ClipType == ClipMode.TransformClip && item is TransformContainer c)
            {
                if (c.Transform == null) c.ReInit(_ppb);
                if (c.Transform is not ITransform t)
                {
                    if (throwOnMissingTransformInput) throw new NullReferenceException($"Transform for clip {c.Id} is null");
                    Log($"[Render] WARN: Transform for clip {c.Id} is null, skipping.");
                    return null;
                }

                IClip? rightClip = null;
                if (t.TransformType != TransformType.OneInputSingleFrameTransform
                    && !IndexedClipList.TryGetValue(t.BindedRightClip, out rightClip)
                    && throwOnMissingTransformInput)
                {
                    throw new NullReferenceException($"Transform {t.Name}({t.TypeName})'s right input for clip {c.Id} is null");
                }

                if (!IndexedClipList.TryGetValue(t.BindedLeftClip, out IClip? leftClip))
                {
                    if (throwOnMissingTransformInput) throw new NullReferenceException($"Transform {t.Name}({t.TypeName})'s left input for clip {c.Id} is null");
                    Log($"[Render] WARN: Left input for transform clip {c.Id} not found, skipping.");
                    return null;
                }

                frame = TransformProcessing.ProcessTransform(leftClip, rightClip, t, clipTargetWidth, clipTargetHeight, frameIndex, ppb);
            }
            else if (item is IImmutableContentClip immutableContent)
            {
                string immutableCacheKey = $"__immutable_{item.Id}_{clipTargetWidth}_{clipTargetHeight}_{ppb}";
                var cacheFrame = ImmutableContentCache.GetOrAdd(immutableCacheKey, _ =>
                {
                    IPicture f;
                    if (item.AlternativeSource is ISourceReplacementEffect sre && sre.SupportsSourceReplacement(item, clipTargetWidth, clipTargetHeight))
                    {
                        f = sre.Compute(
                                item,
                                PluginManager.CreateComputer(sre.NeedComputer),
                               item.GetFrame(frameIndex, clipTargetWidth, clipTargetHeight, ppb),
                                clipTargetWidth,
                                clipTargetHeight,
                                item.GetRelativeFrameIndex(frameIndex)
                                    ?? throw new IndexOutOfRangeException($"Frame #{frameIndex} is not in clip [{StartFrame}, {StartFrame + item.GetEffectiveDuration()})."), ppb);
                    }
                    else
                    {
                        f = immutableContent.GetContent(clipTargetWidth, clipTargetHeight, ppb);
                    }
                    f.CanBeDisposed = false;
                    f.Tag = $"Immutable content for clip {item.Id} at {clipTargetWidth}x{clipTargetHeight} ppb={ppb}";
                    LogDiagnostic($"Cached immutable content for clip {item.Id} with key {immutableCacheKey}");
                    return f;
                });
                // Clone shared immutable frame so callers can safely dispose their copy
                frame = cacheFrame.Clone();
            }
            else if (item.AlternativeSource is ISourceReplacementEffect sre && sre.SupportsSourceReplacement(item, clipTargetWidth, clipTargetHeight))
            {
                frame = sre.Compute(
                        item, 
                        PluginManager.CreateComputer(sre.NeedComputer), 
                        item.GetFrame(frameIndex, clipTargetWidth, clipTargetHeight, ppb), 
                        clipTargetWidth, 
                        clipTargetHeight, 
                        item.GetRelativeFrameIndex(frameIndex) 
                            ?? throw new IndexOutOfRangeException($"Frame #{frameIndex} is not in clip [{StartFrame}, {StartFrame + item.GetEffectiveDuration()})."), ppb);
            }
            else
            {
                frame = item.GetFrame(frameIndex, clipTargetWidth, clipTargetHeight, ppb);
            }

            if (frame is not null && IsClipGeneratedByAI.TryGetValue(item.Id, out var aiMark) && aiMark)
            {
                var old = frame;
                frame = EffectProcessing.ProcessAIWatermark(frame, null);
                // Dispose respects CanBeDisposed, so shared immutable content is not affected
                if (!ReferenceEquals(old, frame)) try { old.Dispose(); } catch { }
            }

            return frame;
        }

        /// <summary>
        /// Applies all effects to a single clip's frame, computes placement, handles HDR conversion,
        /// and composites the clip onto the accumulating result picture. Shared by frame-level and
        /// layer-level rendering paths.
        /// Returns the updated result (may be the same instance, a new picture, or null if cancelled).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
        private IPicture? ProcessAndCompositeClip(
            IClip clip,
            IPicture frame,
            IPicture? currentResult,
            uint targetFrame,
            int layoutRelativeWidth,
            int layoutRelativeHeight,
            Dictionary<string, object> frameLocalCache,
            CancellationToken token)
        {
            if (token.IsCancellationRequested) return null;

            // Update per-thread worker state so external diagnostics can see
            // which clip this worker is currently processing.
            var _ws = IRenderContext.WorkerState;
            if (_ws is not null)
            {
                _ws.CurrentClip = clip;
                _ws.CurrentFrame = targetFrame;
                _ws.Stage = RenderWorkerStage.ProcessingEffects;
            }


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
                    // Copy is only materialized when a bindable effect actually gets removed
                    List<IEffect>? effectCopy = null;
                    // Cache Computer by NeedComputer string to avoid per-effect CreateComputer overhead
                    // (many effects in a chain often share the same computer type)
                    string? lastComputerType = null;
                    IComputer? cachedComputer = null;
                    // Begin the per-frame value-provider context: pre-fills the built-in frame/progress
                    // sources and clears provider values. Value-provider effects write into it during
                    // the effect loop and consumer dynamic parameters read from it.
                    var clipDuration = clip.GetEffectiveDuration();
                    var clipProgress = clipDuration > 0
                        ? Math.Clamp((float)((long)targetFrame - (long)clip.StartFrame) / clipDuration, 0f, 1f)
                        : 0f;
                    ValueProviderFrameContext.BeginFrame(targetFrame, clipProgress);
                    for (int _effectIdx = 0; _effectIdx < effects.Length; _effectIdx++)
                    {
                        // Try GPU batch processing (2+ consecutive GPU effects)
                        if (EnableGPUBatchProcess)
                        {
                            var batch = CollectGpuBatch(effects, _effectIdx, out var nextBatchIdx);
                            if (batch.Count >= 2)
                            {
                                frame = ProcessGpuBatch(frame, batch, frame.Width, frame.Height);
                                _effectIdx = nextBatchIdx - 1; // -1 because for loop will increment
                                continue;
                            }
                        }


                        var item = effects[_effectIdx];
                        // Reuse Computer instance when NeedComputer hasn't changed within the same clip chain
                        if (item.NeedComputer != lastComputerType)
                        {
                            cachedComputer = item.NeedComputer is not null ? PluginManager.CreateComputer(item.NeedComputer) : null;
                            lastComputerType = item.NeedComputer;
                        }
                        var computer = cachedComputer;
                        IRenderContext.CurrentFrameBuffer = frame;

                        try
                        {
                            switch (item.TypeOfEffect)
                            {
                                case EffectType.NormalEffect:
                                    if (item is not INormalEffect e) goto notdefined;
                                    frame = e.Render(frame, computer, TargetWidth, TargetHeight);
                                    continue;
                                case EffectType.ContinuousEffect:
                                    if (item is not IContinuousEffect c) goto notdefined;
                                    int scopedStart = c.IsScoped ? c.StartPoint : (int)clip.StartFrame;
                                    int scopedEnd = c.IsScoped ? c.EndPoint : (int)(clip.StartFrame + clip.GetEffectiveDuration());
                                    if (scopedEnd <= scopedStart || targetFrame < scopedStart || targetFrame >= scopedEnd) continue;
                                    float continuousProgress = Math.Clamp((float)(targetFrame - scopedStart) / (scopedEnd - scopedStart), 0f, 1f);
                                    frame = c.Render(frame, continuousProgress, computer, TargetWidth, TargetHeight);
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
                                case EffectType.NonIPictureOutputValueProvider:
                                    throw new InvalidOperationException($"Effect {item.Name} ({item.Id}) of clip {clip.Id} is a NonIPictureOutputValueProvider and should have been handled in the EffectBindingHelper.RebuildAllEffects. This indicates a logic error.");

                                case EffectType.MixtureProvider:
                                case EffectType.SpeedVarianceProvider:
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
                        if (item is IBindableArgumentEffect be)
                        {
                            EffectProcessing.ProcessBindableArgsEffect(targetFrame, ref frame, ref BindableEffectResultCache, frameLocalCache, clip, be, computer, TargetWidth, TargetHeight);
                        }
                        else if (item is IContinuousEffect c)
                        {
                            int scopedStart = c.IsScoped ? c.StartPoint : (int)clip.StartFrame;
                            int scopedEnd = c.IsScoped ? c.EndPoint : (int)(clip.StartFrame + clip.GetEffectiveDuration());
                            if (scopedEnd <= scopedStart || targetFrame < scopedStart || targetFrame >= scopedEnd) continue;
                            float continuousProgress = Math.Clamp((float)(targetFrame - scopedStart) / (scopedEnd - scopedStart), 0f, 1f);
                            frame = c.Render(frame, continuousProgress, computer, TargetWidth, TargetHeight);
                        }
                        else if (item is INormalEffect n)
                        {
                            frame = n.Render(frame, computer, TargetWidth, TargetHeight);
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
                        else if(item is IValueProviderEffect)
                        {
                            throw new InvalidOperationException($"Effect {item.Name} ({item.Id}) of clip {clip.Id} is a IValueProviderEffect and should have been handled in the EffectBindingHelper.RebuildAllEffects. This indicates a logic error.");
                        }
                        else if (item is IMixture or ISpeedVarianceProvider or ITextEffect or IContinuousTextEffect or ISourceReplacementEffect)
                        {
                            //skip here, they've processed somewhere else
                            continue;
                        }
                        else
                        {
                            throw new NotSupportedException($"The effect's ClipType {item.TypeOfEffect} {item.TypeName} of clip {clip.Id} is not supported by Render. Effect ID: {item.Id}");
                        }

                    }
                    // The per-frame value-provider values are only needed during effect processing.
                    ValueProviderFrameContext.EndFrame();
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
                        frame = HDRPicture16bpp.ToHDRPictureBySignal(frame, PerClipHDRBrightness.TryGetValue(clip.Id, out var b) ? b : SDRClipsBrightnessInHDRMode);
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
                        var computer = PluginManager.CreateComputer(mixer.NeedComputer);
                        var mixResult = mixer.Mix(BlankFrame, frame, computer, _ppb, clipX, clipY, TargetWidth, TargetHeight);
                        return mixResult;
                    }
                }
                else
                {
                    var mixer = clip.MixtureInstance ?? ClassicOverlayMixture.Default;
                    var computer = PluginManager.CreateComputer(mixer.NeedComputer);
                    var temp = mixer.Mix(currentResult, frame, computer, _ppb, clipX, clipY, TargetWidth, TargetHeight);
                    currentResult.Dispose();
                    return temp;
                }
            }
            catch (Exception ex)
            {
                Log(ex, $"internal render logic for {targetFrame}", this);
                throw;
            }
        }

        /// <summary>
        /// 合成帧画面并返回结果。不写入 builder，不更新指标。
        /// 调用方负责将结果写入 <see cref="builder"/> 并更新进度。
        /// </summary>
        /// <param name="targetFrame">目标帧索引</param>
        /// <param name="clipsNeed">该帧的 (clip, 源帧) 列表</param>
        /// <param name="usedFrames">当不为 null 时，会把合成过程中用到的帧加入此列表（调用方负责 dispose）</param>
        /// <param name="token">取消令牌</param>
        /// <returns>合成后的画面，取消或参数错误时返回 null</returns>
        [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
        private IPicture? RenderAFrameInternal(
            uint targetFrame,
            List<ClipFrameTuple> clipsNeed,
            CancellationToken token)
        {
            IPicture? result = null;
            var frameLocalCache = RentFrameLocalCache();
            try
            {
                int layoutRelativeWidth = ProjectRelativeWidth > 0 ? ProjectRelativeWidth : TargetWidth;
                int layoutRelativeHeight = ProjectRelativeHeight > 0 ? ProjectRelativeHeight : TargetHeight;

                foreach (var (clip, Frame) in clipsNeed)
                {
                    var frame = Frame;
                    if (token.IsCancellationRequested) return null;

                    if (frame == null) continue;

                    result = ProcessAndCompositeClip(clip, frame, result, targetFrame, layoutRelativeWidth, layoutRelativeHeight, frameLocalCache, token);
                    if (result is null) return null; // cancelled
                }

                if (result is null)
                {
                    return BlankFrame.Clone();
                }

                if (result.Width < TargetWidth || result.Height < TargetHeight)
                {
                    result = _threadLocalBlankPlace.Value!.Render(result, null, TargetWidth, TargetHeight);
                }
                else if (result.Width > TargetWidth || result.Height > TargetHeight)
                {
                    var old = result;
                    result = result.Resize(TargetWidth, TargetHeight, false);
                    try { old.Dispose(); } catch { }
                }

                return result;
            }
            finally
            {
                ReturnFrameLocalCache(frameLocalCache);
            }
        }

        /// <summary>
        /// 将合成后的帧提交到 builder、更新进度与渲染指标。
        /// 如果帧已被预写入（如 <see cref="RenderSpecificFrame"/> 先写入了），自动丢弃重复。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
        private void SubmitAndFinishFrame(uint frameIndex, IPicture? result, Stopwatch sw)
        {
            ArgumentNullException.ThrowIfNull(result, nameof(result));
            if (builder?.FramePendedToWrite?.ContainsKey(frameIndex) == true)
            {
                if (result != BlankFrame)
                {
                    try
                    {
                        Log($"Frame {frameIndex} already pre-cached in VideoBuilder, disposing duplicate result.", "warn");
                        result.Dispose();
                    }
                    catch { }
                }

                return;
            }

            if (builder is VideoBuilder b)
            {
                b.Append(frameIndex, result);
            }
            else // intended behavior in bench mode or when builder is null: dispose the result to avoid memory leak
            {
                result.Dispose();
            }

            Interlocked.Increment(ref Finished);
            sw.Stop();
            if (LogProcessStack)
            {
                FrameProcessStacks[frameIndex] = result.ProcessStack;
                FrameDirtyTime[frameIndex] = sw.Elapsed - TimeSpan.FromTicks(
                    result.ProcessStack.Where(c => c.Elapsed is not null).Sum(c => c.Elapsed!.Value.Ticks));
            }
            InvokeProgress();
            if (LogRenderState) Log($"[Render] Frame {frameIndex} render done, elapsed {sw.Elapsed}, dirty time {FrameDirtyTime[frameIndex]}");
            TrackRenderElapsed(sw.Elapsed);
            FrameRenderElapsed[frameIndex] = sw.Elapsed;
        }

        #region GPU batch

        /// <summary>
        /// Collect consecutive GPU effects starting at <paramref name="startIndex"/>
        /// that can be batched into a single GPU session.
        /// Returns the batch only if it contains at least 2 effects.
        /// </summary>
        static List<IEffect> CollectGpuBatch(IReadOnlyList<IEffect> effects, int startIndex, out int nextIndex)
        {
            var batch = new List<IEffect>();
            string? fromPlugin = null;

            for (int i = startIndex; i < effects.Count; i++)
            {
                var item = effects[i];
                if (!item.Enabled)
                {
                    if (batch.Count > 0) break;
                    continue;
                }

                if (item.NeedComputer is null || item.TypeOfEffect is EffectType.BindableEffect)
                {
                    if (batch.Count > 0) break;
                    nextIndex = i + 1;
                    return batch;
                }

                if (ComputerSupportsBatching(item.NeedComputer))
                {
                    if (batch.Count == 0)
                    {
                        fromPlugin = item.FromPlugin;
                        batch.Add(item);
                    }
                    else if (item.FromPlugin == fromPlugin)
                    {
                        batch.Add(item);
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    if (batch.Count > 0) break;
                    nextIndex = i + 1;
                    return batch;
                }
            }

            nextIndex = startIndex + batch.Count;
            return batch;
        }

        /// <summary>
        /// Process a batch of GPU effects in a single GPU session:
        /// one upload → chain of kernels → one download.
        /// </summary>
        /// <summary>
        /// Reorder effects within each contiguous block of <see cref="IEffect.IsReorderable"/> effects
        /// to group GPU-batchable effects from the same plugin together, maximizing batch size.
        /// Non-reorderable effects stay in their original positions as anchors.
        /// </summary>
        private static IEffect[] ReorderEffectsForGpuBatching(IEffect[] effects)
        {
            if (effects.Length <= 1) return effects;

            var list = effects.ToList();
            bool changed = false;

            int i = 0;
            while (i < list.Count)
            {
                if (!list[i].IsReorderable) { i++; continue; }

                int blockStart = i;
                while (i < list.Count && list[i].IsReorderable)
                    i++;
                int blockEnd = i;

                if (blockEnd - blockStart <= 1) continue;

                var reorderable = list.GetRange(blockStart, blockEnd - blockStart);
                var gpuBatchable = new List<IEffect>();
                var others = new List<IEffect>();
                foreach (var effect in reorderable)
                {
                    if (IsGpuBatchable(effect))
                        gpuBatchable.Add(effect);
                    else
                        others.Add(effect);
                }

                if (gpuBatchable.Count < 2) continue;

                var gpuGroups = gpuBatchable.GroupBy(e => e.FromPlugin).ToList();

                var reordered = new List<IEffect>(reorderable.Count);
                reordered.AddRange(others);
                foreach (var group in gpuGroups)
                    reordered.AddRange(group);

                list.RemoveRange(blockStart, blockEnd - blockStart);
                list.InsertRange(blockStart, reordered);
                changed = true;
            }

            return changed ? list.ToArray() : effects;
        }

        private static bool IsGpuBatchable(IEffect effect)
        {
            if (!effect.Enabled || effect.NeedComputer is null)
                return false;
            if (effect.TypeOfEffect == EffectType.BindableEffect)
                return false;
            return ComputerSupportsBatching(effect.NeedComputer);
        }

        /// <summary>
        /// Checks (and caches) whether the computer type supports GPU session batching,
        /// avoiding a per-frame computer lookup and type test for every effect.
        /// </summary>
        private static bool ComputerSupportsBatching(string computerType)
            => ComputerBatchSupportCache.GetOrAdd(computerType,
                static id => PluginManager.CreateComputer(id) is ISessionComputer sc && sc.SupportsBatching);

        private static IPicture ProcessGpuBatch(IPicture frame, List<IEffect> batch, int targetWidth, int targetHeight)
        {
            var (r, g, b, a, hasAlpha) = HwAccelEffectHelper.ExtractFloatChannels(frame);

            var firstComputer = (ISessionComputer)PluginManager.CreateComputer(batch[0].NeedComputer)!;
            using var session = firstComputer.CreateSession(r, g, b, a, targetWidth, targetHeight);

            foreach (var effect in batch)
            {
                var computer = (ISessionComputer)PluginManager.CreateComputer(effect.NeedComputer)!;
                var parameters = new Dictionary<string, object>(effect.Parameters)
                {
                    ["BuiltIn.TargetWidth"] = targetWidth,
                    ["BuiltIn.TargetHeight"] = targetHeight
                };
                computer.ExecuteOnSession(session, parameters);
            }

            var (rOut, gOut, bOut, aOut) = session.Download();

            var result = HwAccelEffectHelper.BuildPicture(frame, targetWidth, targetHeight, rOut, gOut, bOut, aOut, hasAlpha);
            try { frame.Dispose(); } catch { }
            return result;
        }
        #endregion

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

            var layerClips = new List<ClipFrameTuple>(group.Count);
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
            List<ClipFrameTuple> clipsNeed,
            CancellationToken token)
        {
            if (clipsNeed.Count == 0) return null;

            IPicture? result = null;
            var frameLocalCache = RentFrameLocalCache();
            try
            {
                int layoutRelativeWidth = ProjectRelativeWidth > 0 ? ProjectRelativeWidth : TargetWidth;
                int layoutRelativeHeight = ProjectRelativeHeight > 0 ? ProjectRelativeHeight : TargetHeight;

                foreach (var (clip, framePic) in clipsNeed)
                {
                    if (token.IsCancellationRequested) return result;
                    if (framePic == null) continue;

                    result = ProcessAndCompositeClip(clip, framePic, result, frame, layoutRelativeWidth, layoutRelativeHeight, frameLocalCache, token);
                    if (result is null) return null; // cancelled
                }

                return result;
            }
            finally
            {
                ReturnFrameLocalCache(frameLocalCache);
            }
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
                    var computer = PluginManager.CreateComputer(mixer.NeedComputer ?? ClassicOverlayMixture.ComputerId);

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
                    merged = BlankFrame.Clone();
                }

                if (merged.Width < TargetWidth || merged.Height < TargetHeight)
                {
                    merged = _threadLocalBlankPlace.Value!.Render(merged, null, TargetWidth, TargetHeight);
                }
                else if (merged.Width > TargetWidth || merged.Height > TargetHeight)
                {
                    merged = merged.Resize(TargetWidth, TargetHeight, false);
                }

                builder?.Append(frame, merged);
                completion.RenderStopwatch?.Stop();
                TrackRenderElapsed(completion.RenderStopwatch?.Elapsed ?? TimeSpan.Zero);
                FrameRenderElapsed[frame] = completion.RenderStopwatch?.Elapsed ?? TimeSpan.Zero;

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

        /// <summary>
        /// Atomically claims a frame in <see cref="_preparedFlagArray"/>.
        /// Equivalent to <c>ConcurrentDictionary.TryAdd(key, 0)</c> on the old <c>PreparedFlag</c> dictionary.
        /// Returns true if the frame was not yet claimed (CAS 0→1 succeeded).
        /// </summary>
        private bool TryClaimPreparedFlag(uint frameIndex)
        {
            int idx = (int)(frameIndex - StartFrame);
            return (uint)idx < _preparedFlagArray.Length && Interlocked.CompareExchange(ref _preparedFlagArray[idx], 1, 0) == 0;
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
            uint totalFrames = Duration;
            int finished = Volatile.Read(ref Finished);
            int prepared = Volatile.Read(ref TotalEnqueued);
            int working = Volatile.Read(ref ThreadWorking);
            int wrote = builder?.WrittenFramesCount ?? 0;
            int totalWriteFrames = builder?.TotalFramesCount ?? 0;
            double finishedProgress = totalFrames > 0 ? (double)finished / totalFrames : 0;
            double preparedProgress = totalFrames > 0 ? (double)prepared / totalFrames : 0;
            TimeSpan eachRender = GetAverageElapsed(Interlocked.Read(ref _renderElapsedTicksTotal), Volatile.Read(ref _renderElapsedCount));
            TimeSpan eachPrepare = GetAverageElapsed(Interlocked.Read(ref _prepareElapsedTicksTotal), Volatile.Read(ref _prepareElapsedCount));
            double onePctLow = OnePercentLowFps;

            if (includeQueueAndWriterStats)
            {
                return $"Overall finished {finishedProgress:p2}, and {preparedProgress:p2} is ready to render. ETA: {GetEstimated(finishedProgress)}, " +
                    $"Memory used by program: {Environment.WorkingSet / 1024 / 1024:n2} MB. \r\n" +
                    $"       (Already elapsed {_renderTotalStopwatch.Elapsed}, Total {prepared}/{totalFrames} prepared and {finished}/{totalFrames} finished, " +
                    $"pending to render: {prepared - finished}, " +
                    $"total write frames: {wrote} wrote and {Math.Max(0, totalWriteFrames - wrote)} pended, " +
                    $"slots {Math.Max(0, MaxThreads - working)}/{MaxThreads}, active workers: {working}, " +
                    $"preparing elapsed average: {eachPrepare}, Each frame render elapsed average: {eachRender}, 1% low FPS: {onePctLow:n1} ({OnePercentLowFrameTime.TotalMilliseconds:n1} ms).)";
            }

            return $"Finished {finishedProgress:p2}. ETA: {GetEstimated(finishedProgress)}, " +
                $"Memory used by program: {Environment.WorkingSet / 1024 / 1024:n2} MB. \r\n" +
                $"       ({finished} of {totalFrames} finished, already elapsed {_renderTotalStopwatch.Elapsed}, " +
                $"preparing elapsed average: {eachPrepare}, Each frame render elapsed average: {eachRender}, 1% low FPS: {onePctLow:n1} ({OnePercentLowFrameTime.TotalMilliseconds:n1} ms).)";
        }

        private static TimeSpan GetAverageElapsed(ConcurrentBag<TimeSpan> elapsedCollection)
        {
            if (elapsedCollection.IsEmpty) return TimeSpan.Zero;
            return new TimeSpan((long)elapsedCollection.Average(x => x.Ticks));
        }

        /// <summary>
        /// Records a per-frame render elapsed time: keeps the public EachElapsed bag for external
        /// consumers and updates O(1) running totals used by the periodic status log.
        /// </summary>
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

        private Dictionary<string, object> RentFrameLocalCache()
        {
            var pool = _frameLocalCachePool.Value;
            if (pool != null && pool.Count > 0)
                return pool.Pop();
            return new Dictionary<string, object>();
        }

        private void ReturnFrameLocalCache(Dictionary<string, object> cache)
        {
            cache.Clear();
            var pool = _frameLocalCachePool.Value;
            if (pool != null && pool.Count < 8)
                pool.Push(cache);
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
                            try { pic?.Dispose(true); } catch { }
                        }
                        perClip.Clear();
                    }
                }
                catch { }

                FrameCache.Clear();

                // Clean up immutable content cache
                try
                {
                    foreach (var pic in ImmutableContentCache.Values)
                        try { pic?.Dispose(true); } catch { }
                }
                catch { }
                ImmutableContentCache.Clear();

                foreach (var item in ClipNeedForFrame.Values.SelectMany(c => c))
                {
                    try
                    {
                        item.Dispose();
                    }
                    catch { }
                }
                ClipNeedForFrame.Clear();

                try
                {
                    foreach (var d in EffectCache.Values.SelectMany(c => c).OfType<IDisposable>())
                    {
                        try { d.Dispose(); } catch { }
                    }
                }
                catch { }
                EffectCache.Clear();

                try { BlankFrame?.Dispose(true); } catch { }

                // Clean up thread-local BlankPlace
                try { _threadLocalBlankPlace?.Dispose(); } catch { }

                // Clean up thread-local frame cache pool
                try { _frameLocalCachePool?.Dispose(); } catch { }

                // Clean up thread limiter
                try { _threadLimiter?.Dispose(); } catch { }

                Array.Clear(_preparedFlagArray);
                while (PreparedFrames.TryDequeue(out _)) { }
                while (BlankFrames.TryDequeue(out _)) { }

                // Clean up layer-by-layer render state
                if (LayerResults != null)
                {
                    foreach (var kv in LayerResults)
                    {
                        try { kv.Value?.Dispose(true); } catch { }
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
                while (!token.IsCancellationRequested && BlankFrames.TryPeek(out var head) && head < frameIndex)
                {
                    if (!BlankFrames.TryDequeue(out var blankIdx))
                        break;

                    if (blankIdx >= frameIndex)
                    {
                        BlankFrames.Enqueue(blankIdx);
                        break;
                    }

                    builder?.Append(blankIdx, BlankFrame.Clone());
                    TrackRenderElapsed(TimeSpan.Zero);
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


        public void ClearCaches()
        {
            FrameCache.Clear();
            ImmutableContentCache.Clear();
            GC.Collect();
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

        private (ulong? Mask, string? Description) ResolveWorkerThreadAffinity()
        {
            (var cores, var desc) = ResolveWorkerThreadAffinityTargetCores();
            if (cores is null || cores.Length == 0)
            {
                return (null, desc);
            }
            return (cores.Aggregate<int, ulong>(0, (mask, c) => mask | (ulong)(1UL << c)), desc);
        }


        private static void StartWorkerThread(string threadName, Action worker, ulong? affinityMask)
        {
            if (affinityMask.HasValue)
            {
                new Thread(() =>
                {
                    try
                    {
                        ThreadAffinityHelper.SetCurrentThreadAffinity(affinityMask.Value);
                    }
                    catch (Exception ex)
                    {
                        Log($"[Renderer] Failed to set thread affinity for {threadName}: {ex.Message}", "warn");
                    }

                    worker();
                })
                {
                    Name = threadName,
                    IsBackground = false,
                    Priority = ThreadPriority.Highest
                }.Start();
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.CurrentThread.Name = threadName;
                worker();
            }, null);
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
            public Stopwatch? RenderStopwatch;
        }

        /// <summary>
        /// Tracks a pair of (clip, frame) for rendering a layer. Used in RenderLayerClips to pass clip/frame pairs to the layer renderer.
        /// </summary>
        /// <param name="Clip"></param>
        /// <param name="Frame"></param>
        internal record struct ClipFrameTuple(IClip Clip, IPicture? Frame)
        {
            public static implicit operator (IClip Clip, IPicture? Frame)(ClipFrameTuple value)
            {
                return (value.Clip, value.Frame);
            }

            public static implicit operator ClipFrameTuple((IClip Clip, IPicture? Frame) value)
            {
                return new ClipFrameTuple(value.Clip, value.Frame);
            }
        }

        #endregion

    }


}
