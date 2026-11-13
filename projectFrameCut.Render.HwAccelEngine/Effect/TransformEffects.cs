using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Transform;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.HwAccelEngine.Effect;
public sealed partial class FadeTransform_HwAccel : FadeTransform, IDisposable
{
    private readonly Lock nativeLock = new();
    private bool disposed;
    public override EffectImplementType ImplementType => EffectImplementType.HwAcceleration;

    public override IEffect WithParameters(Dictionary<string, object> parameters) => new FadeTransform_HwAccel
    {
        Parameters = parameters
    };
    public override IPicture Render(IPicture left, IPicture? right, float progress, TransformSide side, int targetWidth, int targetHeight) =>
        ResidentEffectResult.Download(EffectPictureProcessing.Transform(this, left, right, progress, side, targetWidth, targetHeight));

    public void Dispose()
    {
        using var scope = nativeLock.EnterScope();
        if (disposed)
            return;
        disposed = true;
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
