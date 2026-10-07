using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace SomePublisher;

public sealed class ExampleCrossfadeTransformProvider : EffectProviderBase
{
    public override string TypeName => "ExampleCrossfade";
    public override string FromPlugin => ExamplePluginConstants.PluginId;
    public override EffectType TypeOfEffect => EffectType.Transform;
    public override EffectTarget Target => EffectTarget.Video | EffectTarget.Transform | EffectTarget.IsNotVisibleInNewEffectSelector;
    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => [];
    protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() => new Dictionary<string, EffectArgumentFieldDescriptor>();
    protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.IPicture];
}
