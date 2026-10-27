using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Transform;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.HwAccelEngine.Effect;
public sealed partial class FadeTransform_HwAccel : FadeTransform, IDisposable
{
    private readonly Lock nativeLock = new();
    private readonly FadeOpacityEffect_HwAccel opacity = new();
    private bool disposed;
    public override EffectImplementType ImplementType => EffectImplementType.HwAcceleration;

    public override IEffect WithParameters(Dictionary<string, object> parameters) => new FadeTransform_HwAccel
    {
        Parameters = parameters
    };
    protected override IPicture RenderOpacity(IPicture source, Half value, int width, int height)
    {
        using var scope = nativeLock.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        opacity.Parameters["Opacity"] = value;
        return opacity.Render(source, width, height);
    }

    public void Dispose()
    {
        using var scope = nativeLock.EnterScope();
        if (disposed)
            return;
        disposed = true;
        opacity.Dispose();
    }
}

public partial class CrossfadeTransform_HwAccel : CrossfadeTransform, IDisposable
{
    private readonly Lock nativeLock = new();
    private bool disposed;
    public override EffectImplementType ImplementType => EffectImplementType.HwAcceleration;

    public override IEffect WithParameters(Dictionary<string, object> parameters) => new CrossfadeTransform_HwAccel
    {
        Parameters = parameters
    };
    public override IPicture Render(IPicture left, IPicture? right, float progress, TransformSide side, int targetWidth, int targetHeight) => ResidentEffectResult.Download(EffectPictureProcessing.Transform(this, left, right, progress, side, targetWidth, targetHeight));
    public void Dispose()
    {
        using var scope = nativeLock.EnterScope();
        if (disposed)
            return;
        disposed = true;
    }
}
