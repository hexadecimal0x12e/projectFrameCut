using projectFrameCut.Render.RenderAPIBase.VectorContent;

namespace projectFrameCut.Render.VectorContent;

/// <summary>
/// Shared helper for evaluating a single animated field from a list of keyframes.
/// </summary>
public static class VectorAnimationEvaluator
{
    /// <summary>
    /// Evaluates the value of <paramref name="fieldId"/> at the given normalized
    /// <paramref name="progress"/> by interpolating between the component's
    /// animation keyframes. Returns <paramref name="defaultValue"/> when no
    /// keyframes exist for the field.
    /// </summary>
    public static float EvaluateField(
        this IEnumerable<VectorAnimationKeyFrame> animationFrames,
        string fieldId,
        float progress,
        float defaultValue)
    {
        var frames = animationFrames
            .Where(kf => kf.TargetFieldId == fieldId)
            .OrderBy(kf => kf.Time)
            .ToList();

        if (frames.Count == 0)
        {
            return defaultValue;
        }

        if (frames.Count == 1 || progress <= frames[0].Time)
        {
            return frames[0].Value;
        }

        var last = frames[^1];
        if (progress >= last.Time)
        {
            return last.Value;
        }

        for (int i = 1; i < frames.Count; i++)
        {
            var prev = frames[i - 1];
            var next = frames[i];

            if (progress > next.Time)
            {
                continue;
            }

            float span = next.Time - prev.Time;
            if (span <= 0f)
            {
                return next.Value;
            }

            float t = (progress - prev.Time) / span;
            float eased = EasingFunctions.Apply(prev.Easing, t);
            return prev.Value + (next.Value - prev.Value) * eased;
        }

        return last.Value;
    }
}
