using projectFrameCut.Drawing.Vector;
using System;
using System.Collections.Generic;

namespace projectFrameCut.Render.RenderAPIBase.VectorContent;

/// <summary>
/// Per-component animation timeline with independent duration.
/// Each <see cref="VectorComponent"/> owns one of these to control
/// how its shape animates over the clip's total duration.
/// </summary>
public class ComponentAnimations
{
    /// <summary>
    /// Duration of this component's animation in source frames.
    /// The local progress [0..1] is mapped from the clip frame
    /// relative to this duration.
    /// </summary>
    public uint DurationInFrames { get; set; } = 30;

    /// <summary>
    /// All animation tracks for this component.
    /// <see cref="AnimationTrack.ElementIndex"/> is ignored for
    /// single-shape components (always treated as index 0).
    /// </summary>
    public List<AnimationTrack> Tracks { get; set; } = new();

    /// <summary>
    /// Apply this component's animation to the given element for the
    /// specified clip frame.
    /// </summary>
    /// <param name="element">The built shape element (not modified).</param>
    /// <param name="clipFrame">Zero-based frame index within the clip.</param>
    /// <param name="clipDuration">Total duration of the parent clip in frames.</param>
    /// <returns>
    /// A new animated element if tracks exist and the element supports cloning;
    /// the original element unchanged if there are no tracks.
    /// </returns>
    public VectorCanvasElement Apply(VectorCanvasElement element, uint clipFrame, uint clipDuration)
    {
        if (Tracks is null || Tracks.Count == 0)
            return element;

        float localProgress = CalculateLocalProgress(clipFrame, clipDuration);

        // Only ShapeCanvasElement supports deep-cloning for animation
        if (element is not ShapeCanvasElement shape)
            return element;

        var animated = shape.Clone();

        foreach (var track in Tracks)
        {
            if (track is null || track.KeyFrames is null || track.KeyFrames.Count == 0)
                continue;

            float value = track.GetValue(localProgress);
            VectorAnimations.ApplyValue(animated, track.Property, value);
        }

        return animated;
    }

    /// <summary>
    /// Maps the clip frame to this component's local normalised progress [0..1].
    /// If the component's duration is shorter than the clip's, the animation
    /// finishes early and holds at progress 1.0.
    /// </summary>
    public float CalculateLocalProgress(uint clipFrame, uint clipDuration)
    {
        uint effectiveDuration = Math.Max(1, DurationInFrames);
        uint clampedFrame = Math.Min(clipFrame, effectiveDuration - 1);
        float progress = (float)clampedFrame / (effectiveDuration - 1);
        return Math.Clamp(progress, 0f, 1f);
    }
}
