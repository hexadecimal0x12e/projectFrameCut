using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.Effect;

public sealed class EffectPictureResizer(EffectImplementType implementType = EffectImplementType.NotSpecified) : IPictureResizer
{
    public IPicture ResizePicture(IPicture source, int width, int height) => ResizeCore(source, width, height, false);

    private IPicture ResizeCore(IPicture source, int width, int height, bool preserveAspect) => EffectRuntimeDefaults.Execute(
        "Resize", "Resize", EffectImplementType.IPicture,
        new() { ["Width"] = width, ["Height"] = height, ["PreserveAspectRatio"] = preserveAspect }, e =>
        {
            e.Parameters["Width"] = width;
            e.Parameters["Height"] = height;
            e.Parameters["PreserveAspectRatio"] = preserveAspect;
            return ((INormalEffect)e).Render(source, width, height);
        }, implementType);

    public IPicture<byte> Resize(IPicture<byte> source, int width, int height, bool preserveAspect) => (IPicture<byte>)ResizeCore(source, width, height, preserveAspect);
    public IPicture<ushort> Resize(IPicture<ushort> source, int width, int height, bool preserveAspect) => (IPicture<ushort>)ResizeCore(source, width, height, preserveAspect);
    public IHDRPicture<ushort> Resize(IHDRPicture<ushort> source, int width, int height, bool preserveAspect) => (IHDRPicture<ushort>)ResizeCore(source, width, height, preserveAspect);
}
