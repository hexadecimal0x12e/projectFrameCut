using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System.Text.Json;

namespace projectFrameCut.Render.Effect;

public abstract class EffectVariableProvider : EffectProviderBase
{
    public override bool IsDeterministic => false;
    public override IReadOnlyList<EffectStage> SupportedStages => [EffectStage.Render, EffectStage.VectorContent, EffectStage.Composition];
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override EffectType TypeOfEffect => EffectType.NormalEffect;
    public override EffectTarget Target => EffectTarget.Pipeline | EffectTarget.ValueProvider | EffectTarget.IsNotVisibleInNewEffectSelector;
    public Guid VariableId
    {
        get => Guid.TryParse(MetaData.GetValueOrDefault(nameof(VariableId))?.ToString(), out var id) ? id : Id;
        set => MetaData[nameof(VariableId)] = value.ToString();
    }
    public EffectArgumentFieldType ValueType
    {
        get => Enum.TryParse<EffectArgumentFieldType>(MetaData.GetValueOrDefault(nameof(ValueType))?.ToString(), out var type)
            ? type.BaseType() : EffectArgumentFieldType.Numeric;
        set => MetaData[nameof(ValueType)] = value.BaseType().ToString();
    }
    public static bool SupportsType(EffectArgumentFieldType type) => type.BaseType() is
        EffectArgumentFieldType.Numeric or EffectArgumentFieldType.Integer or EffectArgumentFieldType.UnsignedInteger
        or EffectArgumentFieldType.Long or EffectArgumentFieldType.UnsignedLong or EffectArgumentFieldType.Boolean
        or EffectArgumentFieldType.String or EffectArgumentFieldType.Color or EffectArgumentFieldType.Size
        or EffectArgumentFieldType.Position or EffectArgumentFieldType.SizeAndPosition;

    public static object? ConvertValue(EffectArgumentFieldType type, object? value)
    {
        if (value is JsonElement { ValueKind: JsonValueKind.Object } json) value = json.GetRawText();
        if (type is EffectArgumentFieldType.Size or EffectArgumentFieldType.Position or EffectArgumentFieldType.SizeAndPosition)
            return value is ClipPositionTuple ? value : JsonSerializer.Deserialize<ClipPositionTuple>(value?.ToString() ?? "{}");
        if (type == EffectArgumentFieldType.Color && value is string text && text.TrimStart().StartsWith('{'))
            return JsonSerializer.Deserialize<PictureExtensions.Pixel<ushort>>(text);
        return EffectFieldTypes.ConvertInput(type, value, false);
    }

    protected override EffectArgumentFieldDescriptor DefineOutField() => Field(OutputAnchorKey, ValueType, "");
    public override IEffect[] Build() => [CreateNewEffect()];
    public override IEffect CreateNewEffect() => new EffectVariableEffect(TypeName);
}

public sealed class EffectVariableWriteProvider : EffectVariableProvider
{
    public EffectVariableWriteProvider() => Name = "EffectVariableWrite";
    public override string TypeName => "EffectVariableWrite";
    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => [];
    protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() =>
        new Dictionary<string, EffectArgumentFieldDescriptor>
        {
            [PrimaryInputAnchorKey] = Field(PrimaryInputAnchorKey,
                ValueType | EffectArgumentFieldType.Mandatory | EffectArgumentFieldType.CanNotBeStatic, ""),
        };
}

public sealed class EffectVariableReadProvider : EffectVariableProvider
{
    public const string InitialValueKey = "InitialValue";
    public EffectVariableReadProvider() => Name = "EffectVariableRead";
    public override string TypeName => "EffectVariableRead";
    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() =>
    [
        Field(InitialValueKey, ValueType | EffectArgumentFieldType.CannotBeDynamic, ValueType switch
        {
            EffectArgumentFieldType.String => "",
            EffectArgumentFieldType.Boolean => "false",
            EffectArgumentFieldType.Color => "#00000000",
            EffectArgumentFieldType.Size or EffectArgumentFieldType.Position or EffectArgumentFieldType.SizeAndPosition
                => JsonSerializer.Serialize(new ClipPositionTuple(0, 0, 0, 0, false)),
            _ => "0",
        }),
    ];
    protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() => new Dictionary<string, EffectArgumentFieldDescriptor>();
}

public sealed class EffectVariableEffect(string typeName) : IEffect
{
    public bool IsDeterministic => false;
    public string TypeName => typeName;
    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public EffectType TypeOfEffect => EffectType.NormalEffect;
    public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = typeName;
    public bool Enabled { get; set; } = true;
    public int Index { get; set; }
    public int RelativeWidth { get; set; }
    public int RelativeHeight { get; set; }
    public bool IsReorderable => false;
    public string? BindedEffectProvidingSystemID { get; set; }
    public Dictionary<string, object> Parameters { get; } = new();
    public IEffect WithParameters(Dictionary<string, object> parameters) => new EffectVariableEffect(typeName);
    public object? Compute(EffectExecutionContext context) => throw new InvalidOperationException("Variables require dynamic calculation.");
}
