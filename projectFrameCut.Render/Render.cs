using projectFrameCut.Shared;
using projectFrameCut.VideoMakeEngine;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;

namespace projectFrameCut.Render
{
    public class Renderer
    {
        public IClip[] Clips;
        public uint Duration;
        public VideoBuilder builder;
        public int MaxThreads = (int)(Environment.ProcessorCount * 1.75);
        public bool LogState = false;
        public int GCOption = 0;

        public event Action<double>? OnProgressChanged;

        ConcurrentDictionary<string, ConcurrentDictionary<uint, IPicture>> FrameCache = new();
        ConcurrentDictionary<uint, IClip[]> ClipNeedForFrame = new();
        ConcurrentDictionary<string, IComputer> ComputerCache = new();
        ConcurrentDictionary<MixtureMode, IMixture> MixtureCache = new();
        public ConcurrentBag<TimeSpan> EachElapsed = new(), EachElapsedForPreparing = new();

        List<Thread> Threads = new List<Thread>();

        int ThreadWorking = 0, Finished = 0;

        ConcurrentQueue<uint> PreparedFrames = new(), BlankFrames = new();
        ConcurrentDictionary<uint, byte> PreparedFlag = new();

        int TotalEnqueued = 0;
        volatile bool PreparerFinished = false;
        private int _width;
        private int _height;

        private IPicture BlankFrame = null!;

        public async Task GoRender(CancellationToken token)
        {
            BlankFrame = Picture.GenerateSolidColor(builder.Width, builder.Height, 0, 0, 0, 0);

            Thread preparer = new(() => PrepareSource(token));
            preparer.Name = "Preparer thread";
            preparer.IsBackground = true;
            preparer.Start();

            _width = builder.Width;
            _height = builder.Height;

            await Task.Delay(50);

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

                if (preparedCount > 0 && availableSlots > 0 && working * 0.65 < MaxThreads)
                {
                    int toStart = Math.Min(preparedCount, availableSlots);
                    while (Duration - Volatile.Read(ref Finished) > MaxThreads / 2 - 2 && PreparedFrames.Count < MaxThreads / 2)
                    {
                        await Task.Delay(5);
                    }
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

                        Interlocked.Increment(ref ThreadWorking);
                        var thread = new Thread(() =>
                        {
                            try
                            {
                                FlushBlankFramesBefore(targetFrame, token);
                                RenderAFrame(targetFrame, token);
                            }
                            catch (Exception ex)
                            {
                                Log($"Error rendering frame {targetFrame}: {ex}", "error");
                                throw;
                            }
                            finally
                            {
                                Interlocked.Decrement(ref ThreadWorking);
                                Interlocked.Increment(ref Finished);
                                OnProgressChanged?.Invoke((double)Volatile.Read(ref Finished) / Duration);
                            }
                        });

                        thread.Name = $"Frame {targetFrame}'s render thread";
                        thread.IsBackground = false;
                        thread.Start();
                        Threads.Add(thread);
                    }
                }
                else
                {
                    await Task.Delay(10);
                }

                if (LogState)
                {
                    int f = Volatile.Read(ref Finished);
                    Console.Error.WriteLine($"@@{f},{Duration}");
                }

            }
            if (token.IsCancellationRequested)
            {
                Log("Render cancelled by user.");
                ReleaseResources();
                return;
            }
            Log($"[Preparer] All frames are prepared and waiting for render done...");

