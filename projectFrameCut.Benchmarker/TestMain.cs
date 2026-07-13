//using ILGPU.Runtime;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Shared;
using System.Diagnostics;

namespace projectFrameCut.Benchmarker
{
    [TestClass]
    public sealed class TestMain
    {

        [AssemblyInitialize]
        public static void AssemblyInit(TestContext context)
        {
            MyLoggerExtensions.OnLog += (m, l) =>
            {
                if (l.Equals("info", StringComparison.InvariantCultureIgnoreCase))
                {
                    Console.WriteLine(m);
                    return;
                }

                var oldColor = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = l.Equals("error", StringComparison.InvariantCultureIgnoreCase) ? ConsoleColor.Red :
                                          l.Equals("stat", StringComparison.InvariantCultureIgnoreCase) ? ConsoleColor.Green :
                                          (l.Equals("warning", StringComparison.InvariantCultureIgnoreCase) || l.Equals("warn", StringComparison.InvariantCultureIgnoreCase)) ? ConsoleColor.Yellow :
                                          (l.Equals("debug", StringComparison.InvariantCultureIgnoreCase) || l.Equals("diag", StringComparison.InvariantCultureIgnoreCase)) ? ConsoleColor.Cyan :
                                          l.StartsWith("FFmpeg", StringComparison.InvariantCultureIgnoreCase) ? ConsoleColor.Magenta :
                                          ConsoleColor.Gray;

                    Console.Write($"[{l}]");
                }
                finally
                {
                    Console.ForegroundColor = oldColor;
                }

                Console.WriteLine($" {m}");
            };

            //TODO: Replace this to a ffmpeg library on your computer
            //They've too large and can't included in Git repository, so I can't provide a more general solution for this
            FFmpeg.AutoGen.ffmpeg.RootPath = @"D:\azert\Downloads\ffmpeg-8.1-full_build-shared\bin";
            FFmpeg.AutoGen.DynamicallyLoadedBindings.TryInitialize();

            //var c = ILGPU.Context.CreateDefault();
            //ILGPUPlugin.accelerators = c.Devices.Where(c => c.AcceleratorType != AcceleratorType.CPU).Select(d => d.CreateAccelerator(c)).ToArray();

            //PluginManager.InitGlobalGetter();
            //List<IPluginBase> plugins = [new InternalPluginBase(), new HwAc()];
            //PluginManager.Init(plugins);


        }

        [TestMethod]
        public void TestWhetherPluginLoaded()
        {
            Assert.HasCount(2, PluginManager.LoadedPlugins);
        }
    }
}
