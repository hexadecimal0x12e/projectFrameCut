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
        /// </summary>
        public int StartPoint { get; set; }
        /// <summary>
        /// Represents the end point of the effect inside this Clip.
        /// </summary>
        public int EndPoint { get; set; }


        /// <summary>
        /// Render the effect on the source picture to produce a new picture with the target width and height.
        /// </summary>
        /// <param name="source"></param>
        /// <param name="computer"></param>
        /// <param name="targetWidth"></param>
        /// <param name="targetHeight"></param>
        /// <returns>the processed frame</returns>
        public IPicture Render(IPicture source, uint index, IComputer? computer, int targetWidth, int targetHeight);

        /// <summary>
        /// If you'd like to initialize the effect before use, override it.
        /// </summary>
        public new virtual void Initialize()
        {
        }

        EffectType IEffect.TypeOfEffect => EffectType.ContinuousEffect;

    }

}
