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
    public virtual IEffect WithPictureType<TPicture>() where TPicture : IPicture =>
        ImplementType == EffectImplementType.IPicture ? new FadeTransform<TPicture>() : TypedPictureEffect.Create<TPicture>(this);
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new FadeTransform { Parameters = parameters };

    public override IPicture Render(IPicture left, IPicture? right, float progress, TransformSide side, int targetWidth, int targetHeight) =>
        RenderOpacity(left, Half.Clamp(side == TransformSide.Left ? (Half)progress : Half.One - (Half)progress, Half.Zero, Half.One), targetWidth, targetHeight);

    protected virtual IPicture RenderOpacity(IPicture input, Half opacity, int width, int height) =>
        new FadeOpacityEffect_IPicture { Opacity = (float)opacity }.Render(input, width, height);
}

public class FadeTransform<TPicture> : FadeTransform, ITransform<TPicture> where TPicture : IPicture
{
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new FadeTransform<TPicture> { Parameters = parameters };
    public override IPicture Render(IPicture left, IPicture? right, float progress, TransformSide side, int width, int height) =>
        PictureEffectDispatch.Transform(this, left, right, progress, side, width, height);
    public IPicture Render(TPicture left, TPicture? right, float progress, TransformSide side, int width, int height) =>
        new FadeOpacityEffect<TPicture> { Opacity = (float)Half.Clamp(side == TransformSide.Left ? (Half)progress : Half.One - (Half)progress, Half.Zero, Half.One) }.Render(left, width, height);
}

public class FadeTransformProvider : TransformEffectProviderBase
{
    public override string TypeName => "Fade";
    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => [];
}

public abstract class TransformEffectProviderBase : EffectProviderBase
{
    public override IReadOnlyList<Stage> SupportedStages => [Stage.Transform];

    public override bool IsDeterministic => true;

    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override EffectType TypeOfEffect => EffectType.Transform;
    public override EffectTarget Target => EffectTarget.Video | EffectTarget.Transform | EffectTarget.IsNotVisibleInNewEffectSelector;
    protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() => new Dictionary<string, EffectArgumentFieldDescriptor>();
    protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.IPicture, EffectImplementType.HwAcceleration];
}
