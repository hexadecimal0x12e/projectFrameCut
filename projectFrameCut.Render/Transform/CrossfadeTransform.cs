using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Transform;

public class CrossfadeTransform : TransformEffectBase
{
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override string TypeName => "Crossfade";
    public override TransformDefinition Definition => TransformDefinition.Clip | TransformDefinition.SupportTwoInput;
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new CrossfadeTransform { Parameters = parameters };

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
            float p = Math.Clamp(progress, 0, 1);
            var lc = Channels(l);
            var rc = Channels(r);
            float[] red = Blend(lc.R, rc.R, lc.A, rc.A, p);
            float[] green = Blend(lc.G, rc.G, lc.A, rc.A, p);
            float[] blue = Blend(lc.B, rc.B, lc.A, rc.A, p);
            var alpha = new float[l.Pixels];
            for (int i = 0; i < alpha.Length; i++) alpha[i] = lc.A[i] * (1 - p) + rc.A[i] * p;
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
            float peak = Math.Max(lh?.MaximumBrightness ?? 203, rh?.MaximumBrightness ?? 203);
            Picture16bpp output = lh is not null || rh is not null
                ? new HDRPicture16bpp(l.Width, l.Height) { MaximumBrightness = peak }
                : new Picture16bpp(l.Width, l.Height);
            output.r = red.Select(x => (ushort)Math.Clamp(Math.Round(x), 0, ushort.MaxValue)).ToArray();
            output.g = green.Select(x => (ushort)Math.Clamp(Math.Round(x), 0, ushort.MaxValue)).ToArray();
            output.b = blue.Select(x => (ushort)Math.Clamp(Math.Round(x), 0, ushort.MaxValue)).ToArray();
            output.a = alpha;
            output.HasAlphaChannel = true;
            if (output is IHDRPicture<ushort> hdr)
                hdr.Brightness = Blend(Brightness(lh, l.Pixels, peak), Brightness(rh, r.Pixels, peak), lc.A, rc.A, p);
            return output;
        }
        finally
        {
            if (!ReferenceEquals(l, left)) l.Dispose();
            if (!ReferenceEquals(r, right)) r.Dispose();
        }
    }

    protected virtual float[] Blend(float[] left, float[] right, float[] leftAlpha, float[] rightAlpha, float progress)
    {
        var output = new float[left.Length];
        for (int i = 0; i < output.Length; i++)
        {
            float a = leftAlpha[i] * (1 - progress), b = rightAlpha[i] * progress;
            output[i] = a + b > 0 ? (left[i] * a + right[i] * b) / (a + b) : 0;
        }
        return output;
    }

    private static float[] Brightness(IHDRPicture<ushort>? source, int count, float peak) =>
        Enumerable.Range(0, count).Select(i => source is not null && i < source.Brightness.Length
            ? Math.Clamp(source.Brightness[i] * source.MaximumBrightness / peak, 0, 1) : 203 / peak).ToArray();

    private static (float[] R, float[] G, float[] B, float[] A) Channels(IPicture source)
    {
        if (source is IPicture<ushort> p16)
            return (p16.r.Take(source.Pixels).Select(x => (float)x).ToArray(), p16.g.Take(source.Pixels).Select(x => (float)x).ToArray(),
                p16.b.Take(source.Pixels).Select(x => (float)x).ToArray(), p16.HasAlphaChannel && p16.a is not null ? p16.a.Take(source.Pixels).ToArray() : Enumerable.Repeat(1f, source.Pixels).ToArray());
        var p8 = (IPicture<byte>)source;
        return (p8.r.Take(source.Pixels).Select(x => (float)x).ToArray(), p8.g.Take(source.Pixels).Select(x => (float)x).ToArray(),
            p8.b.Take(source.Pixels).Select(x => (float)x).ToArray(), p8.HasAlphaChannel && p8.a is not null ? p8.a.Take(source.Pixels).ToArray() : Enumerable.Repeat(1f, source.Pixels).ToArray());
    }
}

public sealed class CrossfadeTransformProvider : TransformEffectProviderBase
{
    public override string TypeName => "Crossfade";
    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => [];
}
