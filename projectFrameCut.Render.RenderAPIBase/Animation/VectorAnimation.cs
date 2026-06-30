namespace projectFrameCut.Render.RenderAPIBase.Animation;

/// <summary>
/// Factory methods for common vector-animation scenarios.
/// </summary>
public static class VectorAnimation
{
    /// <summary>
    /// Creates a single-track storyboard that animates one property of one
    /// element between two values over the given duration.
    /// </summary>
    /// <param name="durationInFrames">Total animation length in frames.</param>
    /// <param name="elementIndex">Index into <c>VectorPicture.Elements</c>.</param>
    /// <param name="property">The property to animate.</param>
    /// <param name="fromValue">Value at progress = 0.</param>
    /// <param name="toValue">Value at progress = 1.</param>
    /// <param name="easing">Easing applied to the (from → to) segment.</param>
    public static Storyboard CreateSimple(
        uint durationInFrames,
        int elementIndex,
        AnimatableProperty property,
        float fromValue,
        float toValue,
        EasingMode easing = EasingMode.Linear)
    {
        var track = new AnimationTrack
        {
            ElementIndex = elementIndex,
            Property = property,
            KeyFrames = new()
            {
                new KeyFrame(0f, fromValue, easing),
                new KeyFrame(1f, toValue, EasingMode.Linear),
            },
        };

        return new Storyboard
        {
            DurationInFrames = durationInFrames,
            Tracks = new() { track },
        };
    }

    /// <summary>
    /// Creates a storyboard from a pre-built list of tracks.
    /// </summary>
    public static Storyboard Create(uint durationInFrames, params AnimationTrack[] tracks)
    {
        return new Storyboard
        {
            DurationInFrames = durationInFrames,
            Tracks = new(tracks),
        };
    }
}
