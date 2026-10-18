using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System.Diagnostics;

namespace projectFrameCut.Render.HwAccelEngine.Effect;

internal static class ResidentEffectResult
{
    internal static IHwAccelPicture Render(IEffect effect, EffectExecutionContext context, Func<IHwAccelPicture> render)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        IHwAccelPicture? result = null;
        try
        {
            result = render();
            context.CancellationToken.ThrowIfCancellationRequested();
            result.ProcessStack.Add(new PictureProcessStack { OperationDisplayName = effect.TypeName + " (GPU)", Operator = effect.GetType(), Elapsed = watch.Elapsed, ProcessingFuncStackTrace = new(true) });
            return result;
        }
        catch (Exception ex)
        {
            result?.Dispose();
            projectFrameCut.Shared.Logger.Log(ex, $"Execute hardware effect {effect.TypeName} ({effect.Id})", "HwAccelEngine");
            throw;
        }
    }
}

public partial class BlurEffect_HwAccel : IHwAccelPictureEffect
{
    public IHwAccelPicture RenderHwAccel(IHwAccelPicture source, EffectExecutionContext context)
    {
        using var scope = nativeLock.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        return ResidentEffectResult.Render(this, context, () =>
        {
            float sigma = DynamicParam.Resolve(Parameters.GetValueOrDefault("Sigma"), Sigma);
            if (sigma <= 0)
                return source.Retain();
            using var horizontal = ResidentPictureOperations.Run(source, ResidentOperation.BlurHorizontal, [Math.Max(1, MathF.Ceiling(sigma))]);
            return ResidentPictureOperations.Run(horizontal, ResidentOperation.BlurVertical, [Math.Max(1, MathF.Ceiling(sigma))]);
        });
    }
}

public partial class FlipEffect_HwAccel : IHwAccelPictureEffect
{
    public IHwAccelPicture RenderHwAccel(IHwAccelPicture source, EffectExecutionContext context)
    {
        using var scope = nativeLock.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        return ResidentEffectResult.Render(this, context, () =>
        {
            return ResidentPictureOperations.Run(source, ResidentOperation.Flip, [DynamicParam.Resolve(Parameters.GetValueOrDefault("Horizontal"), Horizontal) ? 1 : 0, DynamicParam.Resolve(Parameters.GetValueOrDefault("Vertical"), Vertical) ? 1 : 0]);
        });
    }
}

public partial class SharpenEffect_HwAccel : IHwAccelPictureEffect
{
    public IHwAccelPicture RenderHwAccel(IHwAccelPicture source, EffectExecutionContext context)
    {
        using var scope = nativeLock.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        return ResidentEffectResult.Render(this, context, () =>
        {
            return ResidentPictureOperations.Run(source, ResidentOperation.Sharpen, [DynamicParam.Resolve(Parameters.GetValueOrDefault("Amount"), Amount)]);
        });
    }
}

public partial class VignetteEffect_HwAccel : IHwAccelPictureEffect
{
    public IHwAccelPicture RenderHwAccel(IHwAccelPicture source, EffectExecutionContext context)
    {
        using var scope = nativeLock.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        return ResidentEffectResult.Render(this, context, () =>
        {
            return ResidentPictureOperations.Run(source, ResidentOperation.Vignette, [DynamicParam.Resolve(Parameters.GetValueOrDefault("Strength"), Strength), DynamicParam.Resolve(Parameters.GetValueOrDefault("Radius"), Radius)]);
        });
    }
}

public partial class FadeOpacityEffect_HwAccel : IHwAccelPictureEffect
{
    public IHwAccelPicture RenderHwAccel(IHwAccelPicture source, EffectExecutionContext context)
    {
        using var scope = nativeLock.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        return ResidentEffectResult.Render(this, context, () =>
        {
            float opacity = Math.Clamp(DynamicParam.Resolve(Parameters.GetValueOrDefault("Opacity"), Opacity), 0, 1);
            return ResidentPictureOperations.Run(source, ResidentOperation.Opacity, [opacity], alpha: source.HasAlphaChannel || opacity < 1);
        });
    }
}

