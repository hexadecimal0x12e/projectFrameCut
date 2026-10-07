using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Transform;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.HwAccelEngine.Effect;

public sealed class FadeTransform_HwAccel : FadeTransform, IDisposable
{
    private readonly Lock nativeLock = new();
    private readonly FadeOpacityEffect_HwAccel opacity = new();
    private bool disposed;
    public override EffectImplementType ImplementType => EffectImplementType.HwAcceleration;
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new FadeTransform_HwAccel { Parameters = parameters };

    protected override IPicture RenderOpacity(IPicture source, float value, int width, int height)
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
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new CrossfadeTransform_HwAccel { Parameters = parameters };

    public override IPicture Render(IPicture left, IPicture? right, float progress, TransformSide side, int targetWidth, int targetHeight)
    {
        using var scope = nativeLock.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        return base.Render(left, right, progress, side, targetWidth, targetHeight);
    }

    protected override float[] Blend(float[] left, float[] right, float[] leftAlpha, float[] rightAlpha, float progress)
        => ComputeBlend(left, right, leftAlpha, rightAlpha, progress);

    public void Dispose()
    {
        using var scope = nativeLock.EnterScope();
        if (disposed)
            return;
        disposed = true;
        ReleaseNativeResources();
    }

    partial void ReleaseNativeResources();
}
