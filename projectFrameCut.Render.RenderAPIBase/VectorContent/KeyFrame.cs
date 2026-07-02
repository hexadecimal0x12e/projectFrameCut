namespace projectFrameCut.Render.RenderAPIBase.VectorContent;

/// <summary>
/// A single keyframe that records a value at a normalised point in time
/// and the easing curve used to reach the <b>next</b> keyframe.
/// </summary>
public class VectorAnimationKeyFrame
{
    /// <summary>
    /// Normalised time [0…1] within the track's parent duration.
    /// </summary>
    public float Time { get; set; }

    /// <summary>
    /// The animated (float) value at this keyframe.
    /// </summary>
    public float Value { get; set; }

    /// <summary>
    /// Easing function used for the segment <b>from</b> this keyframe
    /// <b>to</b> the next one. Ignored for the last keyframe.
    /// </summary>
    public EasingMode Easing { get; set; } = EasingMode.Linear;

    public VectorAnimationKeyFrame() { }

    public VectorAnimationKeyFrame(float time, float value, EasingMode easing = EasingMode.Linear)
    {
        Time = time;
        Value = value;
        Easing = easing;
    }
}
