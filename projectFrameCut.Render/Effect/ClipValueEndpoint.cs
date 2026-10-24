using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

public abstract class ClipValueEndpointProvider : EffectProviderBase, IClipValueEndpointProvider, IEffectProvider
{
    public override bool IsDeterministic => true;

    public override IReadOnlyList<Stage> SupportedStages => EffectStages.Values;
    public abstract bool IsExport { get; }
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override EffectType TypeOfEffect => EffectType.NormalEffect;
    public override EffectTarget Target => EffectTarget.Video | EffectTarget.ValueProvider | EffectTarget.IsNotVisibleInNewEffectSelector;
    public bool AllowCrossClip
    {
        get => !MetaData.TryGetValue(nameof(AllowCrossClip), out var value)
            || bool.TryParse(value?.ToString(), out var allow) && allow;
        set => MetaData[nameof(AllowCrossClip)] = value;
    }
    public EffectArgumentFieldType ValueType
    {
        get => Enum.TryParse<EffectArgumentFieldType>(MetaData.GetValueOrDefault(nameof(ValueType))?.ToString(), out var type)
            ? type.BaseType() : EffectArgumentFieldType.Numeric;
        set => MetaData[nameof(ValueType)] = value.BaseType().ToString();
    }
    public Guid SourceClipId
    {
        get => ReadId(nameof(SourceClipId));
        set => MetaData[nameof(SourceClipId)] = value.ToString();
    }
    public Guid SourceEndpointId
    {
        get => ReadId(nameof(SourceEndpointId));
        set => MetaData[nameof(SourceEndpointId)] = value.ToString();
    }
    private Guid ReadId(string key) => Guid.TryParse(MetaData.GetValueOrDefault(key)?.ToString(), out var id) ? id : Guid.Empty;
    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => [];
    protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() => IsExport
        ? new Dictionary<string, EffectArgumentFieldDescriptor> { [PrimaryInputAnchorKey] = Field(PrimaryInputAnchorKey, ValueType | EffectArgumentFieldType.Mandatory, "") }
        : new Dictionary<string, EffectArgumentFieldDescriptor>();
    protected override EffectArgumentFieldDescriptor DefineOutField() => Field(OutputAnchorKey, ValueType, "");
    public new IEffect[] Build() => [CreateNewEffect()];
    public override IEffect CreateNewEffect() => new ClipValueEndpointEffect(TypeName, IsExport);
}

public sealed class ClipValueExportProvider : ClipValueEndpointProvider
{
    public ClipValueExportProvider() => Name = "Clip Value Output";
    public override string TypeName => "ClipValueOutput";
    public override bool IsExport => true;
}

public sealed class ClipValueImportProvider : ClipValueEndpointProvider
{
    public ClipValueImportProvider() => Name = "Clip Value Input";
    public override string TypeName => "ClipValueInput";
    public override bool IsExport => false;
}

public sealed class ClipValueEndpointEffect(string typeName, bool isExport) : IEffect
{
    public bool IsDeterministic => true;
    public bool AcceptsHwAccelPictures => true;

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
    public IEffect WithParameters(Dictionary<string, object> parameters) => new ClipValueEndpointEffect(typeName, isExport);
    public object? Compute(EffectExecutionContext context) => isExport ? context.Input
        : throw new InvalidOperationException("Clip endpoint inputs must be evaluated by the dynamic effect graph.");
}
