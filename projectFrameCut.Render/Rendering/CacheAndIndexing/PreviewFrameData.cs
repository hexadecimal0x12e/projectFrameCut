using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Processing.Converting;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace projectFrameCut.Render.Rendering;

public sealed record PreviewFrameData(string SourceHash, string EffectHash, bool ScRgb, int Width, int Height,
    int Stride, bool HasTransparency, string ContentHash, byte[] Pixels, byte[] AlphaMask)
{
    public static PreviewFrameData Create(IPicture picture, string sourceHash, string effectHash, bool scRgb)
    {
        int width = picture.Width, height = picture.Height;
        var mask = new byte[checked((picture.Pixels + 7) / 8)];
        bool transparent = false;
        var alpha = picture.GetSpecificChannel(IPicture.ChannelId.Alpha) as float[];
        for (int i = 0; i < picture.Pixels; i++)
        {
            float a = picture.HasAlphaChannel && alpha is not null && i < alpha.Length && float.IsFinite(alpha[i])
                ? Math.Clamp(alpha[i], 0, 1) : 1;
            transparent |= a < 1;
            if (a == 0) mask[i >> 3] |= (byte)(1 << (i & 7));
        }
        byte[] pixels;
        int stride = scRgb ? checked(width * 8) : 0;
        if (scRgb)
        {
            pixels = new byte[checked(stride * height)];
            if (picture is IPicture<ushort> p16)
            {
                bool pq = p16 is IHDRPicture<ushort> hdr && float.IsFinite(hdr.MaximumBrightness) && hdr.MaximumBrightness > 300;
                for (int i = 0; i < picture.Pixels; i++)
                {
                    float a = Alpha(p16.a, p16.HasAlphaChannel, i);
                    var color = pq ? PqToScRgb(p16.r[i] / 65535f, p16.g[i] / 65535f, p16.b[i] / 65535f)
                        : (Linear(p16.r[i] / 65535f), Linear(p16.g[i] / 65535f), Linear(p16.b[i] / 65535f));
                    WritePixel(pixels, i * 8, color.Item1 * a, color.Item2 * a, color.Item3 * a, a);
                }
            }
            else if (picture is IPicture<byte> p8)
            {
                for (int i = 0; i < picture.Pixels; i++)
                {
                    float a = Alpha(p8.a, p8.HasAlphaChannel, i);
                    WritePixel(pixels, i * 8, Linear(p8.r[i] / 255f) * a, Linear(p8.g[i] / 255f) * a,
                        Linear(p8.b[i] / 255f) * a, a);
                }
            }
            else throw new NotSupportedException($"Cannot materialize {picture.GetType().Name} as FP16 scRGB.");
        }
        else pixels = ToPng(picture);
        return new(sourceHash, effectHash, scRgb, width, height, stride, transparent,
            Convert.ToHexString(SHA256.HashData(pixels)), pixels, mask);
    }

    public static byte[] ToPng(IPicture picture)
    {
        IPicture output = picture is IHDRPicture<ushort> hdr
            ? hdr.DegradeToSDR(HDRImageDegradeToSDRMode.NormalizeBrightnessToRGB) : picture;
        try
        {
            using var stream = new MemoryStream();
            output.SaveToPng(stream);
            return stream.ToArray();
        }
        finally { if (!ReferenceEquals(output, picture)) output.Dispose(); }
    }

    public byte[] ToPng()
    {
        if (!ScRgb) return Pixels;
        using var picture = new Picture8bpp(Width, Height) { HasAlphaChannel = true, a = new float[checked(Width * Height)] };
        for (int i = 0; i < picture.Pixels; i++)
        {
            float a = (float)BinaryPrimitives.ReadHalfLittleEndian(Pixels.AsSpan(i * 8 + 6, 2));
            picture.a[i] = a;
            picture.r[i] = Encode((float)BinaryPrimitives.ReadHalfLittleEndian(Pixels.AsSpan(i * 8, 2)), a);
            picture.g[i] = Encode((float)BinaryPrimitives.ReadHalfLittleEndian(Pixels.AsSpan(i * 8 + 2, 2)), a);
            picture.b[i] = Encode((float)BinaryPrimitives.ReadHalfLittleEndian(Pixels.AsSpan(i * 8 + 4, 2)), a);
        }
        return ToPng(picture);
    }

    private static byte Encode(float value, float alpha)
    {
        value = alpha > 0 ? Math.Clamp(value / alpha, 0, 1) : 0;
        return (byte)Math.Clamp((int)MathF.Round((value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1 / 2.4f) - 0.055f) * 255), 0, 255);
    }

    private static float Alpha(float[]? alpha, bool hasAlpha, int i)
        => hasAlpha && alpha is not null && i < alpha.Length && float.IsFinite(alpha[i]) ? Math.Clamp(alpha[i], 0, 1) : 1;

    private static void WritePixel(byte[] bytes, int offset, float r, float g, float b, float a)
    {
        BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(offset, 2), (Half)Math.Clamp(r, -0.5f, 125f));
        BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(offset + 2, 2), (Half)Math.Clamp(g, -0.5f, 125f));
        BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(offset + 4, 2), (Half)Math.Clamp(b, -0.5f, 125f));
        BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(offset + 6, 2), (Half)Math.Clamp(a, 0, 1));
    }

    private static float Linear(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    private static (float, float, float) PqToScRgb(float r, float g, float b)
    {
        r = DecodePq(r) * 125; g = DecodePq(g) * 125; b = DecodePq(b) * 125;
        return (1.660491f * r - 0.587641f * g - 0.072850f * b,
            -0.124550f * r + 1.132900f * g - 0.008349f * b,
            -0.018151f * r - 0.100579f * g + 1.118730f * b);
    }

    private static float DecodePq(float v)
    {
        float p = MathF.Pow(Math.Clamp(v, 0, 1), 32f / 2523);
        return MathF.Pow(MathF.Max(p - 3424f / 4096, 0) / MathF.Max(2413f / 128 - 2392f / 128 * p, 1e-7f), 16384f / 2610);
    }
}
