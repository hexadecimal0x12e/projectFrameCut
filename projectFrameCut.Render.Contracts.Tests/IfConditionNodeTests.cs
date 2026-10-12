using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using static projectFrameCut.Render.Contracts.Tests.DynamicEffectGraphTests;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class IfConditionNodeTests
{
    private static DynamicEffectGraph Graph(IfConditionNodeEffectProvider target, params Provider[] sources)
    {
        var providers = Providers(sources.Cast<IEffectProvider>().Append(target).ToArray());
        Assert.AreEqual(0, DynamicEffectBindings.Validate(providers).Count);
        var effects = sources.ToDictionary(p => p.Id, p => ((IEffectProvider)p).Build());
        effects[target.Id] = [new IfConditionNodeEffect()];
        return new(providers, effects);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ReturnsTheSelectedValueUnchanged(bool condition)
    {
        object value = new object();
        var effect = new IfConditionNodeEffect();
        Assert.AreSame(value, effect.Compute(new()
        {
            Input = condition,
            Parameters = new Dictionary<string, object?>
            {
                ["TrueValue"] = condition ? value : null,
                ["FalseValue"] = condition ? null : value,
            },
        }));
        Assert.IsNull(effect.Compute(new() { Input = condition }));
        Assert.ThrowsExactly<ArgumentException>(() => effect.Compute(new() { Input = 1 }));
        Assert.ThrowsExactly<OperationCanceledException>(() => effect.Compute(new()
        {
            Input = condition, CancellationToken = new CancellationToken(true),
        }));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void OnlyEvaluatesTheSelectedPictureBranch(bool condition)
    {
        int calls = 0;
        IPicture? produced = null;
        var source = new Provider(null, EffectArgumentFieldType.Boolean, _ => condition);
        var selected = new Provider(null, EffectArgumentFieldType.IPicture, _ =>
        {
            calls++;
            return produced = Picture(6);
        });
        var unselected = new Provider(null, EffectArgumentFieldType.IPicture,
            _ => throw new AssertFailedException("Unselected branch should not be evaluated."));
        var target = new IfConditionNodeEffectProvider();
        target.SetMainInputSource(source.Id);
        target.SetFieldBinding("TrueValue", (condition ? selected : unselected).Id.ToString());
        target.SetFieldBinding("FalseValue", (condition ? unselected : selected).Id.ToString());
        target.SetFinalOutputSource(true);
        using var graph = Graph(target, source, selected, unselected);
        using var input = Picture(0);
        using var result = Evaluate(graph, input);
        Assert.AreEqual((byte)6, ((IPicture<byte>)result).r[0]);
        Assert.AreEqual(1, calls);
        Assert.IsTrue(produced!.Disposed);
        Assert.IsFalse(result.Disposed);
        Assert.IsFalse(input.Disposed);
    }

    [TestMethod]
    public void NumericBranchKeepsItsOriginalTypeAndValue()
    {
        object value = 0L;
        var condition = new Provider(null, EffectArgumentFieldType.Boolean, _ => false);
        var source = new Provider(null, EffectArgumentFieldType.Long, _ => value);
        var target = new IfConditionNodeEffectProvider
        {
            Fields = new() { ["OutputType"] = new StaticEffectArgumentField("Long", EffectArgumentFieldType.String) },
        };
        var consumer = new Provider(EffectArgumentFieldType.Any, EffectArgumentFieldType.IPicture, c =>
        {
            Assert.AreSame(value, c.Input);
            return Picture(4);
        });
        target.SetMainInputSource(condition.Id);
        target.SetFieldBinding("FalseValue", source.Id.ToString());
        consumer.SetMainInputSource(target.Id);
        consumer.SetFinalOutputSource(true);
        Assert.AreEqual(EffectArgumentFieldType.Any, target.OutField.FieldType);
        Assert.AreEqual(EffectArgumentFieldType.Long, new EffectFieldTypeResolution(Providers(target, condition, source, consumer)).GetOutputType(target));
        Assert.IsFalse(EffectBindingHelper.SerializeProvider(target).StaticFields!.ContainsKey("OutputType"));
        using var graph = Graph(target, condition, source, consumer);
        using var input = Picture(0);
        using var result = Evaluate(graph, input);
        Assert.AreEqual((byte)4, ((IPicture<byte>)result).r[0]);
    }

    [TestMethod]
    public void CandidateBindingPropagatesTypesAndRejectsConflictingBranches()
    {
        var first = new Provider(null, EffectArgumentFieldType.Integer, _ => 4);
        var numeric = new Provider(null, EffectArgumentFieldType.Numeric, _ => 0.5f);
        var text = new Provider(null, EffectArgumentFieldType.String, _ => "text");
        var target = new IfConditionNodeEffectProvider();
        target.SetFieldBinding("FalseValue", text.Id.ToString());
        var providers = Providers(first, numeric, text, target);
        var original = new EffectFieldTypeResolution(providers);
        Assert.AreEqual(EffectArgumentFieldType.String, original.GetOutputType(target));
        var candidate = new EffectFieldTypeResolution(providers, new Dictionary<Guid, IReadOnlyDictionary<string, string>>
        {
            [target.Id] = new Dictionary<string, string>(target.AnchorsBindingState) { ["TrueValue"] = first.Id.ToString() },
        });
        Assert.AreEqual(EffectArgumentFieldType.Integer, candidate.GetOutputType(target));
        Assert.AreEqual(EffectArgumentFieldType.Integer, candidate.GetInputType(target, "FalseValue"));
        Assert.IsFalse(candidate.AreInputsCompatible(target));
        Assert.IsFalse(target.TryGetFieldBinding("TrueValue", out _));
        target.SetFieldBinding("TrueValue", first.Id.ToString());
        Assert.IsTrue(DynamicEffectBindings.Validate(providers).Any(d => d.Code == "IncompatibleInput"));
        target.SetFieldBinding("FalseValue", numeric.Id.ToString());
        Assert.AreEqual(0, DynamicEffectBindings.Validate(providers).Count);
        Assert.IsTrue(new EffectFieldTypeResolution(providers).AreInputsCompatible(target));
        Assert.AreEqual(EffectArgumentFieldType.String, original.GetOutputType(target));
    }

    [TestMethod]
    public void ProviderAndImplementationAreRegistered()
    {
        var plugin = new InternalPluginBase();
        var provider = plugin.EffectProviderProvider["IfConditionNode"]();
        Assert.AreEqual(EffectArgumentFieldType.Boolean,
            provider.InFields[EffectProviderBase.PrimaryInputAnchorKey].FieldType.BaseType());
        Assert.AreEqual(EffectArgumentFieldType.Any, provider.InFields["TrueValue"].FieldType.BaseType());
        Assert.AreEqual(EffectArgumentFieldType.Any, provider.InFields["FalseValue"].FieldType.BaseType());
        Assert.IsInstanceOfType<IfConditionNodeEffect>(plugin.EffectImplementationProvider[
            new("IfConditionNode", EffectImplementType.NotSpecified)]());
    }
}
