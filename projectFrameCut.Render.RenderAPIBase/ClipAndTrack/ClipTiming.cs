using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.RenderAPIBase.ClipAndTrack;

public static class ClipTiming
{
    public static uint RoundFrame(double frame)
    {
        if (!double.IsFinite(frame) || frame < 0)
            throw new ArgumentOutOfRangeException(nameof(frame));
        double rounded = Math.Round(frame, MidpointRounding.AwayFromZero);
        if (rounded > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(frame));
        return (uint)rounded;
    }

    public static uint EffectiveDuration(uint duration, ISpeedVarianceProvider? provider)
    {
        if (duration == 0 || provider is null) return duration;
        try
        {
            uint effective = provider.GetEffectiveLength(duration);
            return effective > 0 ? effective : duration;
        }
        catch
        {
            return duration;
        }
    }

    public static uint SourceOffset(uint offset, uint duration, ISpeedVarianceProvider? provider, uint? effectiveDuration = null)
    {
        if (duration == 0) return 0;
        if (offset >= (effectiveDuration ?? EffectiveDuration(duration, provider))) return duration;
        if (provider is null) return Math.Min(offset, duration - 1);
        ulong left = 0, right = duration - 1, best = 0;
        while (left <= right)
        {
            ulong mid = left + (right - left) / 2;
            uint target;
            try { target = provider.GetTargetFrame((uint)mid); }
            catch { target = (uint)mid; }
            if (target <= offset)
            {
                best = mid;
                left = mid + 1;
            }
            else
            {
                if (mid == 0) break;
                right = mid - 1;
            }
        }
        return (uint)best;
    }

    public static uint SourceDuration(uint effective, uint maximum, ISpeedVarianceProvider? provider)
    {
        if (maximum == 0) return 0;
        if (provider is null) return Math.Clamp(effective, 1u, maximum);
        uint left = 1, right = maximum;
        while (left < right)
        {
            uint mid = left + (right - left) / 2;
            if (EffectiveDuration(mid, provider) < effective) left = mid + 1;
            else right = mid;
        }
        if (left > 1 && Math.Abs((long)EffectiveDuration(left - 1, provider) - effective)
            < Math.Abs((long)EffectiveDuration(left, provider) - effective)) return left - 1;
        return left;
    }

    // Each interval contains forbidden integer positions, including both ends.
    public static long NearestPosition(long desired, long minimum, long maximum, IEnumerable<(long Start, long End)> forbidden)
    {
        if (minimum > maximum) throw new ArgumentOutOfRangeException(nameof(minimum));
        desired = Math.Clamp(desired, minimum, maximum);
        var ranges = forbidden.Where(r => r.Start <= r.End && r.End >= minimum && r.Start <= maximum)
            .Select(r => (Start: Math.Max(minimum, r.Start), End: Math.Min(maximum, r.End))).OrderBy(r => r.Start).ToArray();
        for (int i = 0; i < ranges.Length; i++)
        {
            long start = ranges[i].Start, end = ranges[i].End;
            while (i + 1 < ranges.Length && ranges[i + 1].Start <= end + 1)
                end = Math.Max(end, ranges[++i].End);
            if (desired < start) return desired;
            if (desired > end) continue;
            bool canLeft = start > minimum, canRight = end < maximum;
            if (!canLeft && !canRight) throw new InvalidOperationException("No free timeline position is available.");
            return canLeft && (!canRight || desired - (start - 1) < end + 1 - desired) ? start - 1 : end + 1;
        }
        return desired;
    }
}
