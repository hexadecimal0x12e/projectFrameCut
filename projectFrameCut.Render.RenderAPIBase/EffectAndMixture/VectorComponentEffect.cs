using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

public interface IVectorComponentEffect : IEffect
{
    EffectType IEffect.TypeOfEffect => EffectType.VectorComponentEffect;

    /// <summary>
    /// Processes the given source vector component based on the effect and the progress value.
    /// </summary>
    /// <param name="source">The source vector component to be processed.</param>
    /// <param name="progress">The progress value indicating the extent of the effect.</param>
    /// <returns>The processed vector component.</returns>
    IVectorComponent Process(IVectorComponent source, float progress);
}