public partial class ColorAdjustmentEffect_HwAccel : IHwAccelPictureEffect
{
    public IHwAccelPicture RenderHwAccel(IHwAccelPicture source, EffectExecutionContext context)
    {
        using var scope = nativeLock.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        return ResidentEffectResult.Render(this, context, () =>
        {
            float opacity = DynamicParam.Resolve(Parameters.GetValueOrDefault("Opacity"), Opacity);
            float[] p = [DynamicParam.Resolve(Parameters.GetValueOrDefault("Brightness"), Brightness), DynamicParam.Resolve(Parameters.GetValueOrDefault("Contrast"), Contrast), DynamicParam.Resolve(Parameters.GetValueOrDefault("Saturation"), Saturation), DynamicParam.Resolve(Parameters.GetValueOrDefault("Hue"), Hue), DynamicParam.Resolve(Parameters.GetValueOrDefault("Gamma"), Gamma), DynamicParam.Resolve(Parameters.GetValueOrDefault("Vibrance"), Vibrance), DynamicParam.Resolve(Parameters.GetValueOrDefault("Temperature"), Temperature), DynamicParam.Resolve(Parameters.GetValueOrDefault("Invert"), Invert) ? 1 : 0, DynamicParam.Resolve(Parameters.GetValueOrDefault("Grayscale"), Grayscale), opacity];
            if (p[0] == 1 && p[1] == 1 && p[2] == 1 && p[3] == 0 && p[4] == 1 && p[5] == 0 && p[6] == 0 && p[7] == 0 && p[8] == 0 && p[9] == 1)
                return source.Retain();
            return ResidentPictureOperations.Run(source, ResidentOperation.ColorAdjustment, p, alpha: source.HasAlphaChannel || opacity < 1);
        });
    }
}

public partial class RotationEffect_HwAccel : IHwAccelPictureEffect
{
    public IHwAccelPicture RenderHwAccel(IHwAccelPicture source, EffectExecutionContext context)
    {
        using var scope = nativeLock.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        return ResidentEffectResult.Render(this, context, () =>
        {
            float angle = DynamicParam.Resolve(Parameters.GetValueOrDefault("Angle"), Angle);
            if (Math.Abs(angle % 360) < float.Epsilon)
                return source.Retain();
            return ResidentPictureOperations.Rotate(source, angle, DynamicParam.Resolve(Parameters.GetValueOrDefault("ExpandCanvas"), ExpandCanvas));
        });
    }
}

public partial class ResizeEffect_HwAccel : IHwAccelPictureEffect
{
    public IHwAccelPicture RenderHwAccel(IHwAccelPicture source, EffectExecutionContext context)
    {
        using var scope = nativeLock.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        return ResidentEffectResult.Render(this, context, () =>
        {
            int width = DynamicParam.Resolve(Parameters.GetValueOrDefault("Width"), Width);
            int height = DynamicParam.Resolve(Parameters.GetValueOrDefault("Height"), Height);
            if (RelativeWidth > 0 && RelativeHeight > 0 && (RelativeWidth != context.TargetWidth || RelativeHeight != context.TargetHeight))
            {
                width = (int)Math.Round((double)width * context.TargetWidth / RelativeWidth, MidpointRounding.AwayFromZero);
                height = (int)Math.Round((double)height * context.TargetHeight / RelativeHeight, MidpointRounding.AwayFromZero);
            }

            return ResidentPictureOperations.Resize(source, width, height, DynamicParam.Resolve(Parameters.GetValueOrDefault("PreserveAspectRatio"), PreserveAspectRatio));
        });
    }
}

public partial class PlaceEffect_HwAccel : IHwAccelPictureEffect
{
    public IHwAccelPicture RenderHwAccel(IHwAccelPicture source, EffectExecutionContext context)
    {
        using var scope = nativeLock.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        return ResidentEffectResult.Render(this, context, () =>
        {
            int x = DynamicParam.Resolve(Parameters.GetValueOrDefault("StartX"), StartX);
            int y = DynamicParam.Resolve(Parameters.GetValueOrDefault("StartY"), StartY);
            if (RelativeWidth > 0 && RelativeHeight > 0)
            {
                x = (int)Math.Round((double)x * context.TargetWidth / RelativeWidth);
                y = (int)Math.Round((double)y * context.TargetHeight / RelativeHeight);
            }

            return ResidentPictureOperations.Run(source, ResidentOperation.Place, [x, y], context.TargetWidth, context.TargetHeight, true);
        });
    }
}

public partial class RemoveColorEffect_HwAccel : IHwAccelPictureEffect
{
    public IHwAccelPicture RenderHwAccel(IHwAccelPicture source, EffectExecutionContext context)
    {
        using var scope = nativeLock.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        return ResidentEffectResult.Render(this, context, () =>
        {
            using var removed = ResidentPictureOperations.Run(source, ResidentOperation.RemoveColor, [DynamicParam.Resolve(Parameters.GetValueOrDefault("R"), R), DynamicParam.Resolve(Parameters.GetValueOrDefault("G"), G), DynamicParam.Resolve(Parameters.GetValueOrDefault("B"), B), DynamicParam.Resolve(Parameters.GetValueOrDefault("Tolerance"), Tolerance)], alpha: true);
            return ResidentPictureOperations.Resize(removed, context.TargetWidth, context.TargetHeight, false);
        });
    }
}

