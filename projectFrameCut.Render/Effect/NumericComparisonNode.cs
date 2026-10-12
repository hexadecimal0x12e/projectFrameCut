using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

public sealed class NumericComparisonNodeEffectProvider : EffectProviderBase
{
    public NumericComparisonNodeEffectProvider() => Name = "NumericComparisonNode";
    public override string TypeName => "NumericComparisonNode";
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override EffectType TypeOfEffect => EffectType.NormalEffect;
    public override EffectTarget Target => EffectTarget.Video | EffectTarget.ValueProvider;

    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() =>
    [
        Field("Operation", EffectArgumentFieldType.String | EffectArgumentFieldType.CannotBeDynamic,
            ">", presetOptions: [">", ">=", "<", "<="]),
    ];

    protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() =>
        new Dictionary<string, EffectArgumentFieldDescriptor>
        {
            ["A"] = Field("A", EffectArgumentFieldType.Numeric, "0"),
            ["B"] = Field("B", EffectArgumentFieldType.Numeric, "0"),
        };

    protected override EffectArgumentFieldDescriptor DefineOutField() => Field(OutputAnchorKey, EffectArgumentFieldType.Boolean, "false");
}

public sealed class NumericComparisonNodeEffect : IEffect
{
    public string TypeName => "NumericComparisonNode";
    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public EffectType TypeOfEffect => EffectType.NormalEffect;
    public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "NumericComparisonNode";
    public bool Enabled { get; set; } = true;
    public int Index { get; set; }
    public int RelativeWidth { get; set; }
    public int RelativeHeight { get; set; }
    public bool IsReorderable => false;
    public string? BindedEffectProvidingSystemID { get; set; }
    public Dictionary<string, object> Parameters { get; set; } = new();
    public IEffect WithParameters(Dictionary<string, object> parameters) => new NumericComparisonNodeEffect { Parameters = parameters };

    public object Compute(EffectExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var a = context.Parameters.GetValueOrDefault("A");
        var b = context.Parameters.GetValueOrDefault("B");
        if (a is null || b is null || !EffectFieldTypes.Accepts(EffectArgumentFieldType.Numeric, a)
            || !EffectFieldTypes.Accepts(EffectArgumentFieldType.Numeric, b))
            throw new ArgumentException("NumericComparisonNode requires two numeric inputs.");
        var operation = context.Parameters.GetValueOrDefault("Operation")?.ToString() ?? ">";
        if (a is float or double || b is float or double)
        {
            double left = Convert.ToDouble(a), right = Convert.ToDouble(b);
            return operation switch
            {
                ">" => left > right,
                ">=" => left >= right,
                "<" => left < right,
                "<=" => left <= right,
                _ => throw new ArgumentException($"Unknown numeric comparison '{operation}'."),
            };
        }
        decimal x = Convert.ToDecimal(a), y = Convert.ToDecimal(b);
        return operation switch
        {
            ">" => x > y,
            ">=" => x >= y,
            "<" => x < y,
            "<=" => x <= y,
            _ => throw new ArgumentException($"Unknown numeric comparison '{operation}'."),
        };
    }
}
