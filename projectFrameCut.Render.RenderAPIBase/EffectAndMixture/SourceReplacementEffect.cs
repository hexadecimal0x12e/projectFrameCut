using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    /// <summary>
    /// A effect for replacing the source of a clip, such as changing the video or image content while keeping the same duration and timing.
    /// This can be used for effects which enhance or modify the source content, such as super-resolution, or frame interpolation, or other AI-based enhancements.
    /// </summary>
    /// <remarks>
    /// When this exists, render pipeline will no longer decode the original source of the clip via <see cref="IClip.GetFrameRelativeToStartPointOfSource(uint, int, int, bool, IPicture.PicturePixelMode)"/>, but instead will use the new source provided by this effect.
    /// </remarks>
    public interface ISourceReplacementEffect : IEffect
    {
        /// <summary>
        /// Gets the working project's frame rate, which is used to determine the timing and duration of the effect.
        /// </summary>
        public int ProjectFrameRate { get; set; }

        /// <summary>
        /// Indicate whether this effect supports source replacement for the given input clip. 
        /// <br />
        /// If it returns true, the render pipeline will use <see cref="Compute(IClip, IComputer?, int, int, uint, IPicture.PicturePixelMode)"/> to compute the new source frames for the clip.
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        public bool SupportsSourceReplacement(IClip input, int targetWidth, int targetHeight);

        /// <summary>
        /// Computes the new picture for the given input clip, with the specified target width, height, and frame.
        /// </summary>
        /// <remarks>
        /// You'll need to manually handle all kind of clips in your implementation, including video clips, image clips, and audio clips. The input clip may have different properties, such as duration, frame rate, and pixel format. 
        /// </remarks>
        /// <param name="input">The input clip to be processed.</param>
        /// <param name="targetWidth">The target width of the output picture.</param>
        /// <param name="targetHeight">The target height of the output picture.</param>
        /// <param name="targetFrame">The target frame number to compute.</param>
        /// <param name="targetPPB">The pixel format of the output picture.</param>
        /// <returns>the processed source frame (<paramref name="targetFrame"/>) in <b>SOURCE, WITH SPECIFIC SIZE IN <paramref name="targetWidth"/> * <paramref name="targetHeight"/>.</b></returns>
        public IPicture Compute(IClip input, IComputer? computer, int targetWidth, int targetHeight, uint targetFrame, IPicture.PicturePixelMode targetPPB);

        EffectType IEffect.TypeOfEffect => EffectType.SourceReplacement;
        int IEffect.RelativeWidth { get => -1; set { } }
        int IEffect.RelativeHeight { get => -1; set { } }
        bool IEffect.IsReorderable => false;
        int IEffect.Index { get => int.MinValue; set { } } 
    }
}
