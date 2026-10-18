using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.Benchmark;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Context;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.Transform;
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
            return Picture8bpp.GenerateSolidColor(2, 1, (byte)frame, 0, 0, 1);
        }, duration: 4);
        using var second = Clip(frame =>
        {
            decoded.Enqueue(frame + 4);
            secondReady.Set();
            return Picture8bpp.GenerateSolidColor(2, 1, (byte)(frame + 4), 0, 0, 1);
        }, startFrame: 4, duration: 2);
        var written = new List<byte>();
        var previews = new List<byte>();
        var threads = new HashSet<int>();
        var writer = Writer();
        writer.OnFrameWrite += (_, picture) =>
        {
            threads.Add(Environment.CurrentManagedThreadId);
            Assert.AreEqual(RenderWorkerStage.WritingOutput, IRenderContext.WorkerState!.Stage);
            written.Add(((IPicture<byte>)picture).r[0]);
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
        var renderer = new Renderer
        {
            Clips = [clip, second], builder = builder, StartFrame = 2, Duration = 4, Use16Bit = false,
            MaxThreads = 2, RenderAheadFrames = 4,
        };
        renderer.PrepareRender(timeout.Token);
        await renderer.GoRender(timeout.Token);

        Assert.AreEqual(4u, decoded.First());
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
    [DataRow(1, false)]
    [DataRow(4, true)]
    public async Task SerialModesInitializeRenderAndWriteOnOneThreadInLayerOrder(int maxThreads, bool diagnostic)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var calls = new ConcurrentQueue<string>();
        var threads = new ConcurrentBag<int>();
        void Record(string call)
        {
            calls.Enqueue(call);
            threads.Add(Environment.CurrentManagedThreadId);
        }
        IPicture Generate(string name, uint frame)
        {
            Record($"{name} {frame}");
            return Picture8bpp.GenerateSolidColor(2, 1, 77, 0, 0, 1);
        }
        using var bottom = Clip(f => Generate("bottom", f), layer: 1, initialize: () => Record("init bottom"));
        using var middle = Clip(f => Generate("middle", f), subLayer: 1, initialize: () => Record("init middle"));
        using var top = Clip(f => Generate("top", f), initialize: () => Record("init top"));
        var writer = Writer();
        int written = 0;
        writer.OnFrameWrite += (_, _) => Record($"write {written++}");
        using var builder = new VideoBuilder(writer) { Duration = 2 };
        var renderer = new Renderer
        {
            Clips = [top, bottom, middle], builder = builder, Duration = 2, Use16Bit = false,
            MaxThreads = maxThreads, DiagnosticRender = diagnostic, RenderAheadFrames = 8,
        };
        renderer.PrepareRender(timeout.Token);
        Assert.IsTrue(calls.IsEmpty);
        await renderer.GoRender(timeout.Token);

        CollectionAssert.AreEqual(new[]
        {
            "init bottom", "bottom 0", "init middle", "middle 0", "init top", "top 0", "write 0",
            "bottom 1", "middle 1", "top 1", "write 1",
        }, calls.ToArray());
        Assert.AreEqual(1, threads.Distinct().Count());
        IRenderContext.Current = null;
    }

    [TestMethod]
    public async Task EachClipKeepsItsWorkerAndLaterClipsCanCreateMoreWorkersThanTheSoftLimit()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var initialized = new ConcurrentDictionary<int, int>();
        var frames = new ConcurrentDictionary<int, List<uint>>();
        IClip[] clips = Enumerable.Range(0, 4).Select(i => (IClip)Clip(frame =>
        {
            Assert.AreEqual(initialized[i], Environment.CurrentManagedThreadId);
            frames.GetOrAdd(i, _ => []).Add(frame);
            return Picture8bpp.GenerateSolidColor(2, 1, (byte)(i * 3 + frame), 0, 0, 1);
        }, startFrame: (uint)i * 3, duration: 3, initialize: () =>
        {
            Assert.IsTrue(initialized.TryAdd(i, Environment.CurrentManagedThreadId));
        })).ToArray();
        try
        {
            var written = new List<byte>();
            var writer = Writer();
            writer.OnFrameWrite += (_, picture) => written.Add(((IPicture<byte>)picture).r[0]);
            using var builder = new VideoBuilder(writer) { Duration = 12 };
            var renderer = new Renderer
            {
                Clips = clips, builder = builder, Duration = 12, Use16Bit = false,
                MaxThreads = 2, RenderAheadFrames = 2,
            };
            renderer.PrepareRender(timeout.Token);
            Assert.IsTrue(initialized.IsEmpty);
            await renderer.GoRender(timeout.Token);

            Assert.AreEqual(4, initialized.Values.Distinct().Count());
            foreach (var frameList in frames.Values)
                CollectionAssert.AreEqual(new uint[] { 0, 1, 2 }, frameList);
            CollectionAssert.AreEqual(Enumerable.Range(0, 12).Select(i => (byte)i).ToArray(), written);
        }
        finally
        {
            foreach (var clip in clips) clip.Dispose();
            IRenderContext.Current = null;
        }
    }

    [TestMethod]
    public async Task CurrentFrameCanExceedWorkerAndMemorySoftLimitsToMakeProgress()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var started = new CountdownEvent(3);
        IClip[] clips = Enumerable.Range(0, 3).Select(i => (IClip)Clip(_ =>
        {
            started.Signal();
            Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5), timeout.Token));
            return Picture8bpp.GenerateSolidColor(2, 1, (byte)i, 0, 0, 1);
        }, duration: 1, layer: (uint)i)).ToArray();
        try
        {
            using var builder = new VideoBuilder(Writer()) { Duration = 1 };
            var renderer = new Renderer
            {
                Clips = clips, builder = builder, Duration = 1, Use16Bit = false,
                MaxThreads = 2, EnableMemoryBudget = true, MaxBufferedBytes = 1,
            };
            renderer.PrepareRender(timeout.Token);
            await renderer.GoRender(timeout.Token);
            Assert.AreEqual(0, started.CurrentCount);
            Assert.AreEqual(1, builder.WrittenFramesCount);
        }
        finally
        {
            foreach (var clip in clips) clip.Dispose();
            IRenderContext.Current = null;
        }
    }

    [TestMethod]
    public async Task DisablingOversubscriptionKeepsCurrentFrameWithinWorkerLimit()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var started = new CountdownEvent(2);
        using var thirdStarted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int count = 0;
        IClip[] clips = Enumerable.Range(0, 3).Select(i => (IClip)Clip(_ =>
        {
            if (Interlocked.Increment(ref count) <= 2) started.Signal();
            else thirdStarted.Set();
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(5), timeout.Token));
            return Picture8bpp.GenerateSolidColor(2, 1, (byte)i, 0, 0, 1);
        }, duration: 1, layer: (uint)i)).ToArray();
        try
        {
            using var builder = new VideoBuilder(Writer()) { Duration = 1 };
            var renderer = new Renderer
            {
                Clips = clips, builder = builder, Duration = 1, Use16Bit = false,
                MaxThreads = 2, AllowWorkerOversubscription = false,
            };
            renderer.PrepareRender(timeout.Token);
            var rendering = renderer.GoRender(timeout.Token);
            try
            {
                Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5), timeout.Token));
                Assert.IsFalse(thirdStarted.Wait(TimeSpan.FromMilliseconds(100), timeout.Token));
            }
            finally
            {
                release.Set();
                await rendering;
            }
            Assert.AreEqual(3, count);
            Assert.AreEqual(1, builder.WrittenFramesCount);
        }
        finally
        {
            foreach (var clip in clips) clip.Dispose();
            IRenderContext.Current = null;
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task MasksReuseTheInitializedLowerClipWithinEachFrame(int maxThreads)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int initialized = 0, reads = 0;
        using var lower = Clip(_ =>
        {
            Assert.AreEqual(1, Volatile.Read(ref initialized));
            Interlocked.Increment(ref reads);
            return Picture8bpp.GenerateSolidColor(2, 1, 77, 0, 0, 1);
        }, layer: 1, initialize: () => Interlocked.Increment(ref initialized));
        using var mask = new MaskClip
        {
            Id = Guid.NewGuid(), Name = "mask", Duration = 2, TargetWidth = 2, TargetHeight = 1,
            ExtraData = new() { [MaskClip.SourceTypeKey] = "Passthrough" },
        };
        var written = new List<byte>();
        var writer = Writer();
        writer.OnFrameWrite += (_, picture) => written.Add(((IPicture<byte>)picture).r[0]);
        using var builder = new VideoBuilder(writer) { Duration = 2 };
        var renderer = new Renderer
        {
            Clips = [mask, lower], builder = builder, Duration = 2, MaxThreads = maxThreads, Use16Bit = false,
        };
        renderer.PrepareRender(timeout.Token);
        await renderer.GoRender(timeout.Token);

        Assert.AreEqual(1, initialized);
        Assert.AreEqual(2, reads);
        CollectionAssert.AreEqual(new byte[] { 77, 77 }, written);
        IRenderContext.Current = null;
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task TransitionsInitializeNeighborsOutsideTheFrameLookahead(int maxThreads)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int initialized = 0, reads = 0;
        using var right = Clip(_ =>
        {
            Assert.AreEqual(1, Volatile.Read(ref initialized));
            Interlocked.Increment(ref reads);
            return Picture8bpp.GenerateSolidColor(2, 1, 90, 0, 0, 1);
        }, startFrame: 2, duration: 2, initialize: () => Interlocked.Increment(ref initialized));
        IEffectProvider transform = new CrossfadeTransformProvider();
        TransformProcessing.Configure(transform, TransformSide.Right, TransformInputMode.TwoInput, 2, right.Id,
            TransformRenderOrder.BeforeEffects);
        using var left = Clip(_ => Picture8bpp.GenerateSolidColor(2, 1, 30, 0, 0, 1), duration: 2, providers: [transform]);
        using var builder = new VideoBuilder(Writer()) { StartFrame = 1, Duration = 1 };
        var renderer = new Renderer
        {
            Clips = [left, right], builder = builder, StartFrame = 1, Duration = 1,
            MaxThreads = maxThreads, RenderAheadFrames = 1, Use16Bit = false,
        };
        renderer.PrepareRender(timeout.Token);
        await renderer.GoRender(timeout.Token);

        Assert.AreEqual(1, initialized);
        Assert.AreEqual(1, reads);
        Assert.AreEqual(1, builder.WrittenFramesCount);
        IRenderContext.Current = null;
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task SpecificFrameReadsReuseInitializationAndDoNotMutateOutput(int maxThreads)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int initialized = 0;
        using var clip = Clip(frame => Picture8bpp.GenerateSolidColor(2, 1, (byte)(frame + 20), 0, 0, 1),
            initialize: () => Interlocked.Increment(ref initialized));
        var written = new List<byte>();
        Renderer? renderer = null;
        var writer = Writer();
        writer.OnFrameWrite += (_, picture) =>
        {
            written.Add(((IPicture<byte>)picture).r[0]);
            if (written.Count == 1)
            {
                using var next = renderer!.RenderSpecificFrame(2);
                Assert.AreEqual((byte)22, ((IPicture<byte>)next).r[0]);
                ((IPicture<byte>)next).r[0] = 99;
            }
        };
        using var builder = new VideoBuilder(writer) { Duration = 3 };
        renderer = new Renderer { Clips = [clip], builder = builder, Duration = 3, MaxThreads = maxThreads, Use16Bit = false };
        renderer.PrepareRender(timeout.Token);
        using (var preview = renderer.RenderSpecificFrame(1))
            Assert.AreEqual((byte)21, ((IPicture<byte>)preview).r[0]);
        await renderer.GoRender(timeout.Token);

        Assert.AreEqual(1, initialized);
        CollectionAssert.AreEqual(new byte[] { 20, 21, 22 }, written);
        IRenderContext.Current = null;
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task InitializationFailureStopsRendering(int maxThreads)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clip = Clip(_ => throw new AssertFailedException("An uninitialized source was read."),
            initialize: () => throw new InvalidOperationException("Source initialization failed."));
        using var builder = new VideoBuilder(Writer()) { Duration = 2 };
        var renderer = new Renderer { Clips = [clip], builder = builder, Duration = 2, MaxThreads = maxThreads, Use16Bit = false };
        renderer.PrepareRender(timeout.Token);
        try
        {
            await renderer.GoRender(timeout.Token);
            Assert.Fail("The render should fail.");
        }
        catch (InvalidOperationException ex)
        {
            Assert.AreEqual("Source initialization failed.", ex.Message);
        }
        Assert.AreEqual(0, builder.WrittenFramesCount);
        IRenderContext.Current = null;
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task CancellationWaitsForTheActiveDecoderAndDoesNotWriteItsFrame(int maxThreads)
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
        var renderer = new Renderer { Clips = [clip], builder = builder, Duration = 2, MaxThreads = maxThreads, Use16Bit = false };
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

    private static VirtualSourceVideoClip Clip(Func<uint, IPicture> generate, uint startFrame = 0, uint duration = 10,
        uint layer = 0, uint subLayer = 0, Action? initialize = null, IEffectProvider[]? providers = null) => new()
    {
        Id = Guid.NewGuid(), Name = "clip", StartFrame = startFrame, Duration = duration, FrameTime = 1f / 30,
        LayerIndex = layer, SubLayerIndex = subLayer,
        EffectProviders = providers?.Select(EffectBindingHelper.SerializeProvider).ToArray(),
        TargetWidth = 2, TargetHeight = 1, VirtualSource = new Source(generate, initialize),
    };

    private sealed class Source(Func<uint, IPicture> generate, Action? initialize) : IVirtualVideoSource
    {
        public string TypeName => nameof(Source);
        public void Init(int width, int height, int fps, uint duration, IPicture.PicturePixelMode mode) => initialize?.Invoke();
        public IPicture Generate(uint frame, bool hasAlpha) => generate(frame);
        public void Dispose() { }
    }
}
