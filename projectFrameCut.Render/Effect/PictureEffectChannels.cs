using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;

namespace projectFrameCut.Render.Effect;

public static class PictureEffectChannels
{
    public static IPicture PreserveHdr(IPicture result, IPicture source, float[]? brightness = null)
    {
        if (source is not IHDRPicture<ushort> hdr) return result;
        return new HDRPicture16bpp((IPicture<ushort>)result, true)
        {
            HasAlphaChannel = result.HasAlphaChannel,
            Brightness = brightness ?? hdr.Brightness.ToArray(),
            MaximumBrightness = hdr.MaximumBrightness,
            ProcessStack = result.ProcessStack,
            Tag = result.Tag
        };
    }

    public static IPicture MapHdr(IPicture result, IPicture source, Func<int, int, (float X, float Y)> map)
    {
        if (source is not IHDRPicture<ushort> hdr) return result;
        var brightness = new float[result.Pixels];
        for (int y = 0; y < result.Height; y++)
        {
            for (int x = 0; x < result.Width; x++)
            {
                var (sx, sy) = map(x, y);
                if (sx < 0 || sx >= source.Width || sy < 0 || sy >= source.Height) continue;
                int x0 = (int)sx, y0 = (int)sy;
                int x1 = Math.Min(x0 + 1, source.Width - 1), y1 = Math.Min(y0 + 1, source.Height - 1);
                float fx = sx - x0, fy = sy - y0;
                brightness[y * result.Width + x] =
                    hdr.Brightness[y0 * source.Width + x0] * (1 - fx) * (1 - fy) + hdr.Brightness[y0 * source.Width + x1] * fx * (1 - fy) +
                    hdr.Brightness[y1 * source.Width + x0] * (1 - fx) * fy + hdr.Brightness[y1 * source.Width + x1] * fx * fy;
            }
        }
        return PreserveHdr(result, source, brightness);
    }
}
