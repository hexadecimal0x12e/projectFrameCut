using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Drawing.Base.Picture;
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
        public void TestVideoSourceFromStream()
        {
            byte[] data = File.ReadAllBytes(TestVideoPath);
            using var stream = new MemoryStream(data);
            using (var source = new HDRDecoderContext().FromStream(stream, stream.Length, true))
            {
                using var first = source.GetFrame(0);
                using var forward = source.GetFrame(10);
                using var backward = source.GetFrame(2);
                Assert.AreEqual(1920, first.Width);
                Assert.AreEqual(1080, first.Height);
                Assert.AreEqual(first.Width, forward.Width);
                Assert.AreEqual(first.Height, backward.Height);
            }
            Assert.IsTrue(stream.CanRead);

            var owned = new MemoryStream(data);
            using (var source = new DecoderContext8Bit(owned, owned.Length))
                using (source.GetFrame(0)) { }
            Assert.ThrowsExactly<ObjectDisposedException>(() => owned.ReadByte());
        }

        [TestMethod]
        public void TestVideoSourceStreamValidation()
        {
            byte[] data = File.ReadAllBytes(TestVideoPath);

            var unreadable = new GuardedStream(data) { Readable = false };
            Assert.ThrowsExactly<ArgumentException>(() => new DecoderContext8Bit(unreadable, unreadable.Length));
            Assert.IsTrue(unreadable.IsDisposed);

            using var unseekable = new GuardedStream(data) { Seekable = false };
            Assert.ThrowsExactly<ArgumentException>(() => new DecoderContext8Bit(unseekable, unseekable.Length, true));
            Assert.IsFalse(unseekable.IsDisposed);

            using var positioned = new GuardedStream(data);
            positioned.Position = 1;
            Assert.ThrowsExactly<ArgumentException>(() => new DecoderContext8Bit(positioned, positioned.Length, true));
            positioned.Position = 0;
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DecoderContext8Bit(positioned, 0, true));
            Assert.ThrowsExactly<ArgumentException>(() => new DecoderContext8Bit(positioned, positioned.Length - 1, true));

            using var readFailure = new GuardedStream(data) { ThrowOnRead = true };
            IOException callback = Assert.ThrowsExactly<IOException>(() => new DecoderContext8Bit(readFailure, readFailure.Length, true));
            Assert.IsInstanceOfType<InvalidOperationException>(callback.InnerException);

            using var earlyEof = new GuardedStream(data) { EarlyEofAt = 1 };
            IOException eof = Assert.ThrowsExactly<IOException>(() => new DecoderContext8Bit(earlyEof, earlyEof.Length, true));
            Assert.IsInstanceOfType<EndOfStreamException>(eof.InnerException);

            using var progressive = new GuardedStream(data);
            var source = new DecoderContext8Bit(progressive, progressive.Length, true);
            Assert.IsLessThan(progressive.Length, progressive.BytesRead);
            source.Dispose();
            source.Dispose();
            Assert.IsFalse(progressive.IsDisposed);
        }

        [TestMethod]
        public void TestUnsupportedVideoSourcesRejectStream()
        {
            byte[] data = File.ReadAllBytes(TestVideoPath);
            using var stream = new MemoryStream(data);
            Assert.ThrowsExactly<NotSupportedException>(() => new HttpDecoderContext().FromStream(stream, stream.Length, true));
            Assert.ThrowsExactly<NotSupportedException>(() => new FFmpegDeviceDecoderContext().FromStream(stream, stream.Length, true));
            Assert.ThrowsExactly<NotSupportedException>(() => new DecoderContextPJFCProject().FromStream(stream, stream.Length, true));
            Assert.ThrowsExactly<NotSupportedException>(() => new InternalPluginBase().VideoSourceProvider["RPSVDecoderContext"].FromStream(stream, stream.Length, true));
        }

        [TestMethod]
        [DoNotParallelize]
        public void TestMultistreamVideoSourceFromStream()
        {
            string path = Path.Combine(Path.GetTempPath(), $"pjfc-multistream-{Guid.NewGuid():N}.mkv");
            try
            {
                using (var writer = new AlphaBrightnessVideoWriter
                {
                    Width = 16,
                    Height = 16,
                    FramePerSecond = 30,
                    OutputPath = path,
                    Channels = AuxiliaryVideoChannels.Alpha | AuxiliaryVideoChannels.Brightness
                })
                {
                    writer.Initialize();
                    for (ushort i = 0; i < 3; i++)
                        using (var frame = HDRPicture16bpp.GenerateSolidColor(16, 16, (ushort)(1000 + i), 2000, 3000, .5f, .25f, 1000))
                            writer.Append(frame);
                    writer.Finish();
                }

                using var fileSource = new AlphaBrightnessDecoderContext(path);
                using var fileMiddle = fileSource.GetHDRFrame(1, true);
                byte[] data = File.ReadAllBytes(path);
                using var stream = new MemoryStream(data);
                using var source = new AlphaBrightnessDecoderContext(stream, stream.Length, true);
                using var first = source.GetHDRFrame(0, true);
                using var last = source.GetHDRFrame(2, true);
                using var middle = source.GetHDRFrame(1, true);
                Assert.AreEqual(16, first.Width);
                Assert.AreEqual(16, last.Height);
                Assert.IsTrue(middle.HasAlphaChannel);
                Assert.AreEqual(fileMiddle.r[0], middle.r[0]);
                Assert.AreEqual(.5f, middle.a![0], .01f);
                Assert.AreEqual(.25f, middle.Brightness[0], .01f);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [TestMethod]
        [DoNotParallelize]
        [DataRow(true, DisplayName = "DiskCache: true")]
        [DataRow(false, DisplayName = "DiskCache: false")]
        public void TestDecodeSpeed(bool diskCache)
        {
            IVideoSource.EnableDiskCache = diskCache;
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

        private sealed class GuardedStream(byte[] data) : Stream
        {
            private readonly MemoryStream _stream = new(data);

            public bool Readable { get; init; } = true;
            public bool Seekable { get; init; } = true;
            public bool ThrowOnRead { get; init; }
            public long? EarlyEofAt { get; init; }
            public long BytesRead { get; private set; }
            public bool IsDisposed { get; private set; }
            public override bool CanRead => Readable;
            public override bool CanSeek => Seekable;
            public override bool CanWrite => false;
            public override long Length => _stream.Length;
            public override long Position { get => _stream.Position; set => _stream.Position = value; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (ThrowOnRead) throw new InvalidOperationException("Test read failure.");
                if (EarlyEofAt is long end)
                {
                    long remaining = end - Position;
                    if (remaining <= 0) return 0;
                    count = (int)Math.Min(count, remaining);
                }
                int read = _stream.Read(buffer, offset, count);
                BytesRead += read;
                return read;
            }

            public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);
            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                IsDisposed = true;
                if (disposing) _stream.Dispose();
                base.Dispose(disposing);
            }
        }

    }
}
