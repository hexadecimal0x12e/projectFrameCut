using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using static projectFrameCut.Render.Contracts.Tests.DynamicEffectGraphTests;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class IsUpstreamAvailableOrDefaultTests
{
    private static DynamicEffectGraph Graph(IsUpstreamAvailableOrDefaultEffectProvider target, params Provider[] sources)
    {
        var providers = Providers(sources.Cast<IEffectProvider>().Append(target).ToArray());
        Assert.AreEqual(0, DynamicEffectBindings.Validate(providers).Count);
        var effects = sources.ToDictionary(p => p.Id, p => ((IEffectProvider)p).Build());
        effects[target.Id] = [new IsUpstreamAvailableOrDefaultEffect()];
        return new(providers, effects);
    }

    [TestMethod]
    public void AvailableValuesAreReturnedUnchanged()
    {
        var effect = new IsUpstreamAvailableOrDefaultEffect();
        foreach (var value in new object[] { 0, false, "", new object() })
            Assert.AreSame(value, effect.Compute(new() { Input = value, Parameters = new Dictionary<string, object?> { ["DefaultValue"] = 99 } }));
        var fallback = new object();
        Assert.AreSame(fallback, effect.Compute(new() { Parameters = new Dictionary<string, object?> { ["DefaultValue"] = fallback } }));
    }

    [TestMethod]
    public void MissingOrNullUpstreamUsesDefaultPicture()
    {
        foreach (bool connected in new[] { false, true })
        {
            var source = new Provider(null, EffectArgumentFieldType.IPicture, _ => null);
            var target = new IsUpstreamAvailableOrDefaultEffectProvider();
            if (connected) target.SetMainInputSource(source.Id);
            target.SetFieldBinding("DefaultValue", IEffectProvider.InputAnchorGUID.ToString());
            target.SetFinalOutputSource(true);
            using var graph = Graph(target, source);
            using var input = Picture(7);
            using var result = Evaluate(graph, input);
            Assert.AreEqual((byte)7, ((projectFrameCut.Drawing.Base.IPicture<byte>)result).r[0]);
            Assert.IsFalse(input.Disposed);
        }
    }

    [TestMethod]
    public void AvailableUpstreamDoesNotEvaluateDefault()
    {
        var fallback = new Provider(null, EffectArgumentFieldType.IPicture,
            _ => throw new AssertFailedException("Default should not be evaluated."));
        var target = new IsUpstreamAvailableOrDefaultEffectProvider();
        target.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        target.SetFieldBinding("DefaultValue", fallback.Id.ToString());
        target.SetFinalOutputSource(true);
        using var graph = Graph(target, fallback);
        using var input = Picture(8);
        using var result = Evaluate(graph, input);
        Assert.AreEqual((byte)8, ((projectFrameCut.Drawing.Base.IPicture<byte>)result).r[0]);
    }

    [TestMethod]
    public void FailedUpstreamUsesDefaultButCancellationPropagates()
    {
        foreach (bool canceled in new[] { false, true })
        {
            var source = new Provider(null, EffectArgumentFieldType.IPicture, _ =>
            {
                if (canceled) throw new OperationCanceledException();
                throw new InvalidOperationException("Unavailable upstream.");
            });
            var target = new IsUpstreamAvailableOrDefaultEffectProvider();
            target.SetMainInputSource(source.Id);
            target.SetFieldBinding("DefaultValue", IEffectProvider.InputAnchorGUID.ToString());
            target.SetFinalOutputSource(true);
            using var graph = Graph(target, source);
            using var input = Picture(9);
            if (canceled) Assert.ThrowsExactly<OperationCanceledException>(() => Evaluate(graph, input));
            else
            {
                using var result = Evaluate(graph, input);
                Assert.AreEqual((byte)9, ((projectFrameCut.Drawing.Base.IPicture<byte>)result).r[0]);
            }
        }
    }

    [TestMethod]
    public void SelectedNumericOutputKeepsTheOriginalValue()
    {
        object value = 0L;
        var source = new Provider(null, EffectArgumentFieldType.Long, _ => value);
        var target = new IsUpstreamAvailableOrDefaultEffectProvider
        {
            Fields = new() { ["OutputType"] = new StaticEffectArgumentField("Long", EffectArgumentFieldType.String) },
        };
        var consumer = new Provider(EffectArgumentFieldType.Any, EffectArgumentFieldType.IPicture, c =>
        {
            Assert.AreSame(value, c.Input);
            return Picture(4);
        });
        target.SetMainInputSource(source.Id);
        consumer.SetMainInputSource(target.Id);
        consumer.SetFinalOutputSource(true);
        Assert.AreEqual(EffectArgumentFieldType.Any, target.OutField.FieldType);
        Assert.AreEqual(EffectArgumentFieldType.Long, new EffectFieldTypeResolution(Providers(target, source, consumer)).GetOutputType(target));
        Assert.IsFalse(EffectBindingHelper.SerializeProvider(target).StaticFields!.ContainsKey("OutputType"));
        using var graph = Graph(target, source, consumer);
        using var input = Picture(0);
        using var result = Evaluate(graph, input);
        Assert.AreEqual((byte)4, ((projectFrameCut.Drawing.Base.IPicture<byte>)result).r[0]);
    }

    [TestMethod]
    public void DefaultDeterminesTheTypeUntilAnUpstreamIsConnected()
    {
        var upstream = new Provider(null, EffectArgumentFieldType.Long, _ => 1L);
        var fallback = new Provider(null, EffectArgumentFieldType.String, _ => "fallback");
        var target = new IsUpstreamAvailableOrDefaultEffectProvider();
        target.SetFieldBinding("DefaultValue", fallback.Id.ToString());
        var providers = Providers(upstream, fallback, target);
        var types = new EffectFieldTypeResolution(providers);
        Assert.AreEqual(EffectArgumentFieldType.String, types.GetOutputType(target));
        Assert.AreEqual(EffectArgumentFieldType.String, types.GetInputType(target, EffectProviderAnchorExtensions.InputKey));
        target.SetMainInputSource(upstream.Id);
        types = new(providers);
        Assert.AreEqual(EffectArgumentFieldType.Long, types.GetOutputType(target));
        Assert.AreEqual(EffectArgumentFieldType.Long, types.GetInputType(target, "DefaultValue"));
        Assert.IsFalse(types.AreInputsCompatible(target));
        Assert.IsTrue(DynamicEffectBindings.Validate(providers).Any(d => d.Code == "IncompatibleInput"));
    }

    [TestMethod]
    public void ProviderAndImplementationAreRegistered()
    {
        var plugin = new InternalPluginBase();
        var provider = plugin.EffectProviderProvider["IsUpstreamAvailableOrDefault"]();
        Assert.AreEqual(EffectArgumentFieldType.Any, provider.InFields[EffectProviderBase.PrimaryInputAnchorKey].FieldType);
        Assert.IsInstanceOfType<IsUpstreamAvailableOrDefaultEffect>(plugin.EffectImplementationProvider[
            new("IsUpstreamAvailableOrDefault", projectFrameCut.Shared.EffectImplementType.NotSpecified)]());
    }
}
