using projectFrameCut.Render.Effect;
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
    public virtual IEffect WithPictureType<TPicture>() where TPicture : IPicture =>
        ImplementType == EffectImplementType.IPicture ? new CrossfadeTransform<TPicture>() : TypedPictureEffect.Create<TPicture>(this);
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new CrossfadeTransform { Parameters = parameters };

    public override IPicture Render(IPicture left, IPicture? right, float progress, TransformSide side, int targetWidth, int targetHeight) => RenderCore(left, right, progress, side, targetWidth, targetHeight);

    protected IPicture RenderCore<TPicture>(TPicture left, TPicture? right, float progress, TransformSide side, int targetWidth, int targetHeight)
        where TPicture : IPicture
    {
        ArgumentNullException.ThrowIfNull(right);
        if (left.Width != right.Width || left.Height != right.Height)
            throw new ArgumentException("Crossfade input sizes must match.");
        if (left is IPicture<Half> lf && right is IPicture<Half> rf)
        {
            var lc = ChannelsFP16(lf); var rc = ChannelsFP16(rf);
            Half p = (Half)Math.Clamp(progress, 0, 1);
            var red = Blend(lc.R, rc.R, lc.A, rc.A, p); var green = Blend(lc.G, rc.G, lc.A, rc.A, p); var blue = Blend(lc.B, rc.B, lc.A, rc.A, p);
            var alpha = new Half[left.Pixels];
            for (int i = 0; i < alpha.Length; i++) alpha[i] = PictureFP16.Unit((float)lc.A[i] * (1 - (float)p) + (float)rc.A[i] * (float)p);
            var lh = lf as IHDRPicture<Half>; var rh = rf as IHDRPicture<Half>;
            Half peak = Half.Max(lh?.MaximumBrightness ?? (Half)203, rh?.MaximumBrightness ?? (Half)203);
            var rr = red.Select(PictureFP16.Unit).ToArray(); var gg = green.Select(PictureFP16.Unit).ToArray(); var bb = blue.Select(PictureFP16.Unit).ToArray();
            if (lh is null && rh is null) return PictureFP16.FromOwnedChannels(left.Width, left.Height, rr, gg, bb, alpha);
            var lb = Enumerable.Range(0, left.Pixels).Select(i => (Half)((float)(lh?.Brightness[i] ?? Half.One) * (float)(lh?.MaximumBrightness ?? (Half)203) / (float)peak)).ToArray();
            var rb = Enumerable.Range(0, right.Pixels).Select(i => (Half)((float)(rh?.Brightness[i] ?? Half.One) * (float)(rh?.MaximumBrightness ?? (Half)203) / (float)peak)).ToArray();
            return HDRPictureFP16.FromOwnedChannels(left.Width, left.Height, rr, gg, bb, alpha, BlendBrightness(lb, rb, lc.A, rc.A, p), peak);
        }
        int bits = PictureCalculation<TPicture>.Is16 ? 16 : PictureCalculation<TPicture>.Is8 ? 8
            : left.BitPerPixel == 16 || right.BitPerPixel == 16 ? 16 : 8;
        var l = left.BitPerPixel == bits ? left : left.ToBitPerPixel(bits);
        var r = right.BitPerPixel == bits ? right : right.ToBitPerPixel(bits);
        try
        {
            Half p = Half.Clamp((Half)progress, Half.Zero, Half.One);
            var lc = Channels<TPicture>(l);
            var rc = Channels<TPicture>(r);
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

    private static (float[] R, float[] G, float[] B, Half[] A) ChannelsFP16(IPicture<Half> p) =>
        (p.r.Select(v => (float)v).ToArray(), p.g.Select(v => (float)v).ToArray(), p.b.Select(v => (float)v).ToArray(),
            p.HasAlphaChannel && p.a is not null ? p.a.ToArray() : Enumerable.Repeat(Half.One, p.Pixels).ToArray());

    private static (float[] R, float[] G, float[] B, Half[] A) Channels<TPicture>(IPicture source) where TPicture : IPicture
    {
        if (PictureCalculation<TPicture>.Is16 || !PictureCalculation<TPicture>.Is8 && source is IPicture<ushort>)
        {
            var p16 = (IPicture<ushort>)source;
            return (p16.r.Take(source.Pixels).Select(x => (float)x).ToArray(), p16.g.Take(source.Pixels).Select(x => (float)x).ToArray(),
                p16.b.Take(source.Pixels).Select(x => (float)x).ToArray(), p16.HasAlphaChannel && p16.a is not null ? p16.a.Take(source.Pixels).ToArray() : Enumerable.Repeat(Half.One, source.Pixels).ToArray());
        }
        var p8 = (IPicture<byte>)source;
        return (p8.r.Take(source.Pixels).Select(x => (float)x).ToArray(), p8.g.Take(source.Pixels).Select(x => (float)x).ToArray(),
            p8.b.Take(source.Pixels).Select(x => (float)x).ToArray(), p8.HasAlphaChannel && p8.a is not null ? p8.a.Take(source.Pixels).ToArray() : Enumerable.Repeat(Half.One, source.Pixels).ToArray());
    }
}

public class CrossfadeTransform<TPicture> : CrossfadeTransform, ITransform<TPicture> where TPicture : IPicture
{
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new CrossfadeTransform<TPicture> { Parameters = parameters };
    public override IPicture Render(IPicture left, IPicture? right, float progress, TransformSide side, int width, int height) =>
        PictureEffectDispatch.Transform(this, left, right, progress, side, width, height);
    public IPicture Render(TPicture left, TPicture? right, float progress, TransformSide side, int width, int height) =>
        RenderCore(left, right, progress, side, width, height);
}

public sealed class CrossfadeTransformProvider : TransformEffectProviderBase
{
    public override string TypeName => "Crossfade";
    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => [];
}
