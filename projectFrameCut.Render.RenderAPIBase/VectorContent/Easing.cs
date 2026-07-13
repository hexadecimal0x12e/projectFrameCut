using System;

namespace projectFrameCut.Render.RenderAPIBase.VectorContent
{
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


    /// <summary>
    /// Static helper that applies an easing curve to a normalised time value.
    /// All formulas are standard Robert Penner easing functions.
    /// </summary>
    public static class EasingFunctions
    {
        /// <summary>
        /// Applies the specified easing <paramref name="mode"/> to the normalised
        /// time <paramref name="t"/> (clamped to [0,1] internally).
        /// Returns the eased value — still typically in [0,1] for most modes,
        /// except Elastic modes which may overshoot.
        /// </summary>
        public static float Apply(EasingMode mode, float t)
        {
            t = Math.Clamp(t, 0f, 1f);

            return mode switch
            {
                EasingMode.Linear     => t,

                EasingMode.QuadIn     => t * t,
                EasingMode.QuadOut    => t * (2f - t),
                EasingMode.QuadInOut  => t < 0.5f ? 2f * t * t : -1f + (4f - 2f * t) * t,

                EasingMode.CubicIn    => t * t * t,
                EasingMode.CubicOut   => (t - 1f) * (t - 1f) * (t - 1f) + 1f,
                EasingMode.CubicInOut => t < 0.5f ? 4f * t * t * t : (t - 1f) * (2f * t - 2f) * (2f * t - 2f) + 1f,

                EasingMode.SineIn     => 1f - MathF.Cos(t * MathF.PI * 0.5f),
                EasingMode.SineOut    => MathF.Sin(t * MathF.PI * 0.5f),
                EasingMode.SineInOut  => 0.5f * (1f - MathF.Cos(t * MathF.PI)),

                EasingMode.ElasticIn  => ElasticIn(t),
                EasingMode.ElasticOut => ElasticOut(t),

                EasingMode.BounceOut  => BounceOut(t),

                _ => t,
            };
        }

        // ---------------------------------------------------------------
        // Private helpers
        // ---------------------------------------------------------------

        private static float ElasticIn(float t)
        {
            if (t <= 0f || t >= 1f) return t;
            const float p = 0.3f;
            const float s = p / 4f;
            return -MathF.Pow(2f, 10f * (t -= 1f))
                   * MathF.Sin((t - s) * (2f * MathF.PI) / p);
        }

        private static float ElasticOut(float t)
        {
            if (t <= 0f || t >= 1f) return t;
            const float p = 0.3f;
            const float s = p / 4f;
            return MathF.Pow(2f, -10f * t)
                   * MathF.Sin((t - s) * (2f * MathF.PI) / p) + 1f;
        }

        private static float BounceOut(float t)
        {
            const float n1 = 7.5625f;
            const float d1 = 2.75f;

            if (t < 1f / d1)
                return n1 * t * t;

            if (t < 2f / d1)
                return n1 * (t -= 1.5f / d1) * t + 0.75f;

            if (t < 2.5f / d1)
                return n1 * (t -= 2.25f / d1) * t + 0.9375f;

            return n1 * (t -= 2.625f / d1) * t + 0.984375f;
        }
    }
}
