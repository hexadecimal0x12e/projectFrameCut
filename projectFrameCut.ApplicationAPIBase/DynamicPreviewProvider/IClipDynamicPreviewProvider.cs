using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.ApplicationAPIBase.DynamicPreviewProvider
{
    public interface IClipDynamicPreviewProvider
    {
        public string TypeName { get; }
        /// <summary>
        /// Check whether the dynamic preview can be generated for the target clip. The implementation should return false if the dynamic preview cannot be generated, and the application will use the default static preview instead. Note that this method may be called multiple times, so it should not contain heavy computations or operations that may cause side effects.
        /// </summary>
        /// <param name="target"></param>
        /// <returns></returns>
        public bool IsAvailable(IClip target);
        /// <summary>
        /// Generate a preview from source Clip.
        /// </summary>
        /// <param name="target"></param>
        /// <returns></returns>
        public View Generate(IClip target, int canvasWidth, int canvasHeight, uint targetFrame);
    }
    public interface IEffectDynamicPreviewProvider
    {
        public string TypeName { get; }
        public bool IsAvailable(IEffect target);
        public View Generate(IEffect target, View input, int canvasWidth, int canvasHeight, uint targetFrame);
    }
}
