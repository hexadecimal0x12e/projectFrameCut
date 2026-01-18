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
        /// Get the processing step for this effect on the source picture to produce a process step with the target width and height.
        /// </summary>
        /// <remarks>
        /// Throw a <see cref="NotImplementedException"/> if this is not supported.
        /// </remarks>
        /// <param name="source"></param>
        /// <param name="targetWidth"></param>
        /// <param name="targetHeight"></param>
        /// <returns></returns>
        public IPictureProcessStep GetStep(IPicture source, uint index, int targetWidth, int targetHeight);


        IPicture IEffect.Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            throw new InvalidOperationException($"Cast this {TypeName} to IContinuousEffect, and call IContinuousEffect.Render().");
        }

        IPictureProcessStep IEffect.GetStep(IPicture source, int targetWidth, int targetHeight)
        {
            throw new InvalidOperationException($"Cast this {TypeName} to IContinuousEffect, and call IContinuousEffect.GetStep().");
        }

        /// <summary>
        /// If you'd like to initialize the effect before use, override it.
        /// </summary>
        public new virtual void Initialize()
        {
        }

        public new bool IsNormalEffect => false;
        public new bool IsContinuousEffect => true;
        public new bool IsBindableArgsEffect => false;
    }

}
