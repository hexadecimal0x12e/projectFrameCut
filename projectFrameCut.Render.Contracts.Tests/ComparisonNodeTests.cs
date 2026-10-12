using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using static projectFrameCut.Render.Contracts.Tests.DynamicEffectGraphTests;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class ComparisonNodeTests
{
    private static EffectExecutionContext Context(object? a, object? b, string operation = ">") => new()
    {
        Parameters = new Dictionary<string, object?> { ["A"] = a, ["B"] = b, ["Operation"] = operation },
    };

    private static void CheckGraph(IEffectProvider comparison, IEffect effect, object? a, object? b, bool expected)
    {
        var left = new Provider(null, EffectArgumentFieldType.Unknown, _ => a);
        var right = new Provider(null, EffectArgumentFieldType.Unknown, _ => b);
        var consumer = new Provider(EffectArgumentFieldType.Boolean, EffectArgumentFieldType.IPicture, c =>
        {
            Assert.AreEqual(expected, c.Input);
            return Picture(3);
        });
        comparison.SetFieldBinding("A", left.Id.ToString());
        comparison.SetFieldBinding("B", right.Id.ToString());
        consumer.SetMainInputSource(comparison.Id);
        consumer.SetFinalOutputSource(true);
        var providers = Providers(comparison, left, right, consumer);
        Assert.AreEqual(0, DynamicEffectBindings.Validate(providers).Count);
        using var graph = new DynamicEffectGraph(providers, new Dictionary<Guid, IEffect[]>()
        {
            [comparison.Id] = [effect], [left.Id] = ((IEffectProvider)left).Build(),
            [right.Id] = ((IEffectProvider)right).Build(), [consumer.Id] = ((IEffectProvider)consumer).Build(),
        });
        using var input = Picture(0);
        using var result = Evaluate(graph, input);
    }

    [TestMethod]
    [DataRow(">", 2d, 1d, true)]
    [DataRow(">", 1d, 1d, false)]
    [DataRow(">=", 1d, 1d, true)]
    [DataRow(">=", 0d, 1d, false)]
    [DataRow("<", -1d, 1d, true)]
    [DataRow("<", 1d, 1d, false)]
    [DataRow("<=", 1d, 1d, true)]
    [DataRow("<=", 2d, 1d, false)]
    public void NumericOperations(string operation, double a, double b, bool expected) =>
        Assert.AreEqual(expected, new NumericComparisonNodeEffect().Compute(Context(a, b, operation)));

    [TestMethod]
    public void NumericGraphRetainsIntegerAndDecimalPrecision()
    {
        (object A, object B)[] pairs =
        [
            (16777217, 16777216), (long.MaxValue, long.MaxValue - 1),
            (ulong.MaxValue, long.MaxValue), (1.0000000000000000000000000001m, 1m),
        ];
        foreach (var (a, b) in pairs)
            CheckGraph(new NumericComparisonNodeEffectProvider(), new NumericComparisonNodeEffect(), a, b, true);
    }

    [TestMethod]
    public void NumericNaNAndInvalidInputs()
    {
        var effect = new NumericComparisonNodeEffect();
        foreach (var operation in new[] { ">", ">=", "<", "<=" })
            Assert.AreEqual(false, effect.Compute(Context(double.NaN, 1, operation)));
        Assert.AreEqual(true, effect.Compute(Context(double.PositiveInfinity, double.MaxValue)));
        Assert.ThrowsExactly<ArgumentException>(() => effect.Compute(Context("1", 1)));
        Assert.ThrowsExactly<ArgumentException>(() => effect.Compute(Context(null, 1)));
        Assert.ThrowsExactly<ArgumentException>(() => effect.Compute(Context(1, 1, "invalid")));
    }

    [TestMethod]
    public void EqualityRequiresTheSameTypeAndValue()
    {
        var same = new object();
        (object? A, object? B, bool Equal)[] pairs =
        [
            (1, 1, true), (1, 1f, false), (1, 1L, false), (1L, 1UL, false),
            ("text", new string("text".ToCharArray()), true), ("text", "Text", false),
            (true, true, true), (false, true, false), (null, null, true), (null, "", false),
            (same, same, true), (new object(), new object(), false),
        ];
        foreach (var (a, b, equal) in pairs)
            CheckGraph(new EqualityComparisonNodeEffectProvider(), new EqualityComparisonNodeEffect(), a, b, equal);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PictureEqualityUsesContentIDsAfterGraphCopies(bool different)
    {
        using var left = Picture(7);
        using var right = DynamicEffectGraph.CopyPicture(left);
        Assert.AreNotSame(left, right);
        if (different) ((projectFrameCut.Drawing.Base.IPicture<byte>)right).r[0] = 8;
        Assert.AreEqual(!different, left.GetUniqueID() == right.GetUniqueID());
        var effect = new EqualityComparisonNodeEffect();
        Assert.AreEqual(!different, effect.Compute(Context(left, right)));
        CheckGraph(new EqualityComparisonNodeEffectProvider(), effect, left, right, !different);
    }

    [TestMethod]
    public void BothNodesAreRegisteredAndPropagateCancellation()
    {
        var plugin = new InternalPluginBase();
        foreach (var name in new[] { "NumericComparisonNode", "EqualityComparisonNode" })
        {
            Assert.AreEqual(EffectArgumentFieldType.Boolean, plugin.EffectProviderProvider[name]().OutField.FieldType);
            var effect = plugin.EffectImplementationProvider[new(name, EffectImplementType.NotSpecified)]();
            Assert.ThrowsExactly<OperationCanceledException>(() => effect.Compute(new()
            {
                Parameters = Context(1, 1).Parameters, CancellationToken = new CancellationToken(true),
            }));
        }
    }
}
