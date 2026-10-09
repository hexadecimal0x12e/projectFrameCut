using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System.Text.Json;
using static projectFrameCut.Render.Contracts.Tests.DynamicEffectGraphTests;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class MultipleOutputEffectTests
{
    internal sealed class MultipleOutputProvider(EffectArgumentFieldType? input,
        EffectArgumentFieldDescriptor[] outputs, Func<EffectExecutionContext, IReadOnlyDictionary<string, object?>> compute,
        params EffectArgumentFieldDescriptor[] fields) : EffectProviderBase, IEffectProvider, IMultipleOutputEffectProvider
    {
        public override string TypeName => "TestMultipleOutput";
        public override string FromPlugin => "test.multiple";
        public override EffectType TypeOfEffect => EffectType.NormalEffect;
        public override EffectTarget Target => outputs.Any(p => p.FieldType.IsPicture()) ? EffectTarget.Video : EffectTarget.ValueProvider;
        public IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> OutFields => outputs.ToDictionary(p => p.Id);
        public Func<MultipleOutputEffect>? Factory { get; init; }
        public IEffect[] Prefix { get; init; } = [];
        public int Builds { get; private set; }
        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => fields;
        protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() => input is { } type
            ? new Dictionary<string, EffectArgumentFieldDescriptor> { [PrimaryInputAnchorKey] = Port(PrimaryInputAnchorKey, type) } : new();
        public new IEffect[] Build()
        {
            Builds++;
            var effect = Factory?.Invoke() ?? new MultipleOutputEffect(compute);
            effect.Parameters = BuildDynamicParameters();
            return [.. Prefix, effect];
        }
    }

    internal class MultipleOutputEffect(Func<EffectExecutionContext, IReadOnlyDictionary<string, object?>> compute)
        : TestEffect(_ => null), IMultipleOutputEffect
    {
        string IEffect.TypeName => "TestMultipleOutput";
        string IEffect.FromPlugin => "test.multiple";
        public IReadOnlyDictionary<string, object?> ComputeOutputs(EffectExecutionContext context) => compute(context);
        public override object? Compute(EffectExecutionContext context) => ComputeOutputs(context);
        public override IEffect WithParameters(Dictionary<string, object> parameters) => new MultipleOutputEffect(compute) { Parameters = parameters };
    }

    private sealed class ScopedMultipleEffect(Func<EffectExecutionContext, IReadOnlyDictionary<string, object?>> compute)
        : MultipleOutputEffect(compute), IContinuousEffect
    {
        public override EffectType TypeOfEffect => EffectType.ContinuousEffect;
        public int StartPoint { get; set; } = 10;
        public int EndPoint { get; set; } = 20;
        public bool IsScoped { get; set; } = true;
        public IPicture Render(IPicture source, float progress, int width, int height) => throw new NotSupportedException();
    }

    private sealed class NativeValueProvider() : Provider(null, EffectArgumentFieldType.IPicture, c => c.Input,
        Port("A", EffectArgumentFieldType.Integer), Port("B", EffectArgumentFieldType.Integer))
    {
        public override EffectType TypeOfEffect => EffectType.TextEffect;
        public override EffectTarget Target => EffectTarget.Text;
    }

    private sealed class NativeValueEffect() : TestEffect(c => c.Input), ITextEffect
    {
        public override EffectType TypeOfEffect => EffectType.TextEffect;
        public int Sum { get; private set; }
        public projectFrameCut.Drawing.Text.Entry.TextEntry[] Process(projectFrameCut.Drawing.Text.Entry.TextEntry[] source)
        {
            Sum = DynamicParam.Resolve(Parameters["A"], 0) + DynamicParam.Resolve(Parameters["B"], 0);
            return source;
        }
    }

    internal static EffectArgumentFieldDescriptor Port(string id, EffectArgumentFieldType type) => new() { Id = id, FieldType = type };
    private static string Output(IEffectProvider provider, string id) => EffectProviderOutputExtensions.CreateOutputSourceId(provider.Id, id);
    private static byte Red(object? picture) => ((IPicture<byte>)picture!).r[0];

    [TestMethod]
    public void MultipleOutputProvidersFeedEachOthersInputsAndParametersOncePerFrame()
    {
        int xCalls = 0, yCalls = 0, zCalls = 0;
        var x = new MultipleOutputProvider(EffectArgumentFieldType.IPicture,
            [Port("A", EffectArgumentFieldType.IPicture), Port("B", EffectArgumentFieldType.IPicture), Port("C", EffectArgumentFieldType.Integer)], c =>
            {
                xCalls++;
                return new Dictionary<string, object?> { ["A"] = Picture((byte)(Red(c.Input) + c.FrameIndex)),
                    ["B"] = Picture((byte)(20 + c.FrameIndex)), ["C"] = 2 };
            });
        var y = new MultipleOutputProvider(null, [Port("Image", EffectArgumentFieldType.IPicture), Port("Amount", EffectArgumentFieldType.Integer)], c =>
        {
            yCalls++;
            return new Dictionary<string, object?> { ["Image"] = Picture((byte)(Red(c.Parameters["Image"]) + (int)c.Parameters["Amount"]!)), ["Amount"] = 1 };
        }, Port("Image", EffectArgumentFieldType.IPicture), Port("Amount", EffectArgumentFieldType.Integer));
        var z = new MultipleOutputProvider(EffectArgumentFieldType.Integer, [Port("Image", EffectArgumentFieldType.IPicture)], c =>
        {
            zCalls++;
            return new Dictionary<string, object?> { ["Image"] = Picture((byte)(Red(c.Parameters["Image"]) + (int)c.Input!)) };
        }, Port("Image", EffectArgumentFieldType.IPicture));
        var result = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture,
            c => Picture((byte)(Red(c.Input) + Red(c.Parameters["Other"]) + (int)c.Parameters["Amount"]!)),
            Port("Other", EffectArgumentFieldType.IPicture), Port("Amount", EffectArgumentFieldType.Integer));
        x.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        y.SetFieldBinding("Image", Output(x, "A"));
        y.SetFieldBinding("Amount", Output(x, "C"));
        z.SetMainInputSource(Output(x, "C"));
        z.SetFieldBinding("Image", Output(x, "B"));
        result.SetMainInputSource(Output(y, "Image"));
        result.SetFieldBinding("Other", Output(z, "Image"));
        result.SetFieldBinding("Amount", Output(y, "Amount"));
        result.SetFinalOutputSource(true);
        using var graph = Build(Providers(x, y, z, result));
        using var source = Picture(10);
        using var first = Evaluate(graph, source, 1);
        using var second = Evaluate(graph, source, 2);
        Assert.AreEqual((byte)37, Red(first));
        Assert.AreEqual((byte)39, Red(second));
        Assert.AreEqual(2, xCalls);
        Assert.AreEqual(2, yCalls);
        Assert.AreEqual(2, zCalls);
        Assert.AreEqual(1, x.Builds);
        Assert.AreEqual((byte)10, Red(source));
    }

    [TestMethod]
    public void BranchesCannotModifySharedPicturesAndUnusedOutputsAreReleased()
    {
        IPicture? shared = null, unused = null;
        var x = new MultipleOutputProvider(null, [Port("Image", EffectArgumentFieldType.IPicture), Port("Unused", EffectArgumentFieldType.IPicture)], _ =>
        {
            shared = Picture(10);
            unused = Picture(30);
            return new Dictionary<string, object?> { ["Image"] = shared, ["Unused"] = unused };
        });
        var branch = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c =>
        {
            ((IPicture<byte>)c.Input!).r[0] = 99;
            return c.Input;
        });
        var result = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture,
            c => Picture((byte)(Red(c.Input) + Red(c.Parameters["Other"]))), Port("Other", EffectArgumentFieldType.IPicture));
        branch.SetMainInputSource(Output(x, "Image"));
        result.SetMainInputSource(Output(x, "Image"));
        result.SetFieldBinding("Other", branch.Id.ToString());
        result.SetFinalOutputSource(true);
        using var graph = Build(Providers(x, branch, result));
        using var source = Picture(1);
        using var output = Evaluate(graph, source);
        Assert.AreEqual((byte)109, Red(output));
        Assert.IsTrue(shared!.Disposed);
        Assert.IsTrue(unused!.Disposed);
        Assert.IsFalse(output.Disposed);
        Assert.IsFalse(source.Disposed);
    }

    [TestMethod]
    public void NamedOutputsSurviveSerializationAndDeletingAProviderClearsEveryPortReference()
    {
        const string key = "A / 中文#%";
        var source = new MultipleOutputProvider(null, [Port(key, EffectArgumentFieldType.IPicture)], _ => new Dictionary<string, object?> { [key] = Picture(5) });
        source.SetFinalOutputSource(true, key);
        var target = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c => c.Input, Port("Other", EffectArgumentFieldType.IPicture));
        target.SetMainInputSource(Output(source, key));
        target.SetFieldBinding("Other", Output(source, key));
        var saved = JsonSerializer.Deserialize<EffectProviderJSONStructure[]>(JsonSerializer.Serialize(new[]
            { EffectBindingHelper.SerializeProvider(source), EffectBindingHelper.SerializeProvider(target) }))!;
        source.AnchorsBindingState = saved[0].AnchorsBindingState;
        target.AnchorsBindingState = saved[1].AnchorsBindingState;
        Assert.AreEqual(key, source.GetFinalOutputFieldId());
        Assert.AreEqual(0, source.EnumerateFieldBindings().Count());
        Assert.IsTrue(EffectProviderOutputExtensions.TryParseOutputSourceId(target.GetMainInputSource(), out var id, out var outputId));
        Assert.AreEqual(source.Id, id);
        Assert.AreEqual(key, outputId);
        var providers = Providers(source, target);
        Assert.AreEqual(0, EffectBindingHelper.ValidateBindings(providers).Count);
        Assert.IsTrue(EffectBindingHelper.RemoveProvider(providers, source.Id));
        Assert.AreEqual(IEffectProvider.NoConnectionGUID.ToString(), target.GetMainInputSource());
        Assert.IsFalse(target.TryGetFieldBinding("Other", out _));
    }

    [TestMethod]
    public void MissingPortsTypesCyclesAndMultipleTerminalsAreRejected()
    {
        var source = new MultipleOutputProvider(null, [Port("Number", EffectArgumentFieldType.Integer)], _ => new Dictionary<string, object?> { ["Number"] = 1 }, Port("Amount", EffectArgumentFieldType.Integer));
        var target = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c => c.Input);
        var providers = Providers(source, target);
        target.SetMainInputSource(source.Id);
        Assert.IsTrue(EffectBindingHelper.ValidateBindings(providers).Any(d => d.Code == "UnknownOutputPort"));
        target.SetMainInputSource(Output(source, "Missing"));
        Assert.IsTrue(EffectBindingHelper.ValidateBindings(providers).Any(d => d.Code == "UnknownOutputPort"));
        target.SetMainInputSource(Output(source, "Number"));
        Assert.IsTrue(EffectBindingHelper.ValidateBindings(providers).Any(d => d.Code == "IncompatibleInput"));
        source.SetFieldBinding("Amount", Output(source, "Number"));
        Assert.IsTrue(EffectBindingHelper.ValidateBindings(providers).Any(d => d.Code == "BindingCycle"));
        Assert.ThrowsExactly<ArgumentException>(() => EffectBindingHelper.SetFinalOutput(providers, source.Id));
        Assert.ThrowsExactly<InvalidOperationException>(() => DynamicEffectBindings.ValidateMultipleOutputEffects(source,
            [new MultipleOutputEffect(_ => new Dictionary<string, object?>()), new MultipleOutputEffect(_ => new Dictionary<string, object?>())]));
    }

    [TestMethod]
    public void DisabledAndOutOfRangeOutputsUseFallbackWithoutComputing()
    {
        int calls = 0;
        var source = new MultipleOutputProvider(null, [Port("Value", EffectArgumentFieldType.Integer)], _ =>
        {
            calls++;
            return new Dictionary<string, object?> { ["Value"] = 20 };
        }) { Enabled = false, Factory = () => new ScopedMultipleEffect(_ => { calls++; return new Dictionary<string, object?> { ["Value"] = 20 }; }) };
        var target = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture,
            c => Picture((byte)(int)c.Parameters["Amount"]!), new EffectArgumentFieldDescriptor { Id = "Amount", FieldType = EffectArgumentFieldType.Integer, DefaultValue = "9" });
        target.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        target.SetFieldBinding("Amount", Output(source, "Value"));
        target.SetFinalOutputSource(true);
        using var picture = Picture(1);
        using (var graph = Build(Providers(source, target)))
        using (var output = Evaluate(graph, picture, 15)) Assert.AreEqual((byte)9, Red(output));
        Assert.AreEqual(0, calls);
        source.Enabled = true;
        using (var graph = Build(Providers(source, target)))
        {
            using var before = Evaluate(graph, picture, 0);
            using var during = Evaluate(graph, picture, 15);
            Assert.AreEqual((byte)9, Red(before));
            Assert.AreEqual((byte)20, Red(during));
        }
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void PureValueOutputsShareEvaluationInNativeParameterBindings()
    {
        int calls = 0;
        var value = new MultipleOutputProvider(null, [Port("A", EffectArgumentFieldType.Integer), Port("B", EffectArgumentFieldType.Integer)], c =>
        {
            calls++;
            return new Dictionary<string, object?> { ["A"] = (int)c.FrameIndex, ["B"] = 10 };
        });
        var native = new NativeValueProvider { Factory = () => new NativeValueEffect() };
        native.SetFieldBinding("A", Output(value, "A"));
        native.SetFieldBinding("B", Output(value, "B"));
        var raster = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture,
            c => Picture((byte)((int)c.Parameters["A"]! + (int)c.Parameters["B"]!)),
            Port("A", EffectArgumentFieldType.Integer), Port("B", EffectArgumentFieldType.Integer));
        raster.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        raster.SetFieldBinding("A", Output(value, "A"));
        raster.SetFieldBinding("B", Output(value, "B"));
        raster.SetFinalOutputSource(true);
        var effects = EffectBindingHelper.RebuildAllEffects(Providers(value, native, raster), null)!;
        using var source = Picture(1);
        try
        {
            var effect = effects.Values.OfType<NativeValueEffect>().Single();
            using (ValueProviderFrameContext.PushFrame(7, 0))
            {
                effect.Process([]);
                effect.Process([]);
                using var output = Evaluate(effects.Values.OfType<DynamicEffectGraph>().Single(), source, 7);
                Assert.AreEqual((byte)17, Red(output));
                Assert.AreEqual(17, effect.Sum);
                Assert.AreEqual(1, calls);
            }
            using (ValueProviderFrameContext.PushFrame(8, 0))
            {
                effect.Process([]);
                using var output = Evaluate(effects.Values.OfType<DynamicEffectGraph>().Single(), source, 8);
                Assert.AreEqual((byte)18, Red(output));
                Assert.AreEqual(18, effect.Sum);
                Assert.AreEqual(2, calls);
            }
            Assert.AreEqual(1, value.Builds);
        }
        finally
        {
            foreach (var effect in effects.Values.OfType<IDisposable>()) effect.Dispose();
        }
    }

    [TestMethod]
    public void InvalidResultsAndCancellationReleaseAllReturnedPictures()
    {
        IPicture? created = null;
        var provider = new MultipleOutputProvider(null, [Port("Image", EffectArgumentFieldType.IPicture), Port("Number", EffectArgumentFieldType.Integer)], _ =>
        {
            created = Picture(5);
            return new Dictionary<string, object?> { ["Image"] = created, ["Number"] = "bad" };
        });
        provider.SetFinalOutputSource(true, "Image");
        using var source = Picture(1);
        using (var graph = Build(Providers(provider)))
            Assert.ThrowsExactly<InvalidOperationException>(() => Evaluate(graph, source));
        Assert.IsTrue(created!.Disposed);
        using var cancellation = new CancellationTokenSource();
        var cancelled = new MultipleOutputProvider(null, [Port("Image", EffectArgumentFieldType.IPicture)], _ =>
        {
            created = Picture(6);
            cancellation.Cancel();
            return new Dictionary<string, object?> { ["Image"] = created };
        });
        cancelled.SetFinalOutputSource(true, "Image");
        using (var graph = Build(Providers(cancelled)))
            Assert.ThrowsExactly<OperationCanceledException>(() => Evaluate(graph, source, token: cancellation.Token));
        Assert.IsTrue(created!.Disposed);
        Assert.IsFalse(source.Disposed);
    }
}
