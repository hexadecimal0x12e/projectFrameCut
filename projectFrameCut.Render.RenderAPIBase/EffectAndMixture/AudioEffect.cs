using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    public interface IAudioNormalEffect : IEffect
    {
        EffectType IEffect.TypeOfEffect => EffectType.AudioNormalEffect;

        /// <summary>
        /// Process the input audio samples and return the processed audio samples.
        /// </summary>
        /// <param name="input">Source audio samples</param>
        /// <returns>Processed audio samples</returns>
        public IAudioSamples Process(IAudioSamples input);
    }

    public interface IAudioContinuousEffect : IEffect
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
        /// Processes the specified audio samples at the given index and returns the resulting audio samples.
        /// </summary>
        /// <param name="input">The input audio samples to be processed. Cannot be null.</param>
        /// <param name="index">The position, as a floating-point value, at which to process the input samples. The interpretation of this
        /// value depends on the implementation.</param>
        /// <returns>The processed audio samples resulting from the operation.</returns>
        public IAudioSamples Process(IAudioSamples input, float index);

        /// <summary>
        /// If you'd like to initialize the effect before use, override it.
        /// </summary>
        public new virtual void Initialize()
        {
        }

        EffectType IEffect.TypeOfEffect => EffectType.AudioContinuousEffect;

    }
}
