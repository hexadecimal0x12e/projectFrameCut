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
using projectFrameCut.Render.RPCProtocol;
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
    [DataRow(false)]
    [DataRow(true)]
    public async Task BlockedWriterDoesNotPreventSchedulingTheNextFrame(bool memoryBudget)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var nextReady = new ManualResetEventSlim();
        using var clip = Clip(frame =>
        {
            if (frame == 1) nextReady.Set();
            return Picture8bpp.GenerateSolidColor(2, 1, (byte)frame, 0, 0, 1);
        }, duration: 3);
        var written = new List<byte>();
        var writer = Writer();
        writer.OnFrameWrite += (_, picture) =>
        {
            if (written.Count == 0)
                Assert.IsTrue(nextReady.Wait(TimeSpan.FromSeconds(5), timeout.Token));
            written.Add(((IPicture<byte>)picture).r[0]);
        };
        using var builder = new VideoBuilder(writer) { Duration = 3 };
        var renderer = new Renderer
        {
            Clips = [clip], builder = builder, Duration = 3, MaxThreads = 2, Use16Bit = false,
            RenderAheadFrames = 1, EnableMemoryBudget = memoryBudget, MaxBufferedBytes = 1,
        };
        try
        {
            renderer.PrepareRender(timeout.Token);
            await renderer.GoRender(timeout.Token);
            CollectionAssert.AreEqual(new byte[] { 0, 1, 2 }, written);
            Assert.AreEqual(3, renderer.CurrentFinished);
        }
        finally { IRenderContext.Current = null; }
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

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public async Task DeferredCompositionWaitsForEveryClipAndWritesInOrder(int maxThreads)
    {
        string root = Path.Combine(Path.GetTempPath(), "pjfc-deferred-" + Guid.NewGuid().ToString("N"));
        int decoded = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var bottom = Clip(frame =>
        {
            Interlocked.Increment(ref decoded);
            return Picture8bpp.GenerateSolidColor(2, 1, (byte)(10 + frame), 0, 0, 1);
        }, duration: 4, layer: 1);
        using var top = Clip(frame =>
        {
            Interlocked.Increment(ref decoded);
            return Picture8bpp.GenerateSolidColor(2, 1, (byte)(20 + frame), 0, 0, 1);
        }, duration: 4);
        var written = new List<byte>();
        var progress = new List<double>();
        var writer = Writer();
        writer.OnFrameWrite += (_, picture) =>
        {
            Assert.AreEqual(8, Volatile.Read(ref decoded));
            Assert.AreEqual(4, Directory.EnumerateFiles(root, "*.vfd", SearchOption.AllDirectories).Count());
            Assert.IsFalse(Directory.EnumerateDirectories(root, "clips", SearchOption.AllDirectories).Any());
            written.Add(((IPicture<byte>)picture).r[0]);
        };
        using var builder = new VideoBuilder(writer) { Duration = 4 };
        var renderer = new Renderer
        {
            Clips = [top, bottom], builder = builder, Duration = 4, MaxThreads = maxThreads, Use16Bit = false,
            EnableDeferredComposition = true,
            DynamicPreviewCacheProjectRoot = root, RenderResumeSignature = "state",
        };
        renderer.OnProgressChanged += (value, _) => progress.Add(value);
        try
        {
            renderer.PrepareRender(timeout.Token);
            await renderer.GoRender(timeout.Token);
            CollectionAssert.AreEqual(new byte[] { 20, 21, 22, 23 }, written);
            Assert.AreEqual(4, builder.WrittenFramesCount);
            Assert.AreEqual(4, renderer.CurrentFinished);
            Assert.AreEqual(1d, renderer.Progress);
            Assert.IsTrue(progress.SequenceEqual(progress.Order()));
            Assert.IsTrue(progress.Contains(0.5));
        }
        finally
        {
            IRenderContext.Current = null;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task DeferredResumeRestoresRenderedFramesWithoutDynamicPreviewCacheReuse()
    {
        string root = Path.Combine(Path.GetTempPath(), "pjfc-deferred-" + Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var first = Clip(frame =>
        {
            if (frame == 2) cancellation.Cancel();
            return Picture8bpp.GenerateSolidColor(2, 1, (byte)(30 + frame), 0, 0, 1);
        }, duration: 4);
        using var firstBuilder = new VideoBuilder(Writer()) { Duration = 4 };
        var initial = DeferredRenderer(first, firstBuilder, root);
        try
        {
            initial.PrepareRender(cancellation.Token);
            try
            {
                await initial.GoRender(cancellation.Token);
                Assert.Fail("The render should be cancelled.");
            }
            catch (OperationCanceledException) { }
            Assert.AreEqual(0, firstBuilder.WrittenFramesCount);
            var existing = Directory.EnumerateFiles(root, "*.vfd", SearchOption.AllDirectories)
                .ToDictionary(path => path, File.ReadAllBytes);
            initial.CleanupCreatedDeferredCache();
            CollectionAssert.AreEquivalent(existing.Keys.ToArray(), Directory.GetFiles(root, "*.vfd", SearchOption.AllDirectories));

            var decoded = new ConcurrentQueue<uint>();
            using var next = Clip(frame =>
            {
                decoded.Enqueue(frame);
                return Picture8bpp.GenerateSolidColor(2, 1, (byte)(30 + frame), 0, 0, 1);
            }, duration: 4, id: first.Id);
            using var builder = new VideoBuilder(Writer()) { Duration = 4 };
            var resumed = DeferredRenderer(next, builder, root);
            resumed.MaxThreads = 3;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            resumed.PrepareRender(timeout.Token);
            await resumed.GoRender(timeout.Token);
            CollectionAssert.AreEqual(new uint[] { 2, 3 }, decoded.ToArray());
            Assert.AreEqual(4, builder.WrittenFramesCount);
            builder.Writer.Finish();
            builder.Dispose();
            resumed.CleanupCreatedDeferredCache();
            CollectionAssert.AreEquivalent(existing.Keys.ToArray(), Directory.GetFiles(root, "*.vfd", SearchOption.AllDirectories));
            foreach (var entry in existing) CollectionAssert.AreEqual(entry.Value, File.ReadAllBytes(entry.Key));
        }
        finally
        {
            IRenderContext.Current = null;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task DeferredResumeRestoresComposedFramesAndRewritesTheVideoFromItsFirstFrame()
    {
        string root = Path.Combine(Path.GetTempPath(), "pjfc-deferred-" + Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var first = Clip(frame => Picture8bpp.GenerateSolidColor(2, 1, (byte)(40 + frame), 0, 0, 1), duration: 4);
        var firstWriter = Writer();
        firstWriter.OnFrameWrite += (_, _) => cancellation.Cancel();
        using var firstBuilder = new VideoBuilder(firstWriter) { Duration = 4 };
        var initial = DeferredRenderer(first, firstBuilder, root);
        try
        {
            initial.PrepareRender(cancellation.Token);
            try
            {
                await initial.GoRender(cancellation.Token);
                Assert.Fail("The render should be cancelled.");
            }
            catch (OperationCanceledException) { }
            string composed = Directory.EnumerateFiles(root, "0.vfd", SearchOption.AllDirectories)
                .Single(p => Path.GetFileName(Path.GetDirectoryName(p)) == "composed");
            var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(composed, stamp);

            using var next = Clip(_ => throw new AssertFailedException("An existing clip frame was rendered again."), duration: 4, id: first.Id);
            var written = new List<byte>();
            var writer = Writer();
            writer.OnFrameWrite += (_, picture) => written.Add(((IPicture<byte>)picture).r[0]);
            using var builder = new VideoBuilder(writer) { Duration = 4 };
            var resumed = DeferredRenderer(next, builder, root);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            resumed.PrepareRender(timeout.Token);
            await resumed.GoRender(timeout.Token);
            CollectionAssert.AreEqual(new byte[] { 40, 41, 42, 43 }, written);
            Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(composed));
            Assert.AreEqual(4, builder.WrittenFramesCount);
        }
        finally
        {
            IRenderContext.Current = null;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public async Task DeferredResumeRejectsChangedStateAndHonorsItsSwitch(bool resume, bool changed)
    {
        string root = Path.Combine(Path.GetTempPath(), "pjfc-deferred-" + Guid.NewGuid().ToString("N"));
        using var first = Clip(frame => Picture8bpp.GenerateSolidColor(2, 1, (byte)frame, 0, 0, 1), duration: 4);
        using var firstBuilder = new VideoBuilder(Writer()) { Duration = 4 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var initial = DeferredRenderer(first, firstBuilder, root);
            initial.PrepareRender(timeout.Token);
            await initial.GoRender(timeout.Token);
            int decoded = 0;
            using var next = Clip(frame =>
            {
                decoded++;
                return Picture8bpp.GenerateSolidColor(2, 1, (byte)frame, 0, 0, 1);
            }, duration: 4, id: first.Id);
            using var builder = new VideoBuilder(Writer()) { Duration = 4 };
            var renderer = DeferredRenderer(next, builder, root);
            renderer.ResumeInterruptedRender = resume;
            if (changed) renderer.RenderResumeSignature = "changed";
            renderer.PrepareRender(timeout.Token);
            await renderer.GoRender(timeout.Token);
            Assert.AreEqual(4, decoded);
        }
        finally
        {
            IRenderContext.Current = null;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task DeferredResumeRegeneratesOnlyTheCorruptFrame()
    {
        string root = Path.Combine(Path.GetTempPath(), "pjfc-deferred-" + Guid.NewGuid().ToString("N"));
        using var first = Clip(frame => Picture8bpp.GenerateSolidColor(2, 1, (byte)(50 + frame), 0, 0, 1), duration: 4);
        using var firstBuilder = new VideoBuilder(Writer()) { Duration = 4 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var initial = DeferredRenderer(first, firstBuilder, root);
            initial.PrepareRender(timeout.Token);
            await initial.GoRender(timeout.Token);
            foreach (string path in Directory.EnumerateFiles(root, "0.vfd", SearchOption.AllDirectories))
                File.WriteAllBytes(path, [1, 2, 3]);
            var decoded = new List<uint>();
            using var next = Clip(frame =>
            {
                decoded.Add(frame);
                return Picture8bpp.GenerateSolidColor(2, 1, (byte)(50 + frame), 0, 0, 1);
            }, duration: 4, id: first.Id);
            using var builder = new VideoBuilder(Writer()) { Duration = 4 };
            var resumed = DeferredRenderer(next, builder, root);
            resumed.PrepareRender(timeout.Token);
            await resumed.GoRender(timeout.Token);
            CollectionAssert.AreEqual(new uint[] { 0 }, decoded);
            Assert.AreEqual(4, builder.WrittenFramesCount);
        }
        finally
        {
            IRenderContext.Current = null;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DeferredCleanupPreservesDynamicPreviewCache(bool reusePreview)
    {
        string root = Path.Combine(Path.GetTempPath(), "pjfc-deferred-" + Guid.NewGuid().ToString("N"));
        using var clip = new SolidColorClip
        {
            Id = Guid.NewGuid(), Name = "solid", Duration = 4, FrameTime = 1f / 30,
            TargetWidth = 2, TargetHeight = 1, A = 1, ExtraData = [],
        };
        var written = new List<byte>();
        var writer = Writer();
        writer.OnFrameWrite += (_, picture) => written.Add(((IPicture<byte>)picture).r[0]);
        using var builder = new VideoBuilder(writer) { Duration = 4 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var identity = FrameHashHelper.GetFrameHash([clip], 0, new()
            {
                Stage = "clip",
                Width = 2, Height = 1, ProjectWidth = 2, ProjectHeight = 1, PixelMode = 8,
            });
            Assert.IsTrue(identity.CanReuse);
            string directory = Path.Combine(root, "thumbs", "perClip", clip.Id.ToString(), "dynamic");
            Directory.CreateDirectory(directory);
            string preview = Path.Combine(directory, $"dynamic_{RenderBackendService.ClipPreviewCacheVersion}_{identity.Hash}_2x1_2x1_vfd8.vfd");
            using var cached = Picture8bpp.GenerateSolidColor(2, 1, 77, 0, 0, 1);
            cached.SaveToDisk(preview, PictureExtensions.SharedVfdPictureEncoder);
            byte[] existing = File.ReadAllBytes(preview);
            var renderer = DeferredRenderer(clip, builder, root);
            renderer.ReuseDynamicPreviewCache = reusePreview;
            renderer.PrepareRender(timeout.Token);
            await renderer.GoRender(timeout.Token);
            CollectionAssert.AreEqual(Enumerable.Repeat(reusePreview ? (byte)77 : (byte)0, 4).ToArray(), written);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "thumbs", "render-deferred", "clips")));
            Assert.IsTrue(Directory.EnumerateFiles(Path.Combine(root, "thumbs", "render-deferred"), "*.vfd", SearchOption.AllDirectories).Any());
            builder.Writer.Finish();
            builder.Dispose();
            renderer.CleanupCreatedDeferredCache();
            renderer.CleanupCreatedDeferredCache();
            Assert.AreEqual(0, Directory.GetFiles(Path.Combine(root, "thumbs", "render-deferred"), "*.vfd", SearchOption.AllDirectories).Length);
            CollectionAssert.AreEqual(existing, File.ReadAllBytes(preview));
        }
        finally
        {
            IRenderContext.Current = null;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static Renderer DeferredRenderer(IClip clip, VideoBuilder builder, string root) => new()
    {
        Clips = [clip], builder = builder, Duration = 4, MaxThreads = 1, Use16Bit = false,
        EnableDeferredComposition = true, DynamicPreviewCacheProjectRoot = root, RenderResumeSignature = "state",
    };

    private static BlackholeVideoWriter Writer() => new()
    {
        Width = 2, Height = 1, FramePerSecond = 30, PixelFormat = "AV_PIX_FMT_RGB24", OutputPath = "unused",
    };

    private static VirtualSourceVideoClip Clip(Func<uint, IPicture> generate, uint startFrame = 0, uint duration = 10,
        uint layer = 0, uint subLayer = 0, Action? initialize = null, IEffectProvider[]? providers = null, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(), Name = "clip", StartFrame = startFrame, Duration = duration, FrameTime = 1f / 30,
        LayerIndex = layer, SubLayerIndex = subLayer,
        EffectProviders = providers?.Select(EffectBindingHelper.SerializeProvider).ToArray(),
        TargetWidth = 2, TargetHeight = 1, VirtualSource = new Source(generate, initialize),
    };

    private sealed class Source(Func<uint, IPicture> generate, Action? initialize) : IVirtualVideoSource
    {
        public bool IsDeterministic => false;

        public string TypeName => nameof(Source);
        public void Init(int width, int height, int fps, uint duration, IPicture.PicturePixelMode mode) => initialize?.Invoke();
        public IPicture Generate(uint frame, bool hasAlpha) => generate(frame);
        public void Dispose() { }
    }
}
