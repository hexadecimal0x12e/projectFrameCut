namespace projectFrameCut.Render.HwAccelContracts;

/// <summary>
/// 不透明度（FadeOpacity）效果计算机的强类型接口。
/// </summary>
public interface IOpacityComputer
{
    string SupportedEffectOrMixture { get; }
    FourChannelResult ComputeOpacity(float[] r, float[] g, float[] b, float[] a, float opacity);
}

/// <summary>
/// 翻转（Flip）效果计算机的强类型接口。
/// </summary>
public interface IFlipComputer
{
    string SupportedEffectOrMixture { get; }
    FourChannelResult ComputeFlip(float[] r, float[] g, float[] b, float[] a,
        int width, int height, bool horizontal, bool vertical);
}

/// <summary>
/// 模糊（Blur）效果计算机的强类型接口。
/// </summary>
public interface IBlurComputer
{
    string SupportedEffectOrMixture { get; }
    FourChannelResult ComputeBlur(float[] r, float[] g, float[] b, float[] a,
        int width, float sigma);
}

/// <summary>
/// 暗角（Vignette）效果计算机的强类型接口。
/// </summary>
public interface IVignetteComputer
{
    string SupportedEffectOrMixture { get; }
    FourChannelResult ComputeVignette(float[] r, float[] g, float[] b, float[] a,
        int width, int height, float strength, float radius);
}

/// <summary>
/// 锐化（Sharpen）效果计算机的强类型接口。
/// </summary>
public interface ISharpenComputer
{
    string SupportedEffectOrMixture { get; }
    FourChannelResult ComputeSharpen(float[] r, float[] g, float[] b, float[] a,
        int width, float amount);
}

/// <summary>
/// 旋转（Rotation）效果计算机的强类型接口。
/// </summary>
public interface IRotationComputer
{
    string SupportedEffectOrMixture { get; }
    FourChannelResult ComputeRotation(float[] r, float[] g, float[] b, float[] a,
        int srcW, int srcH, int dstW, int dstH, float angleDeg);
}

/// <summary>
/// 色彩调整（ColorAdjustment）效果计算机的强类型接口。
/// </summary>
public interface IColorAdjustmentComputer
{
    string SupportedEffectOrMixture { get; }
    FourChannelResult ComputeColorAdjustment(
        float[] r, float[] g, float[] b, float[] a,
        int width, int height,
        float brightness, float contrast, float saturation, float hue,
        float gamma, float vibrance, float temperature, bool invert,
        float grayscale, float opacity, float maxVal);
}

/// <summary>
/// 移除颜色（RemoveColor）效果计算机的强类型接口。
/// 注意：此接口返回单一 float[]（alpha 通道），而非四通道结果。
/// </summary>
public interface IRemoveColorComputer
{
    string SupportedEffectOrMixture { get; }
    float[] ComputeRemoveColor(float[] r, float[] g, float[] b, float[] a,
        float targetR, float targetG, float targetB, float range, int pixels);
}
