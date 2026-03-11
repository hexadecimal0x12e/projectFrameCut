using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace projectFrameCut.Benchmarker
{
    public class TestRunner<TEffect> where TEffect : IEffect
    {
        public void Run()
        {
            IEffect instance = (typeof(TEffect)?.GetConstructor([])?.Invoke([])) as IEffect ?? throw new InvalidOperationException($"Cannot create an instance of {nameof(TEffect)}");
            if (instance is IBindableArgumentEffect) return;
            var src = MakeNoise(1280, 720);
            for (int i = 0; i < 10; i++)
            {
                Console.WriteLine($"Starting testing effect {instance.TypeName} in {i} turn...");
                var sw = Stopwatch.StartNew();
                if (instance is IContinuousEffect c)
                {
                    c.Render(src, 0, PluginManager.CreateComputer(instance.NeedComputer), 1280, 720);
                }
                else
                {
                    instance.Render(src, PluginManager.CreateComputer(instance.NeedComputer), 1280, 720);
                }
                sw.Stop();
                Console.WriteLine($"Test turn {i} done. Elapsed {sw.Elapsed}");
            }
        }

        private static Picture8bpp MakeNoise(int width, int height)
        {
            var pic = new Picture8bpp(width, height);
            var rnd = new Random();
            // Fill color channels with random bytes
            rnd.NextBytes(pic.r);
            rnd.NextBytes(pic.g);
            rnd.NextBytes(pic.b);

            // mark process stack for diagnostic
            pic.ProcessStack = new List<projectFrameCut.Shared.PictureProcessStack>
            {
                new projectFrameCut.Shared.PictureProcessStack
                {
                    OperationDisplayName = "Generated noise",
                    Operator = typeof(Picture8bpp),
                    ProcessingFuncStackTrace = new StackTrace(true),
                    Properties = new Dictionary<string, object>
                    {
                        { "Width", width },
                        { "Height", height }
                    },
                }
            };

            return pic;
        }
    }
}
