using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

public interface INormalEffect<TPicture> : INormalEffect where TPicture : IPicture
{
    IPicture Render(TPicture source, int targetWidth, int targetHeight);
    IPicture INormalEffect.Render(IPicture source, int targetWidth, int targetHeight) => Render((TPicture)source, targetWidth, targetHeight);
}

public interface IColorAdjustEffect<TPicture> : IColorAdjustEffect, INormalEffect<TPicture> where TPicture : IPicture
{
    IPicture Process(TPicture source);
    IPicture IColorAdjustEffect.Process(IPicture source) => Process((TPicture)source);
    IPicture INormalEffect<TPicture>.Render(TPicture source, int targetWidth, int targetHeight) => Process(source);
    IPicture INormalEffect.Render(IPicture source, int targetWidth, int targetHeight) => Process((TPicture)source);
}

public interface IContinuousEffect<TPicture> : IContinuousEffect where TPicture : IPicture
{
    IPicture Render(TPicture source, float progress, int targetWidth, int targetHeight);
    IPicture IContinuousEffect.Render(IPicture source, float progress, int targetWidth, int targetHeight) =>
        Render((TPicture)source, progress, targetWidth, targetHeight);
}

public interface IMixture<TPicture> : IMixture where TPicture : IPicture
{
    IPicture Mix(TPicture basePicture, TPicture topPicture, IPicture.PicturePixelMode targetPPB);
    IPicture Mix(TPicture basePicture, TPicture topPicture, IPicture.PicturePixelMode targetPPB, int x, int y, int width, int height);
    IPicture IMixture.Mix(IPicture bottom, IPicture top, IPicture.PicturePixelMode pixelMode) =>
        PictureEffectDispatch.Mix(this, bottom, top, pixelMode);
    IPicture IMixture.Mix(IPicture bottom, IPicture top, IPicture.PicturePixelMode pixelMode, int x, int y, int width, int height) =>
        PictureEffectDispatch.Mix(this, bottom, top, pixelMode, x, y, width, height);
}

public interface ISourceReplacementEffect<TPicture> : ISourceReplacementEffect where TPicture : IPicture
{
    IPicture Compute(IClip input, TPicture source, int width, int height, uint frame, IPicture.PicturePixelMode pixelMode);
    IPicture ISourceReplacementEffect.Compute(IClip input, IPicture source, int width, int height, uint frame, IPicture.PicturePixelMode pixelMode) =>
        Compute(input, (TPicture)source, width, height, frame, pixelMode);
}

public interface IMultipleOutputEffect<TPicture> : IMultipleOutputEffect where TPicture : IPicture
{
    IReadOnlyDictionary<string, object?> ComputeOutputs(TPicture? input, EffectExecutionContext context);
    IReadOnlyDictionary<string, object?> IMultipleOutputEffect.ComputeOutputs(EffectExecutionContext context) =>
        ComputeOutputs(context.Input is null ? default : (TPicture)context.Input, context);
}

public static class PictureEffectDispatch
{
    public static IPicture Transform<TPicture>(ITransform<TPicture> transform, IPicture left, IPicture? right, float progress,
        TransformSide side, int width, int height) where TPicture : IPicture
    {
        using var a = new PictureInput<TPicture>(left, signalBrightness: false);
        using var b = right is null ? null : new PictureInput<TPicture>(right, signalBrightness: false);
        var output = transform.Render(a.Picture, b is null ? default : b.Picture, progress, side, width, height);
        a.Keep(output);
        b?.Keep(output);
        return output;
    }

    public static IPicture Mix<TPicture>(IMixture<TPicture> mixture, IPicture bottom, IPicture top, IPicture.PicturePixelMode mode) where TPicture : IPicture
    {
        var peak = HdrPeak(bottom, top);
        using var a = new PictureInput<TPicture>(bottom, maximumBrightness: peak);
        using var b = new PictureInput<TPicture>(top, maximumBrightness: peak);
        var output = mixture.Mix(a.Picture, b.Picture, mode);
        a.Keep(output);
        b.Keep(output);
        return output;
    }

    public static IPicture Mix<TPicture>(IMixture<TPicture> mixture, IPicture bottom, IPicture top, IPicture.PicturePixelMode mode, int x, int y, int width, int height) where TPicture : IPicture
    {
        var peak = HdrPeak(bottom, top);
        using var a = new PictureInput<TPicture>(bottom, maximumBrightness: peak);
        using var b = new PictureInput<TPicture>(top, maximumBrightness: peak);
        var output = mixture.Mix(a.Picture, b.Picture, mode, x, y, width, height);
        a.Keep(output);
        b.Keep(output);
        return output;
    }
    private static Half? HdrPeak(IPicture left, IPicture right)
    {
        static Half? Read(IPicture p) => p is IHDRPicture<Half> fp ? fp.MaximumBrightness : p is IHDRPicture<ushort> hdr ? hdr.MaximumBrightness
            : p is IHwAccelPicture { IsHdr: true } gpu ? gpu.MaximumBrightness : null;
        var a = Read(left);
        var b = Read(right);
        return a.HasValue && b.HasValue ? Half.Max(a.Value, b.Value) : a ?? b;
    }

}
