using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    public interface IClipPositionProvider : IEffect
    {
        /// <summary>
        /// Get the position of the clip on the target canvas. The position is represented by a tuple of (X, Y, Width, Height).
        /// </summary>
        /// <param name="source">the source IClip.</param>
        /// <param name="targetWidth">Output canvas' width.</param>
        /// <param name="targetHeight">Output canvas' height.</param>
        /// <returns>The position of the clip on the target canvas.</returns>
        public ClipPositionTuple GetPosition(IClip source, int targetWidth, int targetHeight);

        int IEffect.RelativeWidth { get => -1; set { } }
        int IEffect.RelativeHeight { get => -1; set { } }

        EffectImplementType IEffect.ImplementType => EffectImplementType.NotSpecified;
        EffectType IEffect.TypeOfEffect => EffectType.ClipPositionProvider;
    }

    public interface IContinuousClipPositionProvider : IEffect
    {
        /// <summary>
        /// Get the position of the clip on the target canvas for a specific frame. The position is represented by a tuple of (X, Y, Width, Height).
        /// </summary>
        /// <param name="source">the source IClip.</param>
        /// <param name="index">the index of the frame to be rendered.</param>
        /// <param name="targetWidth">Output canvas' width.</param>
        /// <param name="targetHeight">Output canvas' height.</param>
        /// <returns>The position of the clip on the target canvas.</returns>
        public ClipPositionTuple GetPosition(IClip source, uint index, int targetWidth, int targetHeight);

        /// <summary>
        /// Evaluate against a canvas whose output size differs from its project-relative size.
        /// X/Y remain project-relative; Width/Height are in output pixels.
        /// </summary>
        public virtual ClipPositionTuple GetPosition(IClip source, uint index, int targetWidth, int targetHeight, int relativeWidth, int relativeHeight)
            => GetPosition(source, index, targetWidth, targetHeight);

        /// <summary>
        /// Whether resizing to the supplied rectangle should preserve the content's aspect ratio.
        /// </summary>
        public virtual bool PreserveAspectRatio => true;


        int IEffect.RelativeWidth { get => -1; set { } }
        int IEffect.RelativeHeight { get => -1; set { } }

        EffectImplementType IEffect.ImplementType => EffectImplementType.NotSpecified;
        EffectType IEffect.TypeOfEffect => EffectType.ContinuousClipPositionProvider;

    }
}
