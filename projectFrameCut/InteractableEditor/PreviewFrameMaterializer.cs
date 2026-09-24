using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Processing.Converting;
using System.Buffers.Binary;

namespace projectFrameCut.LivePreview;

internal static class PreviewFrameMaterializer
{
    public static IPicture LoadVfd(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        if (!PictureExtensions.SharedVfdPictureDecoder.TryLoad(stream, out IPicture? picture) || picture is null)
            throw new InvalidDataException($"Preview source '{path}' is not a valid VFD picture.");
        return picture;
    }

    public static byte[] ToPngBytes(string path)
    {
        using var picture = LoadVfd(path);
        return ToPngBytes(picture);
    }

    public static byte[] ToPngBytes(IPicture picture)
    {
        IPicture output = picture is IHDRPicture<ushort> hdr
            ? hdr.DegradeToSDR(HDRImageDegradeToSDRMode.NormalizeBrightnessToRGB)
            : picture;
        try
        {
            using var stream = new MemoryStream();
            output.SaveToPng(stream);
            return stream.ToArray();
        }
        finally
        {
            if (!ReferenceEquals(output, picture)) output.Dispose();
        }
    }

    public static ScRgbFrame ToScRgb(string path)
    {
        using var picture = LoadVfd(path);
        return ToScRgb(picture);
    }

    public static ScRgbFrame ToScRgb(IPicture picture)
    {
        var width = Math.Max(1, picture.Width);
        var height = Math.Max(1, picture.Height);
        var stride = checked(width * 8);
        var bytes = new byte[checked(stride * height)];
        var hasTransparency = false;

        if (picture is IPicture<ushort> picture16)
        {
            var hdr = picture16 as IHDRPicture<ushort>;
            var isPqHdr = hdr is not null && float.IsFinite(hdr.MaximumBrightness) && hdr.MaximumBrightness > 300f;
            for (var i = 0; i < picture16.Pixels; i++)
            {
                var alpha = ResolveAlpha(picture16.a, picture16.HasAlphaChannel, i);
                hasTransparency |= alpha < 1f;
                var red = picture16.r[i] / 65535f;
                var green = picture16.g[i] / 65535f;
                var blue = picture16.b[i] / 65535f;
                var color = isPqHdr
                    ? PqBt2020ToScRgb(red, green, blue)
                    : (SrgbToLinear(red), SrgbToLinear(green), SrgbToLinear(blue));
                WriteHalfPixel(bytes, i * 8, color.Item1 * alpha, color.Item2 * alpha, color.Item3 * alpha, alpha);
            }
            return new ScRgbFrame(bytes, width, height, stride, hasTransparency);
        }

        if (picture is not IPicture<byte> picture8)
            throw new NotSupportedException($"Cannot materialize {picture.GetType().Name} as FP16 scRGB.");

        for (var i = 0; i < picture8.Pixels; i++)
        {
            var alpha = ResolveAlpha(picture8.a, picture8.HasAlphaChannel, i);
            hasTransparency |= alpha < 1f;
            WriteHalfPixel(
                bytes,
                i * 8,
                SrgbToLinear(picture8.r[i] / 255f) * alpha,
                SrgbToLinear(picture8.g[i] / 255f) * alpha,
                SrgbToLinear(picture8.b[i] / 255f) * alpha,
                alpha);
        }
        return new ScRgbFrame(bytes, width, height, stride, hasTransparency);
    }

    public static ImageSource CreateImageSource(string path)
    {
        var bytes = ToPngBytes(path);
        return ImageSource.FromStream(() => new MemoryStream(bytes, writable: false));
    }

    private static float ResolveAlpha(float[]? alpha, bool hasAlpha, int index)
        => hasAlpha && alpha is not null && index < alpha.Length && float.IsFinite(alpha[index])
            ? Math.Clamp(alpha[index], 0f, 1f)
            : 1f;

    private static void WriteHalfPixel(byte[] destination, int offset, float red, float green, float blue, float alpha)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(offset, 2), BitConverter.HalfToUInt16Bits((Half)Math.Clamp(red, -0.5f, 125f)));
        BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(offset + 2, 2), BitConverter.HalfToUInt16Bits((Half)Math.Clamp(green, -0.5f, 125f)));
        BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(offset + 4, 2), BitConverter.HalfToUInt16Bits((Half)Math.Clamp(blue, -0.5f, 125f)));
        BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(offset + 6, 2), BitConverter.HalfToUInt16Bits((Half)Math.Clamp(alpha, 0f, 1f)));
    }

    private static (float Red, float Green, float Blue) PqBt2020ToScRgb(float red, float green, float blue)
    {
        const float nitsPerScRgbUnit = 80f;
        var r2020 = DecodePq(red) * 10000f / nitsPerScRgbUnit;
        var g2020 = DecodePq(green) * 10000f / nitsPerScRgbUnit;
        var b2020 = DecodePq(blue) * 10000f / nitsPerScRgbUnit;
        return (
            1.660491f * r2020 - 0.587641f * g2020 - 0.072850f * b2020,
            -0.124550f * r2020 + 1.132900f * g2020 - 0.008349f * b2020,
            -0.018151f * r2020 - 0.100579f * g2020 + 1.118730f * b2020);
    }

    private static float DecodePq(float encoded)
    {
        const float m1 = 2610f / 16384f;
        const float m2 = 2523f / 32f;
        const float c1 = 3424f / 4096f;
        const float c2 = 2413f / 128f;
        const float c3 = 2392f / 128f;
        var p = MathF.Pow(Math.Clamp(encoded, 0f, 1f), 1f / m2);
        return MathF.Pow(MathF.Max(p - c1, 0f) / MathF.Max(c2 - c3 * p, 1e-7f), 1f / m1);
    }

    private static float SrgbToLinear(float value)
        => value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
}

internal sealed record ScRgbFrame(byte[] Bytes, int Width, int Height, int Stride, bool HasTransparency);
