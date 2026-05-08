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

        int IEffect.RelativeWidth { get => -1; set => Logger.Log("Cannot set RelativeWidth for a IColorAdjustEffect. This operation is ignored.", "warn"); }
        int IEffect.RelativeHeight { get => -1; set => Logger.Log("Cannot set RelativeHeight for a IColorAdjustEffect. This operation is ignored.", "warn"); }
        int IEffect.Index { get => int.MinValue; set => Logger.Log("Cannot set Index for a IClipPositionProvider. This operation is ignored.", "warn"); }

        string? IEffect.NeedComputer => null;
        bool IEffect.YieldProcessStep => false;
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


        int IEffect.RelativeWidth { get => -1; set => Logger.Log("Cannot set RelativeWidth for a IColorAdjustEffect. This operation is ignored.", "warn"); }
        int IEffect.RelativeHeight { get => -1; set => Logger.Log("Cannot set RelativeHeight for a IColorAdjustEffect. This operation is ignored.", "warn"); }
        int IEffect.Index { get => int.MinValue; set => Logger.Log("Cannot set Index for a IContinuousClipPositionProvider. This operation is ignored.", "warn"); }

        string? IEffect.NeedComputer => null;
        bool IEffect.YieldProcessStep => false;
        EffectImplementType IEffect.ImplementType => EffectImplementType.NotSpecified;
        EffectType IEffect.TypeOfEffect => EffectType.ContinuousClipPositionProvider;

    }
}
