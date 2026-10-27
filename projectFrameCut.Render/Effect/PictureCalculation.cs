using projectFrameCut.Drawing.Processing.Resizing;
using PictureAlgorithms = global::projectFrameCut.Drawing.Effect;

namespace projectFrameCut.Render.Effect;

internal static class PictureCalculation<TPicture> where TPicture : IPicture
{
    public static readonly bool IsFP16 = typeof(IPicture<Half>).IsAssignableFrom(typeof(TPicture));
    public static readonly bool Is8 = typeof(IPicture<byte>).IsAssignableFrom(typeof(TPicture));
    public static readonly bool Is16 = typeof(IPicture<ushort>).IsAssignableFrom(typeof(TPicture));
    public static readonly bool IsHdr = typeof(IHDRPicture<ushort>).IsAssignableFrom(typeof(TPicture));

    public static IPicture Blur(TPicture p, float sigma)
    {
        if (IsFP16 || p is IPicture<Half>) return PictureAlgorithms.BlurEffect.Process((IPicture<Half>)(object)p, sigma);
        if (IsHdr) return PictureAlgorithms.BlurEffect.Process((IHDRPicture<ushort>)(object)p, sigma);
        if (Is16) return PictureAlgorithms.BlurEffect.Process((IPicture<ushort>)(object)p, sigma);
        if (Is8) return PictureAlgorithms.BlurEffect.Process((IPicture<byte>)(object)p, sigma);
        return PictureAlgorithms.BlurEffect.Process(p, sigma);
    }

    public static IPicture Sharpen(TPicture p, float amount)
    {
        if (IsFP16 || p is IPicture<Half>) return PictureAlgorithms.SharpenEffect.Process((IPicture<Half>)(object)p, amount);
        if (IsHdr) return PictureAlgorithms.SharpenEffect.Process((IHDRPicture<ushort>)(object)p, amount);
        if (Is16) return PictureAlgorithms.SharpenEffect.Process((IPicture<ushort>)(object)p, amount);
        if (Is8) return PictureAlgorithms.SharpenEffect.Process((IPicture<byte>)(object)p, amount);
        return PictureAlgorithms.SharpenEffect.Process(p, amount);
    }

    public static IPicture Opacity(TPicture p, Half opacity)
    {
        if (IsFP16 || p is IPicture<Half>) return PictureAlgorithms.OpacityEffect.Process((IPicture<Half>)(object)p, opacity);
        if (IsHdr) return PictureAlgorithms.OpacityEffect.Process((IHDRPicture<ushort>)(object)p, opacity);
        if (Is16) return PictureAlgorithms.OpacityEffect.Process((IPicture<ushort>)(object)p, opacity);
        if (Is8) return PictureAlgorithms.OpacityEffect.Process((IPicture<byte>)(object)p, opacity);
        return PictureAlgorithms.OpacityEffect.Process(p, opacity);
    }

    public static IPicture Vignette(TPicture p, float strength, float radius)
    {
        if (IsFP16 || p is IPicture<Half>) return PictureAlgorithms.VignetteEffect.Process((IPicture<Half>)(object)p, strength, radius);
        if (IsHdr) return PictureAlgorithms.VignetteEffect.Process((IHDRPicture<ushort>)(object)p, strength, radius);
        if (Is16) return PictureAlgorithms.VignetteEffect.Process((IPicture<ushort>)(object)p, strength, radius);
        if (Is8) return PictureAlgorithms.VignetteEffect.Process((IPicture<byte>)(object)p, strength, radius);
        return PictureAlgorithms.VignetteEffect.Process(p, strength, radius);
    }

    public static IPicture Flip(TPicture p, bool horizontal, bool vertical)
    {
        if (IsFP16 || p is IPicture<Half>) return PictureAlgorithms.FlipEffect.Process((IPicture<Half>)(object)p, horizontal, vertical);
        if (IsHdr) return PictureAlgorithms.FlipEffect.Process((IHDRPicture<ushort>)(object)p, horizontal, vertical);
        if (Is16) return PictureAlgorithms.FlipEffect.Process((IPicture<ushort>)(object)p, horizontal, vertical);
        if (Is8) return PictureAlgorithms.FlipEffect.Process((IPicture<byte>)(object)p, horizontal, vertical);
        return PictureAlgorithms.FlipEffect.Process(p, horizontal, vertical);
    }

    public static IPicture Place(TPicture p, int x, int y, int width, int height)
    {
        if (IsFP16 || p is IPicture<Half>) return PictureAlgorithms.PlaceEffect.Process((IPicture<Half>)(object)p, x, y, width, height);
        if (Is16) return PictureAlgorithms.PlaceEffect.Process((IPicture<ushort>)(object)p, x, y, width, height);
        if (Is8) return PictureAlgorithms.PlaceEffect.Process((IPicture<byte>)(object)p, x, y, width, height);
        return PictureAlgorithms.PlaceEffect.Process(p, x, y, width, height);
    }

    public static IPicture Crop(TPicture p, int x, int y, int width, int height)
    {
        if (IsFP16 || p is IPicture<Half>) return PictureAlgorithms.CropEffect.Process((IPicture<Half>)(object)p, x, y, width, height);
        if (Is16) return PictureAlgorithms.CropEffect.Process((IPicture<ushort>)(object)p, x, y, width, height);
        if (Is8) return PictureAlgorithms.CropEffect.Process((IPicture<byte>)(object)p, x, y, width, height);
        return PictureAlgorithms.CropEffect.Process(p, x, y, width, height);
    }

    public static IPicture Rotate(TPicture p, float angle, bool expand)
    {
        if (IsFP16 || p is IPicture<Half>) return PictureAlgorithms.RotationEffect.Process((IPicture<Half>)(object)p, angle, expand);
        if (Is16) return PictureAlgorithms.RotationEffect.Process((IPicture<ushort>)(object)p, angle, expand);
        if (Is8) return PictureAlgorithms.RotationEffect.Process((IPicture<byte>)(object)p, angle, expand);
        return PictureAlgorithms.RotationEffect.Process(p, angle, expand);
    }

    public static IPicture Resize(TPicture p, int width, int height, bool preserveAspect)
    {
        if (IsFP16 || p is IPicture<Half>) return FP16Raster.Resize((IPicture<Half>)(object)p, width, height, preserveAspect);
        var resizer = new BilinearPictureResizer();
        if (IsHdr) return resizer.Resize((IHDRPicture<ushort>)(object)p, width, height, preserveAspect);
        if (Is16) return resizer.Resize((IPicture<ushort>)(object)p, width, height, preserveAspect);
        if (Is8) return resizer.Resize((IPicture<byte>)(object)p, width, height, preserveAspect);
        return p switch
        {
            IHDRPicture<ushort> hdr => resizer.Resize(hdr, width, height, preserveAspect),
            IPicture<ushort> p16 => resizer.Resize(p16, width, height, preserveAspect),
            IPicture<byte> p8 => resizer.Resize(p8, width, height, preserveAspect),
            _ => throw new NotSupportedException($"Unsupported picture type: {p.GetType().Name}"),
        };
    }
}
