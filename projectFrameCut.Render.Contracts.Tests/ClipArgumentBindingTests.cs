using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.Transform;
using projectFrameCut.Shared;
using System.Text.Json;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class ClipArgumentBindingTests
{
    private sealed class CustomClip : SolidColorClip, IClip
    {
        public float Ratio { get; set; } = 0.25f;
        public Action? OnHandle { get; init; }
        public IReadOnlyDictionary<string, ClipArgumentFieldDescriptor> ArgumentFields =>
            new Dictionary<string, ClipArgumentFieldDescriptor>(ClipArgumentHandler.DefaultFields)
            {
                [nameof(Ratio)] = new() { Id = nameof(Ratio), FieldType = EffectArgumentFieldType.Numeric,
                    ReadValue = c => ((CustomClip)c).Ratio },
            };

        public IClip HandleArguments(IReadOnlyDictionary<string, object> arguments)
        {
            OnHandle?.Invoke();
            var result = (CustomClip)ClipArgumentHandler.Handle(this,
                arguments.Where(p => p.Key != nameof(Ratio)).ToDictionary());
            result.Ratio = DynamicParam.Resolve(arguments[nameof(Ratio)], Ratio);
            return result;
        }
    }

    private sealed class ValueSource : IntConstantValueProviderProvider, IEffectProvider
    {
        public new IEffect[] Build() => [new IntConstantValueProviderEffect { Parameters = BuildDynamicParameters() }];
    }

    private static ClipArgumentProvider Attach(IClip clip)
    {
        var provider = new ClipArgumentProvider();
        provider.Attach(clip);
        clip.EffectProvidersInstances = [provider];
        return provider;
    }

    [TestMethod]
    public void StaticOverridesRoundTripWithoutFreezingOriginalValues()
    {
        using var source = new SolidColorClip { Id = Guid.NewGuid(), Name = "clip", TargetX = 3, TargetY = 5, ExtraData = [] };
        var provider = Attach(source);
        provider.Fields = new() { [nameof(IClip.TargetX)] = new StaticEffectArgumentField(12, EffectArgumentFieldType.Integer) };
        var json = JsonSerializer.SerializeToElement(EffectBindingHelper.SerializeProvider(provider)).Deserialize<EffectProviderJSONStructure>()!;
        Assert.AreEqual(1, json.StaticFields!.Count);
        var restored = EffectBindingHelper.MigrateToEffectProviders([json], null).Values.OfType<ClipArgumentProvider>().Single();
        restored.Attach(source);
        source.EffectProvidersInstances = [restored];
        source.TargetY = 9;
        var frame = ClipArgumentBinding.InitializeFrame(source, 0);
        Assert.AreEqual(12, frame.TargetX);
        Assert.AreEqual(9, frame.TargetY);
        Assert.AreEqual(3, source.TargetX);
        Assert.IsInstanceOfType<SolidColorClip>(frame);
        Assert.IsInstanceOfType<IImmutableContentClip>(frame);
    }

    [TestMethod]
    public void DynamicFieldsRestoreFrameContextAndUnbindToStaticFallback()
    {
        using var source = new CustomClip { Id = Guid.NewGuid(), Name = "clip", StartFrame = 100, Duration = 10,
            TargetX = 4, ExtraData = [] };
        var provider = Attach(source);
        provider.SetFieldBinding(nameof(IClip.TargetX), ValueProviderFrameContext.BuiltInFrameProviderId);
        provider.SetFieldBinding(nameof(CustomClip.Ratio), ValueProviderFrameContext.BuiltInProgressProviderId);
        using var context = ValueProviderFrameContext.PushFrame(999, 0.1f);
        var first = (CustomClip)ClipArgumentBinding.InitializeFrame(source, 105);
        Assert.AreEqual(105, first.TargetX);
        Assert.AreEqual(0.5f, first.Ratio);
        Assert.AreEqual(999f, ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInFrameProviderId));
        provider.ClearFieldBinding(nameof(IClip.TargetX));
        source.TargetX = 7;
        Assert.AreEqual(7, ClipArgumentBinding.InitializeFrame(source, 106).TargetX);
        Assert.AreEqual(0.25f, source.Ratio);
    }

    [TestMethod]
    public void ParallelFramesUseOneCopyPerFrame()
    {
        int calls = 0;
        using var source = new CustomClip { Id = Guid.NewGuid(), Name = "clip", Duration = 100, ExtraData = [],
            OnHandle = () => Interlocked.Increment(ref calls) };
        var provider = Attach(source);
        provider.SetFieldBinding(nameof(IClip.TargetX), ValueProviderFrameContext.BuiltInFrameProviderId);
        var cache = new ClipArgumentFrameCache();
        Parallel.For(0, 1000, n => Assert.AreEqual(n % 10, cache.Get(source, (uint)(n % 10)).TargetX));
        Assert.AreEqual(10, calls);
        Assert.AreSame(cache.Get(source, 3), cache.Get(source, 3));
        Assert.AreEqual(0, source.TargetX);
        cache.RemoveFrame(3);
        cache.Get(source, 3);
        Assert.AreEqual(11, calls);
    }

    [TestMethod]
    public void CustomBindingsSurviveRestorationBeforeOwnerIsAvailable()
    {
        using var source = new CustomClip { Id = Guid.NewGuid(), Name = "clip", Duration = 10, ExtraData = [] };
        var provider = Attach(source);
        provider.SetFieldBinding(nameof(CustomClip.Ratio), ValueProviderFrameContext.BuiltInProgressProviderId);
        var providers = EffectBindingHelper.MigrateToEffectProviders([EffectBindingHelper.SerializeProvider(provider)], null, out var diagnostics);
        Assert.AreEqual(0, diagnostics.Count);
        var restored = providers.Values.OfType<ClipArgumentProvider>().Single();
        restored.Attach(source);
        source.EffectProvidersInstances = [restored];
        Assert.AreEqual(0.7f, ((CustomClip)ClipArgumentBinding.InitializeFrame(source, 7)).Ratio);
    }

    [TestMethod]
    public void ValueProviderChainsAreInlinedWithoutCreatingPictureEffects()
    {
        using var source = new SolidColorClip { Id = Guid.NewGuid(), Name = "clip", Duration = 20, ExtraData = [] };
        var arguments = Attach(source);
        IEffectProvider a = new ValueSource();
        IEffectProvider b = new ValueSource();
        a.SetFieldBinding("Value", ValueProviderFrameContext.BuiltInFrameProviderId);
        b.SetFieldBinding("Value", a.Id.ToString());
        arguments.SetFieldBinding(nameof(IClip.TargetX), b.Id.ToString());
        var providers = new Dictionary<Guid, IEffectProvider> { [arguments.Id] = arguments, [b.Id] = b, [a.Id] = a };
        Assert.AreEqual(0, EffectBindingHelper.RebuildAllEffects(providers, null)!.Count);
        Assert.AreEqual(8, ClipArgumentBinding.InitializeFrame(source, 8).TargetX);
        Assert.AreEqual(0, EffectBindingHelper.SerializeProvider(arguments).StaticFields!.Count);
        Assert.ThrowsExactly<InvalidOperationException>(() => arguments.SetFinalOutputSource(true));
        Assert.IsFalse(arguments.HasMainPictureInput());
    }

    [TestMethod]
    public void DynamicSizeIsAppliedBeforeSourceReading()
    {
        var arguments = new ClipArgumentProvider();
        arguments.RestoreStaticValues(new Dictionary<string, object> { [nameof(IClip.TargetHeight)] = 2 });
        arguments.AnchorsBindingState[nameof(IClip.TargetWidth)] = ValueProviderFrameContext.BuiltInFrameProviderId;
        using var source = new SolidColorClip { Id = Guid.NewGuid(), Name = "clip", Duration = 20, ExtraData = [],
            EffectProviders = [EffectBindingHelper.SerializeProvider(arguments)] };
        var frames = Timeline.GetFramesInOneFrame([source], 6, 16, 16, 8, 16, 16).ToArray();
        Assert.AreEqual(1, frames.Length);
        Assert.AreEqual(6, frames[0].Clip.Width);
        Assert.AreEqual(2, frames[0].Clip.Height);
        Assert.AreEqual(6, frames[0].ParentClip.TargetWidth);
        Assert.AreEqual(0, source.TargetWidth);
        frames[0].Clip.Dispose();
    }

    [TestMethod]
    public void TransformInputsEvaluateTheirArgumentsOnceForBothOrders()
    {
        foreach (var order in new[] { TransformRenderOrder.BeforeEffects, TransformRenderOrder.AfterEffects })
        {
            var calls = new int[2];
            var leftId = Guid.NewGuid();
            var rightId = Guid.NewGuid();
            IEffectProvider transform = new CrossfadeTransformProvider();
            TransformProcessing.Configure(transform, TransformSide.Right, TransformInputMode.TwoInput, 4, rightId, order);
            using var left = new CustomClip { Id = leftId, Name = "left", Duration = 4, ExtraData = [],
                EffectProviders = [EffectBindingHelper.SerializeProvider(transform)], OnHandle = () => calls[0]++ };
            using var right = new CustomClip { Id = rightId, Name = "right", StartFrame = 4, Duration = 4, ExtraData = [],
                OnHandle = () => calls[1]++ };
            Attach(left).SetFieldBinding(nameof(IClip.TargetWidth), ValueProviderFrameContext.BuiltInFrameProviderId);
            Attach(right);
            left.EffectsInstances = [new CrossfadeTransform { BindedEffectProvidingSystemID = transform.Id.ToString() }];
            Assert.IsTrue(TransformProcessing.TryRender(left, [left, right], 3, 4, 4, 4, 4,
                IPicture.PicturePixelMode.BytePicture, out var result, initializeClips: false));
            result!.Dispose();
            CollectionAssert.AreEqual(new[] { 1, 1 }, calls);
            Assert.AreEqual(0, left.TargetWidth);
        }
    }
}
