namespace projectFrameCut.Render.HwAccelContracts;

/// <summary>
/// 缩放（Resize）计算机的强类型接口。
/// 提供三种输出精度变体：float（默认）、byte（8bpp）、ushort（16bpp）。
/// </summary>
public interface IResizeComputer
{
    string SupportedEffectOrMixture { get; }

    /// <summary>float 输出路径（向后兼容）。</summary>
    FourChannelResult ComputeResizeFloat(float[] r, float[] g, float[] b, float[] a,
        float srcW, float srcH, float dstW, float dstH);

    /// <summary>8bpp 输出路径。</summary>
    FourChannelResult8 ComputeResizeByte(float[] r, float[] g, float[] b, float[] a,
        float srcW, float srcH, float dstW, float dstH);

    /// <summary>16bpp 输出路径。</summary>
    FourChannelResult16 ComputeResizeUshort(float[] r, float[] g, float[] b, float[] a,
        float srcW, float srcH, float dstW, float dstH);
}

/// <summary>
/// 裁剪（Crop）计算机的强类型接口。
/// </summary>
public interface ICropComputer
{
    string SupportedEffectOrMixture { get; }
    FourChannelResult ComputeCrop(float[] r, float[] g, float[] b, float[] a,
        int srcW, int srcH, int startX, int startY, int cropW, int cropH);
}

/// <summary>
/// 放置（Place）计算机的强类型接口。
/// </summary>
public interface IPlaceComputer
{
    string SupportedEffectOrMixture { get; }
    FourChannelResult ComputePlace(float[] r, float[] g, float[] b, float[] a,
        int srcW, int srcH, int startX, int startY, int targetW, int targetH);
}
