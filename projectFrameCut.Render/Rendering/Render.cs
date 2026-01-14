using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.VideoMakeEngine;
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
        public int MaxThreads = (int)(Environment.ProcessorCount * 1.75);
        public bool LogState = false;
        public int GCOption = 0;
        public bool LogStatToLogger = false;
        public bool LogProcessStack = false;
        public bool Use16Bit { get; set; } = true;

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
        public ConcurrentBag<List<PictureProcessStack>> ProcessStacks = new();

        // Per-frame diagnostics (used for CSV reporting)
        public ConcurrentDictionary<uint, TimeSpan> FramePrepareElapsed { get; } = new();
        public ConcurrentDictionary<uint, TimeSpan> FrameRenderElapsed { get; } = new();
        public ConcurrentDictionary<uint, List<PictureProcessStack>> FrameProcessStacks { get; } = new();
        public bool running { get; private set; } = false;

        ConcurrentDictionary<string, ConcurrentDictionary<uint, IPicture>> FrameCache = new();
        ConcurrentDictionary<uint, IClip[]> ClipNeedForFrame = new();
        ConcurrentDictionary<MixtureMode, IMixture> MixtureCache = new();
        ConcurrentDictionary<string, IEffect[]> EffectCache = new();

        int ThreadWorking = 0, Finished = 0;
        private SemaphoreSlim _threadLimiter = null!;

        // Thread-local computer cache to avoid contention
        private ThreadLocal<Dictionary<string, IComputer>> _threadLocalComputerCache =
            new ThreadLocal<Dictionary<string, IComputer>>(() => new Dictionary<string, IComputer>());

        ConcurrentQueue<uint> PreparedFrames = new(), BlankFrames = new();
        ConcurrentDictionary<uint, byte> PreparedFlag = new();

        int TotalEnqueued = 0;
        volatile bool PreparerFinished = false;
        private int _width;
        private int _height;
        private int _ppb;

        private IPicture BlankFrame = null!;

        private PlaceEffect BlankPlace = new()
        {
            StartX = 0,
            StartY = 0
        };

        public async Task GoRender(CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(builder, nameof(builder));
            ArgumentNullException.ThrowIfNull(Clips, nameof(Clips));
            _renderTotalStopwatch.Restart();

            // Initialize thread limiter
            _threadLimiter = new SemaphoreSlim(MaxThreads, MaxThreads);

            BlankFrame = Use16Bit ? Picture16bpp.GenerateSolidColor(builder.Width, builder.Height, 0, 0, 0, 0) : Picture8bpp.GenerateSolidColor(builder.Width, builder.Height, 0, 0, 0, 0);
            BlankFrame.Flag = IPicture.PictureFlag.NoDisposeAfterWrite;
            BlankFrame.Disposed = null;
            GC.KeepAlive(BlankFrame);
            ConcurrentQueue<Exception> exceptions = new();

            _ppb = Use16Bit ? 16 : 8;

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
                            if (EachElapsed.Count > 0)
                                each = new TimeSpan((long)EachElapsed.Average(x => x.Ticks));
                            if (EachElapsedForPreparing.Count > 0)
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

            _width = builder.Width;
            _height = builder.Height;

            await Task.Delay(50, token);

            Stopwatch lastActivity = Stopwatch.StartNew();
            int lastFinished = Volatile.Read(ref Finished);

            while (true)
            {
                if (IsPaused)
                {
                    Log("[Render] Paused.", "info");
                    while (IsPaused)
                    {
                        if (token.IsCancellationRequested) break;
                        await Task.Delay(500, token);
                    }
                    Log("[Render] Resumed.", "info");
                }

                if (MemoryThresholdBytes > 0 && !IsPaused)
                {
                    var currentMem = Environment.WorkingSet;
                    if (currentMem > MemoryThresholdBytes)
                    {
                        Log($"[Render] Memory usage {currentMem / 1024 / 1024}MB exceeded threshold {MemoryThresholdBytes / 1024 / 1024}MB. Pausing...", "warn");
                        IsPaused = true;
                        if (OnLowMemory != null)
                        {
                            _ = Task.Run(async () => await OnLowMemory(this));
                        }
                        continue;
                    }
                }

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
                        const int maxWaitIterations = 200; // ~1 second max wait
                        while (!PreparerFinished && Duration - Volatile.Read(ref Finished) > MaxThreads / 2 - 2 && PreparedFrames.Count < MaxThreads / 2)
                        {
                            await Task.Delay(5);
                            waitIterations++;
                            if (waitIterations >= maxWaitIterations)
                            {
                                Log($"[Render] Wait timeout reached, proceeding with {PreparedFrames.Count} prepared frames.", "warn");
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

        public void PrepareRender(CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(Clips, nameof(Clips));
            MixtureCache = new ConcurrentDictionary<MixtureMode, IMixture>(new Dictionary<MixtureMode, IMixture> { { MixtureMode.Overlay, new OverlayMixture() } });
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
            foreach (var item in Clips)
            {
                var effectInstances = EffectHelper.GetEffectsInstances(item.Effects);
                EffectCache.AddOrUpdate(item.Id, effectInstances, (_, _) => effectInstances);
                foreach (var effect in effectInstances)
                {
                    if (effect.YieldProcessStep == true && effect.NeedComputer is not null) throw new InvalidDataException($"A effect can't both yield process step, and use a computer.");
                }
                Log($"[Preparer] Cached {effectInstances.Length} effects for clip {item.Id} ({string.Join(", ",effectInstances.Select(c => $"{c.TypeName}:'{c.Name}'"))})");
            }

        }

        static ClipEquabilityComparer clipEquabilityComparer = new();


        private void PrepareSource(CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(Clips, nameof(Clips));
            Stopwatch sw = new();
            foreach (var idx in ClipNeedForFrame.Keys.OrderBy(x => x))
            {
                // Throttling: Wait if too many frames are prepared but not yet rendered
                while (Volatile.Read(ref TotalEnqueued) - Volatile.Read(ref Finished) > MaxThreads * 4 && !token.IsCancellationRequested)
                {
                    Log($"[Preparer] Waiting for more render slots... prepared but not rendered: {Volatile.Read(ref TotalEnqueued) - Volatile.Read(ref Finished)}");
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
            Stopwatch sw = Stopwatch.StartNew();
            IPicture result = null!;
            
            PreparedFlag.TryRemove(targetFrame, out _);

            if (!ClipNeedForFrame.Remove(targetFrame, out var ClipsNeed) || ClipsNeed.Length == 0)
            {
                sw.Stop();
                Log($"[Render] Frame {targetFrame} is empty.");
                builder!.Append(targetFrame, BlankFrame);
                Interlocked.Increment(ref Finished);
                InvokeProgress();
                EachElapsed.Add(sw.Elapsed);
                FrameRenderElapsed[targetFrame] = sw.Elapsed;
                return;
            }


            foreach (var clip in ClipsNeed)
            {
                if (token.IsCancellationRequested) return;

                if (!FrameCache.TryGetValue(clip.Id, out var perClipCache))
                {
                    Log($"[Render] WARN: Frame cache not found for clip {clip.Id}.");
                    continue;
                }

                if (!perClipCache.TryGetValue(targetFrame, out var frame) || frame is null)
                {
                    Log($"[Render] WARN: Frame {targetFrame} not found in cache for clip {clip.Id}.");
                    continue;
                }

                if (clip.Effects.ArrayAny() && EffectCache.TryGetValue(clip.Id, out var effects))
                {
                    List<IPictureProcessStep> steps = new();
                    bool lastIsProcessStep = false;
                    foreach (var item in effects)
                    {
                        var computer = GetOrCreateComputer(item.NeedComputer);
                        if (item.YieldProcessStep != lastIsProcessStep)
                        {
                            frame = PictureProcesser.Process(steps, frame, _ppb);
                            steps.Clear();
                        }
                        if (item is IContinuousEffect c)
                        {
                            ProcessContinuousEffect(targetFrame, clip, computer, ref frame, steps, ref lastIsProcessStep, item, c);
                        }
                        else
                        {
                            ProcessEffect(ref frame, steps, ref lastIsProcessStep, item, computer);
                        }
                    }
                    if (steps.ListAny())
                    {
                        frame = PictureProcesser.Process(steps, frame, _ppb);
                    }
                }

                if (result is null)
                {
                    result = frame;
                }
                else
                {
                    if (MixtureCache.TryGetValue(clip.MixtureMode, out var mix))
                    {
                        var temp = mix.Mix(result, frame, GetOrCreateComputer(mix.ComputerId)).Resize(_width, _height, false);
                        result.Dispose();
                        result = temp;
                    }
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
                ProcessStacks.Add(result.ProcessStack);
                FrameProcessStacks[targetFrame] = result.ProcessStack;
            }
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
            Interlocked.Increment(ref Finished);
            InvokeProgress();
            sw.Stop();
            if(LogState) Log($"[Render] Frame {targetFrame} render done, elapsed {sw.Elapsed}");
            EachElapsed.Add(sw.Elapsed);
            FrameRenderElapsed[targetFrame] = sw.Elapsed;
            return;



        }

        private void ProcessEffect(ref IPicture frame, List<IPictureProcessStep> steps, ref bool lastIsProcessStep, IEffect item, IComputer? computer)
        {
            if (item.YieldProcessStep)
            {
                lastIsProcessStep = true;
                try
                {
                    var step = item.GetStep(frame, _width, _height);
                    steps.Add(step);
                    if (IPicture.DiagImagePath is not null) LogDiagnostic($"Process step for effect {item.Name}({item.TypeName}) : {step.GetProcessStack()}");
                }
                catch (Exception ex)
                {
                    Log($"[Render] WARN: Failed to get process steps for effect {item.Name}: {ex}");
                    lastIsProcessStep = false;
                    frame = item.Render(frame, computer, _width, _height);
                }
            }
            else
            {
                frame = item.Render(frame, computer, _width, _height);
            }
        }

        private void ProcessContinuousEffect(uint targetFrame, IClip clip, IComputer? computer, ref IPicture frame, List<IPictureProcessStep> steps, ref bool lastIsProcessStep, IEffect item, IContinuousEffect c)
        {
            if (c.EndPoint == 0 && c.EndPoint == 0)
            {
                c.StartPoint = (int)(clip.StartFrame);
                c.EndPoint = (int)(c.StartPoint + clip.Duration * clip.SecondPerFrameRatio);
            }
            if (c.YieldProcessStep)
            {
                lastIsProcessStep = true;
                try
                {
                    var step = c.GetStep(frame, targetFrame, _width, _height);
                    steps.Add(step);
                    if (IPicture.DiagImagePath is not null) LogDiagnostic($"Process step for effect {c.Name}({c.TypeName}) : {step.GetProcessStack()}");

                }
                catch (Exception ex)
                {
                    Log($"[Render] WARN: Failed to get process steps for continuous effect {c.Name}: {ex}");
                    lastIsProcessStep = false;
                    frame = c.Render(frame, targetFrame, computer, _width, _height);
                }



            }
            else
            {
                frame = c.Render(frame, targetFrame, computer, _width, _height);
            }
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

                try
                {
                    foreach (var mix in MixtureCache.Values)
                    {
                        if (mix is IDisposable d)
                        {
                            try { d.Dispose(); } catch { }
                        }
                    }
                }
                catch { }
                MixtureCache.Clear();

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
