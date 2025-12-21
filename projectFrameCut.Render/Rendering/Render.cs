using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.VideoMakeEngine;
using projectFrameCut.Render.Videos;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        public uint StartFrame { get; set; } = 0;
        public VideoBuilder? builder;
        public int MaxThreads = (int)(Environment.ProcessorCount * 1.75);
        public bool LogState = false;
        public int GCOption = 0;
        public bool LogStatToLogger = false;
        public bool Use16Bit { get; set; } = true;
        public bool LogAnalyzeInfo { get; set; } = false;

        public event Action<double>? OnProgressChanged;
        public ConcurrentBag<TimeSpan> EachElapsed = new(), EachElapsedForPreparing = new();
        public bool running { get; private set; } = false;

        ConcurrentDictionary<string, ConcurrentDictionary<uint, IPicture>> FrameCache = new();
        ConcurrentDictionary<uint, IClip[]> ClipNeedForFrame = new();
        ConcurrentDictionary<MixtureMode, IMixture> MixtureCache = new();

        List<Thread> Threads = new List<Thread>();

        int ThreadWorking = 0, Finished = 0;

        ConcurrentQueue<uint> PreparedFrames = new(), BlankFrames = new();
        ConcurrentDictionary<uint, byte> PreparedFlag = new();

        int TotalEnqueued = 0;
        volatile bool PreparerFinished = false;
        private int _width;
        private int _height;

        private IPicture BlankFrame = null!;

        private PlaceEffect BlankPlace = new()
        {
            StartX = 0,
            StartY = 0
        };

        public async Task GoRender(CancellationToken token, uint startFrame = 0)
        {
            ArgumentNullException.ThrowIfNull(builder, nameof(builder));
            ArgumentNullException.ThrowIfNull(Clips, nameof(Clips));
            StartFrame = startFrame;
            BlankFrame = Use16Bit ? Picture.GenerateSolidColor(builder.Width, builder.Height, 0, 0, 0, 0) : Picture8bpp.GenerateSolidColor(builder.Width, builder.Height, 0, 0, 0, 0);

            running = true;
            if (LogStatToLogger)
            {
                new Thread(() =>
                {
                    float d = Duration;
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
                            float renderRange = Duration - StartFrame;
                            Log($"[STAT] " +
                                $"memory used by program: {Environment.WorkingSet / 1024 / 1024:n2} MB, " +
                                $"total prepared: {TotalEnqueued / renderRange:p2}, " +
                                $"total rendered: {Volatile.Read(ref Finished) / renderRange:p2}, " +
                                $"total wrote frames: {builder.FramePendedToWrite.Count(k => k.Value) / renderRange:p3}, " +
                                $"total pended to write frames: {builder.FramePendedToWrite.Count(k => !k.Value)}/{builder.FramePendedToWrite.Count}, " +
                                $"preparing elapsed average: {eachPrepare}, Each frame render elapsed average: {each}. ");
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

            await Task.Delay(50);

            while (true)
            {
                if (token.IsCancellationRequested)
                {
                    Log("Render cancelled by user.", "info");
                    break;
                }
                if (PreparerFinished && Volatile.Read(ref Finished) >= Duration - StartFrame)
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
                    Console.Error.WriteLine($"@@{StartFrame + f},{Duration}");
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
            _width = builder.Width;
            _height = builder.Height;
            bool found = false;
            for (uint idx = StartFrame; idx < Duration; idx++)
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
                }

                if (idx % 50 == 0)
                {
                    Log($"[Preparer] source preparing finished {(float)(idx - StartFrame) / (float)(Duration - StartFrame):p3} ({idx}/{Duration})");
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
                            if (Use16Bit && frame.bitPerPixel != 2)
                            {
                                frame = frame.ToBitPerPixel(16);
                            }
                            else if (!Use16Bit && frame.bitPerPixel != 1)
                            {
                                frame = frame.ToBitPerPixel(8);
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
            IPicture result = null!;

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
                builder.Append(targetFrame, BlankFrame);
                EachElapsed.Add(sw.Elapsed);
                return;
            }


            foreach (var clip in ClipsNeed)
            {
                if (token.IsCancellationRequested) return;

                if (!FrameCache.TryGetValue(clip.Id, out var perClipCache) || !perClipCache.TryRemove(targetFrame, out var frame))
                {
                    Log($"[Render] WARN: Prepared frame {targetFrame} not found in cache for clip {clip.Id}. Regenerating.");
                    try
                    {
                        var rawFrame = clip.GetFrame(targetFrame, _width, _height, true);
                        if (rawFrame != null)
                        {
                            if (Use16Bit && rawFrame.bitPerPixel != 16)
                            {
                                frame = rawFrame.ToBitPerPixel(16);
                            }
                            else if (!Use16Bit && rawFrame.bitPerPixel != 8)
                            {
                                frame = rawFrame.ToBitPerPixel(8);
                            }
                            else
                            {
                                frame = rawFrame;
                            }
                        }
                        else
                        {
                            frame = Use16Bit ? Picture.GenerateSolidColor(_width, _height, 0, 0, 0, 0) : Picture8bpp.GenerateSolidColor(_width, _height, 0, 0, 0, 0);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log(ex, $"regenerate frame {targetFrame} for clip {clip.Id}", this);
                        throw;
                    }
                }

                if (clip.Effects.ArrayAny())
                {
                    foreach (var item in clip.EffectsInstances ?? EffectHelper.GetEffectsInstances(clip.Effects))
                    {
                        if (item is IContinuousEffect c)
                        {
                            if (c.EndPoint == 0 && c.EndPoint == 0)
                            {
                                c.StartPoint = (int)(clip.StartFrame);
                                c.EndPoint = (int)(c.StartPoint + clip.Duration * clip.SecondPerFrameRatio);
                            }
                            frame =
                                c.Render(frame, targetFrame, PluginManager.CreateComputer(item.NeedComputer), _width, _height)
                                 .Resize(_width, _height, true);
                        }
                        else
                        {
                            frame =
                                item.Render(frame, PluginManager.CreateComputer(item.NeedComputer), _width, _height)
                                    .Resize(_width, _height, true);
                        }
                    }
                }

                if (result is null)
                {
                    result = frame;
                }
                else
                {
                    var mix = GetMixer(clip.MixtureMode);
                    result = MixtureCache.GetOrAdd(clip.MixtureMode, mix)
                                         .Mix(result, frame,
                                              mix.ComputerId is not null ? PluginManager.CreateComputer(mix.ComputerId) : null)
                                         .Resize(_width, _height, true);
                }
            }
            if (result?.Width == _width && result?.Height == _height) goto ok;
            if (result is null)
            {
                result = Use16Bit ? Picture.GenerateSolidColor(_width, _height, 0, 0, 0, 0) : Picture8bpp.GenerateSolidColor(_width, _height, 0, 0, 0, 0);
            }
            else if (result.Width < _width && result.Height < _height)
            {
                result = BlankPlace.Render(result, null, _width, _height);
            }
            else if (result.Width > _width && result.Height > _height)
            {
                result = result.Resize(_height, _height, false);
            }
        ok:
            builder.Append(targetFrame, result);
            sw.Stop();
            Log($"[Render] Frame {targetFrame} render done, elapsed {sw.Elapsed}");
            EachElapsed.Add(sw.Elapsed);
            return;



        }

        private void ReleaseResources()
        {
            try
            {
                running = false;
                Log("Release resources...");
                FrameCache.Clear();
                ClipNeedForFrame.Clear();
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

                    builder.Append(blankIdx, BlankFrame);
                    EachElapsed.Add(TimeSpan.Zero);
                    Interlocked.Increment(ref Finished);
                    OnProgressChanged?.Invoke((double)Volatile.Read(ref Finished) / Duration);
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
