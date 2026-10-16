using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.Benchmark;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Context;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.Rendering;
using System.Collections.Concurrent;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RendererPipelineTests
{
    [TestMethod]
    public async Task WorkersCompleteOutOfOrderButOneCompositorWritesAndPublishesInOrder()
    {
        using var secondReady = new ManualResetEventSlim();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var decoded = new ConcurrentQueue<uint>();
        using var clip = Clip(frame =>
        {
            if (frame == 2) Assert.IsTrue(secondReady.Wait(TimeSpan.FromSeconds(5), timeout.Token));
            decoded.Enqueue(frame);
            if (frame == 3) secondReady.Set();
            return Picture8bpp.GenerateSolidColor(2, 1, (byte)frame, 0, 0, 1);
        });
        var written = new List<byte>();
        var previews = new List<byte>();
        var threads = new HashSet<int>();
        Renderer? renderer = null;
        var writer = Writer();
        writer.OnFrameWrite += (_, picture) =>
        {
            threads.Add(Environment.CurrentManagedThreadId);
            Assert.AreEqual(RenderWorkerStage.WritingOutput, IRenderContext.WorkerState!.Stage);
            written.Add(((IPicture<byte>)picture).r[0]);
            if (written.Count == 1)
            {
                using var next = renderer!.RenderSpecificFrame(3);
                Assert.AreEqual((byte)3, ((IPicture<byte>)next).r[0]);
                ((IPicture<byte>)next).r[0] = 99;
            }
        };
        using var builder = new VideoBuilder(writer)
        {
            StartFrame = 2, Duration = 4, EnablePreview = true, minFrameCountToGeneratePreview = 1,
        };
        builder.OnPreviewGenerated += (_, picture) =>
        {
            using (picture)
            {
                Assert.IsFalse(picture.Disposed);
                Assert.AreEqual(written.Count, builder.WrittenFramesCount);
                previews.Add(((IPicture<byte>)picture).r[0]);
            }
        };
        renderer = new Renderer
        {
            Clips = [clip], builder = builder, StartFrame = 2, Duration = 4, Use16Bit = false,
            MaxThreads = 2, RenderAheadFrames = 2,
        };
        renderer.PrepareRender(timeout.Token);
        await renderer.GoRender(timeout.Token);

        Assert.AreEqual(3u, decoded.First());
        Assert.AreEqual(4, decoded.Count);
        CollectionAssert.AreEqual(new byte[] { 2, 3, 4, 5 }, written);
        CollectionAssert.AreEqual(written, previews);
        Assert.AreEqual(1, threads.Count);
        Assert.AreEqual(4, renderer.CurrentFinished);
        Assert.AreEqual(4, builder.TotalFramesCount);
        Assert.AreEqual(4, builder.WrittenFramesCount);
        Assert.AreEqual(0, builder.PendingWriteCount);
        Assert.IsTrue(builder.FramePendedToWrite.Values.All(value => value));
        IRenderContext.Current = null;
    }

    [TestMethod]
    public async Task WriterCanConsumeFramesBeforePreviewGeneration()
    {
        using var clip = Clip(_ => Picture8bpp.GenerateSolidColor(2, 1, 77, 0, 0, 1));
        using var builder = new VideoBuilder(Writer())
        {
            Duration = 1, EnablePreview = true, minFrameCountToGeneratePreview = 1,
        };
        byte? preview = null;
        builder.OnPreviewGenerated += (_, picture) =>
        {
            using (picture) preview = ((IPicture<byte>)picture).r[0];
        };
        var renderer = new Renderer { Clips = [clip], builder = builder, Duration = 1, MaxThreads = 1, Use16Bit = false };
        renderer.PrepareRender(default);
        await renderer.GoRender(default);
        Assert.AreEqual((byte?)77, preview);
        IRenderContext.Current = null;
    }

    [TestMethod]
    public async Task CancellationWaitsForTheActiveDecoderAndDoesNotWriteItsFrame()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int active = 0;
        using var clip = Clip(_ =>
        {
            Interlocked.Increment(ref active);
            try
            {
                started.TrySetResult();
                var token = ValueProviderFrameContext.CancellationToken;
                Assert.IsTrue(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
                token.ThrowIfCancellationRequested();
                return new Picture8bpp(2, 1);
            }
            finally { Interlocked.Decrement(ref active); }
        });
        using var builder = new VideoBuilder(Writer()) { Duration = 2 };
        var renderer = new Renderer { Clips = [clip], builder = builder, Duration = 2, MaxThreads = 1, Use16Bit = false };
        renderer.PrepareRender(cancellation.Token);
        var rendering = renderer.GoRender(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        try
        {
            await rendering;
            Assert.Fail("The render should be cancelled.");
        }
        catch (OperationCanceledException) { }
        Assert.AreEqual(0, active);
        Assert.AreEqual(0, builder.WrittenFramesCount);
        Assert.AreEqual(0, renderer.CurrentFinished);
        IRenderContext.Current = null;
    }

    private static BlackholeVideoWriter Writer() => new()
    {
        Width = 2, Height = 1, FramePerSecond = 30, PixelFormat = "AV_PIX_FMT_RGB24", OutputPath = "unused",
    };

    private static VirtualSourceVideoClip Clip(Func<uint, IPicture> generate) => new()
    {
        Id = Guid.NewGuid(), Name = "clip", Duration = 10, FrameTime = 1f / 30,
        TargetWidth = 2, TargetHeight = 1, VirtualSource = new Source(generate),
    };

    private sealed class Source(Func<uint, IPicture> generate) : IVirtualVideoSource
    {
        public string TypeName => nameof(Source);
        public void Init(int width, int height, int fps, uint duration, IPicture.PicturePixelMode mode) { }
        public IPicture Generate(uint frame, bool hasAlpha) => generate(frame);
        public void Dispose() { }
    }
}
