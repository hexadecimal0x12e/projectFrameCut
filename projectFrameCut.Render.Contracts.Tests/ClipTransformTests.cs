using System.Text.Json;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.Transform;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class ClipTransformTests
{
    private static TransformClipInfo Clip(uint start, uint duration) => new(Guid.NewGuid(), start, duration, 0, 0, new List<EffectProviderJSONStructure>());

    private static EffectProviderJSONStructure Attach(TransformClipInfo owner, TransformSide side, uint duration,
        Guid next = default, TransformRenderOrder order = TransformRenderOrder.AfterEffects)
    {
        IEffectProvider provider = next == Guid.Empty ? new FadeTransformProvider() : new CrossfadeTransformProvider();
        TransformProcessing.Configure(provider, side, next == Guid.Empty ? TransformInputMode.OneInput : TransformInputMode.TwoInput, duration, next, order);
        var snapshot = EffectBindingHelper.SerializeProvider(provider);
        ((List<EffectProviderJSONStructure>)owner.Providers).Add(snapshot);
        return snapshot;
    }

    [TestMethod]
    public void ConnectedEdgesShareIdentityAndSplitOddDuration()
    {
        var left = Clip(0, 20);
        var right = Clip(20, 20);
        Attach(left, TransformSide.Right, 5, right.Id, TransformRenderOrder.BeforeEffects);
        var a = TransformProcessing.Resolve([left, right], left.Id, TransformSide.Right)!;
        var b = TransformProcessing.Resolve([left, right], right.Id, TransformSide.Left)!;
        Assert.AreSame(a.Provider, b.Provider);
        Assert.AreEqual(18ul, a.Start);
        Assert.AreEqual(5u, a.Duration);
        Assert.IsTrue(a.Contains(22));
        Assert.IsFalse(a.Contains(23));
        Assert.AreEqual(0, right.Providers.Count);
        Assert.AreEqual(TransformRenderOrder.BeforeEffects, TransformProcessing.ReadEnum<TransformRenderOrder>(b.Provider.MetaData, TransformProcessing.OrderKey));
    }

    [TestMethod]
    public void DisconnectionRetainsConfigurationWithoutAdoptingNewNeighbor()
    {
        var left = Clip(0, 10);
        var right = Clip(10, 10);
        var provider = Attach(left, TransformSide.Right, 4, right.Id);
        var moved = right with { Start = 11 };
        var replacement = Clip(10, 10);
        Assert.IsNull(TransformProcessing.Resolve([left, moved, replacement], left.Id, TransformSide.Right));
        Assert.AreEqual(provider.Id, TransformProcessing.Find([left, moved], moved.Id, TransformSide.Left)!.Value.Provider.Id);
        Assert.IsNotNull(TransformProcessing.Resolve([left, right], left.Id, TransformSide.Right));
        Assert.IsNull(TransformProcessing.Resolve([left, right with { Layer = 1 }], left.Id, TransformSide.Right));
        Assert.IsNull(TransformProcessing.Resolve([left, right with { SubLayer = 1 }], left.Id, TransformSide.Right));
    }

    [TestMethod]
    public void DisabledConfigurationStillOccupiesBothEdges()
    {
        var left = Clip(0, 10);
        var right = Clip(10, 10);
        var provider = Attach(left, TransformSide.Right, 4, right.Id);
        provider.Enabled = false;
        Assert.IsNull(TransformProcessing.Resolve([left, right], right.Id, TransformSide.Left));
        Assert.AreSame(provider, TransformProcessing.Find([left, right], right.Id, TransformSide.Left)!.Value.Provider);
        Attach(right, TransformSide.Left, 1);
        Assert.ThrowsExactly<InvalidOperationException>(() => new TransformProcessing.Index([left, right]));
    }

    [TestMethod]
    public void ShortClipEdgeRangesNeverOverlap()
    {
        for (uint duration = 1; duration < 12; duration++)
        {
            for (uint leading = 1; leading < 15; leading++)
            {
                for (uint trailing = 1; trailing < 15; trailing++)
                {
                    var clip = Clip(0, duration);
                    Attach(clip, TransformSide.Left, leading);
                    Attach(clip, TransformSide.Right, trailing);
                    var l = TransformProcessing.Resolve([clip], clip.Id, TransformSide.Left)!;
                    var r = TransformProcessing.Resolve([clip], clip.Id, TransformSide.Right)!;
                    Assert.IsTrue(l.Start + l.Duration <= r.Start);
                }
            }
        }
        var previous = Clip(0, 3);
        var next = Clip(3, 1);
        Attach(previous, TransformSide.Right, 100, next.Id);
        var shared = TransformProcessing.Resolve([previous, next], next.Id, TransformSide.Left)!;
        Assert.AreEqual(2u, shared.Duration);
    }

    [TestMethod]
    public void CopyPreservesIndependentSingleInputAndDropsDualAssociation()
    {
        var clip = Clip(0, 10);
        var next = Clip(10, 10);
        var single = Attach(clip, TransformSide.Left, 3, order: TransformRenderOrder.BeforeEffects);
        Attach(clip, TransformSide.Right, 4, next.Id);
        var copies = TransformProcessing.CopySingleInputs(clip.Providers.ToArray())!;
        Assert.AreEqual(1, copies.Length);
        Assert.AreNotEqual(single.Id, copies[0].Id);
        Assert.AreEqual(TransformRenderOrder.BeforeEffects, TransformProcessing.ReadEnum<TransformRenderOrder>(copies[0].MetaData, TransformProcessing.OrderKey));
        copies[0].MetaData![TransformProcessing.DurationKey] = 1u;
        Assert.AreEqual(3u, TransformProcessing.ReadDuration(single.MetaData));
    }

    [TestMethod]
    public void ProviderJsonPreservesBindingsImplementationAndStaticConfiguration()
    {
        var provider = new ExternalSourceTransformProvider();
        TransformProcessing.Configure(provider, TransformSide.Right, TransformInputMode.TwoInput, 5, Guid.NewGuid(), TransformRenderOrder.BeforeEffects, true);
        provider.MetaData[EffectProviderBase.ImplementTypeParameterKey] = EffectImplementType.IPicture;
        provider.Fields = new() { ["SourcePath"] = new StaticEffectArgumentField("generated.mp4", EffectArgumentFieldType.String) };
        provider.AnchorsBindingState["SourcePath"] = Guid.NewGuid().ToString();
        var saved = EffectBindingHelper.SerializeProvider(provider);
        var loaded = JsonSerializer.SerializeToElement(saved).Deserialize<EffectProviderJSONStructure>()!;
        Assert.AreEqual(saved.Id, loaded.Id);
        Assert.AreEqual(saved.AnchorsBindingState["SourcePath"], loaded.AnchorsBindingState["SourcePath"]);
        Assert.AreEqual("generated.mp4", loaded.StaticFields["SourcePath"].ToString());
        Assert.AreEqual(5u, TransformProcessing.ReadDuration(loaded.MetaData));
        Assert.AreEqual(TransformProcessing.ReadNextClip(saved.MetaData), TransformProcessing.ReadNextClip(loaded.MetaData));
        Assert.AreEqual(TransformRenderOrder.BeforeEffects, TransformProcessing.ReadEnum<TransformRenderOrder>(loaded.MetaData, TransformProcessing.OrderKey));
        Assert.AreEqual(EffectImplementType.IPicture, TransformProcessing.ReadEnum<EffectImplementType>(loaded.MetaData, EffectProviderBase.ImplementTypeParameterKey));
        Assert.IsTrue(TransformProcessing.IsAI(loaded.MetaData));
    }

    [TestMethod]
    public void FadeHonorsSideAndKeepsInputAlive()
    {
        using var input = Picture8bpp.GenerateSolidColor(2, 2, 255, 0, 0, 1);
        var fade = new FadeTransform();
        using var entering = TransformProcessing.ProcessFrames(input, null, fade, TransformInputMode.OneInput, 0, TransformSide.Left, 2, 2);
        using var leaving = TransformProcessing.ProcessFrames(input, null, fade, TransformInputMode.OneInput, 0, TransformSide.Right, 2, 2);
        Assert.AreEqual(0f, ((IPicture<byte>)entering).a![0]);
        Assert.AreEqual(1f, ((IPicture<byte>)leaving).a![0]);
        Assert.AreEqual(1f, input.a![0]);
        Assert.ThrowsExactly<NotSupportedException>(() => TransformProcessing.ProcessFrames(input, input, fade, TransformInputMode.TwoInput, 0, TransformSide.Right, 2, 2));
        Assert.IsFalse(((IEffect)fade).IsReorderable);
        Assert.AreEqual(EffectType.Transform, ((IEffect)fade).TypeOfEffect);
    }

    private sealed class DisposableTransform : FadeTransform, IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private sealed class PassThroughTransform : FadeTransform
    {
        public override IPicture Render(IPicture left, IPicture? right, float progress, TransformSide side, int width, int height) => left;
    }

    private sealed class SizeTrackingCrossfade : CrossfadeTransform
    {
        public List<int> Widths { get; } = [];
        public override IPicture Render(IPicture left, IPicture? right, float progress, TransformSide side, int width, int height)
        {
            Assert.AreEqual(left.Width, right!.Width);
            Assert.AreEqual(width, left.Width);
            Widths.Add(width);
            return base.Render(left, right, progress, side, width, height);
        }
    }

    [TestMethod]
    public void BeforeEffectsAlignsInputsToCurrentMaterialsActualSize()
    {
        var info = Clip(0, 10);
        var next = Clip(10, 10);
        var provider = Attach(info, TransformSide.Right, 5, next.Id, TransformRenderOrder.BeforeEffects);
        using var left = new SolidColorClip { Id = info.Id, Name = "left", Duration = 10, OutputWidth = 3, OutputHeight = 1,
            ExtraData = new(), EffectProviders = [provider] };
        using var right = new SolidColorClip { Id = next.Id, Name = "right", StartFrame = 10, Duration = 10,
            OutputWidth = 2, OutputHeight = 1, ExtraData = new() };
        var transform = new SizeTrackingCrossfade { BindedEffectProvidingSystemID = provider.Id.ToString() };
        left.EffectsInstances = [transform];
        right.EffectsInstances = [];
        Assert.IsTrue(TransformProcessing.TryRender(left, [left, right], 9, 1, 1, 1, 1, IPicture.PicturePixelMode.BytePicture,
            out var preceding, initializeClips: false));
        preceding!.Dispose();
        Assert.IsTrue(TransformProcessing.TryRender(right, [left, right], 10, 1, 1, 1, 1, IPicture.PicturePixelMode.BytePicture,
            out var following, initializeClips: false));
        following!.Dispose();
        CollectionAssert.AreEqual(new[] { 3, 2 }, transform.Widths);
    }

    [TestMethod]
    public void PreparedHdrAndAliasedOutputRetainBrightnessAndBufferOwnership()
    {
        var info = Clip(0, 5);
        var provider = Attach(info, TransformSide.Left, 5, order: TransformRenderOrder.BeforeEffects);
        using var clip = new SolidColorClip { Id = info.Id, Name = "hdr", Duration = 5, ExtraData = new(), EffectProviders = [provider] };
        clip.EffectsInstances = [new PassThroughTransform { BindedEffectProvidingSystemID = provider.Id.ToString() }];
        using var input = HDRPicture16bpp.GenerateSolidColor(2, 2, 30000, 0, 0, 1, 0.5f, 1000);
        Assert.IsTrue(TransformProcessing.TryRender(clip, [clip], 2, 2, 2, 2, 2, IPicture.PicturePixelMode.UShortPicture,
            out var result, initializeClips: false, preparedSource: input));
        using (result)
        {
            var hdr = (IHDRPicture<ushort>)result!;
            Assert.AreEqual(1000f, hdr.MaximumBrightness);
            Assert.AreEqual(0.5f, hdr.Brightness[0], 0.00001f);
            hdr.r[0] = 0;
            Assert.AreEqual((ushort)30000, input.r[0]);
        }
        Assert.IsFalse(input.Disposed);
    }

    private sealed class DisposableTransformProvider : FadeTransformProvider, IEffectProvider
    {
        IEffect[] IEffectProvider.Build() => [new DisposableTransform()];
    }

    [TestMethod]
    public void RebuildDisableRemoveAndClipExitReleaseTransformInstances()
    {
        IEffectProvider provider = new DisposableTransformProvider();
        TransformProcessing.Configure(provider, TransformSide.Left, TransformInputMode.OneInput, 2, Guid.Empty, TransformRenderOrder.AfterEffects);
        var providers = new Dictionary<Guid, IEffectProvider> { [provider.Id] = provider };
        var first = EffectBindingHelper.RebuildAllEffects(providers, null)!;
        var a = (DisposableTransform)first.Values.Single();
        Assert.IsTrue(a.Enabled);
        var second = EffectBindingHelper.RebuildAllEffects(providers, first)!;
        Assert.AreEqual(1, a.DisposeCount);
        var b = (DisposableTransform)second.Values.Single();
        provider.Enabled = false;
        var disabled = EffectBindingHelper.RebuildAllEffects(providers, second)!;
        Assert.AreEqual(0, disabled.Count);
        Assert.AreEqual(1, b.DisposeCount);
        provider.Enabled = true;
        var enabled = EffectBindingHelper.RebuildAllEffects(providers, disabled)!;
        var c = (DisposableTransform)enabled.Values.Single();
        providers.Clear();
        Assert.AreEqual(0, EffectBindingHelper.RebuildAllEffects(providers, enabled)!.Count);
        Assert.AreEqual(1, c.DisposeCount);
        var exiting = new DisposableTransform();
        using var clip = new SolidColorClip { Id = Guid.NewGuid(), Name = "exit", ExtraData = new() };
        clip.EffectsInstances = [exiting];
        EffectHelper.ReleaseClipEffects(clip);
        Assert.AreEqual(1, exiting.DisposeCount);
        Assert.AreEqual(0, clip.EffectsInstances.Length);
    }

    [TestMethod]
    public void NestedFrameScopesRestoreOuterParameterValues()
    {
        using var outer = ValueProviderFrameContext.PushFrame(100, 0.25f);
        ValueProviderFrameContext.Set("custom", 42);
        using (ValueProviderFrameContext.PushFrame(5, 0.5f))
        {
            Assert.AreEqual(5f, ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInFrameProviderId));
            Assert.IsNull(ValueProviderFrameContext.Get("custom"));
        }
        Assert.AreEqual(100f, ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInFrameProviderId));
        Assert.AreEqual(42, ValueProviderFrameContext.Get("custom"));
    }

    [TestMethod]
    public void CrossfadeWeightsColorAndHdrBrightnessByAlpha()
    {
        using var left = HDRPicture16bpp.GenerateSolidColor(2, 2, ushort.MaxValue, 0, 0, 1, 0.5f, 1000);
        using var right = HDRPicture16bpp.GenerateSolidColor(2, 2, 0, 0, 0, 0, 0.1f, 1000);
        using var result = new CrossfadeTransform().Render(left, right, 0.5f, TransformSide.Right, 2, 2);
        var hdr = (IHDRPicture<ushort>)result;
        Assert.AreEqual(ushort.MaxValue, hdr.r[0]);
        Assert.AreEqual(0.5f, hdr.a![0]);
        Assert.AreEqual(0.5f, hdr.Brightness[0]);
        Assert.AreEqual(1000f, hdr.MaximumBrightness);
        using var l8 = Picture8bpp.GenerateSolidColor(1, 1, 200, 0, 0, 0.25f);
        using var r8 = Picture8bpp.GenerateSolidColor(1, 1, 0, 100, 0, 0.75f);
        using var mixed = new CrossfadeTransform().Render(l8, r8, 0.5f, TransformSide.Right, 1, 1);
        Assert.AreEqual((byte)50, ((IPicture<byte>)mixed).r[0]);
        Assert.AreEqual((byte)75, ((IPicture<byte>)mixed).g[0]);
        Assert.AreEqual(0.5f, ((IPicture<byte>)mixed).a![0]);
    }

    private sealed class SquareEffect : INormalEffect
    {
        public int Calls { get; private set; }
        public string FromPlugin => "test";
        public string TypeName => "Square";
        public EffectImplementType ImplementType => EffectImplementType.IPicture;
        public string Name { get; set; } = "Square";
        public string Id { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public bool IsReorderable => true;
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string? BindedEffectProvidingSystemID { get; set; }
        public Dictionary<string, object> Parameters { get; } = [];
        public IEffect WithParameters(Dictionary<string, object> parameters) => new SquareEffect();
        public IPicture Render(IPicture source, int width, int height)
        {
            Calls++;
            var result = (IPicture<byte>)source.Clone();
            for (int i = 0; i < result.Pixels; i++) result.r[i] = (byte)Math.Round(result.r[i] * result.r[i] / 255f);
            return result;
        }
    }

    [TestMethod]
    public void RenderOrderUsesCurrentClipsEffectsOnBothSidesOfSeam()
    {
        var info = Clip(0, 10);
        var next = Clip(10, 10);
        var provider = Attach(info, TransformSide.Right, 5, next.Id);
        using var left = new SolidColorClip { Id = info.Id, Name = "left", Duration = 10, R = ushort.MaxValue,
            UseFixedOutputSize = false, ExtraData = new(), EffectProviders = [provider] };
        using var right = new SolidColorClip { Id = next.Id, Name = "right", StartFrame = 10, Duration = 10,
            UseFixedOutputSize = false, ExtraData = new() };
        var l = new SquareEffect();
        var r = new SquareEffect();
        left.EffectsInstances = [new CrossfadeTransform { BindedEffectProvidingSystemID = provider.Id.ToString() }, l];
        right.EffectsInstances = [r];
        Assert.IsTrue(TransformProcessing.TryRender(right, [left, right], 10, 1, 1, 1, 1, IPicture.PicturePixelMode.BytePicture, out var after, initializeClips: false));
        using (after) Assert.AreEqual((byte)128, ((IPicture<byte>)after!).r[0]);
        Assert.AreEqual(1, l.Calls);
        Assert.AreEqual(1, r.Calls);
        provider.MetaData![TransformProcessing.OrderKey] = TransformRenderOrder.BeforeEffects;
        Assert.IsTrue(TransformProcessing.TryRender(right, [left, right], 10, 1, 1, 1, 1, IPicture.PicturePixelMode.BytePicture, out var before, initializeClips: false));
        using (before) Assert.AreEqual((byte)64, ((IPicture<byte>)before!).r[0]);
        Assert.AreEqual(1, l.Calls);
        Assert.AreEqual(2, r.Calls);
        Assert.IsTrue(TransformProcessing.TryRender(left, [left, right], 9, 1, 1, 1, 1, IPicture.PicturePixelMode.BytePicture, out var preceding, initializeClips: false));
        preceding!.Dispose();
        Assert.AreEqual(2, l.Calls);
        Assert.AreEqual(2, r.Calls);
    }

    [TestMethod]
    public void NeighborAndConfigurationChangesInvalidateSharedCacheIdentity()
    {
        var info = Clip(0, 10);
        var next = Clip(10, 10);
        var provider = Attach(info, TransformSide.Right, 5, next.Id);
        using var left = new SolidColorClip { Id = info.Id, Name = "left", Duration = 10, ExtraData = new(), EffectProviders = [provider] };
        using var right = new SolidColorClip { Id = next.Id, Name = "right", StartFrame = 10, Duration = 10, ExtraData = new() };
        var hash = Timeline.GetClipFrameHash([left, right], right, 10);
        provider.MetaData![TransformProcessing.OrderKey] = TransformRenderOrder.BeforeEffects;
        var orderHash = Timeline.GetClipFrameHash([left, right], right, 10);
        Assert.AreNotEqual(hash, orderHash);
        provider.AnchorsBindingState["Strength"] = Guid.NewGuid().ToString();
        Assert.AreNotEqual(orderHash, Timeline.GetClipFrameHash([left, right], right, 10));
        hash = Timeline.GetClipFrameHash([left, right], right, 10);
        left.ExtraData["HDRBrightness"] = 1000;
        Assert.AreNotEqual(hash, Timeline.GetClipFrameHash([left, right], right, 10));
    }

    [TestMethod]
    public void IsolationUsesEffectDescriptorAndDoublePictureFrameContract()
    {
        var descriptor = new IsolationEffectDescriptor { EffectType = (int)EffectType.Transform,
            TransformDefinition = (int)(TransformDefinition.Clip | TransformDefinition.SupportTwoInput),
            DynamicProviderIds = ["strength"], Parameters = new() { ["Strength"] = new() { Kind = IsolationValueKind.Double, NumberValue = 0.5 } } };
        var cloned = RenderRpcSerializer.Clone(descriptor);
        Assert.AreEqual(descriptor.TransformDefinition, cloned.TransformDefinition);
        Assert.AreEqual(0.5, cloned.Parameters["Strength"].NumberValue);
        var request = new IsolationEffectFrameRequest { ObjectId = 1, SecondSource = new() { Locator = "right" },
            Progress = 0.5f, TransformSide = (int)TransformSide.Right, TargetFrame = 12, ClipProgress = 0.75f,
            DynamicValues = new() { ["strength"] = new() { Kind = IsolationValueKind.Double, NumberValue = 0.75 } } };
        var frame = RenderRpcSerializer.Clone(request);
        Assert.AreEqual(request.TransformSide, frame.TransformSide);
        Assert.AreEqual(request.TargetFrame, frame.TargetFrame);
        Assert.AreEqual(request.ClipProgress, frame.ClipProgress);
        Assert.AreEqual("right", frame.SecondSource!.Locator);
        Assert.AreEqual(0.75, frame.DynamicValues["strength"].NumberValue);
        Assert.AreEqual(2061, (int)RenderOperation.IsolationProcessTransformEffect);
    }
}
