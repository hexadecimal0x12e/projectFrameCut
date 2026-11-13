using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.ObjectTrackingPlugin;

internal sealed class TrackingProvider : EffectProviderBase, IMultipleOutputEffectProvider
{
    public const string ProviderType = "ObjectTracking";
    public static readonly EffectPortTypeContract TargetContract = new(TrackingPlugin.PluginId + ".target.v1", "Tracked object");
    public TrackingProvider() => Name = "Object tracking";
    public override string TypeName => ProviderType;
    public override string FromPlugin => TrackingPlugin.PluginId;
    // Native RANSAC may choose different samples between invocations.
    public override bool IsDeterministic => false;
    public override EffectType TypeOfEffect => EffectType.NormalEffect;
    public override EffectTarget Target => EffectTarget.Video | EffectTarget.ValueProvider;
    public override IReadOnlyList<EffectStage> SupportedStages => [EffectStage.Render];
    protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.IPicture];
    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() =>
    [
        TargetField(),
        Field("CanvasCoordinates", EffectArgumentFieldType.Boolean, "true", remarks: "False: input pixels. True: project canvas coordinates."),
    ];
    protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() =>
        new Dictionary<string, EffectArgumentFieldDescriptor>
        {
            [PrimaryInputAnchorKey] = Field(PrimaryInputAnchorKey, EffectArgumentFieldType.IPicture, ""),
            ["Target"] = TargetField(),
            ["CanvasCoordinates"] = Field("CanvasCoordinates", EffectArgumentFieldType.Boolean, "true"),
            ["ImagePlacement"] = Field("ImagePlacement", EffectArgumentFieldType.SizeAndPosition, "", remarks: "Optional image placement in project coordinates; defaults to the full canvas."),
        };
    public IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> OutFields => new Dictionary<string, EffectArgumentFieldDescriptor>
    {
        ["Position"] = Field("Position", EffectArgumentFieldType.SizeAndPosition, ""),
        ["Found"] = Field("Found", EffectArgumentFieldType.Boolean, "false"),
    };
    private EffectArgumentFieldDescriptor TargetField() => new()
    {
        Id = "Target", TypeName = nameof(CustomEffectPortValue), FromPlugin = FromPlugin,
        FieldType = EffectArgumentFieldType.CustomType, TypeContract = TargetContract, DefaultValue = "{}",
        DataFormat = new CustomEffectPortEditorConfiguration
        {
            Windows = new() { EntryPoint = "projectFrameCut.ObjectTrackingPlugin.exe", Arguments = ["edit_target"] },
        },
    };
}
