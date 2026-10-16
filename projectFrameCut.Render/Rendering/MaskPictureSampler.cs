using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System.Numerics;

namespace projectFrameCut.Render.Rendering;

internal static class MaskPictureSampler
{
    public static IPicture Extract(IPicture source, MaskClip clip, MaskFrameContext context, uint frame, int width, int height)
    {
        var rect = new ClipPositionTuple(clip.TargetX, clip.TargetY,
            clip.TargetWidth > 0 ? Scale(clip.TargetWidth, context.ProjectWidth, context.Width) : context.Width,
            clip.TargetHeight > 0 ? Scale(clip.TargetHeight, context.ProjectHeight, context.Height) : context.Height, false, clip.Rotation);
        foreach (var effect in (clip.EffectsInstances ?? []).Where(e => e.Enabled).OrderBy(e => e.Index))
        {
            ClipPositionTuple position;
            if (effect is IClipPositionProvider p) position = p.GetPosition(clip, context.Width, context.Height);
            else if (effect is IContinuousClipPositionProvider cp)
                position = cp.GetPosition(clip, frame, context.Width, context.Height, context.ProjectWidth, context.ProjectHeight);
            else continue;
            rect = position.IsDelta ? new(rect.TargetX + position.TargetX, rect.TargetY + position.TargetY,
                rect.TargetWidth + position.TargetWidth, rect.TargetHeight + position.TargetHeight, false, rect.Rotation + position.Rotation) : position;
        }
        IPicture result = source switch
        {
            IHDRPicture<ushort> hdr => new HDRPicture16bpp(width, height) { MaximumBrightness = hdr.MaximumBrightness },
            IPicture<ushort> => new Picture16bpp(width, height),
            IPicture<byte> => new Picture8bpp(width, height),
            _ => throw new NotSupportedException($"Unsupported mask picture {source.GetType().Name}.")
        };
        try
        {
            result.HasAlphaChannel = true;
            if (source is IPicture<byte> p8) Sample(p8, (IPicture<byte>)result, rect, context);
            else Sample((IPicture<ushort>)source, (IPicture<ushort>)result, rect, context);
            result.ProcessStack = source.ProcessStack.ToList();
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static int Scale(int value, int relative, int output)
        => Math.Max(1, (int)Math.Round((double)value * output / relative, MidpointRounding.AwayFromZero));

    private static void Sample<T>(IPicture<T> source, IPicture<T> result, ClipPositionTuple rect,
        MaskFrameContext context) where T : unmanaged, INumber<T>
    {
        result.a = new float[result.Pixels];
        double angle = rect.Rotation * Math.PI / 180, cos = Math.Cos(angle), sin = Math.Sin(angle);
        var hdr = source as IHDRPicture<ushort>;
        var outputHdr = result as IHDRPicture<ushort>;
        double cx = Math.Round((double)rect.TargetX * context.Width / context.ProjectWidth, MidpointRounding.AwayFromZero) + rect.TargetWidth / 2d;
        double cy = Math.Round((double)rect.TargetY * context.Height / context.ProjectHeight, MidpointRounding.AwayFromZero) + rect.TargetHeight / 2d;
        for (int y = 0; y < result.Height; y++)
        {
            context.Token.ThrowIfCancellationRequested();
            for (int x = 0; x < result.Width; x++)
            {
                double dx = ((x + 0.5) / result.Width - 0.5) * rect.TargetWidth;
                double dy = ((y + 0.5) / result.Height - 0.5) * rect.TargetHeight;
                // Sample the rotated canvas region before the compositor rotates this local frame.
                double sx = cx + dx * cos - dy * sin - 0.5;
                double sy = cy + dx * sin + dy * cos - 0.5;
                int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                double fx = sx - x0, fy = sy - y0, r = 0, g = 0, b = 0, a = 0, brightness = 0;
                for (int j = 0; j < 2; j++)
                {
                    for (int i = 0; i < 2; i++)
                    {
                        int xx = x0 + i, yy = y0 + j;
                        if (xx < 0 || xx >= source.Width || yy < 0 || yy >= source.Height) continue;
                        int index = yy * source.Width + xx;
                        double w = (i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy)
                            * (source.HasAlphaChannel && source.a is not null ? source.a[index] : 1);
                        a += w;
                        r += double.CreateChecked(source.r[index]) * w;
                        g += double.CreateChecked(source.g[index]) * w;
                        b += double.CreateChecked(source.b[index]) * w;
                        if (hdr is not null) brightness += hdr.Brightness[index] * w;
                    }
                }
                if (a <= 1e-10) continue;
                int output = y * result.Width + x;
                result.r[output] = T.CreateSaturating(Math.Round(r / a));
                result.g[output] = T.CreateSaturating(Math.Round(g / a));
                result.b[output] = T.CreateSaturating(Math.Round(b / a));
                result.a[output] = (float)Math.Clamp(a, 0, 1);
                if (outputHdr is not null) outputHdr.Brightness[output] = (float)(brightness / a);
            }
        }
    }
}
