using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    public interface IClipPositionProvider : IEffect
    {
        /// <summary>
        /// Get the position of the clip on the target canvas as (X, Y, Width, Height, Rotation).
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
        /// Get the position of the clip on the target canvas for a specific frame as (X, Y, Width, Height, Rotation).
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
        {
            relativeWidth = relativeWidth > 0 ? relativeWidth : targetWidth;
            relativeHeight = relativeHeight > 0 ? relativeHeight : targetHeight;
            // Legacy providers evaluate in project space; only dimensions become output pixels.
            var position = GetPosition(source, index, relativeWidth, relativeHeight);
            int width = relativeWidth > 0 && relativeWidth != targetWidth
                ? (int)Math.Round((double)position.TargetWidth * targetWidth / relativeWidth, MidpointRounding.AwayFromZero)
                : position.TargetWidth;
            int height = relativeHeight > 0 && relativeHeight != targetHeight
                ? (int)Math.Round((double)position.TargetHeight * targetHeight / relativeHeight, MidpointRounding.AwayFromZero)
                : position.TargetHeight;
            return position with
            {
                TargetWidth = !position.IsDelta && position.TargetWidth > 0 ? Math.Max(1, width) : width,
                TargetHeight = !position.IsDelta && position.TargetHeight > 0 ? Math.Max(1, height) : height,
            };
        }

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
