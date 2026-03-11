using ILGPU.Runtime;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.WindowsRender;
using System.Diagnostics;

namespace projectFrameCut.Benchmarker
{
    [TestClass]
    public sealed class TestMain
    {

        [AssemblyInitialize]
        public static void AssemblyInit(TestContext context)
        {
            var c = ILGPU.Context.CreateDefault();
            ILGPUPlugin.accelerators = c.Devices.Where(c => c.AcceleratorType != AcceleratorType.CPU).Select(d => d.CreateAccelerator(c)).ToArray();

            PluginManager.InitGlobalGetter();
            List<IPluginBase> plugins = [new InternalPluginBase(), new ILGPUPlugin()];
            PluginManager.Init(plugins);


        }

        [TestMethod]
        public void TestWhetherPluginLoaded()
        {
            Assert.HasCount(2, PluginManager.LoadedPlugins);
        }
    }
}
