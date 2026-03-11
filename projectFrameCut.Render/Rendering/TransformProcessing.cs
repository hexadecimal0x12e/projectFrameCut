using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.Rendering
{
    public static class TransformProcessing
    {

        /// <summary>
        /// Processes a transform between two clips and returns the resulting frame.
        /// </summary>
        /// <param name="left">The left (preceding) clip.</param>
        /// <param name="right">The right (following) clip.</param>
        /// <param name="source">The transform to apply.</param>
        /// <param name="width">Target output width.</param>
        /// <param name="height">Target output height.</param>
        /// <param name="frameIndex">The current frame index relative to the start of the project.</param>
        /// <returns>The resulting composited frame.</returns>
        public static IPicture ProcessTransform(IClip left, IClip? right, ITransform source, int width, int height, uint frameIndex)
        {
            var computer = PluginManager.CreateComputer(source.NeedComputer);

            // Helper: clamp a global frameIndex into a valid frame index for a clip
            static uint ClampFrameForClip(IClip clip, uint globalFrame)
            {
                double dur = clip.Duration * clip.SecondPerFrameRatio;
                uint endExclusive = clip.StartFrame + (uint)Math.Round(dur);
                if (endExclusive == clip.StartFrame) return clip.StartFrame;
                uint lastFrame = endExclusive - 1;
                if (globalFrame < clip.StartFrame) return clip.StartFrame;
                if (globalFrame > lastFrame) return lastFrame;
                return globalFrame;
            }

            // Determine transform start based on the right clip's StartFrame and the transform duration.
            // We assume the transform occupies [right.StartFrame - source.Duration, right.StartFrame)
            long transformStart = (long)right.StartFrame - (long)source.Duration;
            long indexInTransform = (long)frameIndex - transformStart;
            // Clamp index within [0, Duration-1]
            if (indexInTransform < 0) indexInTransform = 0;
            if (indexInTransform >= source.Duration && source.Duration > 0) indexInTransform = source.Duration - 1;

            double progress;
            if (source.Duration <= 1)
            {
                progress = 0.0;
            }
            else
            {
                progress = indexInTransform / (double)(source.Duration - 1);
                if (progress < 0.0) progress = 0.0;
                if (progress > 1.0) progress = 1.0;
            }

            switch (source.TransformType)
            {
                case TransformType.SingleFrameTransform:
                    // Could be either ISingleFrameTransform (two-input) or IOneInputSingleFrameTransform (one-input)
                    if (source is ISingleFrameTransform sft)
                    {
                        var leftFrame = left.GetFrame(ClampFrameForClip(left, frameIndex), width, height, true);
                        var rightFrame = right.GetFrame(ClampFrameForClip(right, frameIndex), width, height, true);
                        return sft.GetFrame(leftFrame, rightFrame, computer, width, height);
                    }
                    if (source is IOneInputSingleFrameTransform oneInput)
                    {
                        // choose the input clip by proximity: if the current frame is closer to right.StartFrame, use right; else use left
                        uint clampLeft = ClampFrameForClip(left, frameIndex);
                        uint clampRight = ClampFrameForClip(right, frameIndex);
                        // distance to clip edges
                        long distToLeft = Math.Abs((long)frameIndex - (long)left.StartFrame - (long)left.Duration + 1);
                        long distToRight = Math.Abs((long)frameIndex - (long)right.StartFrame);
                        var input = distToRight <= distToLeft ? right.GetFrame(clampRight, width, height, true) : left.GetFrame(clampLeft, width, height, true);
                        return oneInput.GetFrame(input, progress, computer, width, height);
                    }
                    break;
                case TransformType.ContinuousTransform:
                    if (source is IContinuousTransform cont)
                    {
                        var leftFrame = left.GetFrame(ClampFrameForClip(left, frameIndex), width, height, true);
                        var rightFrame = right.GetFrame(ClampFrameForClip(right, frameIndex), width, height, true);
                        return cont.GetFrame(leftFrame, rightFrame, progress, computer, width, height);
                    }
                    break;
                default:
                    throw new NotSupportedException($"Unknown TransformType: {source.TransformType}");
            }

            throw new NotSupportedException($"Transform implementation for type {source.TransformType} not found.");
        }


    }
}
