using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace SomePublisher;

public sealed class ExampleInvertEffectProvider : EffectProviderBase
{
    public override IReadOnlyList<Stage> SupportedStages => [Stage.Picture];

    public override bool IsDeterministic => true;

    public override string TypeName => "ExampleInvert";
    public override string FromPlugin => ExamplePluginConstants.PluginId;
    public override EffectType TypeOfEffect => EffectType.NormalEffect;
    public override EffectTarget Target => EffectTarget.Video;

    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() =>
    [
        Field("Amount", EffectArgumentFieldType.Numeric, "1", "0", "1",
            remarks: "0 keeps the original colors; 1 fully inverts them.")
    ];

    protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.IPicture];


}
