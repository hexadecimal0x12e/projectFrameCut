using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using SomePublisher;
using System.Text.Json;

namespace projectFrameCut.ExternalPluginBackendExample;

internal sealed record InvertSettings
{
    public InvertSettings() { }

    public float Amount { get; init; } = 1;
    public bool Red { get; init; } = true;
    public bool Green { get; init; } = true;
    public bool Blue { get; init; } = true;
}

internal sealed class ExternalInvertEffectProvider : EffectProviderBase
{
    public const string ProviderTypeName = "ExampleExternalInvert";
    public const string SettingsFieldId = "Settings";
    public static readonly EffectPortTypeContract SettingsContract = new("SomePublisher.MyPlugin.invert-settings.v1", "Invert settings");

    public ExternalInvertEffectProvider() => Name = "External editor invert";

    public override string TypeName => ProviderTypeName;
    public override string FromPlugin => ExamplePluginConstants.PluginId;
    public override bool IsDeterministic => true;
    public override EffectType TypeOfEffect => EffectType.NormalEffect;
    public override EffectTarget Target => EffectTarget.Video;
    public override IReadOnlyList<EffectStage> SupportedStages => [EffectStage.Render];

    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => [SettingsField()];

    protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() => new Dictionary<string, EffectArgumentFieldDescriptor>
    {
        [PrimaryInputAnchorKey] = Field(PrimaryInputAnchorKey, EffectArgumentFieldType.IPicture, ""),
        [SettingsFieldId] = SettingsField(),
    };

    private EffectArgumentFieldDescriptor SettingsField() => new()
    {
        Id = SettingsFieldId,
        TypeName = nameof(CustomEffectPortValue),
        FromPlugin = FromPlugin,
        FieldType = EffectArgumentFieldType.CustomType,
        TypeContract = SettingsContract,
        DefaultValue = JsonSerializer.Serialize(new InvertSettings()),
        Remarks = "Invert strength and RGB channel switches, configured through an external Windows Forms editor.",
        DataFormat = new CustomEffectPortEditorConfiguration
        {
            Windows = new()
            {
                EntryPoint = "projectFrameCut.ExternalPluginBackendExample.exe",
                Arguments = ["edit_invert"],
            },
        },
    };

    protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.IPicture];

    public static InvertSettings ReadSettings(CustomEffectPortValue? value)
    {
        var settings = value is null ? new InvertSettings() : SettingsContract.ReadValue<InvertSettings>(value)
            ?? throw new InvalidDataException("The invert settings cannot be null.");
        if (!float.IsFinite(settings.Amount) || settings.Amount is < 0 or > 1)
            throw new InvalidDataException("Invert strength must be between 0 and 1.");
        return settings;
    }
}
