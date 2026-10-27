using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

public sealed class IfConditionNodeEffectProvider : EffectProviderBase
{
    public override IReadOnlyList<Stage> SupportedStages => EffectStages.Values;

    public override bool IsDeterministic => true;

    public const string ConditionKey = "Condition";

    public IfConditionNodeEffectProvider() => Name = "IfConditionNode";
    public override string TypeName => "IfConditionNode";
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override EffectType TypeOfEffect => EffectType.NormalEffect;
    public override EffectTarget Target => EffectTarget.Video | EffectTarget.ValueProvider;

    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() =>
    [
        Field(ConditionKey, EffectArgumentFieldType.Boolean | EffectArgumentFieldType.Mandatory, "false"),
    ];

    protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() =>
        new Dictionary<string, EffectArgumentFieldDescriptor>
        {
            ["TrueValue"] = Field("TrueValue", EffectArgumentFieldType.Any | EffectArgumentFieldType.CanNotBeStatic, ""),
            ["FalseValue"] = Field("FalseValue", EffectArgumentFieldType.Any | EffectArgumentFieldType.CanNotBeStatic, ""),
        };

    protected override EffectArgumentFieldDescriptor DefineOutField() => Field(OutputAnchorKey, EffectArgumentFieldType.Any, "");

    public override object? ResolveDataFormat(string fieldId, bool isInput, EffectFieldTypeContext context)
    {
        if (isInput) return base.ResolveDataFormat(fieldId, true, context);
        var a = context.InputDataFormats.GetValueOrDefault("TrueValue");
        var b = context.InputDataFormats.GetValueOrDefault("FalseValue");
        return a is PictureDataFormat && Equals(a, b) ? a : null;
    }

    public override EffectArgumentFieldType ResolveFieldType(string fieldId, bool isInput, EffectFieldTypeContext context)
    {
        if (isInput && context.State.Fields.TryGetValue(fieldId, out var field) && !field.FieldType.HasFlag(EffectArgumentFieldType.Any)) return field.FieldType;
        var type = context.InputTypes.GetValueOrDefault("TrueValue", EffectArgumentFieldType.Any);
        return type.HasFlag(EffectArgumentFieldType.Any) || type.BaseType() == EffectArgumentFieldType.Unknown
            ? context.InputTypes.GetValueOrDefault("FalseValue", EffectArgumentFieldType.Any) : type;
    }
}

public sealed class IfConditionNodeEffect : IEffect
{
    public bool IsDeterministic => true;
    public bool AcceptsHwAccelPictures => true;

    public string TypeName => "IfConditionNode";
    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public EffectType TypeOfEffect => EffectType.NormalEffect;
    public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "IfConditionNode";
    public bool Enabled { get; set; } = true;
    public int Index { get; set; }
    public int RelativeWidth { get; set; }
    public int RelativeHeight { get; set; }
    public bool IsReorderable => false;
    public string? BindedEffectProvidingSystemID { get; set; }
    public Dictionary<string, object> Parameters { get; set; } = new();
    public IEffect WithParameters(Dictionary<string, object> parameters) => new IfConditionNodeEffect { Parameters = parameters };

    public object? Compute(EffectExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (context.Parameters.GetValueOrDefault(IfConditionNodeEffectProvider.ConditionKey, false) is not bool condition)
            throw new ArgumentException("IfConditionNode requires a bool condition.");
        return context.Parameters.GetValueOrDefault(condition ? "TrueValue" : "FalseValue");
    }
}
