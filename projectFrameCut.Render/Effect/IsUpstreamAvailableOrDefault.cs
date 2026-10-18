using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

public sealed class IsUpstreamAvailableOrDefaultEffectProvider : EffectProviderBase
{
    public override bool IsDeterministic => true;

    public IsUpstreamAvailableOrDefaultEffectProvider() => Name = "IsUpstreamAvailableOrDefault";
    public override string TypeName => "IsUpstreamAvailableOrDefault";
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override EffectType TypeOfEffect => EffectType.NormalEffect;
    public override EffectTarget Target => EffectTarget.Video | EffectTarget.ValueProvider;

    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => [];

    protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() =>
        new Dictionary<string, EffectArgumentFieldDescriptor>
        {
            [PrimaryInputAnchorKey] = Field(PrimaryInputAnchorKey, EffectArgumentFieldType.Any, ""),
            ["DefaultValue"] = Field("DefaultValue", EffectArgumentFieldType.Any | EffectArgumentFieldType.CanNotBeStatic, ""),
        };

    protected override EffectArgumentFieldDescriptor DefineOutField() => Field(OutputAnchorKey, EffectArgumentFieldType.Any, "");

    public override EffectArgumentFieldType ResolveFieldType(string fieldId, bool isInput, EffectFieldTypeContext context)
    {
        if (isInput && context.State.Fields.TryGetValue(fieldId, out var field) && !field.FieldType.HasFlag(EffectArgumentFieldType.Any)) return field.FieldType;
        var type = context.InputTypes.GetValueOrDefault(PrimaryInputAnchorKey, EffectArgumentFieldType.Any);
        return type.HasFlag(EffectArgumentFieldType.Any) || type.BaseType() == EffectArgumentFieldType.Unknown
            ? context.InputTypes.GetValueOrDefault("DefaultValue", EffectArgumentFieldType.Any) : type;
    }
}

public sealed class IsUpstreamAvailableOrDefaultEffect : IEffect
{
    public bool IsDeterministic => true;
    public bool AcceptsHwAccelPictures => true;

    public string TypeName => "IsUpstreamAvailableOrDefault";
    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public EffectType TypeOfEffect => EffectType.NormalEffect;
    public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "IsUpstreamAvailableOrDefault";
    public bool Enabled { get; set; } = true;
    public int Index { get; set; }
    public int RelativeWidth { get; set; }
    public int RelativeHeight { get; set; }
    public bool IsReorderable => false;
    public string? BindedEffectProvidingSystemID { get; set; }
    public Dictionary<string, object> Parameters { get; set; } = new();
    public IEffect WithParameters(Dictionary<string, object> parameters) => new IsUpstreamAvailableOrDefaultEffect { Parameters = parameters };

    public object? Compute(EffectExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        return context.Input ?? context.Parameters.GetValueOrDefault("DefaultValue");
    }
}
