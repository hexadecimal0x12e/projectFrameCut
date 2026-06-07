using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Shared;
using System.Diagnostics;

namespace projectFrameCut.Benchmarker;

[TestClass]
public class ResizeTester
{
    [TestMethod]
    public void TestResize()
    {
        var source = Picture8bpp.GenerateSolidColor(1920, 1080, 255, 0, 0, null);
        var target = source.Resize(1280, 720, false);
        Assert.IsNotNull(target);
        Assert.AreEqual(1280, target.Width);
        Assert.AreEqual(720, target.Height);
    }

    [TestMethod]
    [DataRow(9999, 8888, 1234, 567, false, DisplayName = "Non-ratio no forceResize")]
    [DataRow(9999, 8888, 1234, 567, true, DisplayName = "Non-ratio forceResize")]
    [DataRow(7680, 4320, 1280, 720, false, DisplayName = "In-ratio no forceResize")]
    [DataRow(7680, 4320, 1280, 720, true, DisplayName = "In-ratio forceResize")]
    [DataRow(1234, 567, 9999, 8888, false, DisplayName = "zoom out, Non-ratio no forceResize")]
    [DataRow(1234, 567, 9999, 8888, true, DisplayName = "zoom out,Non-ratio forceResize")]
    [DataRow(1280, 720, 7680, 4320, false, DisplayName = "zoom out, In-ratio no forceResize")]
    [DataRow(1280, 720, 7680, 4320, true, DisplayName = "zoom out, In-ratio forceResize")]
    public void TestResizeSpeed(int srcWidth, int srcHeight, int destWidth, int destHeight, bool forceResize)
    {
        var src = TestRunner<projectFrameCut.Render.Effect.ResizeEffect_HwAccel>.MakeNoise(srcWidth, srcHeight);
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++)
        {
            stopwatch.Restart();
            src.Resize(destWidth, destHeight, forceResize);
            Console.WriteLine($"Resize turn {i} done. Elapsed {stopwatch.Elapsed}");
        }
    }
    [TestMethod]
    [DataRow(9999, 8888, 1234, 567, false, DisplayName = "Non-ratio no forceResize")]
    [DataRow(9999, 8888, 1234, 567, true, DisplayName = "Non-ratio forceResize")]
    [DataRow(7680, 4320, 1280, 720, false, DisplayName = "In-ratio no forceResize")]
    [DataRow(7680, 4320, 1280, 720, true, DisplayName = "In-ratio forceResize")]
    [DataRow(1234, 567, 9999, 8888, false, DisplayName = "zoom out, Non-ratio no forceResize")]
    [DataRow(1234, 567, 9999, 8888, true, DisplayName = "zoom out,Non-ratio forceResize")]
    [DataRow(1280, 720, 7680, 4320, false, DisplayName = "zoom out, In-ratio no forceResize")]
    [DataRow(1280, 720, 7680, 4320, true, DisplayName = "zoom out, In-ratio forceResize")]
    public void TestHwAccelResizeSpeed(int srcWidth, int srcHeight, int destWidth, int destHeight, bool forceResize)
    {
        var src = TestRunner<projectFrameCut.Render.Effect.ResizeEffect_HwAccel>.MakeNoise(srcWidth, srcHeight);
        Stopwatch stopwatch = Stopwatch.StartNew();
        var resizer = new projectFrameCut.Render.Effect.ResizeEffect_HwAccel() { Width = destWidth, Height = destHeight, PreserveAspectRatio = !forceResize };
        var computer = PluginManager.CreateComputer(resizer.NeedComputer);
        for (var i = 0; i < 10; i++)
        {
            stopwatch.Restart();
            resizer.Render(src, computer, destWidth, destHeight);
            Console.WriteLine($"Resize turn {i} done. Elapsed {stopwatch.Elapsed}");
        }
    }
}
