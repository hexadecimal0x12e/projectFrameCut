using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.HwAccelEngine.Effect;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.HwAccelEngine;
public static class HardwareEffectFactories
{
    public static IReadOnlyDictionary<EffectImplementationKey, Func<IEffect>> Create() => new Dictionary<EffectImplementationKey, Func<IEffect>>
    {
#if WINDOWS || LINUX || WINNETCORE || ANDROID || IOS || MACCATALYST
        [new("Blur", EffectImplementType.HwAcceleration)] = () => new BlurEffect_HwAccel(),
        [new("Crop", EffectImplementType.HwAcceleration)] = () => new CropEffect_HwAccel(),
        [new("ProgressCrop", EffectImplementType.HwAcceleration)] = () => new ProgressCropper_HwAccel(),
        [new("Resize", EffectImplementType.HwAcceleration)] = () => new ResizeEffect_HwAccel(),
        [new("Flip", EffectImplementType.HwAcceleration)] = () => new FlipEffect_HwAccel(),
        [new("Sharpen", EffectImplementType.HwAcceleration)] = () => new SharpenEffect_HwAccel(),
        [new("Vignette", EffectImplementType.HwAcceleration)] = () => new VignetteEffect_HwAccel(),
        [new("FadeOpacity", EffectImplementType.HwAcceleration)] = () => new FadeOpacityEffect_HwAccel(),
        [new("Fade", EffectImplementType.HwAcceleration)] = () => new FadeTransform_HwAccel(),
        [new("Crossfade", EffectImplementType.HwAcceleration)] = () => new CrossfadeTransform_HwAccel(),
        [new("ColorAdjustment", EffectImplementType.HwAcceleration)] = () => new ColorAdjustmentEffect_HwAccel(),
        [new("Rotation", EffectImplementType.HwAcceleration)] = () => new RotationEffect_HwAccel(),
        [new("Place", EffectImplementType.HwAcceleration)] = () => new PlaceEffect_HwAccel(),
        [new("RemoveColor", EffectImplementType.HwAcceleration)] = () => new RemoveColorEffect_HwAccel(),
        [new("ClassicOverlayMixture", EffectImplementType.HwAcceleration)] = () => new ClassicOverlayMixture_HwAccel(),
        [new("AddMixture", EffectImplementType.HwAcceleration)] = () => new AddMixture_HwAccel(),
        [new("SubtractMixture", EffectImplementType.HwAcceleration)] = () => new SubtractMixture_HwAccel(),
        [new("MultiplyMixture", EffectImplementType.HwAcceleration)] = () => new MultiplyMixture_HwAccel(),
        [new("ScreenMixture", EffectImplementType.HwAcceleration)] = () => new ScreenMixture_HwAccel(),
        [new("OverlayBlendMixture", EffectImplementType.HwAcceleration)] = () => new OverlayBlendMixture_HwAccel(),
        [new("DarkenMixture", EffectImplementType.HwAcceleration)] = () => new DarkenMixture_HwAccel(),
        [new("LightenMixture", EffectImplementType.HwAcceleration)] = () => new LightenMixture_HwAccel(),
        [new("DifferenceMixture", EffectImplementType.HwAcceleration)] = () => new DifferenceMixture_HwAccel(),
#endif
    };
}
