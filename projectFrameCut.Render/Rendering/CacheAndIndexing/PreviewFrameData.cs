using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Processing.Converting;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace projectFrameCut.Render.Rendering;

public sealed record PreviewFrameInfo(string SourceHash, string EffectHash, bool ScRgb, int Width, int Height,
    int Stride, bool HasTransparency, string ContentHash, int PixelsLength, int MaskLength);

public sealed record PreviewFrameData(string SourceHash, string EffectHash, bool ScRgb, int Width, int Height,
    int Stride, bool HasTransparency, string ContentHash, byte[] Pixels, byte[] AlphaMask)
{
    public PreviewFrameInfo Info => new(SourceHash, EffectHash, ScRgb, Width, Height, Stride,
        HasTransparency, ContentHash, Pixels.Length, AlphaMask.Length);

    public static PreviewFrameData Create(IPicture picture, string sourceHash, string effectHash, bool scRgb)
        => Create(picture, sourceHash, effectHash, scRgb, null);

    public static PreviewFrameData Create(IPicture picture, string sourceHash, string effectHash, bool scRgb,
        Func<int, byte[]>? allocatePixels)
    {
        if (picture is IHwAccelPicture gpu)
        {
            if (scRgb && gpu.Context is IHwAccelPictureScRgb converter)
                return FromScRgb(gpu, sourceHash, effectHash, converter.DownloadScRgb(gpu, allocatePixels));
            using var downloaded = gpu.Download();
            return Create(downloaded, sourceHash, effectHash, scRgb, allocatePixels);
        }
        int width = picture.Width, height = picture.Height;
        var mask = new byte[checked((picture.Pixels + 7) / 8)];
        bool transparent = false;
        ReadOnlySpan<Half> alpha = picture is IReadOnlyPictureChannels<Half> read ? read.ReadAlpha : picture.GetSpecificChannel(IPicture.ChannelId.Alpha) as Half[];
        for (int i = 0; i < picture.Pixels; i++)
        {
            Half a = picture.HasAlphaChannel && !alpha.IsEmpty && i < alpha.Length && Half.IsFinite(alpha[i])
                ? Half.Clamp(alpha[i], Half.Zero, Half.One) : Half.One;
            transparent |= a < Half.One;
            if (a == Half.Zero) mask[i >> 3] |= (byte)(1 << (i & 7));
        }
        byte[] pixels;
        int stride = scRgb ? checked(width * 8) : 0;
        if (scRgb)
        {
            int length = checked(stride * height);
            pixels = allocatePixels?.Invoke(length) ?? GC.AllocateUninitializedArray<byte>(length);
            if (pixels.Length != length) throw new InvalidDataException("Invalid preview pixel buffer length.");
            if (picture is IPicture<Half> fp)
            {
                var c = new PictureReadChannels<Half>(fp);
                var hdr = fp as IHDRPicture<Half>;
                ReadOnlySpan<Half> brightness = fp is IReadOnlyPictureChannels<Half> readFp ? readFp.ReadBrightness : hdr is not null ? hdr.Brightness : [];
                bool pq = hdr is not null && hdr.MaximumBrightness > (Half)300;
                for (int i = 0; i < fp.Pixels; i++)
                {
                    Half a = Alpha(alpha, fp.HasAlphaChannel, i);
                    var color = pq ? PqToScRgb((float)c.Red[i], (float)c.Green[i], (float)c.Blue[i])
                        : (Linear((float)c.Red[i]), Linear((float)c.Green[i]), Linear((float)c.Blue[i]));
                    if (hdr is not null)
                    {
                        float luma = .2126f * color.Item1 + .7152f * color.Item2 + .0722f * color.Item3;
                        float target = (float)PictureFP16.Unit((float)brightness[i]) * (float)hdr.MaximumBrightness / 80;
                        float gain = luma > 1e-6f ? target / luma : 0;
                        color = (color.Item1 * gain, color.Item2 * gain, color.Item3 * gain);
                    }
                    WritePixel(pixels, i * 8, color.Item1 * (float)a, color.Item2 * (float)a, color.Item3 * (float)a, a);
                }
            }
            else if (picture is IPicture<ushort> p16)
            {
                bool pq = p16 is IHDRPicture<ushort> hdr && Half.IsFinite(hdr.MaximumBrightness) && hdr.MaximumBrightness > (Half)300;
                for (int i = 0; i < picture.Pixels; i++)
                {
                    Half a = Alpha(p16.a, p16.HasAlphaChannel, i);
                    var color = pq ? PqToScRgb(p16.r[i] / 65535f, p16.g[i] / 65535f, p16.b[i] / 65535f)
                        : (Linear(p16.r[i] / 65535f), Linear(p16.g[i] / 65535f), Linear(p16.b[i] / 65535f));
                    WritePixel(pixels, i * 8, color.Item1 * (float)a, color.Item2 * (float)a, color.Item3 * (float)a, a);
                }
            }
            else if (picture is IPicture<byte> p8)
            {
                for (int i = 0; i < picture.Pixels; i++)
                {
                    Half a = Alpha(p8.a, p8.HasAlphaChannel, i);
                    WritePixel(pixels, i * 8, Linear(p8.r[i] / 255f) * (float)a, Linear(p8.g[i] / 255f) * (float)a,
                        Linear(p8.b[i] / 255f) * (float)a, a);
                }
            }
            else throw new NotSupportedException($"Cannot materialize {picture.GetType().Name} as FP16 scRGB.");
        }
        else pixels = ToPng(picture);
        return new(sourceHash, effectHash, scRgb, width, height, stride, transparent,
            Convert.ToHexString(SHA256.HashData(pixels)), pixels, mask);
    }

    private static PreviewFrameData FromScRgb(IHwAccelPicture picture, string sourceHash, string effectHash, byte[] pixels)
    {
        int stride = checked(picture.Width * 8);
        if (pixels.Length != checked(stride * picture.Height))
            throw new InvalidDataException("Invalid scRGB preview pixel buffer length.");
        var mask = new byte[checked((picture.Pixels + 7) / 8)];
        bool transparent = false;
        if (picture.HasAlphaChannel)
            for (int i = 0; i < picture.Pixels; i++)
            {
                Half a = BinaryPrimitives.ReadHalfLittleEndian(pixels.AsSpan(i * 8 + 6, 2));
                transparent |= a < Half.One;
                if (a == Half.Zero) mask[i >> 3] |= (byte)(1 << (i & 7));
            }
        return new(sourceHash, effectHash, true, picture.Width, picture.Height, stride, transparent,
            Convert.ToHexString(SHA256.HashData(pixels)), pixels, mask);
    }

    public static byte[] ToPng(IPicture picture)
    {
        IPicture output = picture is IHDRPicture<Half> fpHdr ? fpHdr.DegradeToSDR(HDRImageDegradeToSDRMode.NormalizeBrightnessToRGB)
            : picture is IHDRPicture<ushort> hdr
            ? hdr.DegradeToSDR(HDRImageDegradeToSDRMode.NormalizeBrightnessToRGB) : picture;
        try
        {
            using var stream = new MemoryStream();
            if (output is IPicture<Half> fp) PictureExtensions.SharedPngPictureEncoder.SaveFP16(fp, stream, 8);
            else output.SaveToPng(stream);
            return stream.ToArray();
        }
        finally { if (!ReferenceEquals(output, picture)) output.Dispose(); }
    }

    public byte[] ToPng()
    {
        if (!ScRgb) return Pixels;
        using var picture = new PictureFP16(Width, Height) { HasAlphaChannel = true, a = new Half[checked(Width * Height)] };
        for (int i = 0; i < picture.Pixels; i++)
        {
            Half a = BinaryPrimitives.ReadHalfLittleEndian(Pixels.AsSpan(i * 8 + 6, 2));
            picture.a[i] = a;
            picture.r[i] = Encode((float)BinaryPrimitives.ReadHalfLittleEndian(Pixels.AsSpan(i * 8, 2)), a);
            picture.g[i] = Encode((float)BinaryPrimitives.ReadHalfLittleEndian(Pixels.AsSpan(i * 8 + 2, 2)), a);
            picture.b[i] = Encode((float)BinaryPrimitives.ReadHalfLittleEndian(Pixels.AsSpan(i * 8 + 4, 2)), a);
        }
        return ToPng(picture);
    }

    private static Half Encode(float value, Half alpha)
    {
        value = alpha > Half.Zero ? Math.Clamp(value / (float)alpha, 0, 1) : 0;
        return PictureFP16.Unit(value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1 / 2.4f) - 0.055f);
    }

    private static Half Alpha(ReadOnlySpan<Half> alpha, bool hasAlpha, int i)
        => hasAlpha && !alpha.IsEmpty && i < alpha.Length && Half.IsFinite(alpha[i]) ? Half.Clamp(alpha[i], Half.Zero, Half.One) : Half.One;

    private static void WritePixel(byte[] bytes, int offset, float r, float g, float b, Half a)
    {
        BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(offset, 2), (Half)Math.Clamp(r, -0.5f, 125f));
        BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(offset + 2, 2), (Half)Math.Clamp(g, -0.5f, 125f));
        BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(offset + 4, 2), (Half)Math.Clamp(b, -0.5f, 125f));
        BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(offset + 6, 2), Half.Clamp(a, Half.Zero, Half.One));
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
