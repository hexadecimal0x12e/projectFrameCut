namespace projectFrameCut.Render.HwAccelContracts;

/// <summary>
/// 精确叠加（Overlay）计算机的强类型接口。
/// 含 8bpp/16bpp/HDR 三种输出变体。
/// </summary>
public interface IOverlayComputer
{
    string SupportedEffectOrMixture { get; }
    BlendResult8 Overlay8(float[] top, float[] bottom,
        float[] topAlpha, float[] bottomAlpha, int pixelCount);
    BlendResult16 Overlay16(float[] top, float[] bottom,
        float[] topAlpha, float[] bottomAlpha, int pixelCount);
    BlendResultHdr OverlayHdr(float[] top, float[] bottom,
        float[] topAlpha, float[] bottomAlpha, int pixelCount);
}

/// <summary>
/// 近似叠加（OverlayApproximate）计算机的强类型接口。
/// 含 8bpp/16bpp/HDR 三种输出变体。
/// </summary>
public interface IApproximateOverlayComputer
{
    string SupportedEffectOrMixture { get; }
    BlendResult8 ApproximateOverlay8(float[] top, float[] bottom,
        float[] topAlpha, float[] bottomAlpha, int pixelCount);
    BlendResult16 ApproximateOverlay16(float[] top, float[] bottom,
        float[] topAlpha, float[] bottomAlpha, int pixelCount);
    BlendResultHdr ApproximateOverlayHdr(float[] top, float[] bottom,
        float[] topAlpha, float[] bottomAlpha, int pixelCount);
}

/// <summary>
/// 混合模式计算机（Add/Subtract/Multiply/Screen/OverlayBlend/Darken/Lighten/Difference）的强类型接口。
/// 所有 8 种混合模式共用此接口，因为它们的输入/输出签名完全一致。
/// </summary>
public interface IBlendModeComputer
{
    string SupportedEffectOrMixture { get; }
    BlendResult16 ComputeBlend(float[] top, float[] bottom,
        float[] topAlpha, float[] bottomAlpha, int pixelCount);
}
