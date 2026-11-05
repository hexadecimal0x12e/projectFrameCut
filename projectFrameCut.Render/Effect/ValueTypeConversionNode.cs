using System.Globalization;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System.Text.Json;

namespace projectFrameCut.Render.Effect;

public sealed class ValueTypeConversionNodeProvider : EffectProviderBase
{
    public const string TargetTypeKey = "TargetType";
    private static readonly EffectArgumentFieldType[] valueTypes =
    [
        EffectArgumentFieldType.Numeric,
        EffectArgumentFieldType.Integer,
        EffectArgumentFieldType.UnsignedInteger,
        EffectArgumentFieldType.Long,
        EffectArgumentFieldType.UnsignedLong,
        EffectArgumentFieldType.Boolean,
        EffectArgumentFieldType.String,
        EffectArgumentFieldType.Color,
        EffectArgumentFieldType.Size,
        EffectArgumentFieldType.Position,
        EffectArgumentFieldType.SizeAndPosition,
    ];

    public ValueTypeConversionNodeProvider() => Name = "ValueTypeConversionNode";
    public override string TypeName => "ValueTypeConversionNode";
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override bool IsDeterministic => true;
    public override EffectType TypeOfEffect => EffectType.NormalEffect;
    public override EffectTarget Target => EffectTarget.Pipeline | EffectTarget.ValueProvider;
    public override IReadOnlyList<EffectStage> SupportedStages => EffectStages.Values;

    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() =>
    [
        Field(TargetTypeKey, EffectArgumentFieldType.String | EffectArgumentFieldType.CannotBeDynamic, "Numeric",
            presetOptions: valueTypes.Select(type => type.ToString()).ToArray()) with
        {
            LocalizedNameKey = "Effect_ValueTypeConversionNode_TargetType",
        },
    ];

    protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() =>
        new Dictionary<string, EffectArgumentFieldDescriptor>
        {
            [PrimaryInputAnchorKey] = Field(PrimaryInputAnchorKey,
                EffectArgumentFieldType.Any | EffectArgumentFieldType.Mandatory | EffectArgumentFieldType.CanNotBeStatic, ""),
        };

    protected override EffectArgumentFieldDescriptor DefineOutField() => Field(OutputAnchorKey, EffectArgumentFieldType.Any, "");

    public override EffectArgumentFieldType ResolveFieldType(string fieldId, bool isInput, EffectFieldTypeContext context)
    {
        if (isInput) return EffectArgumentFieldType.Any;
        return Enum.TryParse<EffectArgumentFieldType>(context.Parameters.GetValueOrDefault(TargetTypeKey)?.ToString(), out var type)
            && valueTypes.Contains(type) ? type : EffectArgumentFieldType.Numeric;
    }

    public override IEffect CreateNewEffect() => new ValueTypeConversionNodeEffect();
}

public sealed class ValueTypeConversionNodeEffect : IEffect
{
    public bool IsDeterministic => true;
    public string TypeName => "ValueTypeConversionNode";
    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public EffectType TypeOfEffect => EffectType.NormalEffect;
    public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "ValueTypeConversionNode";
    public bool Enabled { get; set; } = true;
    public int Index { get; set; }
    public int RelativeWidth { get; set; }
    public int RelativeHeight { get; set; }
    public bool IsReorderable => false;
    public string? BindedEffectProvidingSystemID { get; set; }
    public Dictionary<string, object> Parameters { get; set; } = new();
    public IEffect WithParameters(Dictionary<string, object> parameters) => new ValueTypeConversionNodeEffect { Parameters = parameters };

    public object? Compute(EffectExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (context.Input is null) return null;
        if (!Enum.TryParse<EffectArgumentFieldType>(Parameters.GetValueOrDefault(ValueTypeConversionNodeProvider.TargetTypeKey)?.ToString(), out var type))
            throw new InvalidOperationException("Value type conversion requires a target type.");
        if (type == EffectArgumentFieldType.String)
        {
            if (context.Input is string text) return text;
            if (context.Input is IFormattable formatted) return formatted.ToString(null, CultureInfo.InvariantCulture);
            return JsonSerializer.Serialize(context.Input);
        }
        return EffectVariableProvider.ConvertValue(type, context.Input);
    }
}
