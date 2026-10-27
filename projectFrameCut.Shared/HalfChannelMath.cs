namespace projectFrameCut.Shared;

public static class HalfChannelMath
{
    public static Half Clamp(Half value) => Half.IsFinite(value) ? Half.Clamp(value, Half.Zero, Half.One) : Half.Zero;

    public static Half Lerp(Half left, Half right, Half amount) => left * (Half.One - amount) + right * amount;

    public static Half Overlay(Half top, Half bottom) => top + bottom * (Half.One - top);

    public static Half Blend(Half top, Half bottom, Half topAlpha, Half bottomAlpha)
    {
        Half weight = bottomAlpha * (Half.One - topAlpha), alpha = topAlpha + weight;
        return alpha > Half.Zero ? (top * topAlpha + bottom * weight) / alpha : Half.Zero;
    }

    public static Half Luminance(float red, float green, float blue, float maximum) => Clamp(
        (Half)(red / maximum) * (Half)0.2627f + (Half)(green / maximum) * (Half)0.6780f + (Half)(blue / maximum) * (Half)0.0593f);

    public static Half FromUInt16(ushort value) => (Half)(value / 65535f);

    public static ushort ToUInt16(Half value) => (ushort)MathF.Round((float)Clamp(value) * 65535f);

    public static Half DecodePq(Half value)
    {
        Half p = Half.Pow(Clamp(value), (Half)(32f / 2523f));
        return Half.Pow(Half.Max(p - (Half)(3424f / 4096f), Half.Zero)
            / Half.Max((Half)(2413f / 128f) - (Half)(2392f / 128f) * p, (Half)1e-7f), (Half)(16384f / 2610f));
    }

    public static Half DecodeHlg(Half value)
    {
        value = Clamp(value);
        return value <= (Half)0.5f ? value * value / (Half)3
            : (Half.Exp((value - (Half)0.55991073f) / (Half)0.17883277f) + (Half)0.28466892f) / (Half)12;
    }

    public static Half Average(ReadOnlySpan<Half> source, CancellationToken token = default)
    {
        Half average = Half.Zero;
        for (int start = 0; start < source.Length; start += 256)
        {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(256, source.Length - start);
            Half sum = Half.Zero;
            for (int i = 0; i < count; i++) sum += source[start + i];
            average = Lerp(average, sum / (Half)count, (Half)((double)count / (start + count)));
        }
        return average;
    }
}

public struct HalfChannelAccumulator
{
    private Half sum, mean;
    private int count, total;

    public void Add(Half value)
    {
        sum += value;
        if (++count < 256) return;
        mean = HalfChannelMath.Lerp(mean, sum / (Half)count, (Half)((double)count / (total + count)));
        total += count;
        count = 0;
        sum = Half.Zero;
    }

    public readonly Half Average => count == 0 ? mean
        : HalfChannelMath.Lerp(mean, sum / (Half)count, (Half)((double)count / (total + count)));
}
