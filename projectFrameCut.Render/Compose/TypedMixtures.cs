using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.Compose;

public class ClassicOverlayMixture<TPicture> : ClassicOverlayMixture, IMixture<TPicture> where TPicture : IPicture
{
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new ClassicOverlayMixture<TPicture> { Parameters = parameters };
    public override IPicture Mix(IPicture bottom, IPicture top, IPicture.PicturePixelMode mode) =>
        PictureEffectDispatch.Mix(this, bottom, top, mode);
    public override IPicture Mix(IPicture bottom, IPicture top, IPicture.PicturePixelMode mode, int x, int y, int width, int height) =>
        PictureEffectDispatch.Mix(this, bottom, top, mode, x, y, width, height);
    public IPicture Mix(TPicture bottom, TPicture top, IPicture.PicturePixelMode mode) =>
        MixInternal(bottom, top, mode, true, 0, 0, bottom.Width, bottom.Height);
    public IPicture Mix(TPicture bottom, TPicture top, IPicture.PicturePixelMode mode, int x, int y, int width, int height) =>
        MixInternal(bottom, top, mode, false, x, y, width, height);
}

public class AddMixture<TPicture> : AddMixture, IMixture<TPicture> where TPicture : IPicture
{
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new AddMixture<TPicture> { Parameters = parameters };
    public override IPicture Mix(IPicture bottom, IPicture top, IPicture.PicturePixelMode mode) =>
        PictureEffectDispatch.Mix(this, bottom, top, mode);
    public override IPicture Mix(IPicture bottom, IPicture top, IPicture.PicturePixelMode mode, int x, int y, int width, int height) =>
        PictureEffectDispatch.Mix(this, bottom, top, mode, x, y, width, height);
    public IPicture Mix(TPicture bottom, TPicture top, IPicture.PicturePixelMode mode) =>
        MixInternal(bottom, top, mode, true, 0, 0, bottom.Width, bottom.Height);
    public IPicture Mix(TPicture bottom, TPicture top, IPicture.PicturePixelMode mode, int x, int y, int width, int height) =>
        MixInternal(bottom, top, mode, false, x, y, width, height);
}

public class SubtractMixture<TPicture> : SubtractMixture, IMixture<TPicture> where TPicture : IPicture
{
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new SubtractMixture<TPicture> { Parameters = parameters };
    public override IPicture Mix(IPicture bottom, IPicture top, IPicture.PicturePixelMode mode) =>
        PictureEffectDispatch.Mix(this, bottom, top, mode);
    public override IPicture Mix(IPicture bottom, IPicture top, IPicture.PicturePixelMode mode, int x, int y, int width, int height) =>
        PictureEffectDispatch.Mix(this, bottom, top, mode, x, y, width, height);
    public IPicture Mix(TPicture bottom, TPicture top, IPicture.PicturePixelMode mode) =>
        MixInternal(bottom, top, mode, true, 0, 0, bottom.Width, bottom.Height);
    public IPicture Mix(TPicture bottom, TPicture top, IPicture.PicturePixelMode mode, int x, int y, int width, int height) =>
        MixInternal(bottom, top, mode, false, x, y, width, height);
}

public class MultiplyMixture<TPicture> : MultiplyMixture, IMixture<TPicture> where TPicture : IPicture
{
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new MultiplyMixture<TPicture> { Parameters = parameters };
    public override IPicture Mix(IPicture bottom, IPicture top, IPicture.PicturePixelMode mode) =>
        PictureEffectDispatch.Mix(this, bottom, top, mode);
    public override IPicture Mix(IPicture bottom, IPicture top, IPicture.PicturePixelMode mode, int x, int y, int width, int height) =>
        PictureEffectDispatch.Mix(this, bottom, top, mode, x, y, width, height);
    public IPicture Mix(TPicture bottom, TPicture top, IPicture.PicturePixelMode mode) =>
        MixInternal(bottom, top, mode, true, 0, 0, bottom.Width, bottom.Height);
    public IPicture Mix(TPicture bottom, TPicture top, IPicture.PicturePixelMode mode, int x, int y, int width, int height) =>
        MixInternal(bottom, top, mode, false, x, y, width, height);
}

