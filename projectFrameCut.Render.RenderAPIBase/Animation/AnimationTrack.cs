using System;
using System.Collections.Generic;

namespace projectFrameCut.Render.RenderAPIBase.Animation;

/// <summary>
/// Describes an animation of a single <see cref="AnimatableProperty"/> on a
/// single element (identified by its index in <c>VectorPicture.Elements</c>)
/// over a set of <see cref="KeyFrame"/>s.
/// </summary>
public class AnimationTrack
{
    /// <summary>
    /// Index into <c>VectorPicture.Elements</c> identifying the element
    /// that this track animates.
    /// </summary>
    public int ElementIndex { get; set; }

    /// <summary>Which property of the element to animate.</summary>
    public AnimatableProperty Property { get; set; }

    /// <summary>
    /// Keyframes sorted by <see cref="KeyFrame.Time"/> ascending.
    /// Must contain at least one entry; with a single entry the value
    /// is considered constant.
    /// </summary>
    public List<KeyFrame> KeyFrames { get; set; } = new();

    /// <summary>
    /// Evaluate the track's value at the given normalised progress [0…1].
    /// </summary>
    public float GetValue(float progress)
    {
        var frames = KeyFrames;
        if (frames is null || frames.Count == 0)
            return 0f;

        if (frames.Count == 1)
            return frames[0].Value;

        progress = Math.Clamp(progress, 0f, 1f);

        // Before or at the first keyframe
        if (progress <= frames[0].Time)
            return frames[0].Value;

        // After or at the last keyframe
        KeyFrame last = frames[^1];
        if (progress >= last.Time)
            return last.Value;

        // Find the segment containing this progress and interpolate
        for (int i = 1; i < frames.Count; i++)
        {
            KeyFrame prev = frames[i - 1];
            KeyFrame next = frames[i];

            if (progress >= next.Time)
                continue;

            float span = next.Time - prev.Time;
            if (span <= 0f)
                return next.Value;

            float t = (progress - prev.Time) / span;
            float eased = EasingFunctions.Apply(prev.Easing, t);
            return prev.Value + (next.Value - prev.Value) * eased;
        }

        return last.Value;
    }
}
