using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.Transform;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Render.VectorContent.Components;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class VectorEffectPipelineTests
{
    private sealed class VectorProvider : EffectProviderBase
    {
        public override string TypeName => "TestVectorPicture";
        public override string FromPlugin => "test";
        public override EffectType TypeOfEffect => EffectType.VectorPictureEffect;
        public override EffectTarget Target => EffectTarget.VectorPicture;
        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => [];
    }

    private sealed class VectorEffect : IVectorPictureEffect
    {
        public string FromPlugin => "test";
        public string TypeName => "TestVectorPicture";
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "TestVectorPicture";
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string? BindedEffectProvidingSystemID { get; set; }
        public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
        public bool IsReorderable => true;
        public Dictionary<string, object> Parameters { get; set; } = [];
        public Func<VectorPicture, float, VectorPicture> Apply { get; init; } = (source, _) => source;
        public IEffect WithParameters(Dictionary<string, object> parameters) => new VectorEffect { Apply = Apply, Parameters = parameters };
        public VectorPicture Process(VectorPicture source, float progress) => Apply(source, progress);
    }

    private sealed class TextEffect(Action called) : ITextEffect
    {
        public string FromPlugin => "test";
        public string TypeName => "TestText";
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "TestText";
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string? BindedEffectProvidingSystemID { get; set; }
        public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
        public bool IsReorderable => true;
        public Dictionary<string, object> Parameters { get; set; } = [];
        public IEffect WithParameters(Dictionary<string, object> parameters) => new TextEffect(called) { Parameters = parameters };
        public TextEntry[] Process(TextEntry[] source) { called(); return source; }
    }

    private sealed class FixedPosition : ProgressPlacer, IContinuousClipPositionProvider
    {
        ClipPositionTuple IContinuousClipPositionProvider.GetPosition(IClip clip, uint frame, int width, int height)
            => new(2, 0, 2, 2, false);
        bool IContinuousClipPositionProvider.PreserveAspectRatio => false;
    }

    [TestMethod]
    public void AddingAndClearingPictureOutputPreservesNativeOutput()
    {
        var native = new TextFadeInEffectProvider();
        var picture = new CropEffectProvider();
        var providers = new Dictionary<Guid, IEffectProvider> { [native.Id] = native, [picture.Id] = picture };
        EffectBindingHelper.AutoConnectProviderToOutput(providers, native, EffectTarget.Text | EffectTarget.Video);
        EffectBindingHelper.AutoConnectProviderToOutput(providers, picture, EffectTarget.Text | EffectTarget.Video);
        Assert.IsTrue(native.IsFinalOutputSource());
        Assert.IsTrue(picture.IsFinalOutputSource());
        Assert.AreEqual(0, EffectBindingHelper.ValidateBindings(providers).Count);
        CollectionAssert.AreEquivalent(new[] { native.Id, picture.Id }, EffectBindingHelper.GetActivePictureProviderIds(providers).ToArray());
        EffectBindingHelper.SetFinalOutput(providers, null);
        Assert.IsTrue(native.IsFinalOutputSource());
        Assert.IsFalse(picture.IsFinalOutputSource());
    }

    [TestMethod]
    public void LegacyMixedChainSplitsWithoutActivatingDisconnectedNodes()
    {
        var text = new TextFadeInEffectProvider { Enabled = false };
        var crop = new CropEffectProvider();
        var other = new TextFadeInEffectProvider();
        text.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        crop.SetMainInputSource(text.Id);
        crop.SetFinalOutputSource(true);
        var providers = new Dictionary<Guid, IEffectProvider> { [text.Id] = text, [crop.Id] = crop, [other.Id] = other };
        Assert.AreEqual(0, EffectBindingHelper.NormalizeStoredBindings(providers).Count);
        Assert.AreEqual(IEffectProvider.InputAnchorGUID.ToString(), crop.GetMainInputSource());
        Assert.IsTrue(text.IsFinalOutputSource());
        Assert.IsTrue(crop.IsFinalOutputSource());
        Assert.IsFalse(text.Enabled);
        Assert.IsFalse(EffectBindingHelper.GetActivePictureProviderIds(providers).Contains(other.Id));
        Assert.AreEqual(0, EffectBindingHelper.ValidateBindings(providers).Count);
        var before = providers.Values.Select(EffectBindingHelper.SerializeProvider).Select(p => System.Text.Json.JsonSerializer.Serialize(p)).ToArray();
        EffectBindingHelper.NormalizeStoredBindings(providers);
        CollectionAssert.AreEqual(before, providers.Values.Select(EffectBindingHelper.SerializeProvider).Select(p => System.Text.Json.JsonSerializer.Serialize(p)).ToArray());
    }

    [TestMethod]
    public void GraphRejectsCrossPipelineAndBackwardsNativeConnections()
    {
        var text = new TextFadeInEffectProvider();
        var vector = new VectorProvider();
        var picture = new CropEffectProvider();
        Assert.IsTrue(text.CanConnectContent(vector));
        Assert.IsFalse(vector.CanConnectContent(text));
        Assert.IsFalse(vector.CanConnectContent(picture));
        picture.SetMainInputSource(text.Id);
        var providers = new Dictionary<Guid, IEffectProvider> { [text.Id] = text, [picture.Id] = picture };
        Assert.IsTrue(EffectBindingHelper.ValidateBindings(providers).Any(d => d.Code == "IncompatibleContentInput"));
    }

    [TestMethod]
    public void SpecializedEffectIsInsertedBeforeVectorPictureStage()
    {
        var vector = new VectorProvider();
        var text = new TextFadeInEffectProvider();
        var providers = new Dictionary<Guid, IEffectProvider> { [vector.Id] = vector, [text.Id] = text };
        EffectBindingHelper.AutoConnectProviderToOutput(providers, vector, EffectTarget.VectorPicture);
        EffectBindingHelper.AutoConnectProviderToOutput(providers, text, EffectTarget.Text);
        Assert.AreEqual(text.Id.ToString(), vector.GetMainInputSource());
        Assert.IsTrue(vector.IsFinalOutputSource());
        Assert.IsFalse(text.IsFinalOutputSource());
        Assert.AreEqual(0, EffectBindingHelper.ValidateBindings(providers).Count);
    }

    [TestMethod]
    public void ImmutableVectorEffectsEvaluateEachFrameWithoutMutatingSource()
    {
        var element = new RectangleComponent().Compute();
        var original = element.RelativeX;
        var calls = 0;
        var effect = new VectorEffect
        {
            Apply = (source, progress) => { calls++; source.Elements[0].RelativeX += progress; return source; }
        };
        using var clip = new VectorPhotoClip
        {
            Id = Guid.NewGuid(), Name = "svg", Duration = 11,
            Picture = new VectorPicture { Elements = [element] }, EffectsInstances = [effect]
        };
        var vector = (IVectorContentClip)clip;
        var first = vector.GetVectorPictureRelativeToStartPointOfSource(0, 32, 32);
        var last = vector.GetVectorPictureRelativeToStartPointOfSource(10, 32, 32);
        Assert.AreEqual(original, first.Elements[0].RelativeX);
        Assert.AreEqual(original + 1, last.Elements[0].RelativeX);
        Assert.AreEqual(original, element.RelativeX);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public void TextEffectsEvaluateOnceAndVectorEffectsCanFillEmptyText()
    {
        var calls = 0;
        using var clip = new TextClip
        {
            Id = Guid.NewGuid(), Name = "text", Duration = 11, ExtraData = [],
            EffectsInstances = [new TextEffect(() => calls++), new VectorEffect
            {
                Apply = (source, _) => { source.Elements.Add(new RectangleComponent().Compute()); return source; }
            }]
        };
        var result = clip.GetVectorPictureRelativeToStartPointOfSource(5, 32, 32);
        Assert.AreEqual(1, calls);
        Assert.AreEqual(1, result.Elements.Count);
    }

    [TestMethod]
    public void NativeEffectsHonorEnabledOrderAndRestoreFrameContext()
    {
        var calls = new List<int>();
        using var clip = new VectorCanvasClip { Id = Guid.NewGuid(), Name = "vector", Duration = 11 };
        clip.EffectsInstances = [
            new VectorEffect { Index = 2, Apply = (s, _) => { calls.Add(2); return s; } },
            new VectorEffect { Index = 0, Enabled = false, Apply = (s, _) => throw new InvalidOperationException() },
            new VectorEffect { Index = 1, Apply = (s, p) =>
            {
                Assert.AreEqual(0.5f, p);
                Assert.AreEqual(5f, ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInFrameProviderId));
                calls.Add(1); return s;
            } }
        ];
        using var scope = ValueProviderFrameContext.PushFrame(99, 0.2f);
        clip.GetVectorPictureRelativeToStartPointOfSource(5, 32, 32);
        CollectionAssert.AreEqual(new[] { 1, 2 }, calls);
        Assert.AreEqual(99f, ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInFrameProviderId));
    }

    [TestMethod]
    public void RasterPipelineAppliesOpacityAndPositionWithoutReevaluatingNativeEffects()
    {
        using var clip = new VectorCanvasClip { Id = Guid.NewGuid(), Name = "vector", Duration = 5 };
        clip.EffectsInstances = [new VectorEffect { Apply = (_, _) => throw new InvalidOperationException("Native effect ran after rasterization.") },
            new FadeOpacityEffect_IPicture { Opacity = 0.5f }, new FixedPosition { Index = 1 }];
        using var source = Picture8bpp.GenerateSolidColor(2, 2, 255, 0, 0, 1);
        var frame = new OneFrame(2, clip, source, resolveEffects: false);
        Assert.AreEqual(2, frame.Effects.Length);
        using var output = Timeline.MixtureLayers([frame], 2, 4, 2, 8, transparentBackground: true);
        var pixels = (IPicture<byte>)output;
        Assert.AreEqual(0f, pixels.a![0], 0.001f);
        Assert.AreEqual(0.5f, pixels.a![2], 0.001f);
    }

    [TestMethod]
    public void DualInputTransformEvaluatesBothNativePipelinesOnceForEitherOrder()
    {
        foreach (var order in new[] { TransformRenderOrder.BeforeEffects, TransformRenderOrder.AfterEffects })
        {
            var calls = new int[2];
            var leftId = Guid.NewGuid();
            var rightId = Guid.NewGuid();
            IEffectProvider provider = new CrossfadeTransformProvider();
            TransformProcessing.Configure(provider, TransformSide.Right, TransformInputMode.TwoInput, 4, rightId, order);
            using var left = new VectorCanvasClip { Id = leftId, Name = "left", Duration = 4,
                EffectProviders = [EffectBindingHelper.SerializeProvider(provider)] };
            using var right = new VectorCanvasClip { Id = rightId, Name = "right", StartFrame = 4, Duration = 4 };
            left.EffectsInstances = [new VectorEffect { Apply = (s, _) => { calls[0]++; return s; } },
                new CrossfadeTransform { BindedEffectProvidingSystemID = provider.Id.ToString() }];
            right.EffectsInstances = [new VectorEffect { Apply = (s, _) => { calls[1]++; return s; } }];
            Assert.IsTrue(TransformProcessing.TryRender(left, [left, right], 3, 4, 4, 4, 4,
                IPicture.PicturePixelMode.BytePicture, out var result, initializeClips: false));
            result!.Dispose();
            CollectionAssert.AreEqual(new[] { 1, 1 }, calls);
        }
    }
}