            foreach (var t in Threads)
            {
                try
                {
                    if (t.IsAlive)
                    {
                        await Task.Delay(5000, token);
                    }
                }
                catch { }
            }
            ReleaseResources();

        }

        public void PrepareRender(CancellationToken token)
        {
            bool found = false;
            for (uint idx = 0; idx < Duration; idx++)
            {
                found = false;
                if (token.IsCancellationRequested) return;
                foreach (var item in Clips)
                {
                    if (token.IsCancellationRequested) return;


                    if (item.StartFrame * item.SecondPerFrameRatio <= idx && item.Duration * item.SecondPerFrameRatio + item.StartFrame * item.SecondPerFrameRatio >= idx)
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
                    //Log($"[Preparer] Frame {idx} is empty.");
                    BlankFrames.Enqueue(idx);
                }
                else
                {
                    //Log($"[Preparer] Frame {idx} have {ClipNeedForFrame[idx].Length} clips.");
                }

                if (idx % 50 == 0)
                {
                    Log($"[Preparer] source preparing finished {(float)idx / (float)Duration:p3} ({idx}/{Duration})");
                }

            }
            Log($"[Preparer] source preparing done.");

        }

        static ClipEquabilityComparer clipEquabilityComparer = new();

        private void PrepareSource(CancellationToken token)
        {
            Stopwatch sw = new();
            foreach (var idx in ClipNeedForFrame.Keys)
            {
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
                Log($"[Preparer] Frame {idx} is ready to render, elapsed {sw.Elapsed}");

            }

            // mark preparer finished so main loop can complete when renders done
            PreparerFinished = true;
        }

        private void RenderAFrame(uint targetFrame, CancellationToken token)
        {
            if (targetFrame > Duration)
            {
                Log($"[Render] WARN: Target frame {targetFrame} exceeds project duration. Ignore.");
                return;
            }
            Stopwatch sw = Stopwatch.StartNew();
            Picture result = null!;

            if (!PreparedFlag.TryRemove(targetFrame, out _))
            {
                Log($"[Render] WARN: Target frame {targetFrame} not ready yet. Waiting...");
                while (!PreparedFrames.Contains(targetFrame)) Thread.Sleep(500);
                Log($"Target frame {targetFrame} is ready yet. Continue.");
                PreparedFlag.TryRemove(targetFrame, out _);
            }

            if (!ClipNeedForFrame.Remove(targetFrame, out var ClipsNeed) || ClipsNeed.Length == 0)
            {
                sw.Stop();
                Log($"[Render] Frame {targetFrame} is empty.");
                builder.Append(targetFrame, (Picture)BlankFrame);
                EachElapsed.Add(sw.Elapsed);
                return;
            }


            foreach (var clip in ClipsNeed)
            {
                if (token.IsCancellationRequested) return;

                IPicture frame;
                if (!FrameCache.TryGetValue(clip.Id, out var perClipCache) || !perClipCache.TryRemove(targetFrame, out frame))
                {
                    Log($"[Render] WARN: Prepared frame {targetFrame} not found in cache for clip {clip.Id}. Regenerating.");
                    try
                    {
                        frame = clip.GetFrame(targetFrame, _width, _height, true) ?? Picture.GenerateSolidColor(_width, _height, 0, 0, 0, 0);
                    }
                    catch (Exception ex)
                    {
                        Log(ex, $"regenerate frame {targetFrame} for clip {clip.Id}", this);
                        throw;
                    }
                }

                if (clip.Effects != null)
                {
                    foreach (var item in clip.EffectsInstances ?? IClip.GetEffectsInstances(clip.Effects))
                    {
                        Picture picFrame;
                        if (frame is Picture p) picFrame = p;
                        else picFrame = (Picture)frame.ToBitPerPixel(16);

                        frame = item.Render(picFrame,
                                        ComputerCache.GetOrAdd(item.TypeName, AcceleratedComputerBridge.RequireAComputer?.Invoke(item.TypeName)
                                        is IComputer c1 ? c1 : throw new NotSupportedException($"Effect mode {item.TypeName} is not supported in accelerated computer bridge.")))
                                    .Resize(_width, _height, true);
                    }
                }

                if (result is null)
                {
                    if (frame is Picture p) result = p;
                    else result = (Picture)frame.ToBitPerPixel(16);
                }
                else
                {
                    Picture picFrame;
                    if (frame is Picture p) picFrame = p;
                    else picFrame = (Picture)frame.ToBitPerPixel(16);

                    result = MixtureCache.GetOrAdd(clip.MixtureMode, GetMixer(clip.MixtureMode))
                                         .Mix(result, picFrame,
                                             ComputerCache.GetOrAdd(
                                                clip.MixtureMode.ToString(),
                                                AcceleratedComputerBridge.RequireAComputer
                                                    ?.Invoke(clip.MixtureMode.ToString()) is IComputer c2 ? c2 :
                                                    throw new NotSupportedException($"Mixture mode {clip.MixtureMode} is not supported in accelerated computer bridge.")))
                                         .Resize(_width, _height, true);
                }
            }
            builder.Append(targetFrame, result?.Resize(_width, _height, false) ?? Picture.GenerateSolidColor(builder.Width, builder.Height, 0, 0, 0, 0));
            sw.Stop();
            Log($"[Render] Frame {targetFrame} render done, elapsed {sw.Elapsed}");
            EachElapsed.Add(sw.Elapsed);
            return;



        }

        private void ReleaseResources()
        {
            try
            {
                Log("Release resources...");
                FrameCache.Clear();
                ClipNeedForFrame.Clear();
                ComputerCache.Clear();
                MixtureCache.Clear();
            }
            catch { }

        }

        private static IMixture GetMixer(MixtureMode mixtureMode)
        {
            switch (mixtureMode)
            {
                case MixtureMode.Overlay:
                    return new OverlayMixture();
                default:
                    throw new NotSupportedException($"Mixture mode {mixtureMode} is not supported.");
            }
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

                    try
                    {
                        builder.Append(blankIdx, (Picture)BlankFrame);
                        EachElapsed.Add(TimeSpan.Zero);
                        Interlocked.Increment(ref Finished);
                        OnProgressChanged?.Invoke((double)Volatile.Read(ref Finished) / Duration);
                        Log($"[Render] Wrote blank frame {blankIdx} before starting frame {frameIndex}.");
                    }
                    catch (Exception ex)
                    {
                        Log(ex, $"Write blank frame {blankIdx}", this);
                    }
                }
            }
            catch (Exception ex)
            {
                Log(ex, $"Write blank frames", this);
            }
        }


    }
}
