using projectFrameCut.Drawing.Base;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    public interface IContinuousEffect : IEffect
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
        /// Render the effect on the source picture to produce a new picture with the target width and height.
        /// </summary>
        /// <param name="source">The input frame.</param>
        /// <param name="progress">A value between 0 and 1 indicating the current progress of the effect.</param>
        /// <param name="computer">A provided computer for accelerated computing.</param>
        /// <param name="targetWidth">Output canvas' width.</param>
        /// <param name="targetHeight">Output canvas' height.</param>
        /// <returns>the processed frame</returns>
        public IPicture Render(IPicture source, float progress, IComputer? computer, int targetWidth, int targetHeight);

        /// <summary>
        /// If you'd like to initialize the effect before use, override it.
        /// </summary>
        public new virtual void Initialize()
        {
        }

        EffectType IEffect.TypeOfEffect => EffectType.ContinuousEffect;

    }

}