public partial class CropEffect_HwAccel : IHwAccelPictureEffect
{
    public IHwAccelPicture RenderHwAccel(IHwAccelPicture source, EffectExecutionContext context)
    {
        using var scope = nativeLock.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        return ResidentEffectResult.Render(this, context, () =>
        {
            int x = DynamicParam.Resolve(Parameters.GetValueOrDefault("StartX"), StartX);
            int y = DynamicParam.Resolve(Parameters.GetValueOrDefault("StartY"), StartY);
            int width = DynamicParam.Resolve(Parameters.GetValueOrDefault("Width"), Width);
            int height = DynamicParam.Resolve(Parameters.GetValueOrDefault("Height"), Height);
            float angle = DynamicParam.Resolve(Parameters.GetValueOrDefault("Angle"), Angle);
            if (RelativeWidth > 0 && RelativeHeight > 0)
            {
                x = (int)Math.Round((double)x * context.TargetWidth / RelativeWidth);
                y = (int)Math.Round((double)y * context.TargetHeight / RelativeHeight);
                width = (int)Math.Round((double)width * context.TargetWidth / RelativeWidth);
                height = (int)Math.Round((double)height * context.TargetHeight / RelativeHeight);
            }

            return ResidentPictureOperations.Crop(source, x, y, width, height, angle);
        });
    }
}

public partial class ProgressCropper_HwAccel : IHwAccelPictureEffect
{
    public IHwAccelPicture RenderHwAccel(IHwAccelPicture source, EffectExecutionContext context)
    {
        using var scope = nativeLock.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        return ResidentEffectResult.Render(this, context, () =>
        {
            int x = DynamicParam.Resolve(Parameters.GetValueOrDefault("StartX"), StartX);
            int y = DynamicParam.Resolve(Parameters.GetValueOrDefault("StartY"), StartY);
            int width = DynamicParam.Resolve(Parameters.GetValueOrDefault("Width"), Width);
            int height = DynamicParam.Resolve(Parameters.GetValueOrDefault("Height"), Height);
            float angle = DynamicParam.Resolve(Parameters.GetValueOrDefault("Angle"), Angle);
            if (CropList.Count > 0)
            {
                var crop = CropEffectShared.Scale(CropEffectShared.GetCropForProgress(CropList, Math.Clamp(context.Progress, 0, 1)), context.TargetWidth, context.TargetHeight, RelativeWidth, RelativeHeight);
                return ResidentPictureOperations.Crop(source, crop.StartX, crop.StartY, crop.Width, crop.Height, crop.Angle);
            }

            if (RelativeWidth > 0 && RelativeHeight > 0)
            {
                x = (int)Math.Round((double)x * context.TargetWidth / RelativeWidth);
                y = (int)Math.Round((double)y * context.TargetHeight / RelativeHeight);
                width = (int)Math.Round((double)width * context.TargetWidth / RelativeWidth);
                height = (int)Math.Round((double)height * context.TargetHeight / RelativeHeight);
            }

            return ResidentPictureOperations.Crop(source, x, y, width, height, angle);
        });
    }
}

public partial class ClassicOverlayMixture_HwAccel : IHwAccelMixture
{
    public IHwAccelPicture MixHwAccel(IHwAccelPicture bottom, IHwAccelPicture top, IPicture.PicturePixelMode pixelMode, int x, int y, int width, int height)
    {
        using var scope = nativeLock.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        return ResidentEffectResult.Render(this, new() { CancellationToken = ValueProviderFrameContext.CancellationToken }, () => ResidentPictureOperations.Mix(bottom, top, 0, pixelMode, x, y, width, height));
    }
}

public partial class AddMixture_HwAccel : IHwAccelMixture
{
    public IHwAccelPicture MixHwAccel(IHwAccelPicture bottom, IHwAccelPicture top, IPicture.PicturePixelMode pixelMode, int x, int y, int width, int height)
    {
        using var scope = nativeLock.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        return ResidentEffectResult.Render(this, new() { CancellationToken = ValueProviderFrameContext.CancellationToken }, () => ResidentPictureOperations.Mix(bottom, top, 1, pixelMode, x, y, width, height));
    }
}

