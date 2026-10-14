using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Shared;
using System.Text.Json;
using static projectFrameCut.Render.Contracts.Tests.DynamicEffectGraphTests;
using static projectFrameCut.Render.Contracts.Tests.MultipleOutputEffectTests;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ConditionComputeNodeTests
{
    private static string Output(IEffectProvider provider, bool condition) => EffectProviderOutputExtensions.CreateOutputSourceId(
        provider.Id, condition ? ConditionComputeNodeEffectProvider.TrueOutputKey : ConditionComputeNodeEffectProvider.FalseOutputKey);

    private static ConditionComputeNodeEffectProvider Gate(bool condition) => new()
    {
        Fields = new() { ["Condition"] = new StaticEffectArgumentField(condition, EffectArgumentFieldType.Boolean) },
    };

    private static IsUpstreamAvailableOrDefaultEffectProvider Join(string main, string fallback)
    {
        var provider = new IsUpstreamAvailableOrDefaultEffectProvider();
        provider.SetMainInputSource(main);
        provider.SetFieldBinding("DefaultValue", fallback);
        return provider;
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RoutesPicturesWithoutEvaluatingTheOtherRouteOrItsParameters(bool condition)
    {
        int inputCalls = 0, conditionCalls = 0;
        IPicture? produced = null;
        var source = new Provider(null, EffectArgumentFieldType.IPicture, _ => { inputCalls++; return produced = Picture(6); });
        var predicate = new Provider(null, EffectArgumentFieldType.Boolean, _ => { conditionCalls++; return condition; });
        var expensive = new Provider(null, EffectArgumentFieldType.Integer,
            _ => throw new AssertFailedException("The inactive route's parameter must not run."));
        var gate = Gate(false);
        gate.SetMainInputSource(source.Id);
        gate.SetFieldBinding("Condition", predicate.Id.ToString());
        Provider Route(bool branch)
        {
            var provider = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c =>
            {
                Assert.AreEqual(condition, branch);
                return c.Input;
            }, Port("Amount", EffectArgumentFieldType.Integer));
            provider.SetMainInputSource(Output(gate, branch));
            if (branch != condition) provider.SetFieldBinding("Amount", expensive.Id.ToString());
            return provider;
        }
        var yes = Route(true);
        var no = Route(false);
        var join = Join(yes.Id.ToString(), no.Id.ToString());
        join.SetFinalOutputSource(true);
        using var graph = Build(Providers(source, predicate, expensive, gate, yes, no, join));
        using var input = Picture(0);
        var observed = new List<IEffect>();
        var position = new ClipPositionTuple(2, 3, 4, 5, false, 17);
        var result = graph.Evaluate(input, null, 0, 1, 1, 1, 1, position, preserveAspect: false, afterNode: (effect, value) =>
        {
            observed.Add(effect);
            if (effect is ConditionComputeNodeEffect)
            {
                var outputs = (IReadOnlyDictionary<string, object?>)value!;
                Assert.IsNull(outputs[condition ? "False" : "True"]);
                Assert.IsInstanceOfType<IPicture>(outputs[condition ? "True" : "False"]);
            }
        });
        using var picture = result.Picture;
        Assert.AreEqual((byte)6, ((IPicture<byte>)picture).r[0]);
        Assert.AreEqual(position, result.Position);
        Assert.IsFalse(result.PreserveAspect);
        Assert.AreEqual(1, inputCalls);
        Assert.AreEqual(1, conditionCalls);
        Assert.IsTrue(produced!.Disposed);
        Assert.IsFalse(picture.Disposed);
        Assert.IsFalse(input.Disposed);
        Assert.IsFalse(observed.Contains((condition ? no : yes).LastEffect!));
        Assert.IsFalse(observed.Contains(expensive.LastEffect!));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InactiveParameterSkipsTheMainInputAndEarlierParameters(bool viaJoin)
    {
        var source = new Provider(null, EffectArgumentFieldType.Integer,
            _ => throw new AssertFailedException("The inactive output must not read its data input."));
        var main = new Provider(null, EffectArgumentFieldType.IPicture,
            _ => throw new AssertFailedException("The inactive consumer must not read its main input."));
        var expensive = new Provider(null, EffectArgumentFieldType.Integer,
            _ => throw new AssertFailedException("The earlier parameter must not run."));
        var gate = Gate(true);
        gate.SetMainInputSource(source.Id);
        var pictureGate = Gate(true);
        pictureGate.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        var availability = Join(main.Id.ToString(), Output(pictureGate, true));
        var consumer = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture,
            _ => throw new AssertFailedException("The inactive consumer must not run."),
            Port("Expensive", EffectArgumentFieldType.Integer), Port("Gate", EffectArgumentFieldType.Integer));
        consumer.SetMainInputSource(viaJoin ? availability.Id : main.Id);
        consumer.SetFieldBinding("Expensive", expensive.Id.ToString());
        consumer.SetFieldBinding("Gate", Output(gate, false));
        var join = Join(consumer.Id.ToString(), IEffectProvider.InputAnchorGUID.ToString());
        join.SetFinalOutputSource(true);
        using var graph = Build(Providers(source, main, expensive, gate, pictureGate, availability, consumer, join));
        using var input = Picture(7);
        using var output = Evaluate(graph, input);
        Assert.AreEqual((byte)7, ((IPicture<byte>)output).r[0]);
    }

    [TestMethod]
    public void ActiveNullParameterStillUsesItsStaticFallback()
    {
        var gate = Gate(true);
        gate.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        var empty = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.Integer, _ => null);
        empty.SetMainInputSource(Output(gate, true));
        var consumer = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture,
            c => Picture((byte)(int)c.Parameters["Amount"]!),
            new EffectArgumentFieldDescriptor { Id = "Amount", FieldType = EffectArgumentFieldType.Integer, DefaultValue = "9" });
        consumer.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        consumer.SetFieldBinding("Amount", empty.Id.ToString());
        consumer.SetFinalOutputSource(true);
        using var graph = Build(Providers(gate, empty, consumer));
        using var input = Picture(0);
        using var output = Evaluate(graph, input);
        Assert.AreEqual((byte)9, ((IPicture<byte>)output).r[0]);
    }

    [TestMethod]
    public void InactiveDependenciesPropagateThroughEveryOutputOfAMultipleOutputNode()
    {
        var gate = Gate(true);
        gate.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        var multiple = new MultipleOutputProvider(EffectArgumentFieldType.IPicture,
            [Port("Image", EffectArgumentFieldType.IPicture), Port("Number", EffectArgumentFieldType.Integer)],
            _ => throw new AssertFailedException("The inactive multiple-output node must not run."));
        multiple.SetMainInputSource(Output(gate, false));
        var consumer = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture,
            _ => throw new AssertFailedException("The inactive downstream node must not run."), Port("Amount", EffectArgumentFieldType.Integer));
        consumer.SetMainInputSource(EffectProviderOutputExtensions.CreateOutputSourceId(multiple.Id, "Image"));
        consumer.SetFieldBinding("Amount", EffectProviderOutputExtensions.CreateOutputSourceId(multiple.Id, "Number"));
        var join = Join(consumer.Id.ToString(), Output(gate, true));
        join.SetFinalOutputSource(true);
        using var graph = Build(Providers(gate, multiple, consumer, join));
        using var input = Picture(3);
        using var output = Evaluate(graph, input);
        Assert.AreEqual((byte)3, ((IPicture<byte>)output).r[0]);
    }

    [TestMethod]
    public void NestedInactiveConditionDoesNotEvaluateItsPredicate()
    {
        var predicate = new Provider(null, EffectArgumentFieldType.Boolean,
            _ => throw new AssertFailedException("The nested inactive condition must not run."));
        var outer = Gate(false);
        outer.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        var inner = Gate(true);
        inner.SetMainInputSource(Output(outer, true));
        inner.SetFieldBinding("Condition", predicate.Id.ToString());
        var join = Join(Output(inner, true), Output(outer, false));
        join.SetFinalOutputSource(true);
        using var graph = Build(Providers(predicate, outer, inner, join));
        using var input = Picture(8);
        using var output = Evaluate(graph, input);
        Assert.AreEqual((byte)8, ((IPicture<byte>)output).r[0]);
    }

    [TestMethod]
    public void DynamicConditionAndSharedDataAreComputedOncePerFrame()
    {
        int conditionCalls = 0, inputCalls = 0, trueCalls = 0, falseCalls = 0;
        object value = 7L;
        var predicate = new Provider(null, EffectArgumentFieldType.Boolean, c => { conditionCalls++; return c.FrameIndex % 2 == 0; });
        var source = new Provider(null, EffectArgumentFieldType.Long, _ => { inputCalls++; return value; });
        var gate = Gate(false);
        gate.SetMainInputSource(source.Id);
        gate.SetFieldBinding("Condition", predicate.Id.ToString());
        Provider Route(bool branch)
        {
            var provider = new Provider(EffectArgumentFieldType.Any, EffectArgumentFieldType.IPicture, c =>
            {
                Assert.AreSame(value, c.Input);
                Assert.AreSame(value, c.Parameters["Repeat"]);
                if (branch) trueCalls++;
                else falseCalls++;
                return Picture(branch ? (byte)1 : (byte)2);
            }, Port("Repeat", EffectArgumentFieldType.Any));
            provider.SetMainInputSource(Output(gate, branch));
            provider.SetFieldBinding("Repeat", Output(gate, branch));
            return provider;
        }
        var yes = Route(true);
        var no = Route(false);
        var join = Join(yes.Id.ToString(), no.Id.ToString());
        join.SetFinalOutputSource(true);
        using var graph = Build(Providers(predicate, source, gate, yes, no, join));
        using var input = Picture(0);
        using var first = Evaluate(graph, input, 0);
        using var second = Evaluate(graph, input, 1);
        Assert.AreEqual((byte)1, ((IPicture<byte>)first).r[0]);
        Assert.AreEqual((byte)2, ((IPicture<byte>)second).r[0]);
        Assert.AreEqual(2, conditionCalls);
        Assert.AreEqual(2, inputCalls);
        Assert.AreEqual(1, trueCalls);
        Assert.AreEqual(1, falseCalls);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ExistingIfConditionSelectsOnlyItsChosenInput(bool condition)
    {
        var predicate = new Provider(null, EffectArgumentFieldType.Boolean, _ => condition);
        var gate = Gate(false);
        gate.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        gate.SetFieldBinding("Condition", predicate.Id.ToString());
        var selected = new IfConditionNodeEffectProvider();
        selected.SetFieldBinding(IfConditionNodeEffectProvider.ConditionKey, predicate.Id.ToString());
        selected.SetFieldBinding("TrueValue", Output(gate, true));
        selected.SetFieldBinding("FalseValue", Output(gate, false));
        selected.SetFinalOutputSource(true);
        using var graph = Build(Providers(predicate, gate, selected));
        using var input = Picture(5);
        using var output = Evaluate(graph, input);
        Assert.AreEqual((byte)5, ((IPicture<byte>)output).r[0]);
    }

    [TestMethod]
    public void DisabledConditionClosesBothOutputsWithoutEvaluatingItsInputs()
    {
        var source = new Provider(null, EffectArgumentFieldType.IPicture,
            _ => throw new AssertFailedException("Disabled condition inputs must not run."));
        var gate = Gate(true);
        gate.Enabled = false;
        gate.SetMainInputSource(source.Id);
        var closed = Join(Output(gate, true), Output(gate, false));
        var join = Join(closed.Id.ToString(), IEffectProvider.InputAnchorGUID.ToString());
        join.SetFinalOutputSource(true);
        using var graph = Build(Providers(source, gate, closed, join));
        using var input = Picture(9);
        using var output = Evaluate(graph, input);
        Assert.AreEqual((byte)9, ((IPicture<byte>)output).r[0]);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ConditionalUpstreamFailuresUseDefaultAndCancellationPropagates(bool canceled)
    {
        var predicate = new Provider(null, EffectArgumentFieldType.Boolean, _ =>
        {
            if (canceled) throw new OperationCanceledException();
            throw new InvalidOperationException("Failed predicate.");
        });
        var gate = Gate(false);
        gate.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        gate.SetFieldBinding("Condition", predicate.Id.ToString());
        var join = Join(Output(gate, true), IEffectProvider.InputAnchorGUID.ToString());
        join.SetFinalOutputSource(true);
        using var graph = Build(Providers(predicate, gate, join));
        using var input = Picture(4);
        if (canceled) Assert.ThrowsExactly<OperationCanceledException>(() => Evaluate(graph, input));
        else
        {
            using var output = Evaluate(graph, input);
            Assert.AreEqual((byte)4, ((IPicture<byte>)output).r[0]);
        }
        Assert.IsFalse(input.Disposed);
    }

    [TestMethod]
    public void InactiveFinalOutputReportsInvalidContent()
    {
        var gate = Gate(false);
        gate.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        gate.SetFinalOutputSource(true, "True");
        using var graph = Build(Providers(gate));
        using var input = Picture(0);
        Assert.ThrowsExactly<InvalidOperationException>(() => Evaluate(graph, input));
        Assert.IsFalse(input.Disposed);
    }

    [TestMethod]
    public void BothPortsFollowTheMainBindingAndScalarValuesKeepTheirTypes()
    {
        (EffectArgumentFieldType Type, object Value)[] values =
        [
            (EffectArgumentFieldType.Long, 0L), (EffectArgumentFieldType.Numeric, 0.25m),
            (EffectArgumentFieldType.Boolean, false), (EffectArgumentFieldType.String, ""),
        ];
        foreach (var (type, value) in values)
        {
            var source = new Provider(null, type, _ => value);
            var gate = Gate(false);
            gate.SetMainInputSource(source.Id);
            var consumer = new Provider(EffectArgumentFieldType.Any, EffectArgumentFieldType.IPicture, c =>
            {
                Assert.AreSame(value, c.Input);
                return Picture(1);
            });
            consumer.SetMainInputSource(Output(gate, false));
            consumer.SetFinalOutputSource(true);
            var providers = Providers(source, gate, consumer);
            var types = new EffectFieldTypeResolution(providers);
            Assert.AreEqual(type, types.GetInputType(gate, EffectProviderBase.PrimaryInputAnchorKey));
            Assert.AreEqual(type, types.GetOutputType(gate, "True"));
            Assert.AreEqual(type, types.GetOutputType(gate, "False"));
            using var graph = Build(providers);
            using var input = Picture(0);
            using var output = Evaluate(graph, input);
            Assert.AreEqual((byte)1, ((IPicture<byte>)output).r[0]);
        }
        var first = new Provider(null, EffectArgumentFieldType.Long, _ => 1L);
        var second = new Provider(null, EffectArgumentFieldType.String, _ => "text");
        var target = Gate(true);
        target.SetMainInputSource(first.Id);
        var original = new EffectFieldTypeResolution(Providers(first, second, target));
        var candidate = original.ResolveBindingCandidate(target, EffectProviderBase.PrimaryInputAnchorKey, second.Id.ToString())!;
        Assert.AreEqual(EffectArgumentFieldType.Long, original.GetOutputType(target, "True"));
        Assert.AreEqual(EffectArgumentFieldType.String, candidate.GetOutputType(target, "True"));
        Assert.AreEqual(EffectArgumentFieldType.String, candidate.GetOutputType(target, "False"));
        Assert.AreEqual(first.Id.ToString(), target.GetMainInputSource());
    }

    [TestMethod]
    public void DirectComputationRejectsMissingInputInvalidConditionAndCancellation()
    {
        var effect = new ConditionComputeNodeEffect();
        object value = new();
        foreach (bool condition in new[] { true, false })
        {
            var outputs = effect.ComputeOutputs(new() { Input = value, Parameters = new Dictionary<string, object?> { ["Condition"] = condition } });
            Assert.AreEqual(2, outputs.Count);
            Assert.AreSame(value, outputs[condition ? "True" : "False"]);
            Assert.AreNotSame(value, outputs[condition ? "False" : "True"]);
        }
        Assert.ThrowsExactly<ArgumentException>(() => effect.ComputeOutputs(new()));
        Assert.ThrowsExactly<ArgumentException>(() => effect.ComputeOutputs(new()
        {
            Input = value, Parameters = new Dictionary<string, object?> { ["Condition"] = "true" },
        }));
        Assert.ThrowsExactly<OperationCanceledException>(() => effect.ComputeOutputs(new()
        {
            Input = value, CancellationToken = new CancellationToken(true),
        }));
    }

    [TestMethod]
    public void RegisteredProviderAndItsBindingsRoundTripThroughSerialization()
    {
        var plugin = new InternalPluginBase();
        Assert.IsInstanceOfType<ConditionComputeNodeEffectProvider>(plugin.EffectProviderProvider["ConditionComputeNode"]());
        Assert.IsInstanceOfType<ConditionComputeNodeEffect>(plugin.EffectImplementationProvider[
            new("ConditionComputeNode", EffectImplementType.NotSpecified)]());
        var gate = Gate(true);
        Assert.IsFalse(gate.InFields.ContainsKey(ConditionComputeNodeEffectProvider.ConditionKey));
        Assert.AreEqual(EffectArgumentFieldType.Boolean, gate.Fields[ConditionComputeNodeEffectProvider.ConditionKey].FieldType);
        gate.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        var join = Join(Output(gate, true), Output(gate, false));
        join.SetFinalOutputSource(true);
        var plugins = (IDictionary<string, IPluginBase>)PluginManager.LoadedPlugins;
        plugins.TryGetValue(plugin.PluginID, out var previous);
        plugins[plugin.PluginID] = plugin;
        try
        {
            var dtos = new[] { EffectBindingHelper.SerializeProvider(gate), EffectBindingHelper.SerializeProvider(join) };
            var restored = EffectBindingHelper.MigrateToEffectProviders(JsonSerializer.Deserialize<EffectProviderJSONStructure[]>(JsonSerializer.Serialize(dtos)), null, out var diagnostics);
            Assert.AreEqual(0, diagnostics.Count);
            Assert.IsInstanceOfType<IMultipleOutputEffectProvider>(restored[gate.Id]);
            Assert.AreEqual(Output(gate, true), restored[join.Id].GetMainInputSource());
            Assert.IsTrue(restored[join.Id].TryGetFieldBinding("DefaultValue", out var fallback));
            Assert.AreEqual(Output(gate, false), fallback);
            using var graph = Build(restored);
            using var input = Picture(2);
            using var output = Evaluate(graph, input);
            Assert.AreEqual((byte)2, ((IPicture<byte>)output).r[0]);
        }
        finally
        {
            if (previous is null) plugins.Remove(plugin.PluginID);
            else plugins[plugin.PluginID] = previous;
        }
    }
}
