using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.HwAccelEngine.Effect;
internal enum ResidentOperation
{
    Convert,
    Resize,
    Crop,
    Rotation,
    Flip,
    BlurHorizontal,
    BlurVertical,
    Sharpen,
    Vignette,
    Opacity,
    ColorAdjustment,
    RemoveColor,
    Mixture,
    Crossfade,
    Hdr,
    Place,
}

internal static class ResidentPictureOperations
{
    internal static ResidentPicture Own(IHwAccelPicture source)
    {
        if (source is ResidentPicture picture)
            return (ResidentPicture)picture.Retain();
        using var context = CreateContext();
        return (ResidentPicture)context.Upload(source);
    }

    internal static ResidentPictureContext CreateContext()
    {
        try
        {
#if WINDOWS || LINUX || HEADLESS
            return new ILGPUResidentPictureContext();
#elif ANDROID
            return Platforms.Android.AndroidExecutionHelper.UseVulkanBackend
                ? new Platforms.Android.VulkanResidentPictureContext() : new Platforms.Android.OpenGLResidentPictureContext();
#elif IOS || MACCATALYST
            return new Platforms.iOS.MetalResidentPictureContext();
#else
            throw new PlatformNotSupportedException("No hardware picture backend is available.");
#endif
        }
        catch (Exception ex)
        {
            projectFrameCut.Shared.Logger.Log(ex, "Initialize hardware picture context", "HwAccelEngine");
            throw;
        }
    }

    internal static ResidentPicture Run(IHwAccelPicture source, ResidentOperation operation, float[] args, int? width = null, int? height = null, bool? alpha = null)
    {
        using var input = Own(source);
        return input.Storage.Context.Run(input, operation, width ?? input.Width, height ?? input.Height, args, alpha);
    }

    internal static ResidentPicture Resize(IHwAccelPicture source, int width, int height, bool preserveAspect)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (preserveAspect)
        {
            if ((double)source.Width / source.Height > (double)width / height)
                height = Math.Max(1, (int)Math.Round((double)width * source.Height / source.Width, MidpointRounding.AwayFromZero));
            else
                width = Math.Max(1, (int)Math.Round((double)height * source.Width / source.Height, MidpointRounding.AwayFromZero));
        }

        return width == source.Width && height == source.Height ? Own(source) : Run(source, ResidentOperation.Resize, [], width, height);
    }

    internal static ResidentPicture Rotate(IHwAccelPicture source, float angle, bool expand, bool pixelCenters = false)
    {
        float a = angle * MathF.PI / 180, cos = MathF.Cos(a), sin = MathF.Sin(a);
        if (pixelCenters)
        {
            cos = (float)Math.Cos(angle * Math.PI / 180);
            sin = (float)Math.Sin(angle * Math.PI / 180);
            if (Math.Abs(cos) < 1e-10)
                cos = 0;
            if (Math.Abs(sin) < 1e-10)
                sin = 0;
        }

        int width = expand ? (int)MathF.Ceiling(source.Width * MathF.Abs(cos) + source.Height * MathF.Abs(sin)) : source.Width;
        int height = expand ? (int)MathF.Ceiling(source.Width * MathF.Abs(sin) + source.Height * MathF.Abs(cos)) : source.Height;
        return Run(source, ResidentOperation.Rotation, [cos, sin, pixelCenters ? 0.5f : 0], width, height);
    }

    internal static ResidentPicture Crop(IHwAccelPicture source, int x, int y, int width, int height, float angle)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (x < source.Width && y < source.Height && (long)x + width > 0 && (long)y + height > 0)
        {
            var rect = projectFrameCut.Render.Effect.CropEffectShared.BuildSafeCropRect(x, y, width, height, source.Width, source.Height);
            x = rect.X;
            y = rect.Y;
            width = rect.Width;
            height = rect.Height;
        }

        float a = angle * MathF.PI / 180;
        return Run(source, ResidentOperation.Crop, [x, y, MathF.Cos(a), MathF.Sin(a)], width, height, source.HasAlphaChannel || Math.Abs(angle) > float.Epsilon || x >= source.Width || y >= source.Height || x + width <= 0 || y + height <= 0);
    }

    internal static ResidentPicture Mix(IHwAccelPicture bottom, IHwAccelPicture top, int mode, int bits, int x, int y, int width, int height, bool approximate = false)
    {
        using var a = Own(bottom);
        using var b = (ResidentPicture)a.Context.Upload(top);
        Half peak = a.IsHdr ? b.IsHdr ? Half.Max(a.MaximumBrightness, b.MaximumBrightness) : a.MaximumBrightness : b.IsHdr ? b.MaximumBrightness : (Half)1000;
        bool alpha = a.HasAlphaChannel || b.HasAlphaChannel || a.Width != width || a.Height != height;
        return a.Storage.Context.Run(a, ResidentOperation.Mixture, width, height, [x, y, mode, approximate ? 1 : 0], alpha, bits, (a.IsHdr || b.IsHdr), peak, b);
    }

    internal static ResidentPicture Crossfade(IHwAccelPicture left, IHwAccelPicture right, float progress)
    {
        if (left.Width != right.Width || left.Height != right.Height)
            throw new ArgumentException("Crossfade input sizes must match.");
        using var a = Own(left);
        using var b = (ResidentPicture)a.Context.Upload(right);
        Half peak = Half.Max(a.MaximumBrightness, b.MaximumBrightness);
        return a.Storage.Context.Run(a, ResidentOperation.Crossfade, a.Width, a.Height, [(float)Half.Clamp((Half)progress, Half.Zero, Half.One), (float)peak], true, IPicture.PicturePixelMode.Float16Picture, a.IsHdr || b.IsHdr, peak, b);
    }
}
