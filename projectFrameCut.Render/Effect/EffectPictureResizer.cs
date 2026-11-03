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

    private IPicture ResizeCore(IPicture source, int width, int height, bool preserveAspect, bool keepHwAccelPicture = false)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var type = implementType == EffectImplementType.NotSpecified
            ? EffectHelper.ForcePreferToType ?? EffectHelper.DefaultImplementsType.GetValueOrDefault(
                $"{Plugin.InternalPluginBase.InternalPluginBaseID}.Resize", EffectImplementType.IPicture)
            : implementType;
        if (type == EffectImplementType.HwAcceleration)
        {
            using var input = EffectPictureProcessing.Upload(source);
            if (input.Context is not IHwAccelPictureGeometry geometry)
                throw new NotSupportedException("The hardware backend does not support picture resizing.");
            var result = geometry.Resize(input, width, height, preserveAspect);
            if (keepHwAccelPicture) return result;
            using (result) return result.Download();
        }
        using var downloaded = source is IHwAccelPicture gpu ? gpu.Download() : null;
        return PictureCalculation<IPicture>.Resize(downloaded ?? source, width, height, preserveAspect);
    }

    public IPicture<Half> Resize(IPicture<Half> source, int width, int height, bool preserveAspect) => (IPicture<Half>)ResizeCore(source, width, height, preserveAspect);
    public IPicture<byte> Resize(IPicture<byte> source, int width, int height, bool preserveAspect) => (IPicture<byte>)ResizeCore(source, width, height, preserveAspect);
    public IPicture<ushort> Resize(IPicture<ushort> source, int width, int height, bool preserveAspect) => (IPicture<ushort>)ResizeCore(source, width, height, preserveAspect);
    public IHDRPicture<ushort> Resize(IHDRPicture<ushort> source, int width, int height, bool preserveAspect) => (IHDRPicture<ushort>)ResizeCore(source, width, height, preserveAspect);
}
