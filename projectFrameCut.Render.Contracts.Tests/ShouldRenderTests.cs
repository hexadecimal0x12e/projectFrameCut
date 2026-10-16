using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.RPCProtocol;
using projectFrameCut.Render.Transform;
using projectFrameCut.Shared;
using System.Text.Json;
using static projectFrameCut.Render.Contracts.Tests.DynamicEffectGraphTests;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ShouldRenderTests
{
    private sealed class TrackingClip : SolidColorClip, IImmutableContentClip
    {
        public Action? OnRead { get; init; }
        public new IPicture GetContent(int width, int height, IPicture.PicturePixelMode pixelMode)
        {
            OnRead?.Invoke();
            return base.GetContent(width, height, pixelMode);
        }

        public IPicture GetFrameRelativeToStartPointOfSource(uint frame, int width, int height, IPicture.PicturePixelMode pixelMode)
            => GetContent(width, height, pixelMode);

        public IClip HandleArguments(IReadOnlyDictionary<string, object> arguments)
        {
            Assert.IsFalse(arguments.ContainsKey(ClipArgumentHandler.ShouldRenderKey));
            return ClipArgumentHandler.Handle(this, arguments);
        }
    }

    private static TrackingClip Clip(Action? read = null, uint start = 0, uint layer = 0, params IEffectProvider[] providers)
    {
        var clip = new TrackingClip
        {
            Id = Guid.NewGuid(), Name = "clip", StartFrame = start, Duration = 8, LayerIndex = layer, ExtraData = [],
            TargetWidth = 2, TargetHeight = 1, OutputWidth = 2, OutputHeight = 1, R = ushort.MaxValue, A = 1,
            OnRead = read, EffectProviders = providers.Select(EffectBindingHelper.SerializeProvider).ToArray(),
        };
        EffectHelper.ResolveClipEffects(clip);
        return clip;
    }

    private static ClipArgumentProvider Bind(IEffectProvider source)
    {
        var arguments = new ClipArgumentProvider();
        arguments.SetFieldBinding(ClipArgumentHandler.ShouldRenderKey, source.Id.ToString());
        return arguments;
    }

    private static NumericComparisonNodeEffectProvider AfterFrameThree()
    {
        var comparison = new NumericComparisonNodeEffectProvider();
        comparison.SetFieldBinding("A", ValueProviderFrameContext.BuiltInFrameProviderId);
        comparison.Fields = new() { ["B"] = new StaticEffectArgumentField(3f, EffectArgumentFieldType.Numeric) };
        return comparison;
    }

    [TestMethod]
    public void ComparisonBindingSkipsSourceAndEffectsUntilTrue()
    {
        int reads = 0, effects = 0;
        var comparison = AfterFrameThree();
        using var clip = Clip(read: () => reads++, providers: [Bind(comparison), comparison]);
        clip.EffectsInstances = [.. clip.EffectsInstances!, new TestEffect(c => { effects++; return c.Input; })];
        Assert.AreEqual(0, Timeline.GetFramesInOneFrame([clip], 0, 2, 1, 8, 2, 1, initializeClips: false).Count());
        Assert.AreEqual(0, reads);
        Assert.AreEqual(0, effects);
        Assert.IsFalse(ClipInitializationFailure.IsMarked(clip));
        var frames = Timeline.GetFramesInOneFrame([clip], 4, 2, 1, 8, 2, 1, initializeClips: false).ToArray();
        try
        {
            Assert.AreEqual(1, frames.Length);
            using var result = Timeline.MixtureLayers(frames, 4, 2, 1, 8, transparentBackground: true, disposeIntermediateFrames: true);
            Assert.AreEqual(1, reads);
            Assert.AreEqual(1, effects);
            Assert.IsTrue(ClipArgumentBinding.ShouldRender(frames[0].ParentClip));
        }
        finally
        {
            foreach (var frame in frames) frame.Clip.Dispose(true);
        }
    }

    [TestMethod]
    public void UnboundStaticFalseDisabledAndDisconnectedArgumentsStillRender()
    {
        var comparison = new NumericComparisonNodeEffectProvider();
        using var clip = Clip(providers: [Bind(comparison), comparison]);
        var arguments = clip.EffectProvidersInstances!.OfType<ClipArgumentProvider>().Single();
        Assert.IsFalse(ClipArgumentBinding.ShouldRender(ClipArgumentBinding.InitializeFrame(clip, 0)));
        arguments.ClearFieldBinding(ClipArgumentHandler.ShouldRenderKey);
        Assert.IsTrue(ClipArgumentBinding.ShouldRender(ClipArgumentBinding.InitializeFrame(clip, 0)));
        arguments.Fields = new() { [ClipArgumentHandler.ShouldRenderKey] = new StaticEffectArgumentField(false, EffectArgumentFieldType.Boolean) };
        Assert.IsTrue(ClipArgumentBinding.ShouldRender(ClipArgumentBinding.InitializeFrame(clip, 0)));
        arguments.SetFieldBinding(ClipArgumentHandler.ShouldRenderKey, comparison.Id.ToString());
        arguments.Enabled = false;
        Assert.IsTrue(ClipArgumentBinding.ShouldRender(ClipArgumentBinding.InitializeFrame(clip, 0)));
        arguments.Enabled = true;
        arguments.SetFieldBinding(ClipArgumentHandler.ShouldRenderKey, IEffectProvider.NoConnectionGUID.ToString());
        Assert.IsTrue(ClipArgumentBinding.ShouldRender(ClipArgumentBinding.InitializeFrame(clip, 0)));
    }

    [TestMethod]
    public void HiddenArgumentsShortCircuitAndShareTheFrameSnapshot()
    {
        int calls = 0;
        var comparison = new NumericComparisonNodeEffectProvider();
        using var clip = Clip(providers: [Bind(comparison), comparison]);
        var arguments = clip.EffectProvidersInstances!.OfType<ClipArgumentProvider>().Single();
        arguments.Fields = new()
        {
            [ClipArgumentHandler.ShouldRenderKey] = new ValueEffect(_ => { calls++; return false; }),
            [nameof(IClip.TargetX)] = new ValueEffect(_ => throw new InvalidOperationException("Other arguments must not be evaluated.")),
        };
        using (var context = new ClipValueFrameContext([clip], 0, _ => Assert.Fail(), DynamicEffectGraph.CopyPicture))
        {
            var frame = new ClipArgumentFrameCache().Get(clip, 0);
            Assert.IsFalse(ClipArgumentBinding.ShouldRender(frame));
            Assert.AreSame(frame, new ClipArgumentFrameCache().Get(clip, 0, _ => Assert.Fail()));
            Assert.AreSame(frame, ClipArgumentBinding.InitializeFrame(clip, 0));
            Assert.AreEqual(1, calls);
            Assert.IsFalse(ClipArgumentBinding.ShouldRender(ClipArgumentBinding.InitializeFrame(frame, 1)));
            Assert.AreEqual(2, calls);
        }
        Assert.IsFalse(ClipInitializationFailure.IsMarked(clip));
        Assert.IsFalse(ClipArgumentBinding.ShouldRender(ClipArgumentBinding.InitializeFrame(clip, 0)));
        Assert.AreEqual(3, calls);
    }

    [TestMethod]
    public void BooleanBindingsRoundTripWithoutPersistingFrameState()
    {
        var comparison = AfterFrameThree();
        var serialized = JsonSerializer.SerializeToElement(new[] { EffectBindingHelper.SerializeProvider(Bind(comparison)),
            EffectBindingHelper.SerializeProvider(comparison) }).Deserialize<EffectProviderJSONStructure[]>()!;
        using var clip = new TrackingClip { Id = Guid.NewGuid(), Name = "clip", Duration = 8, ExtraData = [], EffectProviders = serialized };
        EffectHelper.ResolveClipEffects(clip);
        Assert.IsFalse(ClipArgumentBinding.ShouldRender(ClipArgumentBinding.InitializeFrame(clip, 0)));
        Assert.IsTrue(ClipArgumentBinding.ShouldRender(ClipArgumentBinding.InitializeFrame(clip, 4)));
        Assert.IsTrue(ClipArgumentBinding.ShouldRender(clip));
        Assert.AreEqual(0, EffectBindingHelper.SerializeProvider(clip.EffectProvidersInstances!.OfType<ClipArgumentProvider>().Single()).StaticFields!.Count);
    }

    [TestMethod]
    [DataRow(8)]
    [DataRow(16)]
    public void CompositingHiddenPreparedFramesReturnsTransparentOutput(int bits)
    {
        var comparison = new NumericComparisonNodeEffectProvider();
        using var clip = Clip(providers: [Bind(comparison), comparison]);
        using var source = Picture8bpp.GenerateSolidColor(2, 1, 255, 0, 0, 1);
        using var result = Timeline.MixtureLayers([new OneFrame(0, clip, source, resolveEffects: false)], 0, 2, 1, bits,
            transparentBackground: true, disposeIntermediateFrames: true, AfterEffectCallback: (_, _) => Assert.Fail());
        Assert.AreEqual(bits, (int)result.BitPerPixel.Value);
        Assert.AreEqual(0f, bits == 8 ? ((IPicture<byte>)result).a![0] : ((IPicture<ushort>)result).a![0]);
        Assert.IsTrue(source.Disposed);
    }

    [TestMethod]
    [DataRow(TransformRenderOrder.BeforeEffects, 8)]
    [DataRow(TransformRenderOrder.AfterEffects, 8)]
    [DataRow(TransformRenderOrder.BeforeEffects, 16)]
    [DataRow(TransformRenderOrder.AfterEffects, 16)]
    public void HiddenTransformInputsAreTransparentWithoutReadingTheSource(TransformRenderOrder order, int bits)
    {
        int reads = 0;
        var comparison = new NumericComparisonNodeEffectProvider();
        using var right = Clip(read: () => reads++, start: 8, providers: [Bind(comparison), comparison]);
        IEffectProvider transform = new CrossfadeTransformProvider();
        TransformProcessing.Configure(transform, TransformSide.Right, TransformInputMode.TwoInput, 4, right.Id, order);
        using var left = Clip(providers: [transform]);
        Assert.IsTrue(TransformProcessing.TryRender(left, [left, right], 7, 2, 1, 2, 1, bits, out var result, initializeClips: false));
        using (result)
        {
            Assert.AreEqual(0, reads);
            Assert.AreEqual(bits, (int)result!.BitPerPixel.Value);
            Assert.AreEqual(2f / 3f, bits == 8 ? ((IPicture<byte>)result).a![0] : ((IPicture<ushort>)result).a![0], 0.0001f);
        }
    }

    [TestMethod]
    public void HiddenEndpointSourcesAreUnavailableAndRecoverAtLaterFrames()
    {
        int reads = 0;
        var comparison = AfterFrameThree();
        var output = new ClipValueExportProvider { ValueType = EffectArgumentFieldType.Numeric };
        output.SetMainInputSource(ValueProviderFrameContext.BuiltInFrameProviderId);
        using var source = Clip(read: () => reads++, layer: 2, providers: [Bind(comparison), comparison, output]);
        var input = new ClipValueImportProvider { SourceClipId = source.Id, SourceEndpointId = output.Id, ValueType = output.ValueType };
        using var target = Clip(layer: 1, providers: [input]);
        using (ClipValuePassThrough.Open([source, target], 0, 2, 1, 2, 1, 8, CancellationToken.None))
            Assert.ThrowsExactly<ClipValueBindingException>(() => ClipValueFrameContext.Current!.Read(target, 0, input));
        Assert.AreEqual(0, reads);
        Assert.IsFalse(ClipInitializationFailure.IsMarked(source));
        using (ClipValuePassThrough.Open([source, target], 4, 2, 1, 2, 1, 8, CancellationToken.None))
            Assert.AreEqual(4f, ClipValueFrameContext.Current!.Read(target, 4, input));
        Assert.AreEqual(1, reads);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HiddenClipPreviewsReturnTransparentArtifacts(bool beforeLayout)
    {
        using var files = new ProjectExternalSourceTests.SourceFiles();
        var root = files.Directory("project");
        var comparison = new NumericComparisonNodeEffectProvider();
        var clip = new ClipDraftDTO
        {
            Id = Guid.NewGuid(), Name = "hidden", FromPlugin = InternalPluginBase.InternalPluginBaseID,
            ClipType = ClipMode.SolidColorClip, TypeName = nameof(SolidColorClip), Duration = 1, SecondPerFrameRatio = 1,
            TargetWidth = 2, TargetHeight = 1,
            EffectProviders = [EffectBindingHelper.SerializeProvider(Bind(comparison)), EffectBindingHelper.SerializeProvider(comparison)],
            MetaData = new() { [nameof(SolidColorClip.R)] = ushort.MaxValue, [nameof(SolidColorClip.A)] = 1f },
        };
        await using var service = new RenderBackendService();
        await using var client = new RenderClient(new DirectRenderTransport(service), "should-render-test");
        var session = await client.OpenProjectAsync(new()
        {
            ProjectRoot = root, ProjectWidth = 2, ProjectHeight = 1,
            TimelineJson = JsonSerializer.Serialize(new DraftStructureJSON { Clips = [clip], Duration = 1 }),
        });
        var artifact = await client.RenderClipPreviewAsync(new()
        {
            SessionId = session.SessionId, ClipId = clip.Id, CanvasWidth = 2, CanvasHeight = 1,
            PreferredPixelFormat = PreviewPixelFormat.VfdPicture, BeforeLayout = beforeLayout,
        });
        using var stream = File.OpenRead(Path.Combine(root, artifact.ProjectRelativePath));
        Assert.IsTrue(PictureExtensions.SharedVfdPictureDecoder.TryLoad(stream, out IPicture? picture));
        using (picture)
        {
            Assert.AreEqual(2, picture!.Width);
            Assert.AreEqual(1, picture.Height);
            Assert.IsTrue(((IPicture<byte>)picture).a!.All(a => a == 0));
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task AllHiddenFramesFinishWithoutReadingSources(int workers)
    {
        int reads = 0;
        var a = new NumericComparisonNodeEffectProvider();
        var b = new NumericComparisonNodeEffectProvider();
        using var left = Clip(read: () => Interlocked.Increment(ref reads), layer: 2, providers: [Bind(a), a]);
        using var right = Clip(read: () => Interlocked.Increment(ref reads), layer: 1, providers: [Bind(b), b]);
        var renderer = new Renderer
        {
            Clips = [left, right], Duration = 2, TargetWidth = 2, TargetHeight = 1, ProjectRelativeWidth = 2, ProjectRelativeHeight = 1,
            Use16Bit = false, MaxThreads = workers, RenderAheadFrames = 1,
            AutoSetupRenderContext = false,
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        renderer.PrepareRender(timeout.Token);
        await renderer.GoRender(timeout.Token);
        Assert.AreEqual(2, renderer.CurrentFinished);
        Assert.AreEqual(0, reads);
        Assert.IsFalse(timeout.IsCancellationRequested);
    }
}