public class ScreenMixture<TPicture> : ScreenMixture, IMixture<TPicture> where TPicture : IPicture
{
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new ScreenMixture<TPicture> { Parameters = parameters };
    public override IPicture Mix(IPicture bottom, IPicture top, IPicture.PicturePixelMode mode) =>
        PictureEffectDispatch.Mix(this, bottom, top, mode);
    public override IPicture Mix(IPicture bottom, IPicture top, IPicture.PicturePixelMode mode, int x, int y, int width, int height) =>
        PictureEffectDispatch.Mix(this, bottom, top, mode, x, y, width, height);
    public IPicture Mix(TPicture bottom, TPicture top, IPicture.PicturePixelMode mode) =>
        MixInternal(bottom, top, mode, true, 0, 0, bottom.Width, bottom.Height);
    public IPicture Mix(TPicture bottom, TPicture top, IPicture.PicturePixelMode mode, int x, int y, int width, int height) =>
        MixInternal(bottom, top, mode, false, x, y, width, height);
}

public class OverlayBlendMixture<TPicture> : OverlayBlendMixture, IMixture<TPicture> where TPicture : IPicture
{
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new OverlayBlendMixture<TPicture> { Parameters = parameters };
    public override IPicture Mix(IPicture bottom, IPicture top, IPicture.PicturePixelMode mode) =>
        PictureEffectDispatch.Mix(this, bottom, top, mode);
    public override IPicture Mix(IPicture bottom, IPicture top, IPicture.PicturePixelMode mode, int x, int y, int width, int height) =>
        PictureEffectDispatch.Mix(this, bottom, top, mode, x, y, width, height);
    public IPicture Mix(TPicture bottom, TPicture top, IPicture.PicturePixelMode mode) =>
        MixInternal(bottom, top, mode, true, 0, 0, bottom.Width, bottom.Height);
    public IPicture Mix(TPicture bottom, TPicture top, IPicture.PicturePixelMode mode, int x, int y, int width, int height) =>
        MixInternal(bottom, top, mode, false, x, y, width, height);
}

public class DarkenMixture<TPicture> : DarkenMixture, IMixture<TPicture> where TPicture : IPicture
{
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new DarkenMixture<TPicture> { Parameters = parameters };
    public override IPicture Mix(IPicture bottom, IPicture top, IPicture.PicturePixelMode mode) =>
        PictureEffectDispatch.Mix(this, bottom, top, mode);
    public override IPicture Mix(IPicture bottom, IPicture top, IPicture.PicturePixelMode mode, int x, int y, int width, int height) =>
        PictureEffectDispatch.Mix(this, bottom, top, mode, x, y, width, height);
    public IPicture Mix(TPicture bottom, TPicture top, IPicture.PicturePixelMode mode) =>
        MixInternal(bottom, top, mode, true, 0, 0, bottom.Width, bottom.Height);
    public IPicture Mix(TPicture bottom, TPicture top, IPicture.PicturePixelMode mode, int x, int y, int width, int height) =>
        MixInternal(bottom, top, mode, false, x, y, width, height);
}

public class LightenMixture<TPicture> : LightenMixture, IMixture<TPicture> where TPicture : IPicture
{
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new LightenMixture<TPicture> { Parameters = parameters };
    public override IPicture Mix(IPicture bottom, IPicture top, IPicture.PicturePixelMode mode) =>
        PictureEffectDispatch.Mix(this, bottom, top, mode);
    public override IPicture Mix(IPicture bottom, IPicture top, IPicture.PicturePixelMode mode, int x, int y, int width, int height) =>
        PictureEffectDispatch.Mix(this, bottom, top, mode, x, y, width, height);
    public IPicture Mix(TPicture bottom, TPicture top, IPicture.PicturePixelMode mode) =>
        MixInternal(bottom, top, mode, true, 0, 0, bottom.Width, bottom.Height);
    public IPicture Mix(TPicture bottom, TPicture top, IPicture.PicturePixelMode mode, int x, int y, int width, int height) =>
        MixInternal(bottom, top, mode, false, x, y, width, height);
}

public class DifferenceMixture<TPicture> : DifferenceMixture, IMixture<TPicture> where TPicture : IPicture
{
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new DifferenceMixture<TPicture> { Parameters = parameters };
    public override IPicture Mix(IPicture bottom, IPicture top, IPicture.PicturePixelMode mode) =>
        PictureEffectDispatch.Mix(this, bottom, top, mode);
    public override IPicture Mix(IPicture bottom, IPicture top, IPicture.PicturePixelMode mode, int x, int y, int width, int height) =>
        PictureEffectDispatch.Mix(this, bottom, top, mode, x, y, width, height);
    public IPicture Mix(TPicture bottom, TPicture top, IPicture.PicturePixelMode mode) =>
        MixInternal(bottom, top, mode, true, 0, 0, bottom.Width, bottom.Height);
    public IPicture Mix(TPicture bottom, TPicture top, IPicture.PicturePixelMode mode, int x, int y, int width, int height) =>
        MixInternal(bottom, top, mode, false, x, y, width, height);
}
