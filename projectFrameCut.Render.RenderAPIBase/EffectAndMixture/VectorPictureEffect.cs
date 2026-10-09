using projectFrameCut.Drawing.Vector;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

/// <summary>Processes frame-owned, clip-local vector data before rasterization.</summary>
public interface IVectorPictureEffect : IEffect
{
    EffectType IEffect.TypeOfEffect => EffectType.VectorPictureEffect;
    VectorPicture Process(VectorPicture source, float progress);
}

public static class EffectPipelineExtensions
{
    public static EffectPipeline GetPipeline(this EffectType type) => type is
        EffectType.VectorComponentEffect or EffectType.TextEffect or EffectType.ContinuousTextEffect or EffectType.VectorPictureEffect
            ? EffectPipeline.NativeContent : EffectPipeline.Picture;

    public static bool CanConnectContent(this IEffectProvider source, IEffectProvider target)
    {
        if (!source.HasMainPictureInput() || !target.HasMainPictureInput()
            || source.TypeOfEffect.GetPipeline() != target.TypeOfEffect.GetPipeline()) return false;
        if (target.TypeOfEffect.GetPipeline() == EffectPipeline.Picture) return true;
        return target.TypeOfEffect == EffectType.VectorPictureEffect
            || source.TypeOfEffect == target.TypeOfEffect
            || (source.TypeOfEffect is EffectType.TextEffect or EffectType.ContinuousTextEffect)
                && (target.TypeOfEffect is EffectType.TextEffect or EffectType.ContinuousTextEffect);
    }
}
