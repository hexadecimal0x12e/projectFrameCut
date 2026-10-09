using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using System.Diagnostics;
using System.Numerics;

namespace projectFrameCut.Render.ClipsAndTracks;

public static class VideoClipRotation
{
    public const string LegacyEffectName = "__Internal_Rotation__";

    public static float Normalize(float angle)
    {
        if (!float.IsFinite(angle)) return 0;
        angle %= 360f;
        return angle < 0 ? angle + 360f : angle;
    }
    public static float GetAngle(IClip clip) => Normalize(clip.Rotation);

    public static float GetAngle(IClip clip, uint frame, int width, int height)
        => GetAngle(clip.Rotation, clip, clip.EffectsInstances, frame, width, height);

    public static float GetAngle(float angle, IClip? clip, IEnumerable<IEffect>? effects, uint frame, int width, int height)
    {
        if (clip is null || effects is null) return Normalize(angle);
        using var context = ValueProviderFrameContext.PushFrame(frame,
            Math.Clamp(((float)frame - clip.StartFrame) / Math.Max(1u, clip.GetEffectiveDuration()), 0, 1));
        foreach (var effect in effects.Where(e => e.Enabled).OrderBy(e => e.Index))
        {
            ClipPositionTuple position;
            if (effect is IClipPositionProvider p) position = p.GetPosition(clip, width, height);
            else if (effect is IContinuousClipPositionProvider cp) position = cp.GetPosition(clip, frame, width, height);
            else continue;
            angle = position.IsDelta ? angle + position.Rotation : position.Rotation;
        }
        return Normalize(angle);
    }

    public static (double X, double Y, double Width, double Height) GetBounds(float angle, double x, double y, double width, double height)
    {
        double radians = Normalize(angle) * Math.PI / 180d;
        double cos = Math.Abs(Math.Cos(radians)), sin = Math.Abs(Math.Sin(radians));
        if (cos < 1e-10) cos = 0;
        if (sin < 1e-10) sin = 0;
        double w = width * cos + height * sin, h = width * sin + height * cos;
        return (x + (width - w) / 2, y + (height - h) / 2, w, h);
    }

    public static (int Width, int Height) GetOutputSize(float angle, int width, int height)
    {
        if (Math.Abs(Normalize(angle)) < 0.0001f) return (width, height);
        var bounds = GetBounds(angle, 0, 0, width, height);
        return (Math.Max(1, (int)Math.Ceiling(bounds.Width - 1e-9)), Math.Max(1, (int)Math.Ceiling(bounds.Height - 1e-9)));
    }

    public static bool Migrate(ClipDraftDTO dto)
    {
        if (dto.Effects?.FirstOrDefault(e => e.Name == LegacyEffectName && e.TypeName == "Rotation") is not { } effect)
            return false;
        if (effect.Enabled) dto.Rotation += DynamicParam.ToFloat(effect.Parameters?.GetValueOrDefault("Angle"));
        dto.Rotation = Normalize(dto.Rotation);
        dto.Effects = dto.Effects.Where(e => e != effect).ToArray();
        Log($"Migrated rotation for clip {dto.Id}: {dto.Rotation} degrees.");
        return true;
    }

    public static IPicture ReadFrame(IClip clip, uint frameIndex, int width, int height, IPicture.PicturePixelMode pixelMode)
    {
        var source = VectorPictureEffectProcessing.ReadFrame(clip, frameIndex, width, height, pixelMode);
        try
        {
            if (clip.AlternativeSource is { } replacement && replacement.SupportsSourceReplacement(clip, width, height))
            {
                var replaced = replacement.Compute(clip, source, width, height, frameIndex, pixelMode);
                if (!ReferenceEquals(source, replaced)) source.Dispose();
                source = replaced;
            }
            return source;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    public static IPicture Apply(IClip clip, IPicture source) => Apply(source, clip.Rotation);

    public static IPicture Apply(IPicture source, float angle)
    {
        var result = Rotate(source, angle);
        if (!ReferenceEquals(source, result)) source.Dispose();
        return result;
    }

    public static IPicture Rotate(IPicture source, float angle)
    {
        angle = Normalize(angle);
        if (Math.Abs(angle) < 0.0001f) return source;
        var sw = Stopwatch.StartNew();
        var (width, height) = GetOutputSize(angle, source.Width, source.Height);
        IPicture result = source switch
        {
            IHDRPicture<ushort> hdr => new HDRPicture16bpp(width, height) { MaximumBrightness = hdr.MaximumBrightness },
            IPicture<ushort> => new Picture16bpp(width, height),
            IPicture<byte> => new Picture8bpp(width, height),
            _ => throw new NotSupportedException($"Clip rotation does not support {source.GetType().Name}.")
        };
        result.HasAlphaChannel = true;
        if (source is IPicture<byte> p8) RotatePixels(p8, (IPicture<byte>)result, angle);
        else RotatePixels((IPicture<ushort>)source, (IPicture<ushort>)result, angle);
        result.Tag = source.Tag;
        result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack
        {
            OperationDisplayName = "Clip rotation",
            Operator = typeof(VideoClipRotation),
            Elapsed = sw.Elapsed,
            Properties = new() { ["Angle"] = angle },
            ProcessingFuncStackTrace = new StackTrace(true)
        }).ToList();
        return result;
    }

    private static void RotatePixels<T>(IPicture<T> source, IPicture<T> result, float angle) where T : unmanaged, INumber<T>
    {
        result.a = new float[result.Pixels];
        double radians = angle * Math.PI / 180d;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        if (Math.Abs(cos) < 1e-10) cos = 0;
        if (Math.Abs(sin) < 1e-10) sin = 0;
        var hdrSource = source as IHDRPicture<ushort>;
        var hdrResult = result as IHDRPicture<ushort>;
        for (int y = 0; y < result.Height; y++)
        {
            for (int x = 0; x < result.Width; x++)
            {
                // Map pixel centers back into the source; interpolate premultiplied channels.
                double dx = x + 0.5 - result.Width / 2d, dy = y + 0.5 - result.Height / 2d;
                double sx = dx * cos + dy * sin + source.Width / 2d - 0.5;
                double sy = -dx * sin + dy * cos + source.Height / 2d - 0.5;
                int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                double fx = sx - x0, fy = sy - y0;
                double r = 0, g = 0, b = 0, a = 0, brightness = 0;
                for (int j = 0; j < 2; j++)
                {
                    for (int i = 0; i < 2; i++)
                    {
                        int px = x0 + i, py = y0 + j;
                        if (px < 0 || px >= source.Width || py < 0 || py >= source.Height) continue;
                        int index = py * source.Width + px;
                        double weight = (i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy)
                            * (source.HasAlphaChannel && source.a is not null ? source.a[index] : 1);
                        a += weight;
                        r += double.CreateChecked(source.r[index]) * weight;
                        g += double.CreateChecked(source.g[index]) * weight;
                        b += double.CreateChecked(source.b[index]) * weight;
                        if (hdrSource is not null) brightness += hdrSource.Brightness[index] * weight;
                    }
                }
                if (a <= 1e-10) continue;
                int output = y * result.Width + x;
                result.r[output] = T.CreateSaturating(Math.Round(r / a));
                result.g[output] = T.CreateSaturating(Math.Round(g / a));
                result.b[output] = T.CreateSaturating(Math.Round(b / a));
                result.a[output] = (float)Math.Clamp(a, 0, 1);
                if (hdrResult is not null) hdrResult.Brightness[output] = (float)(brightness / a);
            }
        }
    }
}
