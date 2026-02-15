using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace projectFrameCut.Render.Rendering
{
    public class Renderer
    {
        public IClip[]? Clips;
        public uint Duration;
        public uint StartFrame = 0;
        public VideoBuilder? builder;
        private int _maxThreads = (int)(Environment.ProcessorCount * 1.75);
        public int MaxThreads
        {
            get => _maxThreads;
            set => _maxThreads = value;
        }
        public bool LogState = false;
        public int GCOption = 0;
        public bool LogStatToLogger = false;
        public bool LogProcessStack = false;
        public bool Use16Bit { get; set; } = true;

        /// <summary>
        /// Android 平台由于 OpenGL 必须在主线程执行，多线程会导致主线程拥塞。
        /// 自动检测并限制 Android 的并发度。
        /// </summary>
        private bool IsAndroid => OperatingSystem.IsAndroid();
        private int GetOptimalMaxThreads()
        {
            if (IsAndroid)
            {
                // Android OpenGL 受主线程限制，过多线程会排队等待主线程导致死锁
                // 建议: 1-2 个线程，最多不超过 3
                int optimal = Math.Min(2, Math.Max(1, Environment.ProcessorCount / 4));
                Log($"[Renderer] Android 平台检测到，限制渲染线程数为 {optimal} (原: {_maxThreads}) 以避免主线程拥塞", "info");
                return optimal;
            }
            return _maxThreads;
        }

        public bool IsPaused { get; set; } = false;
        public long MemoryThresholdBytes { get; set; } = 0;
        public Func<Renderer, Task>? OnLowMemory;

        public void ClearCaches()
        {
            FrameCache.Clear();
            GC.Collect();
        }

        public event Action<double, TimeSpan>? OnProgressChanged;
        private Stopwatch _renderTotalStopwatch = new();

        public ConcurrentBag<TimeSpan> EachElapsed = new(), EachElapsedForPreparing = new();

        // Per-frame diagnostics (used for CSV reporting)
        public ConcurrentDictionary<uint, TimeSpan> FramePrepareElapsed { get; } = new();
        public ConcurrentDictionary<uint, TimeSpan> FrameRenderElapsed { get; } = new();
        public ConcurrentDictionary<uint, List<PictureProcessStack>> FrameProcessStacks { get; } = new();

        public bool running { get; private set; } = false;

        ConcurrentDictionary<string, ConcurrentDictionary<uint, IPicture>> FrameCache = new();
        ConcurrentDictionary<uint, IClip[]> ClipNeedForFrame = new();
        //ConcurrentDictionary<MixtureMode, IMixture> MixtureCache = new();
        ConcurrentDictionary<string, IEffect[]> EffectCache = new();
        ConcurrentDictionary<string, object> BindableEffectResultCache = new();
        OverlayMixture globalMix = new();
        IComputer mixComputer = null!;

        int ThreadWorking = 0, Finished = 0;
        private SemaphoreSlim _threadLimiter = null!;

        // Thread-local computer cache to avoid contention
        private ThreadLocal<Dictionary<string, IComputer>> _threadLocalComputerCache =
            new ThreadLocal<Dictionary<string, IComputer>>(() => new Dictionary<string, IComputer>());

        private static bool IsProfilerAttached =>
            string.Equals(Environment.GetEnvironmentVariable("COR_ENABLE_PROFILING"), "1", StringComparison.Ordinal);

        ConcurrentQueue<uint> PreparedFrames = new(), BlankFrames = new();
        ConcurrentDictionary<uint, byte> PreparedFlag = new();

        int TotalEnqueued = 0;
        volatile bool PreparerFinished = false;
        private int _width;
        private int _height;
        private int _ppb;

        private IPicture BlankFrame = null!;

        private PlaceEffect_ImageSharp BlankPlace = new()
        {
            StartX = 0,
            StartY = 0
        };

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

            // Apply platform-specific optimizations
            int effectiveMaxThreads = GetOptimalMaxThreads();
            if (effectiveMaxThreads != MaxThreads)
            {
                Log($"[Renderer] 平台优化: MaxThreads 从 {MaxThreads} 调整为 {effectiveMaxThreads}", "info");
                MaxThreads = effectiveMaxThreads;
            }

            // Initialize thread limiter
            _threadLimiter = new SemaphoreSlim(MaxThreads, MaxThreads);

            BlankFrame = Use16Bit ? Picture16bpp.GenerateSolidColor(builder.Width, builder.Height, 0, 0, 0, 0) : Picture8bpp.GenerateSolidColor(builder.Width, builder.Height, 0, 0, 0, 0);
            BlankFrame.Flag = IPicture.PictureFlag.NoDisposeAfterWrite;
            BlankFrame.Disposed = null;
            GC.KeepAlive(BlankFrame);
            ConcurrentQueue<Exception> exceptions = new();

            _ppb = Use16Bit ? 16 : 8;
            _width = builder.Width;
            _height = builder.Height;


            running = true;
            if (LogStatToLogger)
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

                            Log($"[STAT] " +
                                $"Overall finished {finished / d:p2}, and {TotalEnqueued / d:p2} is ready to render. ETA: {GetEstimated(finished / d)}, " +
                                $"Memory used by program: {Environment.WorkingSet / 1024 / 1024:n2} MB. \r\n" +
                                $"       (Already elapsed {_renderTotalStopwatch.Elapsed}, Total {TotalEnqueued}/{d} prepared and {finished}/{d} finished, " +
                                $"pending to render: {Volatile.Read(ref TotalEnqueued) - finished}, " +
                                $"total write frames: {wrote} wrote and {builder.TotalFramesCount - wrote} pended, " +
                                $"slots {Math.Max(0, MaxThreads - working)}/{MaxThreads}, active workers: {working}, " +
                                $"preparing elapsed average: {eachPrepare}, Each frame render elapsed average: {each}.)");
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

            Thread preparer = new(() => PrepareSource(token));
            preparer.Name = "Preparer thread";
            preparer.IsBackground = true;
            preparer.Start();



            await Task.Delay(50, token);

            Stopwatch lastActivity = Stopwatch.StartNew();
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

                bool forceStart = lastActivity.Elapsed.TotalMinutes >= 1;

                if (preparedCount > 0 && (forceStart || (availableSlots > 0 && working * 0.65 < MaxThreads)))
                {
                    int toStart = forceStart ? preparedCount : Math.Min(preparedCount, availableSlots);

                    if (forceStart)
                    {
                        Log($"[Watchdog] No rendered frame progress for 1 minute. prepared={preparedCount}, working={working}/{MaxThreads}, finished={Volatile.Read(ref Finished)}/{Duration}.", "warn");
                        if (availableSlots == 0)
                        {
                            Log($"[Watchdog] No available slots (all render threads busy). This often means a render thread is blocked (e.g. in effects/mixer) or the writer is stuck waiting for a missing frame index.", "warn");
                        }
                    }
                    else
                    {
                        // Add timeout to avoid infinite wait when preparer is slow (e.g., on Android with OpenGL main-thread bottleneck)
                        int waitIterations = 0;
                        // Android 平台减少等待时间和阈值，因为主线程串行处理 OpenGL 很慢
                        int maxWaitIterations = IsAndroid ? 100 : 200; // Android: ~0.5秒, 其他: ~1秒
                        int minPreparedFrames = IsAndroid ? Math.Max(1, MaxThreads / 4) : MaxThreads / 2;

                        while (!PreparerFinished && Duration - Volatile.Read(ref Finished) > MaxThreads / 2 - 2 && PreparedFrames.Count < minPreparedFrames)
                        {
                            await Task.Delay(5);
                            waitIterations++;
                            if (waitIterations >= maxWaitIterations)
                            {
                                Log($"[Render] Wait timeout reached (platform: {(IsAndroid ? "Android" : "Other")}), proceeding with {PreparedFrames.Count} prepared frames.", "warn");
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

                        if (!_threadLimiter.Wait(0))
                        {
                            PreparedFrames.Enqueue(targetFrame);
                            break;
                        }

                        Interlocked.Increment(ref ThreadWorking);

                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try
                            {
                                FlushBlankFramesBefore(targetFrame, token);
                                RenderAFrame(targetFrame, token);
                            }
                            catch (Exception ex)
                            {
                                Log($"Error rendering frame {targetFrame}: {ex}", "error");
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
                else
                {
                    if (PreparerFinished && PreparedFrames.IsEmpty && Volatile.Read(ref ThreadWorking) == 0 && !BlankFrames.IsEmpty)
                    {
                        FlushBlankFramesBefore(StartFrame + Duration, token);
                    }
                    await Task.Delay(10, token);
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

            _ppb = Use16Bit ? 16 : 8;
            _width = builder.Width;
            _height = builder.Height;

            BlankFrame = Use16Bit
                ? Picture16bpp.GenerateSolidColor(builder.Width, builder.Height, 0, 0, 0, 0)
                : Picture8bpp.GenerateSolidColor(builder.Width, builder.Height, 0, 0, 0, 0);
            BlankFrame.Flag = IPicture.PictureFlag.NoDisposeAfterWrite;
            BlankFrame.Disposed = null;
            GC.KeepAlive(BlankFrame);

            running = true;
            InitializeRenderCaches();

            if (LogStatToLogger)
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

                            Log($"[STAT] " +
                                $"Finished {finished / d:p2}. ETA: {GetEstimated(finished / d)}, " +
                                $"Memory used by program: {Environment.WorkingSet / 1024 / 1024:n2} MB. \r\n" +
                                $"       ({finished} of {d} finished, already elapsed {_renderTotalStopwatch.Elapsed}, " +
                                $"preparing elapsed average: {eachPrepare}, Each frame render elapsed average: {each}.)");
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
                RenderAFrameSync(idx, token);
            }

            if (token.IsCancellationRequested)
            {
                ReleaseResources();
                return;
            }

            ReleaseResources();
        }

        public void PrepareRender(CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(Clips, nameof(Clips));
            if (IsAndroid)
            {
                Log($"[Preparer] Android 平台：OpenGL 计算限制在主线程，预期渲染速度较慢", "info");
            }
            InitializeRenderCaches();
            bool found = false;
            for (uint idx = StartFrame; idx < StartFrame + Duration; idx++)
            {
                found = false;
                if (token.IsCancellationRequested) return;
                foreach (var item in Clips)
                {
                    if (token.IsCancellationRequested) return;


                    if (item.StartFrame <= idx && item.Duration * item.SecondPerFrameRatio + item.StartFrame >= idx)
                    {
                        found = true;
                        ClipNeedForFrame.AddOrUpdate(
                            idx,
                            (_) => [item],
                            (_, old) => old.Append(item).OrderBy(x => x.LayerIndex).ToArray());
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

        static ClipEquabilityComparer clipEquabilityComparer = new();


        private void PrepareSource(CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(Clips, nameof(Clips));
            Stopwatch sw = new();
            // Android 需要更激进的节流策略，因为主线程串行处理 OpenGL 导致渲染很慢
            int throttleThreshold = IsAndroid ? MaxThreads : MaxThreads * 4;

            foreach (var idx in ClipNeedForFrame.Keys.OrderBy(x => x))
            {
                // Throttling: Wait if too many frames are prepared but not yet rendered
                while (!IsProfilerAttached && Volatile.Read(ref TotalEnqueued) - Volatile.Read(ref Finished) > throttleThreshold && !token.IsCancellationRequested)
                {
                    Log($"[Preparer] Waiting for more render slots... prepared but not rendered: {Volatile.Read(ref TotalEnqueued) - Volatile.Read(ref Finished)} (threshold: {throttleThreshold})");
                    Thread.Sleep(500);
                }

                if (token.IsCancellationRequested) return;
                sw.Restart();

                foreach (var item in Clips)
                {
                    if (token.IsCancellationRequested) return;

                    if (ClipNeedForFrame[idx].Contains(item, clipEquabilityComparer))
                    {
                        var frame = item.GetFrame(idx, _width, _height, true);
                        if (frame != null)
                        {
                            if (Use16Bit && frame.bitPerPixel != IPicture.PicturePixelMode.UShortPicture)
                            {
                                frame = frame.ToBitPerPixel(IPicture.PicturePixelMode.UShortPicture);
                            }
                            else if (!Use16Bit && frame.bitPerPixel != IPicture.PicturePixelMode.BytePicture)
                            {
                                frame = frame.ToBitPerPixel(IPicture.PicturePixelMode.BytePicture);
                            }
                            FrameCache.GetOrAdd(item.Id, (_) => new()).TryAdd(idx, frame);
                        }
                    }
                }
                if (PreparedFlag.TryAdd(idx, 0))
                {
                    PreparedFrames.Enqueue(idx);
                    Interlocked.Increment(ref TotalEnqueued);
                }
                sw.Stop();
                EachElapsedForPreparing.Add(sw.Elapsed);
                FramePrepareElapsed[idx] = sw.Elapsed;
                if (LogState) Log($"[Preparer] Frame {idx} is ready to render, elapsed {sw.Elapsed}");

            }
            Log($"[Preparer] All frames are ready.");

            // mark preparer finished so main loop can complete when renders done
            PreparerFinished = true;
        }

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
                    token,
                    [],
                    logEmptyFrame: true,
                    recordPrepareElapsed: false,
                    stopBeforeAppendInBlank: true,
                    usedFrames: null);
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
                token,
                framesToRender,
                logEmptyFrame: true,
                recordPrepareElapsed: false,
                stopBeforeAppendInBlank: true,
                usedFrames: null);

            if (token.IsCancellationRequested) return;

            foreach (var clip in ClipsNeed)
            {
                if (FrameCache.TryGetValue(clip.Id, out var perClipCache))
                {
                    if (perClipCache.TryRemove(targetFrame, out var pic))
                    {
                        try { pic?.Dispose(); } catch { }
                    }
                }
            }
            return;
        }

        private void RenderAFrameSync(uint targetFrame, CancellationToken token)
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
                if (item.StartFrame <= targetFrame && item.Duration * item.SecondPerFrameRatio + item.StartFrame >= targetFrame)
                {
                    clipsNeed.Add(item);
                }
            }
            clipsNeed = clipsNeed.OrderBy(x => x.LayerIndex).ToList();

            var usedFrames = new List<IPicture>();
            var framesToRender = new List<(IClip Clip, IPicture? Frame)>(clipsNeed.Count);
            foreach (var clip in clipsNeed)
            {
                var frame = clip.GetFrame(targetFrame, _width, _height, true);
                if (frame == null)
                {
                    Log($"[Render] WARN: Frame {targetFrame} not found for clip {clip.Id}.");
                    framesToRender.Add((clip, null));
                    continue;
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

            RenderAFrameInternal(
                targetFrame,
                token,
                framesToRender,
                logEmptyFrame: false,
                recordPrepareElapsed: true,
                stopBeforeAppendInBlank: false,
                usedFrames: usedFrames);

            if (token.IsCancellationRequested) return;

            foreach (var pic in usedFrames)
            {
                try { pic?.Dispose(); } catch { }
            }
        }

        private void RenderAFrameInternal(
            uint targetFrame,
            CancellationToken token,
            List<(IClip Clip, IPicture? Frame)> clipsNeed,
            bool logEmptyFrame,
            bool recordPrepareElapsed,
            bool stopBeforeAppendInBlank,
            List<IPicture>? usedFrames)
        {
            Stopwatch sw = Stopwatch.StartNew();
            IPicture result = null!;
            Dictionary<string, object> frameLocalCache = new();

            if (clipsNeed.Count == 0)
            {
                if (stopBeforeAppendInBlank) sw.Stop();
                if (logEmptyFrame) Log($"[Render] Frame {targetFrame} is empty.");
                builder!.Append(targetFrame, BlankFrame);
                Interlocked.Increment(ref Finished);
                InvokeProgress();
                if (!stopBeforeAppendInBlank) sw.Stop();
                EachElapsed.Add(sw.Elapsed);
                FrameRenderElapsed[targetFrame] = sw.Elapsed;
                if (recordPrepareElapsed) FramePrepareElapsed.TryAdd(targetFrame, TimeSpan.Zero);
                return;
            }

            foreach (var (clip, Frame) in clipsNeed)
            {
                var frame = Frame;
                if (token.IsCancellationRequested) return;

                if (frame == null)
                {
                    continue;
                }

                usedFrames?.Add(frame);

                if (EffectCache.TryGetValue(clip.Id, out var effects) && effects is not null)
                {
                    List<IPictureProcessStep> steps = new();
                    bool lastIsProcessStep = false, effectsChanged = false;
                    var effectCopy = effects.ToList();
                    foreach (var item in effects)
                    {
                        var computer = GetOrCreateComputer(item.NeedComputer);
                        if (item.YieldProcessStep != lastIsProcessStep)
                        {
                            frame = PictureProcesser.Process(steps, frame, _ppb);
                            steps.Clear();
                        }

                        if (item.IsNormalEffect)
                        {
                            if (item.IsContinuousEffect || item.IsBindableArgsEffect) goto notdefined;
                            EffectProcessing.ProcessEffect(ref frame, steps, ref lastIsProcessStep, item, computer, _width, _height);
                            continue;
                        }
                        else if (item.IsContinuousEffect)
                        {
                            if (item.IsBindableArgsEffect || item.IsNormalEffect) goto notdefined;
                            if (item is not IContinuousEffect c) goto notdefined;
                            EffectProcessing.ProcessContinuousEffect(targetFrame, clip, computer, ref frame, steps, ref lastIsProcessStep, item, c, _width, _height);
                            continue;
                        }
                        else if (item.IsBindableArgsEffect)
                        {
                            if (item.IsContinuousEffect || item.IsNormalEffect) goto notdefined;
                            if (item is not IBindableArgumentEffect b) goto notdefined;
                            if (EffectProcessing.ProcessBindableArgsEffect(targetFrame, ref frame, ref BindableEffectResultCache, frameLocalCache, clip, steps, ref lastIsProcessStep, b, computer, _width, _height))
                            {
                                effectCopy.Remove(item);
                                effectsChanged = true;
                            }
                            continue;
                        }

                    notdefined:
                        Log($"[Render] Effect {item.Name} of clip {clip.Id} has an not static defined type.", "warn");
                        if (item is IBindableArgumentEffect be)
                        {
                            if (EffectProcessing.ProcessBindableArgsEffect(targetFrame, ref frame, ref BindableEffectResultCache, frameLocalCache, clip, steps, ref lastIsProcessStep, be, computer, _width, _height))
                            {
                                effectCopy.Remove(item);
                                effectsChanged = true;
                            }
                        }
                        else if (item is IContinuousEffect c)
                        {
                            EffectProcessing.ProcessContinuousEffect(targetFrame, clip, computer, ref frame, steps, ref lastIsProcessStep, item, c, _width, _height);
                        }
                        else
                        {
                            EffectProcessing.ProcessEffect(ref frame, steps, ref lastIsProcessStep, item, computer, _width, _height);
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
                }

                if (result is null)
                {
                    result = frame;
                }
                else
                {
                    var temp = globalMix.Mix(result, frame, mixComputer, _ppb).Resize(_width, _height, false);
                    result.Dispose();
                    result = temp;
                }
            }

            if (result is null)
            {
                result = BlankFrame;
            }
            else if (result.Width < _width || result.Height < _height)
            {
                result = BlankPlace.Render(result, null, _width, _height);
            }
            else if (result.Width > _width || result.Height > _height)
            {
                result = result.Resize(_width, _height, false);
            }

            builder!.Append(targetFrame, result);
            if (LogProcessStack)
            {
                FrameProcessStacks[targetFrame] = result.ProcessStack;
            }

            Interlocked.Increment(ref Finished);
            sw.Stop();
            InvokeProgress();
            if (LogState) Log($"[Render] Frame {targetFrame} render done, elapsed {sw.Elapsed}");
            EachElapsed.Add(sw.Elapsed);
            FrameRenderElapsed[targetFrame] = sw.Elapsed;
            if (recordPrepareElapsed) FramePrepareElapsed.TryAdd(targetFrame, TimeSpan.Zero);
        }

        private void InitializeRenderCaches()
        {
            EffectCache.Clear();
            //MixtureCache = new ConcurrentDictionary<MixtureMode, IMixture>(new Dictionary<MixtureMode, IMixture>
            //{
            //    { MixtureMode.Overlay, new OverlayMixture() }
            //});

            foreach (var item in Clips ?? Array.Empty<IClip>())
            {
                if (!item.Effects.ArrayAny()) continue;
                var effectInstances = EffectHelper.GetEffectsInstances(item.Effects);
                EffectCache.AddOrUpdate(item.Id, effectInstances, (_, _) => effectInstances);
                foreach (var effect in effectInstances)
                {
                    if (effect.YieldProcessStep == true && effect.NeedComputer is not null)
                        throw new InvalidDataException("A effect can't both yield process step, and use a computer.");
                }
                Log($"[Preparer] Cached {effectInstances.Length} effects for clip {item.Id} ({string.Join(", ", effectInstances.Select(c => $"{c.TypeName}:'{c.Name}'"))})");
            }

            mixComputer = GetOrCreateComputer(globalMix.ComputerId) ?? throw new NullReferenceException("Can't create computer for global mixer.");
        }

        private void InvokeProgress()
        {
            double prog = (double)Volatile.Read(ref Finished) / Duration;
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

                //try
                //{
                //    foreach (var mix in MixtureCache.Values)
                //    {
                //        if (mix is IDisposable d)
                //        {
                //            try { d.Dispose(); } catch { }
                //        }
                //    }
                //}
                //catch { }
                //MixtureCache.Clear();

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

                // Clean up thread limiter
                try { _threadLimiter?.Dispose(); } catch { }

                PreparedFlag.Clear();
                while (PreparedFrames.TryDequeue(out _)) { }
                while (BlankFrames.TryDequeue(out _)) { }
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


    }
}
