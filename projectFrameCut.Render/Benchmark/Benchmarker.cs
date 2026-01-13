using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace projectFrameCut.Render.Benchmark
{
    public static class Benchmarker
    {
        public static async Task Start(Action<double, TimeSpan> progChangedCallback)
        {
            int width = 1920;
            int height = 1080;
            int fps = 30;

            var stru = BenchmarkSourceGenerator.GetDraftStructure();
            long max = 0;
            foreach (var clip in stru)
            {
                max = Math.Max(clip.StartFrame + clip.Duration, max);
            }

            uint duration = (uint)max;

            VideoBuilder builder = new VideoBuilder("/dev/null", width, height, fps, "BlackHoleWriter", "");
            builder.Duration = duration;
            Renderer renderer = new Renderer
            {
                builder = builder,
                Clips = stru,
                Duration = duration,
                MaxThreads = 32,
                LogState = false,
                LogStatToLogger = true,
                GCOption = 0,
                Use16Bit = false,
                
            };

            renderer.OnProgressChanged += progChangedCallback;

            List<List<PictureProcessStack>> stacks = new();
            object stacksLock = new();

            if(builder.Writer is BlackholeVideoWriter w)
            {
                w.OnFrameWrite += (s, e) =>
                {
                    lock (stacksLock)
                    {
                        stacks.Add(e.ProcessStack);
                    }
                    e.Dispose();
                };
            }

            var _cts = new System.Threading.CancellationTokenSource();
            builder?.Build()?.Start();
            renderer.PrepareRender(_cts.Token);
            await renderer.GoRender(_cts.Token);

            var avgTime = renderer.EachElapsed.Average(ts => ts.TotalMilliseconds);
            var avgPrepTime = renderer.EachElapsedForPreparing.Average(ts => ts.TotalMilliseconds); 
            avgTime += avgPrepTime;
            Log($"Average frame render time: {avgTime} ms");

            // Aggregate per-step average elapsed time from ProcessStack.
            List<List<PictureProcessStack>> stacksSnapshot;
            lock (stacksLock)
            {
                stacksSnapshot = stacks.ToList();
            }

            static IEnumerable<PictureProcessStack> FlattenStacks(IEnumerable<PictureProcessStack>? steps)
            {
                if (steps is null) yield break;
                foreach (var step in steps)
                {
                    if (step is null) continue;
                    yield return step;

                    if (step is OverlayedPictureProcessStack overlay)
                    {
                        foreach (var s in FlattenStacks(overlay.TopSteps)) yield return s;
                        foreach (var s in FlattenStacks(overlay.BaseSteps)) yield return s;
                    }
                }
            }

            static string GetStepKey(PictureProcessStack step)
            {
                var name = step.OperationDisplayName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = step.StepUsed?.Name;
                }
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = step.Operator?.Name;
                }
                return string.IsNullOrWhiteSpace(name) ? "(unknown)" : name;
            }

            var orderedKeys = new List<string>(capacity: 64);
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            var sumTicksByKey = new Dictionary<string, long>(StringComparer.Ordinal);
            var countByKey = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var frameStack in stacksSnapshot)
            {
                foreach (var step in FlattenStacks(frameStack))
                {
                    if (step.Elapsed is not TimeSpan elapsed) continue;

                    var key = GetStepKey(step);
                    if (seenKeys.Add(key))
                    {
                        orderedKeys.Add(key);
                        sumTicksByKey[key] = 0;
                        countByKey[key] = 0;
                    }

                    sumTicksByKey[key] += elapsed.Ticks;
                    countByKey[key] += 1;
                }
            }

            Log($"[Benchmark] ProcessStack frames collected: {stacksSnapshot.Count}");

            for (int i = 0; i < orderedKeys.Count; i++)
            {
                var key = orderedKeys[i];
                var count = countByKey[key];
                if (count <= 0) continue;

                var avg = TimeSpan.FromTicks(sumTicksByKey[key] / count);
                var line = $"[Benchmark] Step #{i + 1}: {key}, Avg Elapsed: {avg} (n={count})";
                Log(line);
            }


        }

        
    }
}