public partial class SubtractMixture_HwAccel : IHwAccelMixture
{
    public IHwAccelPicture MixHwAccel(IHwAccelPicture bottom, IHwAccelPicture top, IPicture.PicturePixelMode pixelMode, int x, int y, int width, int height)
    {
        using var scope = nativeLock.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        return ResidentEffectResult.Render(this, new() { CancellationToken = ValueProviderFrameContext.CancellationToken }, () => ResidentPictureOperations.Mix(bottom, top, 2, pixelMode, x, y, width, height));
    }
}

public partial class MultiplyMixture_HwAccel : IHwAccelMixture
{
    public IHwAccelPicture MixHwAccel(IHwAccelPicture bottom, IHwAccelPicture top, IPicture.PicturePixelMode pixelMode, int x, int y, int width, int height)
    {
        using var scope = nativeLock.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        return ResidentEffectResult.Render(this, new() { CancellationToken = ValueProviderFrameContext.CancellationToken }, () => ResidentPictureOperations.Mix(bottom, top, 3, pixelMode, x, y, width, height));
    }
}

public partial class ScreenMixture_HwAccel : IHwAccelMixture
{
    public IHwAccelPicture MixHwAccel(IHwAccelPicture bottom, IHwAccelPicture top, IPicture.PicturePixelMode pixelMode, int x, int y, int width, int height)
    {
        using var scope = nativeLock.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        return ResidentEffectResult.Render(this, new() { CancellationToken = ValueProviderFrameContext.CancellationToken }, () => ResidentPictureOperations.Mix(bottom, top, 4, pixelMode, x, y, width, height));
    }
}

public partial class OverlayBlendMixture_HwAccel : IHwAccelMixture
{
    public IHwAccelPicture MixHwAccel(IHwAccelPicture bottom, IHwAccelPicture top, IPicture.PicturePixelMode pixelMode, int x, int y, int width, int height)
    {
        using var scope = nativeLock.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        return ResidentEffectResult.Render(this, new() { CancellationToken = ValueProviderFrameContext.CancellationToken }, () => ResidentPictureOperations.Mix(bottom, top, 5, pixelMode, x, y, width, height));
    }
}

public partial class DarkenMixture_HwAccel : IHwAccelMixture
{
    public IHwAccelPicture MixHwAccel(IHwAccelPicture bottom, IHwAccelPicture top, IPicture.PicturePixelMode pixelMode, int x, int y, int width, int height)
    {
        using var scope = nativeLock.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        return ResidentEffectResult.Render(this, new() { CancellationToken = ValueProviderFrameContext.CancellationToken }, () => ResidentPictureOperations.Mix(bottom, top, 6, pixelMode, x, y, width, height));
    }
}

public partial class LightenMixture_HwAccel : IHwAccelMixture
{
    public IHwAccelPicture MixHwAccel(IHwAccelPicture bottom, IHwAccelPicture top, IPicture.PicturePixelMode pixelMode, int x, int y, int width, int height)
    {
        using var scope = nativeLock.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        return ResidentEffectResult.Render(this, new() { CancellationToken = ValueProviderFrameContext.CancellationToken }, () => ResidentPictureOperations.Mix(bottom, top, 7, pixelMode, x, y, width, height));
    }
}

public partial class DifferenceMixture_HwAccel : IHwAccelMixture
{
    public IHwAccelPicture MixHwAccel(IHwAccelPicture bottom, IHwAccelPicture top, IPicture.PicturePixelMode pixelMode, int x, int y, int width, int height)
    {
        using var scope = nativeLock.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        return ResidentEffectResult.Render(this, new() { CancellationToken = ValueProviderFrameContext.CancellationToken }, () => ResidentPictureOperations.Mix(bottom, top, 8, pixelMode, x, y, width, height));
    }
}

public sealed partial class FadeTransform_HwAccel : IHwAccelTransform
{
    public IHwAccelPicture RenderHwAccel(IHwAccelPicture left, IHwAccelPicture? right, float progress, TransformSide side, int width, int height)
    {
        using var scope = nativeLock.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        return ResidentEffectResult.Render(this, new() { CancellationToken = ValueProviderFrameContext.CancellationToken }, () => ResidentPictureOperations.Run(left, ResidentOperation.Opacity, [Math.Clamp(side == TransformSide.Left ? progress : 1 - progress, 0, 1)], alpha: true));
    }
}

public partial class CrossfadeTransform_HwAccel : IHwAccelTransform
{
    public IHwAccelPicture RenderHwAccel(IHwAccelPicture left, IHwAccelPicture? right, float progress, TransformSide side, int width, int height)
    {
        using var scope = nativeLock.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(right);
        return ResidentEffectResult.Render(this, new() { CancellationToken = ValueProviderFrameContext.CancellationToken }, () => ResidentPictureOperations.Crossfade(left, right, progress));
    }
}
