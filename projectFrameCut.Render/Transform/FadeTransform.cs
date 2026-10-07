using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Transform;

public class FadeTransform : TransformEffectBase
{
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override string TypeName => "Fade";
    public override TransformDefinition Definition => TransformDefinition.Clip | TransformDefinition.SupportOneInput;
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new FadeTransform { Parameters = parameters };

    public override IPicture Render(IPicture left, IPicture? right, float progress, TransformSide side, int targetWidth, int targetHeight) =>
        RenderOpacity(left, Math.Clamp(side == TransformSide.Left ? progress : 1 - progress, 0, 1), targetWidth, targetHeight);

    protected virtual IPicture RenderOpacity(IPicture input, float opacity, int width, int height) =>
        new FadeOpacityEffect_IPicture { Opacity = opacity }.Render(input, width, height);
}

public class FadeTransformProvider : TransformEffectProviderBase
{
    public override string TypeName => "Fade";
    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => [];
}

public abstract class TransformEffectProviderBase : EffectProviderBase
{
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override EffectType TypeOfEffect => EffectType.Transform;
    public override EffectTarget Target => EffectTarget.Video | EffectTarget.Transform | EffectTarget.IsNotVisibleInNewEffectSelector;
    protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() => new Dictionary<string, EffectArgumentFieldDescriptor>();
    protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.IPicture, EffectImplementType.HwAcceleration];
}
