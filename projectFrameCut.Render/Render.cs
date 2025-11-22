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

        ConcurrentDictionary<string, ConcurrentDictionary<uint, Picture>> FrameCache = new();
        ConcurrentDictionary<uint, IClip[]> ClipNeedForFrame = new();
        ConcurrentDictionary<string, IComputer> ComputerCache = new();
        ConcurrentDictionary<MixtureMode, IMixture> MixtureCache = new();
        public ConcurrentBag<TimeSpan> EachElapsed = new(), EachElapsedForPreparing = new();

        List<Thread> Threads = new List<Thread>();

        int ThreadWorking = 0, Finished = 0;

        ConcurrentQueue<uint> PreparedFrames = new();
        ConcurrentDictionary<uint, byte> PreparedFlag = new();

        int TotalEnqueued = 0;
        volatile bool PreparerFinished = false;
        private int _width;
        private int _height;

        public async Task GoRender()
        {
            Thread preparer = new(PrepareRender);
            preparer.Name = "Preparer thread";
            preparer.IsBackground = true;
            preparer.Start();

            _width = builder.Width;
            _height = builder.Height;

            await Task.Delay(50);

            while (true)
            {
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
                                RenderAFrame(targetFrame);
                            }
                            finally
                            {
                                Interlocked.Decrement(ref ThreadWorking);
                                Interlocked.Increment(ref Finished);
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
                    Thread.Sleep(10);
                }

                if (LogState)
                {
                    int f = Volatile.Read(ref Finished);
                    Console.Error.WriteLine($"@@{f},{Duration}");
                }

            }

            foreach (var t in Threads)
            {
                try { if (t.IsAlive) t.Join(); } catch { }
            }
        }

        private void PrepareRender()
        {
            Stopwatch sw = new();
            for (uint idx = 0; idx < Duration; idx++)
            {
                sw.Restart();
                foreach (var item in Clips)
                {

                    if (item.Duration != 0 && idx > item.Duration)
                    {
                        continue;
                    }

                    var frame = item.GetFrameRelativeToStartPointOfSource(idx,_width,_height,true);
                    if (frame != null)
                    {
                        var cache = FrameCache.GetOrAdd(item.Id, (_) => new());
                        cache.TryAdd(idx + item.StartFrame, frame);
                    }

                    uint frameIndex = idx + item.StartFrame;

                    if (item.StartFrame * item.SecondPerFrameRatio <= idx && item.Duration * item.SecondPerFrameRatio + item.StartFrame * item.SecondPerFrameRatio >= idx)
                    {
                        // add or update the mapping of which clips are needed for this frame
                        ClipNeedForFrame.AddOrUpdate(
                            frameIndex,
                            (_) => [item],
                            (_, old) => old.Append(item).OrderBy(x => x.LayerIndex).ToArray());

                        if (PreparedFlag.TryAdd(frameIndex, 0))
                        {
                            PreparedFrames.Enqueue(frameIndex);
                            Interlocked.Increment(ref TotalEnqueued);
                        }
                    }


                }
                sw.Stop();
                EachElapsedForPreparing.Add(sw.Elapsed);
                Console.WriteLine($"Frame {idx} is ready to render, elapsed {sw.Elapsed}");

            }

            // mark preparer finished so main loop can complete when renders done
            PreparerFinished = true;
        }

        private void RenderAFrame(uint targetFrame)
        {
            if(targetFrame > Duration)
            {
                Console.WriteLine($"WARN: Target frame {targetFrame} exceeds project duration. Ignore.");
                return;
            }
            Stopwatch sw = Stopwatch.StartNew();
            Picture result = null!;

            if (!ClipNeedForFrame.Remove(targetFrame, out var ClipsNeed) || ClipsNeed.Length == 0) goto noFrame;

            PreparedFlag.TryRemove(targetFrame, out _);

            foreach (var clip in ClipsNeed)
            {
                if (!FrameCache[clip.Id].Remove(targetFrame, out var frame)) throw new IndexOutOfRangeException("The specific frame is not in cache.");
                if (clip.Effects != null)
                {
                    foreach (var item in clip.EffectsInstances ?? IClip.GetEffectsInstances(clip.Effects))
                    {
                        frame = item.Render(frame,
                                        ComputerCache.GetOrAdd(item.TypeName, AcceleratedComputerBridge.RequireAComputer?.Invoke(item.TypeName)
                                        is IComputer c1 ? c1 : throw new NotSupportedException($"Effect mode {item.TypeName} is not supported in accelerated computer bridge.")))
                                    .Resize(_width, _height, true);
                    }
                }

                if (result is null) result = frame;
                else
                {
                    result = MixtureCache.GetOrAdd(clip.MixtureMode, GetMixer(clip.MixtureMode))
                                         .Mix(result, frame,
                                             ComputerCache.GetOrAdd(
                                                clip.MixtureMode.ToString(),
                                                AcceleratedComputerBridge.RequireAComputer
                                                    ?.Invoke(clip.MixtureMode.ToString()) is IComputer c2 ? c2 :
                                                    throw new NotSupportedException($"Mixture mode {clip.MixtureMode} is not supported in accelerated computer bridge.")))
                                         .Resize(_width, _height, true);
                }
            }

        noFrame:
            builder.Append(targetFrame, result.Resize(_width,_height, false) ?? Picture.GenerateSolidColor(builder.Width, builder.Height, 0, 0, 0, 0));
            sw.Stop();
            Console.WriteLine($"frame {targetFrame} render done, elapsed {sw.Elapsed}");
            EachElapsed.Add(sw.Elapsed);
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

    }
}
