using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

public sealed class EqualityComparisonNodeEffectProvider : EffectProviderBase
{
    public EqualityComparisonNodeEffectProvider() => Name = "EqualityComparisonNode";
    public override string TypeName => "EqualityComparisonNode";
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override EffectType TypeOfEffect => EffectType.NormalEffect;
    public override EffectTarget Target => EffectTarget.Video | EffectTarget.ValueProvider;
    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => [];

    protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() =>
        new Dictionary<string, EffectArgumentFieldDescriptor>
        {
            ["A"] = Field("A", EffectArgumentFieldType.Any | EffectArgumentFieldType.CanNotBeStatic, ""),
            ["B"] = Field("B", EffectArgumentFieldType.Any | EffectArgumentFieldType.CanNotBeStatic, ""),
        };

    protected override EffectArgumentFieldDescriptor DefineOutField() => Field(OutputAnchorKey, EffectArgumentFieldType.Boolean, "false");
}

public sealed class EqualityComparisonNodeEffect : IEffect
{
    public string TypeName => "EqualityComparisonNode";
    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public EffectType TypeOfEffect => EffectType.NormalEffect;
    public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "EqualityComparisonNode";
    public bool Enabled { get; set; } = true;
    public int Index { get; set; }
    public int RelativeWidth { get; set; }
    public int RelativeHeight { get; set; }
    public bool IsReorderable => false;
    public string? BindedEffectProvidingSystemID { get; set; }
    public Dictionary<string, object> Parameters { get; set; } = new();
    public IEffect WithParameters(Dictionary<string, object> parameters) => new EqualityComparisonNodeEffect { Parameters = parameters };

    public object Compute(EffectExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var a = context.Parameters.GetValueOrDefault("A");
        var b = context.Parameters.GetValueOrDefault("B");
        if (a is null) return b is null;
        if (b is null || a.GetType() != b.GetType()) return false;
        return a is IPicture left && b is IPicture right
            ? left.GetUniqueID() == right.GetUniqueID() : a.Equals(b);
    }
}
