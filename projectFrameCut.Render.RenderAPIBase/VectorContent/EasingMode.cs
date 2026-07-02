namespace projectFrameCut.Render.RenderAPIBase.VectorContent;

/// <summary>
/// Standard easing-curve types used by <see cref="VectorAnimationKeyFrame"/> and
/// <see cref="EasingFunctions"/>.
/// Based on Robert Penner's easing functions.
/// </summary>
public enum EasingMode
{
    /// <summary>No easing — linear interpolation.</summary>
    Linear,

    /// <summary>Quadratic ease-in (slow start).</summary>
    QuadIn,

    /// <summary>Quadratic ease-out (slow end).</summary>
    QuadOut,

    /// <summary>Quadratic ease-in-out.</summary>
    QuadInOut,

    /// <summary>Cubic ease-in.</summary>
    CubicIn,

    /// <summary>Cubic ease-out.</summary>
    CubicOut,

    /// <summary>Cubic ease-in-out.</summary>
    CubicInOut,

    /// <summary>Sinusoidal ease-in.</summary>
    SineIn,

    /// <summary>Sinusoidal ease-out.</summary>
    SineOut,

    /// <summary>Sinusoidal ease-in-out.</summary>
    SineInOut,

    /// <summary>Elastic ease-in (overshoot at start).</summary>
    ElasticIn,

    /// <summary>Elastic ease-out (overshoot at end).</summary>
    ElasticOut,

    /// <summary>Bounce ease-out (bounce at end).</summary>
    BounceOut,
}
