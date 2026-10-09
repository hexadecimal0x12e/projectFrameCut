using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class DynamicEffectGraphTests
{
    internal class Provider(EffectArgumentFieldType? input, EffectArgumentFieldType output,
        Func<EffectExecutionContext, object?> compute, params EffectArgumentFieldDescriptor[] fields) : EffectProviderBase, IEffectProvider
    {
        public override string TypeName => "TestGraph";
        public override string FromPlugin => "test.graph";
        public override EffectType TypeOfEffect => output.IsPicture() ? EffectType.NormalEffect : EffectType.NonIPictureOutputValueProvider;
        public override EffectTarget Target => output.IsPicture() ? EffectTarget.Video : EffectTarget.ValueProvider;
        public Func<TestEffect>? Factory { get; init; }
        public int Builds { get; private set; }
        public TestEffect? LastEffect { get; private set; }
        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => fields;
        protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() => input is { } type
            ? new Dictionary<string, EffectArgumentFieldDescriptor> { [PrimaryInputAnchorKey] = Field(PrimaryInputAnchorKey, type, "") } : new();
        protected override EffectArgumentFieldDescriptor DefineOutField() => Field(OutputAnchorKey, output, "");
        public new IEffect[] Build()
        {
            Builds++;
            LastEffect = Factory?.Invoke() ?? (output.IsPicture() ? new TestEffect(compute) : new ValueEffect(compute));
            LastEffect.Parameters = BuildDynamicParameters();
            return [LastEffect];
        }
    }

    internal class TestEffect(Func<EffectExecutionContext, object?> compute) : INormalEffect, IDisposable
    {
        public string FromPlugin => "test.graph";
        public string TypeName => "TestGraph";
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "TestGraph";
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string? BindedEffectProvidingSystemID { get; set; }
        public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
        public virtual EffectType TypeOfEffect => EffectType.NormalEffect;
        public bool IsReorderable => false;
        public Dictionary<string, object> Parameters { get; set; } = [];
        public bool Disposed { get; private set; }
        public int Initializations { get; private set; }
        public virtual IEffect WithParameters(Dictionary<string, object> parameters) => new TestEffect(compute) { Parameters = parameters };
        public void Initialize() => Initializations++;
        public virtual object? Compute(EffectExecutionContext context)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            return compute(context);
        }
        public IPicture Render(IPicture source, int width, int height) => (IPicture)Compute(new() { Input = source, TargetWidth = width, TargetHeight = height })!;
        public void Dispose() => Disposed = true;
    }

    internal sealed class ValueEffect(Func<EffectExecutionContext, object?> compute) : TestEffect(compute), IValueProviderEffect
    {
        public override IEffect WithParameters(Dictionary<string, object> parameters) => new ValueEffect(compute) { Parameters = parameters };
        public override EffectType TypeOfEffect => EffectType.NonIPictureOutputValueProvider;
        public bool IsDynamic => true;
        public bool IsDynamicAtRenderTime => true;
        public EffectArgumentFieldType FieldType => EffectArgumentFieldType.Integer;
        public string DefaultValue { get; set; } = "0";
        public string MinValue { get; set; } = "";
        public string MaxValue { get; set; } = "";
        public string[]? PresetOptions { get; set; }
        public string? Remarks { get; set; }
        public Func<object> GetGetter() => () => Compute(new())!;
    }

    private sealed class ScopedEffect(Func<EffectExecutionContext, object?> compute) : TestEffect(compute), IContinuousEffect
    {
        public override EffectType TypeOfEffect => EffectType.ContinuousEffect;
        public int StartPoint { get; set; } = 10;
        public int EndPoint { get; set; } = 20;
        public bool IsScoped { get; set; } = true;
        public IPicture Render(IPicture source, float progress, int width, int height) => (IPicture)Compute(new() { Input = source, Progress = progress })!;
    }

    private sealed class InlineSource : IntConstantValueProviderProvider, IEffectProvider
    {
        public new IEffect[] Build() => [new IntConstantValueProviderEffect { Parameters = BuildDynamicParameters() }];
    }

    private sealed class NativeProvider() : Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c => c.Input)
    {
        public override EffectType TypeOfEffect => EffectType.TextEffect;
        public override EffectTarget Target => EffectTarget.Text;
    }

    private sealed class NativeEffect() : TestEffect(c => c.Input), ITextEffect
    {
        public override EffectType TypeOfEffect => EffectType.TextEffect;
        public TextEntry[] Process(TextEntry[] source) => source;
    }

    private sealed class PictureParameterProvider(Func<EffectExecutionContext, object?> compute)
        : Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, compute)
    {
        protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() => new Dictionary<string, EffectArgumentFieldDescriptor>(base.DefineInFields())
        {
            ["Other"] = Field("Other", EffectArgumentFieldType.IPicture, ""),
        };
    }

    internal static Dictionary<Guid, IEffectProvider> Providers(params IEffectProvider[] providers) => providers.ToDictionary(p => p.Id);
    internal static Picture8bpp Picture(byte value) => Picture8bpp.GenerateSolidColor(1, 1, value, 0, 0, 1);
    private static byte Red(IPicture picture) => ((IPicture<byte>)picture).r[0];
    internal static DynamicEffectGraph Build(Dictionary<Guid, IEffectProvider> providers) => EffectBindingHelper.RebuildAllEffects(providers, null)!.Values.OfType<DynamicEffectGraph>().Single();
    internal static IPicture Evaluate(DynamicEffectGraph graph, IPicture source, uint frame = 0, CancellationToken token = default) =>
        graph.Evaluate(source, null, frame, 1, 1, 1, 1, new(0, 0, 1, 1, false), cancellationToken: token).Picture;

    [TestMethod]
    public void OrdinaryClipsKeepInlineParametersAndSeparateClipsSelectTheirOwnPipeline()
    {
        IEffectProvider value = new InlineSource();
        value.SetFieldBinding("Value", ValueProviderFrameContext.BuiltInFrameProviderId);
        var picture = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c => c.Input,
            new EffectArgumentFieldDescriptor { Id = "Amount", FieldType = EffectArgumentFieldType.Integer, DefaultValue = "0" });
        picture.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        picture.SetFinalOutputSource(true);
        picture.SetFieldBinding("Amount", value.Id.ToString());
        var ordinary = Providers(picture, value);
        Assert.IsFalse(DynamicEffectBindings.RequiresGraph(ordinary));
        var effects = EffectBindingHelper.RebuildAllEffects(ordinary, null)!;
        Assert.AreEqual(1, effects.Count);
        Assert.IsFalse(effects.Values.Any(e => e is DynamicEffectGraph));
        using (ValueProviderFrameContext.PushFrame(7, 0))
            Assert.AreEqual(7, DynamicParam.Resolve(picture.LastEffect!.Parameters["Amount"], -1));
        var scalar = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.Integer, c => Red((IPicture)c.Input!));
        Assert.IsTrue(DynamicEffectBindings.RequiresGraph(Providers(scalar)));
        Assert.IsFalse(DynamicEffectBindings.RequiresGraph(ordinary));
    }

    [TestMethod]
    public void AddingPictureAnalysisPreservesBothNativeAndRasterOutputs()
    {
        var native = new NativeProvider { Factory = () => new NativeEffect() };
        native.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        native.SetFinalOutputSource(true);
        var raster = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c => c.Input);
        raster.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        raster.SetFinalOutputSource(true);
        var value = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.Integer, _ => 1);
        var providers = Providers(native, raster, value);
        EffectBindingHelper.AutoConnectProviderToOutput(providers, value, EffectTarget.Video);
        Assert.IsTrue(native.IsFinalOutputSource());
        Assert.IsTrue(raster.IsFinalOutputSource());
        Assert.IsFalse(value.IsFinalOutputSource());
        Assert.AreEqual(raster.Id.ToString(), value.GetMainInputSource());
        var effects = EffectBindingHelper.RebuildAllEffects(providers, null)!;
        try
        {
            Assert.IsTrue(effects.Values.Single(e => e.BindedEffectProvidingSystemID == native.Id.ToString()).Enabled);
            Assert.AreEqual(1, effects.Values.OfType<DynamicEffectGraph>().Count());
        }
        finally
        {
            foreach (var effect in effects.Values.OfType<IDisposable>()) effect.Dispose();
        }
    }

    [TestMethod]
    public void PictureToValueAndValueToPictureEvaluateEachFrameWithOneBuild()
    {
        int calls = 0;
        var value = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.Integer, c => { calls++; return Red((IPicture)c.Input!) + (int)c.FrameIndex; });
        var picture = new Provider(EffectArgumentFieldType.Integer, EffectArgumentFieldType.IPicture, c => Picture((byte)(int)c.Input!));
        value.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        picture.SetMainInputSource(value.Id);
        picture.SetFinalOutputSource(true);
        using var graph = Build(Providers(value, picture));
        using var source = Picture(10);
        using var first = Evaluate(graph, source, 1);
        using var second = Evaluate(graph, source, 2);
        Assert.AreEqual((byte)11, Red(first));
        Assert.AreEqual((byte)12, Red(second));
        Assert.AreEqual(2, calls);
        Assert.AreEqual(1, value.Builds);
        Assert.AreEqual(1, picture.LastEffect!.Initializations);
    }

    [TestMethod]
    public void TimelineCompositesInlineAndGraphClipsWithFreshValuesForEachFrame()
    {
        Provider? ordinary = null;
        ordinary = new(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture,
            c => Picture((byte)(Red((IPicture)c.Input!) + DynamicParam.Resolve(ordinary.LastEffect!.Parameters["Amount"], 0))),
            new EffectArgumentFieldDescriptor { Id = "Amount", FieldType = EffectArgumentFieldType.Integer });
        var value = new InlineSource();
        value.SetFieldBinding("Value", ValueProviderFrameContext.BuiltInFrameProviderId);
        ordinary.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        ordinary.SetFieldBinding("Amount", value.Id.ToString());
        ordinary.SetFinalOutputSource(true);
        var analysis = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.Integer,
            c => Red((IPicture)c.Input!) + (int)c.FrameIndex);
        var output = new Provider(EffectArgumentFieldType.Integer, EffectArgumentFieldType.IPicture,
            c => Picture((byte)(int)c.Input!));
        analysis.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        output.SetMainInputSource(analysis.Id);
        output.SetFinalOutputSource(true);
        using var left = new SolidColorClip { Id = Guid.NewGuid(), Name = "inline", Duration = 10, ExtraData = [],
            R = 10 * 257, TargetWidth = 1, TargetHeight = 1 };
        using var right = new SolidColorClip { Id = Guid.NewGuid(), Name = "graph", Duration = 10, ExtraData = [],
            R = 20 * 257, TargetX = 1, TargetWidth = 1, TargetHeight = 1 };
        left.EffectsInstances = EffectBindingHelper.RebuildAllEffects(Providers(ordinary, value), null)!.Values.ToArray();
        right.EffectsInstances = EffectBindingHelper.RebuildAllEffects(Providers(analysis, output), null)!.Values.ToArray();
        foreach (uint index in new uint[] { 1, 2 })
        {
            var frames = Timeline.GetFramesInOneFrame([left, right], index, 2, 1, 8, 2, 1, initializeClips: false).ToArray();
            try
            {
                var scalars = new List<object?>();
                using var picture = Timeline.MixtureLayers(frames, index, 2, 1, 8,
                    projectRelativeWidth: 2, projectRelativeHeight: 1, transparentBackground: true,
                    disposeIntermediateFrames: true, afterNodeCallback: (_, v) => { if (v is not IPicture) scalars.Add(v); });
                CollectionAssert.AreEqual(new byte[] { (byte)(10 + index), (byte)(20 + index) }, ((IPicture<byte>)picture).r);
                CollectionAssert.AreEqual(new object[] { 20 + (int)index }, scalars.ToArray());
            }
            finally
            {
                foreach (var frame in frames) frame.Clip.Dispose(true);
            }
        }
        Assert.AreEqual(1, ordinary.Builds);
        Assert.AreEqual(1, analysis.Builds);
        Assert.AreEqual(1, output.Builds);
    }

    [TestMethod]
    public void SharedPictureBranchesComputeOnceAndCannotMutateTheirSource()
    {
        int calls = 0;
        var root = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c => { calls++; return c.Input; });
        var left = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c => { ((IPicture<byte>)c.Input!).r[0] = 40; return c.Input; });
        var right = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c => c.Input);
        var final = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture,
            c => Picture((byte)(Red((IPicture)c.Input!) + Red((IPicture)c.Parameters["Other"]!))),
            new EffectArgumentFieldDescriptor { Id = "Other", FieldType = EffectArgumentFieldType.IPicture | EffectArgumentFieldType.Mandatory });
        root.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        left.SetMainInputSource(root.Id);
        right.SetMainInputSource(root.Id);
        final.SetMainInputSource(left.Id);
        final.SetFieldBinding("Other", right.Id.ToString());
        final.SetFinalOutputSource(true);
        using var graph = Build(Providers(root, left, right, final));
        using var source = Picture(10);
        using var output = Evaluate(graph, source);
        Assert.AreEqual((byte)50, Red(output));
        Assert.AreEqual((byte)10, Red(source));
        Assert.AreEqual(1, calls);
        Assert.AreEqual(0, EffectBindingHelper.SerializeProvider(final).StaticFields!.Count);
    }

    [TestMethod]
    public void PictureParameterGettersRemainCompatibleWithExistingEffects()
    {
        PictureParameterProvider? provider = null;
        provider = new(c => Picture(Red(DynamicParam.Resolve<IPicture>(provider.LastEffect!.Parameters["Other"], (IPicture)c.Input!))));
        provider.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        provider.SetFieldBinding("Other", IEffectProvider.InputAnchorGUID.ToString());
        provider.SetFinalOutputSource(true);
        using var graph = Build(Providers(provider));
        using var source = Picture(21);
        using var output = Evaluate(graph, source);
        Assert.AreEqual((byte)21, Red(output));
    }

    [TestMethod]
    public void ParallelFramesRestoreOuterContext()
    {
        var provider = new Provider(EffectArgumentFieldType.Integer, EffectArgumentFieldType.IPicture, c =>
        {
            Assert.AreEqual((float)c.FrameIndex, ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInFrameProviderId));
            return Picture((byte)(int)c.Input!);
        });
        provider.SetMainInputSource(ValueProviderFrameContext.BuiltInFrameProviderId);
        provider.SetFinalOutputSource(true);
        using var graph = Build(Providers(provider));
        Parallel.For(0, 100, n =>
        {
            using var context = ValueProviderFrameContext.PushFrame(999, 0);
            using var source = Picture(0);
            using var output = Evaluate(graph, source, (uint)n);
            Assert.AreEqual((byte)n, Red(output));
            Assert.AreEqual(999f, ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInFrameProviderId));
        });
        Assert.AreEqual(1, provider.LastEffect!.Initializations);
    }

    [TestMethod]
    public void DisabledNodesUseScalarFallbackAndScopedPicturesBypassOutsideTheirRange()
    {
        var value = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.Integer, _ => 200) { Enabled = false };
        var output = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture,
            c => Picture((byte)(int)c.Parameters["Amount"]!), new EffectArgumentFieldDescriptor { Id = "Amount", FieldType = EffectArgumentFieldType.Integer, DefaultValue = "9" });
        value.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        output.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        output.SetFieldBinding("Amount", value.Id.ToString());
        output.SetFinalOutputSource(true);
        using (var graph = Build(Providers(value, output)))
        using (var source = Picture(5))
        using (var result = Evaluate(graph, source)) Assert.AreEqual((byte)9, Red(result));
        var scoped = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c => c.Input,
            new EffectArgumentFieldDescriptor { Id = "Other", FieldType = EffectArgumentFieldType.IPicture })
        { Factory = () => new ScopedEffect(c => Picture((byte)(c.Progress * 100))) };
        scoped.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        scoped.SetFinalOutputSource(true);
        using var scopedGraph = Build(Providers(scoped));
        using var input = Picture(5);
        Assert.AreSame(input, Evaluate(scopedGraph, input, 0));
        using var middle = Evaluate(scopedGraph, input, 15);
        Assert.AreEqual((byte)50, Red(middle));
    }

    [TestMethod]
    public void CyclesAndReverseStageDependenciesAreRejected()
    {
        var a = new Provider(EffectArgumentFieldType.Integer, EffectArgumentFieldType.Integer, c => c.Input);
        var b = new Provider(EffectArgumentFieldType.Integer, EffectArgumentFieldType.Integer, c => c.Input);
        a.SetMainInputSource(b.Id);
        b.SetMainInputSource(a.Id);
        Assert.IsTrue(EffectBindingHelper.ValidateBindings(Providers(a, b)).Any(e => e.Code == "BindingCycle"));
        using var clip = new SolidColorClip { Id = Guid.NewGuid(), Name = "clip", ExtraData = [] };
        var arguments = new ClipArgumentProvider();
        arguments.Attach(clip);
        a.SetMainInputSource(ValueProviderFrameContext.BuiltInFrameProviderId);
        arguments.SetFieldBinding(nameof(IClip.TargetX), a.Id.ToString());
        Assert.IsTrue(EffectBindingHelper.ValidateBindings(Providers(a, arguments)).Any(e => e.Code == "CrossStageBinding"));
    }

    [TestMethod]
    public void CancellationDisposesFrameCopiesAndDefersLiveInstanceDisposal()
    {
        using var cancellation = new CancellationTokenSource();
        IPicture? temporary = null;
        DynamicEffectGraph? graph = null;
        Provider? provider = null;
        provider = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c =>
        {
            temporary = (IPicture)c.Input!;
            graph!.Dispose();
            Assert.IsFalse(provider.LastEffect!.Disposed);
            cancellation.Cancel();
            return c.Input;
        }, new EffectArgumentFieldDescriptor { Id = "Other", FieldType = EffectArgumentFieldType.IPicture });
        provider.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        provider.SetFinalOutputSource(true);
        using (graph = Build(Providers(provider)))
        using (var source = Picture(10))
        {
            Assert.ThrowsExactly<OperationCanceledException>(() => Evaluate(graph, source, token: cancellation.Token));
            Assert.IsTrue(temporary!.Disposed);
            Assert.IsFalse(source.Disposed);
            Assert.IsTrue(provider.LastEffect!.Disposed);
        }
    }

    [TestMethod]
    public void BranchCopiesPreserveHdrAndAlpha()
    {
        using var source = HDRPicture16bpp.GenerateSolidColor(1, 1, 12345, 0, 0, 0.25f, 0.5f, 1000);
        using var copy = DynamicEffectGraph.CopyPicture(source);
        Assert.IsInstanceOfType<IHDRPicture<ushort>>(copy);
        var hdr = (IHDRPicture<ushort>)copy;
        Assert.AreEqual(1000f, hdr.MaximumBrightness);
        Assert.AreEqual(0.5f, hdr.Brightness[0]);
        Assert.AreEqual(0.25f, hdr.a[0]);
        hdr.r[0] = 0;
        hdr.Brightness[0] = 0;
        Assert.AreEqual((ushort)12345, source.r[0]);
        Assert.AreEqual(0.5f, source.Brightness[0]);
    }
}
