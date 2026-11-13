using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;

using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace SomePublisher;

public class ExampleCrossfadeTransform : TransformEffectBase
{
    public override string FromPlugin => ExamplePluginConstants.PluginId;
    public override string TypeName => "ExampleCrossfade";
    public override TransformDefinition Definition => TransformDefinition.Clip | TransformDefinition.SupportTwoInput;
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new ExampleCrossfadeTransform { Parameters = parameters };

    public override IPicture Render(IPicture left, IPicture? right, float progress, TransformSide side, int targetWidth, int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(right);
        if (left.Width != right.Width || left.Height != right.Height)
            throw new ArgumentException("Crossfade input sizes must match.");
        int bits = left.BitPerPixel == 16 || right.BitPerPixel == 16 ? 16 : 8;
        var l = left.BitPerPixel == bits ? left : left.ToBitPerPixel(bits);
        var r = right.BitPerPixel == bits ? right : right.ToBitPerPixel(bits);
        try
        {
            Half p = Half.Clamp((Half)progress, Half.Zero, Half.One);
            var lc = Channels(l);
            var rc = Channels(r);
            float[] red = Blend(lc.R, rc.R, lc.A, rc.A, p);
            float[] green = Blend(lc.G, rc.G, lc.A, rc.A, p);
            float[] blue = Blend(lc.B, rc.B, lc.A, rc.A, p);
            var alpha = new Half[l.Pixels];
            for (int i = 0; i < alpha.Length; i++) alpha[i] = lc.A[i] * (Half.One - p) + rc.A[i] * p;
            if (bits == 8)
                return new Picture8bpp(l.Width, l.Height)
                {
                    r = red.Select(x => (byte)Math.Clamp(Math.Round(x), 0, byte.MaxValue)).ToArray(),
                    g = green.Select(x => (byte)Math.Clamp(Math.Round(x), 0, byte.MaxValue)).ToArray(),
                    b = blue.Select(x => (byte)Math.Clamp(Math.Round(x), 0, byte.MaxValue)).ToArray(),
                    a = alpha, HasAlphaChannel = true
                };
            var lh = l as IHDRPicture<ushort>;
            var rh = r as IHDRPicture<ushort>;
            Half peak = Half.Max(lh?.MaximumBrightness ?? (Half)203, rh?.MaximumBrightness ?? (Half)203);
            Picture16bpp output = lh is not null || rh is not null
                ? new HDRPicture16bpp(l.Width, l.Height) { MaximumBrightness = peak }
                : new Picture16bpp(l.Width, l.Height);
            output.r = red.Select(x => (ushort)Math.Clamp(Math.Round(x), 0, ushort.MaxValue)).ToArray();
            output.g = green.Select(x => (ushort)Math.Clamp(Math.Round(x), 0, ushort.MaxValue)).ToArray();
            output.b = blue.Select(x => (ushort)Math.Clamp(Math.Round(x), 0, ushort.MaxValue)).ToArray();
            output.a = alpha;
            output.HasAlphaChannel = true;
            if (output is IHDRPicture<ushort> hdr)
                hdr.Brightness = BlendBrightness(Brightness(lh, l.Pixels, peak), Brightness(rh, r.Pixels, peak), lc.A, rc.A, p);
            return output;
        }
        finally
        {
            if (!ReferenceEquals(l, left)) l.Dispose();
            if (!ReferenceEquals(r, right)) r.Dispose();
        }
    }

    protected virtual float[] Blend(float[] left, float[] right, Half[] leftAlpha, Half[] rightAlpha, Half progress)
    {
        var output = new float[left.Length];
        for (int i = 0; i < output.Length; i++)
        {
            Half a = leftAlpha[i] * (Half.One - progress), b = rightAlpha[i] * progress;
            output[i] = a + b > Half.Zero ? (left[i] * (float)a + right[i] * (float)b) / (float)(a + b) : 0;
        }
        return output;
    }

    private static Half[] BlendBrightness(Half[] left, Half[] right, Half[] leftAlpha, Half[] rightAlpha, Half progress)
    {
        var result = new Half[left.Length];
        for (int i = 0; i < result.Length; i++)
        {
            Half a = leftAlpha[i] * (Half.One - progress), b = rightAlpha[i] * progress;
            result[i] = a + b > Half.Zero ? (left[i] * a + right[i] * b) / (a + b) : Half.Zero;
        }
        return result;
    }

    private static Half[] Brightness(IHDRPicture<ushort>? source, int count, Half peak) =>
        Enumerable.Range(0, count).Select(i => source is not null && i < source.Brightness.Length
            ? Half.Clamp(source.Brightness[i] * source.MaximumBrightness / peak, Half.Zero, Half.One) : (Half)203 / peak).ToArray();

    private static (float[] R, float[] G, float[] B, Half[] A) Channels(IPicture source)
    {
        if (source is IPicture<ushort> p16)
            return (p16.r.Take(source.Pixels).Select(x => (float)x).ToArray(), p16.g.Take(source.Pixels).Select(x => (float)x).ToArray(),
                p16.b.Take(source.Pixels).Select(x => (float)x).ToArray(), p16.HasAlphaChannel && p16.a is not null ? p16.a.Take(source.Pixels).ToArray() : Enumerable.Repeat(Half.One, source.Pixels).ToArray());
        var p8 = (IPicture<byte>)source;
        return (p8.r.Take(source.Pixels).Select(x => (float)x).ToArray(), p8.g.Take(source.Pixels).Select(x => (float)x).ToArray(),
            p8.b.Take(source.Pixels).Select(x => (float)x).ToArray(), p8.HasAlphaChannel && p8.a is not null ? p8.a.Take(source.Pixels).ToArray() : Enumerable.Repeat(Half.One, source.Pixels).ToArray());
    }
}
