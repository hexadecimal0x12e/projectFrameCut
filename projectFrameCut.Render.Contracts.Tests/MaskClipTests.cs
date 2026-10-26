using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Context;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.Sources;
using projectFrameCut.Render.Transform;
using projectFrameCut.Shared;
using System.Reflection;
using System.Text.Json;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class MaskClipTests
{
    private static MaskClip Mask(string source = "Blur", uint layer = 1, int x = 0, int width = 5, int height = 1, float rotation = 0,
        uint duration = 10, EffectProviderJSONStructure[]? providers = null)
        => new()
        {
            Id = Guid.NewGuid(), Name = "mask", LayerIndex = layer, Duration = duration, FrameTime = 1f / 30,
            TargetX = x, TargetWidth = width, TargetHeight = height, Rotation = rotation,
            EffectProviders = providers,
            ExtraData = new() { [MaskClip.SourceTypeKey] = source }
        };

    private static ClipArgumentProvider Arguments(MaskClip clip, string field, object value)
    {
        var provider = new ClipArgumentProvider();
        provider.Attach(clip);
        provider.Fields = new() { [MaskClip.ArgumentPrefix + field] = new StaticEffectArgumentField(value,
            clip.ArgumentFields[MaskClip.ArgumentPrefix + field].FieldType) };
        clip.EffectProvidersInstances = [provider];
        return provider;
    }

    private static VirtualSourceVideoClip Pattern(Func<uint, IPicture> generate, int width = 5, int height = 1)
        => new()
        {
            Id = Guid.NewGuid(), Name = "pattern", Duration = 10, LayerIndex = 2, FrameTime = 1f / 30,
            TargetWidth = width, TargetHeight = height, VirtualSource = new TestSource(generate)
        };

    private static IPicture Render(IClip[] clips, int width = 5, int height = 1, uint frame = 0)
        => Timeline.MixtureLayers(Timeline.GetFramesInOneFrame(clips, frame, width, height, 8,
            width, height, initializeClips: false), frame, width, height,
            projectRelativeWidth: width, projectRelativeHeight: height, transparentBackground: true,
            disposeIntermediateFrames: true, allClips: clips);

    [TestMethod]
    public void BlurSamplesOutsideTheMaskRectangle()
    {
        using var pattern = Pattern(_ => new Picture8bpp(5, 1) { r = [0, 0, 255, 0, 0] });
        using var mask = Mask(x: 1, width: 3);
        Arguments(mask, "Sigma", 1f);
        using var result = Render([mask, pattern]);
        CollectionAssert.AreEqual(new byte[] { 0, 85, 85, 85, 0 }, ((IPicture<byte>)result).r);
    }

    [TestMethod]
    public void ClipPreviewUsesTheMaskSizeAndTracksItsPosition()
    {
        using var pattern = Pattern(_ => new Picture8bpp(5, 1) { r = [0, 10, 20, 30, 40] });
        using var mask = Mask(x: 1, width: 2);
        Arguments(mask, "Sigma", 0f);
        using var first = Preview(mask, [pattern, mask]);
        Assert.AreEqual(2, first.Width);
        Assert.AreEqual(1, first.Height);
        CollectionAssert.AreEqual(new byte[] { 10, 20 }, ((IPicture<byte>)first).r);

        mask.TargetX = 3;
        using var moved = Preview(mask, [pattern, mask]);
        Assert.AreEqual(2, moved.Width);
        Assert.AreEqual(1, moved.Height);
        CollectionAssert.AreEqual(new byte[] { 30, 40 }, ((IPicture<byte>)moved).r);
    }

    [TestMethod]
    public void ClipPreviewKeepsTheOffCanvasPartOfTheMaskTransparent()
    {
        using var pattern = Pattern(_ => new Picture8bpp(5, 1) { r = [0, 10, 20, 30, 40] });
        using var mask = Mask(x: -1, width: 3);
        Arguments(mask, "Sigma", 0f);
        using var result = Preview(mask, [pattern, mask]);
        Assert.AreEqual(3, result.Width);
        Assert.AreEqual(1, result.Height);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 10 }, ((IPicture<byte>)result).r);
        CollectionAssert.AreEqual(new float[] { 0, 1, 1 }, ((IPicture<byte>)result).a!);
    }

    [TestMethod]
    public void SourceFieldsAreAdaptedWithoutClipSpecificParameterDefinitions()
    {
        MaskSources.Register("MaskTest", () => new CustomSource());
        using var mask = Mask("MaskTest");
        Assert.IsTrue(mask.ArgumentFields.ContainsKey("Source.Amount"));
        Assert.IsFalse(mask.ArgumentFields.ContainsKey("Source.Sigma"));
        Arguments(mask, "Amount", 7f);
        var copy = (MaskClip)ClipArgumentBinding.InitializeFrame(mask, 0);
        Assert.AreEqual(7f, copy.SourceArguments["Amount"]);
        Assert.AreEqual(0, mask.SourceArguments.Count);
    }

    [TestMethod]
    public void SourceBindingsRoundTripAndRemainFrameLocal()
    {
        using var mask = Mask();
        var provider = Arguments(mask, "Sigma", 3f);
        provider.SetFieldBinding("Source.Sigma", ValueProviderFrameContext.BuiltInFrameProviderId);
        var saved = JsonSerializer.SerializeToElement(EffectBindingHelper.SerializeProvider(provider))
            .Deserialize<EffectProviderJSONStructure>()!;
        var restored = EffectBindingHelper.MigrateToEffectProviders([saved], null).Values.OfType<ClipArgumentProvider>().Single();
        restored.Attach(mask);
        mask.EffectProvidersInstances = [restored];
        Parallel.For(0, 10, i =>
        {
            var copy = (MaskClip)ClipArgumentBinding.InitializeFrame(mask, (uint)i);
            Assert.AreEqual((float)i, copy.SourceArguments["Sigma"]);
        });
        Assert.AreEqual(0, mask.SourceArguments.Count);
    }

    [TestMethod]
    public void MaskExcludesUpperClipsAndIncludesLowerEffects()
    {
        using var pattern = Pattern(_ => Picture8bpp.GenerateSolidColor(5, 1, 10, 0, 0, 1));
        pattern.EffectsInstances = [new InspectEffect(p => Array.Fill(((IPicture<byte>)p).r, (byte)40))];
        using var mask = Mask();
        Arguments(mask, "Sigma", 0f);
        using var upper = new SolidColorClip { Id = Guid.NewGuid(), Name = "upper", LayerIndex = 0,
            Duration = 10, R = 65535, ExtraData = [] };
        using var scope = MaskFrameContext.Open([upper, mask, pattern], 0, 5, 1, 5, 1, 8, default);
        var copy = (MaskClip)ClipArgumentBinding.InitializeFrame(mask, 0);
        using var result = copy.GetUnrotatedFrame(0, 5, 1, 8);
        CollectionAssert.AreEqual(Enumerable.Repeat((byte)40, 5).ToArray(), ((IPicture<byte>)result).r);
    }

    [TestMethod]
    public void RotatingTheRegionDoesNotRotateTheBackdrop()
    {
        var values = Enumerable.Range(0, 16).Select(i => (byte)(i * 10)).ToArray();
        using var pattern = Pattern(_ => new Picture8bpp(4, 4) { r = values.ToArray() }, 4, 4);
        using var mask = Mask(x: 1, width: 2, height: 2, rotation: 90);
        mask.TargetY = 1;
        Arguments(mask, "Sigma", 0f);
        using var result = Render([pattern, mask], 4, 4);
        CollectionAssert.AreEqual(values, ((IPicture<byte>)result).r);
    }

    [TestMethod]
    public void MosaicHandlesPartialBlocksAndHdrBrightnessWithoutMutatingInput()
    {
        using var input = HDRPicture16bpp.GenerateSolidColor(3, 1, 0, 0, 0, 1);
        input.r = [0, 100, 200];
        input.Brightness = [0, 1, 0.25f];
        using var result = new MosaicMaskSource().Process(input, new Dictionary<string, object> { ["BlockSize"] = 2 }, 1, default);
        CollectionAssert.AreEqual(new ushort[] { 50, 50, 200 }, ((IPicture<ushort>)result).r);
        CollectionAssert.AreEqual(new float[] { 0.5f, 0.5f, 0.25f }, ((IHDRPicture<ushort>)result).Brightness);
        CollectionAssert.AreEqual(new ushort[] { 0, 100, 200 }, input.r);
        Assert.AreEqual(input.MaximumBrightness, ((IHDRPicture<ushort>)result).MaximumBrightness);
    }

    [TestMethod]
    public void MultipleMasksUsePreviouslyProcessedLowerContent()
    {
        using var pattern = Pattern(_ => new Picture8bpp(5, 1) { r = [0, 0, 255, 0, 0] });
        using var first = Mask(width: 5);
        Arguments(first, "Sigma", 1f);
        using var second = Mask("Mosaic", layer: 0);
        Arguments(second, "BlockSize", 2);
        using var result = Render([second, first, pattern]);
        CollectionAssert.AreEqual(new byte[] { 42, 42, 85, 85, 0 }, ((IPicture<byte>)result).r);
    }

    [TestMethod]
    public void EmptyBackdropAndOffCanvasMasksRemainTransparent()
    {
        using var mask = Mask(x: -20, width: 2);
        using var result = Render([mask]);
        Assert.IsTrue(((IPicture<byte>)result).a!.All(a => a == 0));
    }

    [TestMethod]
    public void HashAndCacheCapabilityIncludeLowerSources()
    {
        using var pattern = Pattern(_ => new Picture8bpp(5, 1));
        using var mask = Mask();
        string first = Timeline.GetClipFrameHash([pattern, mask], mask, 0);
        pattern.TargetX = 1;
        Assert.AreNotEqual(first, Timeline.GetClipFrameHash([pattern, mask], mask, 0));
        using var changing = new VideoClip { Id = Guid.NewGuid(), Name = "unavailable", LayerIndex = 2,
            Duration = 10, ExtraData = [] };
        Assert.IsFalse(Timeline.CanCacheClipFrame([changing, mask], mask));
    }

    [TestMethod]
    [DataRow(TransformRenderOrder.BeforeEffects)]
    [DataRow(TransformRenderOrder.AfterEffects)]
    public void TransitionMaskSamplesTheOutputTimeInsteadOfTheClampedMaskTime(TransformRenderOrder order)
    {
        using var pattern = Pattern(i => Picture8bpp.GenerateSolidColor(5, 1, (byte)(i * 20), 0, 0, 1));
        using var next = new SolidColorClip { Id = Guid.NewGuid(), Name = "next", LayerIndex = 1,
            StartFrame = 4, Duration = 4, TargetWidth = 5, TargetHeight = 1, A = 0, ExtraData = [] };
        IEffectProvider provider = new CrossfadeTransformProvider();
        TransformProcessing.Configure(provider, TransformSide.Right, TransformInputMode.TwoInput, 4, next.Id, order);
        using var mask = Mask(duration: 4, providers: [EffectBindingHelper.SerializeProvider(provider)]);
        Arguments(mask, "Sigma", 0f);
        mask.EffectsInstances = [new CrossfadeTransform { BindedEffectProvidingSystemID = provider.Id.ToString() }];
        foreach (uint frame in new uint[] { 3, 4 })
        {
            using var result = Render([pattern, mask, next], frame: frame);
            byte expected = (byte)(((IClip)pattern).GetRelativeFrameIndex(frame)!.Value * 20);
            Assert.IsTrue(((IPicture<byte>)result).r.All(r => r == expected));
        }
    }

    [TestMethod]
    public void RendererMaskSamplesTheBackdropWithoutChangingItsSource()
    {
        using var original = new Picture8bpp(5, 1) { r = [0, 0, 255, 0, 0] };
        using var pattern = Pattern(_ => original.Clone());
        using var mask = Mask("Mosaic", x: 1, width: 3);
        var renderer = new Renderer { Clips = [pattern, mask], Duration = 1, TargetWidth = 5, TargetHeight = 1,
            ProjectRelativeWidth = 5, ProjectRelativeHeight = 1, Use16Bit = false, AutoSetupRenderContext = false };
        renderer.PrepareRender(default);
        try
        {
            using var picture = renderer.RenderSpecificFrame(0);
            CollectionAssert.AreEqual(new byte[] { 0, 51, 51, 51, 0 }, ((IPicture<byte>)picture).r);
            CollectionAssert.AreEqual(new byte[] { 0, 0, 255, 0, 0 }, original.r);
            Assert.AreEqual(0, renderer.CurrentFinished);
        }
        finally { Release(renderer); }
    }

    [TestMethod]
    public void SpecificFramesReturnIndependentPictures()
    {
        using var pattern = Pattern(_ => Picture8bpp.GenerateSolidColor(5, 1, 12, 0, 0, 1));
        var renderer = Renderer(pattern);
        try
        {
            using var first = renderer.RenderSpecificFrame(0);
            ((IPicture<byte>)first).r[0] = 99;
            using var second = renderer.RenderSpecificFrame(0);
            Assert.AreEqual((byte)12, ((IPicture<byte>)second).r[0]);
            Assert.AreEqual((byte)99, ((IPicture<byte>)first).r[0]);
            Assert.AreEqual(0, renderer.CurrentFinished);
        }
        finally { Release(renderer); }
    }

    [TestMethod]
    public void RendererPrefersCompatiblePreviewVfdWhenEnabled()
    {
        int calls = 0;
        using var pattern = Pattern(_ => { calls++; return new Picture8bpp(5, 1); });
        string root = Path.Combine(Path.GetTempPath(), "pjfc-mask-" + Guid.NewGuid().ToString("N"));
        var renderer = Renderer(pattern);
        renderer.DynamicPreviewCacheProjectRoot = root;
        renderer.ReuseDynamicPreviewCache = true;
        try
        {
            string directory = Path.Combine(root, "thumbs", "perClip", pattern.Id.ToString(), "dynamic");
            Directory.CreateDirectory(directory);
            using var cached = Picture8bpp.GenerateSolidColor(5, 1, 77, 0, 0, 1);
            cached.SaveToDisk(Path.Combine(directory,
                $"dynamic_v8-mask-vfd_{Timeline.GetClipFrameHash([pattern], pattern, 0)}_5x1_5x1_vfd8.vfd"),
                PictureExtensions.SharedVfdPictureEncoder);
            using var result = renderer.RenderSpecificFrame(0);
            Assert.AreEqual((byte)77, ((IPicture<byte>)result).r[0]);
            Assert.AreEqual(0, calls);
        }
        finally
        {
            Release(renderer);
            Assert.IsTrue(Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static IPicture Preview(IClip clip, IClip[] clips)
        => (IPicture)typeof(MaskClip).Assembly.GetType("projectFrameCut.Render.RPCProtocol.ClipPreviewRenderer")!
            .GetMethod("Render", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [clip, clips, 5, 1, 5, 1, 0u, CancellationToken.None,
                IPicture.PicturePixelMode.BytePicture, false, false])!;

    private static Renderer Renderer(IClip clip)
    {
        var renderer = new Renderer { Clips = [clip], Duration = 1, TargetWidth = 5, TargetHeight = 1,
            ProjectRelativeWidth = 5, ProjectRelativeHeight = 1, Use16Bit = false,
            AutoSetupRenderContext = false };
        renderer.PrepareRender(default);
        return renderer;
    }

    private static void Release(Renderer renderer)
    {
        typeof(Renderer).GetMethod("ReleaseResources", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(renderer, null);
        IRenderContext.Current = null;
    }

    private sealed class CustomSource : MaskSource
    {
        public override bool IsDeterministic => true;

        public override string TypeName => "MaskTest";
        public override IReadOnlyList<EffectArgumentFieldDescriptor> Fields =>
            [new() { Id = "Amount", FieldType = EffectArgumentFieldType.Numeric, DefaultValue = "2" }];
        public override IPicture Process(IPicture backdrop, IReadOnlyDictionary<string, object> arguments, float scale, CancellationToken token)
            => backdrop.Clone();
    }

    private sealed class InspectEffect(Action<IPicture> inspect) : RotationEffect_IPicture, INormalEffect
    {
        IPicture INormalEffect.Render(IPicture source, int width, int height) { inspect(source); return source; }
    }

    private sealed class TestSource(Func<uint, IPicture> generate) : IVirtualVideoSource
    {
        public bool IsDeterministic => false;

        public string TypeName => nameof(TestSource);
        public void Init(int width, int height, int fps, uint duration, IPicture.PicturePixelMode pixelMode) { }
        public IPicture Generate(uint index, bool hasAlpha) => generate(index);
        public void Dispose() { }
    }
}
