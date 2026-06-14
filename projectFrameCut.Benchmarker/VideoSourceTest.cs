using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Sources;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace projectFrameCut.Benchmarker
{
    [TestClass]
    public class VideoSourceTest
    {
#pragma warning disable CS8618 // Path will be set in setup
        public string TestVideoPath { get; set; }
#pragma warning restore CS8618

        [TestInitialize]
        public void Setup()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            while (Path.GetDirectoryName(dir) != null)
            {
                if (dir is null) throw new FileNotFoundException("Test video 'SampleMedia.mkv' not found in any parent directory.", TestVideoPath);
                var testVideoPath = System.IO.Path.Combine(dir, "SampleMedia.mp4");
                if (System.IO.File.Exists(testVideoPath))
                {
                    TestVideoPath = testVideoPath;
                    break;
                }
                dir = System.IO.Path.GetDirectoryName(dir);
            }
            if (!File.Exists(TestVideoPath)) throw new FileNotFoundException("Test video 'SampleMedia.mkv' not found in any parent directory.", TestVideoPath);

            if (Directory.Exists(Environment.ExpandEnvironmentVariables(@"%userprofile%\AppData\Local\Packages\hexadecimal0x12e.projectFrameCut_f91nmrsqwpk6y\LocalCache\VideoFrameCache")))
            {
                VideoFrameDiskCache.CacheBaseDir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(@"%userprofile%\AppData\Local\Packages\hexadecimal0x12e.projectFrameCut_f91nmrsqwpk6y\LocalCache\VideoFrameCache"));
            }

            Console.WriteLine($"Using test video: {TestVideoPath}");
            Console.WriteLine($"Disk Cache root: {VideoFrameDiskCache.CacheBaseDir ?? "N/A"}");
        }

        [TestMethod]
        public void TestMetadata()
        {
            Assert.AreEqual(10, projectFrameCut.Render.EncodeAndDecode.FFmpegHelper.DetectVideoBitDepth(TestVideoPath));
            Assert.IsTrue(projectFrameCut.Render.EncodeAndDecode.HDRDecoderContext.IsHdrVideo(TestVideoPath));

        }

        [TestMethod]
        public void TestVideoSource()
        {
            var source = PluginManager.CreateVideoSource(TestVideoPath);
            Assert.IsNotNull(source);
            Console.WriteLine($"Video {source.Width}*{source.Height}, FPS:{source.Fps}, frameCount:{source.TotalFrames}");
            var frame = source.GetFrame(0);
            Assert.IsNotNull(frame);
            Assert.AreEqual(1920, frame.Width);
            Assert.AreEqual(1080, frame.Height);
            Assert.AreEqual(713, source.TotalFrames);
        }

        [TestMethod]
        [DoNotParallelize]
        [DataRow(true, DisplayName = "DiskCache: true")]
        [DataRow(false, DisplayName = "DiskCache: false")]
        public void TestDecodeSpeed(bool diskCache)
        {
            IVideoSource.EnableDiskCache = diskCache;
            IVideoSource.EnableMemoryCache = false; //ramcache is not suitable in this scenario, as it may cause OOM and affect the benchmark result
            var source = new HDRDecoderContext(TestVideoPath);
            Console.WriteLine($"Video {source.Width}*{source.Height}, FPS:{source.Fps}, frameCount:{source.TotalFrames}");
            Assert.IsNotNull(source);
            Assert.IsGreaterThan(5, source.TotalFrames);
            var sw = Stopwatch.StartNew();
            for (uint i = 0; i < source.TotalFrames; i++)
            {
                var frame = source.GetFrame(i);
                Assert.IsNotNull(frame);
                Debug.WriteLine($"Decoded frame {i + 1}/{source.TotalFrames}");
            }

            Console.WriteLine($"FPS: {source.TotalFrames / sw.Elapsed.TotalSeconds}, Time taken: {sw.Elapsed}");
        }

    }
}
