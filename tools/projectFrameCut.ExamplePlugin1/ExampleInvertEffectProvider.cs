using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace SomePublisher;

public sealed class ExampleInvertEffectProvider : EffectProviderBase
{
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

    protected override IEffect[] BuildEffects(EffectImplementType implementType, Dictionary<string, object> parameters)
    {
        if (implementType != EffectImplementType.IPicture)
            throw new NotSupportedException("ExampleInvert supports the IPicture implementation only.");

        object value = parameters.GetValueOrDefault("Amount", 1f);
        if (value is Func<object> getter) value = getter();
        float amount = Math.Clamp(Convert.ToSingle(value), 0, 1);
        return [new ExampleInvertEffect(amount)];
    }
}
