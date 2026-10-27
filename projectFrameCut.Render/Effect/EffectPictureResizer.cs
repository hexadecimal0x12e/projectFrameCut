using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.Effect;

public sealed class EffectPictureResizer(EffectImplementType implementType = EffectImplementType.NotSpecified) : IPictureResizer
{
    public IPicture ResizePicture(IPicture source, int width, int height) => Resize(source, width, height, false);

    public IPicture ResizePicture<TPicture>(TPicture source, int width, int height) where TPicture : IPicture =>
        implementType == EffectImplementType.IPicture ? PictureCalculation<TPicture>.Resize(source, width, height, false)
            : Resize(source, width, height, false);

    public IPicture Resize(IPicture source, int width, int height, bool preserveAspect) => ResizeCore(source, width, height, preserveAspect, keepHwAccelPicture: true);

    private IPicture ResizeCore(IPicture source, int width, int height, bool preserveAspect, bool keepHwAccelPicture = false) => EffectRuntimeDefaults.Execute(
        "Resize", "Resize", EffectImplementType.IPicture,
        new() { ["Width"] = width, ["Height"] = height, ["PreserveAspectRatio"] = preserveAspect }, e =>
        {
            e.Parameters["Width"] = width;
            e.Parameters["Height"] = height;
            e.Parameters["PreserveAspectRatio"] = preserveAspect;
            return keepHwAccelPicture && e is IHwAccelPictureEffect
                ? EffectPictureProcessing.Render(e, source, width, height)
                : ((INormalEffect)e).Render(source, width, height);
        }, implementType);

    public IPicture<Half> Resize(IPicture<Half> source, int width, int height, bool preserveAspect) => (IPicture<Half>)ResizeCore(source, width, height, preserveAspect);
    public IPicture<byte> Resize(IPicture<byte> source, int width, int height, bool preserveAspect) => (IPicture<byte>)ResizeCore(source, width, height, preserveAspect);
    public IPicture<ushort> Resize(IPicture<ushort> source, int width, int height, bool preserveAspect) => (IPicture<ushort>)ResizeCore(source, width, height, preserveAspect);
    public IHDRPicture<ushort> Resize(IHDRPicture<ushort> source, int width, int height, bool preserveAspect) => (IHDRPicture<ushort>)ResizeCore(source, width, height, preserveAspect);
}
