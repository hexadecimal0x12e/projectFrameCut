using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    public interface ITextEffect : IEffect
    {
        /// <summary>
        /// Process the input text clip's entries.
        /// </summary>
        /// <returns>the updated entries.</returns>
        public TextClipEntry[] Process(TextClipEntry[] source);

        string? IEffect.NeedComputer => null;
        EffectType IEffect.TypeOfEffect => EffectType.TextEffect;
    }
    public interface IContinuousTextEffect : IEffect
    {
        /// <summary>
        /// Represents the start point of the effect inside this Clip.
        /// Only used when <see cref="IsScoped"/> is true.
        /// </summary>
        public int StartPoint { get; set; }
        /// <summary>
        /// Represents the end point of the effect inside this Clip.
        /// Only used when <see cref="IsScoped"/> is true.
        /// </summary>
        public int EndPoint { get; set; }
        /// <summary>
        /// If true, the effect is scoped to <see cref="StartPoint"/> and <see cref="EndPoint"/>.
        /// The caller will skip the effect if the current frame is outside this range,
        /// and compute progress relative to this range instead of the full clip duration.
        /// </summary>
        public bool IsScoped { get; set; }

        /// <summary>
        /// Process the input text clip's entries.
        /// </summary>
        /// <param name="source">The input text entries.</param>
        /// <param name="progress">A value between 0 and 1 indicating the current progress of the effect.</param>
        /// <returns>the updated entries.</returns>
        public TextClipEntry[] Process(TextClipEntry[] source, float progress);

        string? IEffect.NeedComputer => null;
        EffectType IEffect.TypeOfEffect => EffectType.ContinuousTextEffect;
    }
}
