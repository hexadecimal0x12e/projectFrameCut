using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.Transform;

public sealed class FadeTransform : IOneInputSingleFrameTransform
{
    public string FromPlugin => "projectFrameCut.Render.Plugins.InternalPluginBase";
    public string TypeName => "Fade";
    public string Name { get; init; } = "Fade";
    public TransformDefinition Definition => TransformDefinition.Clip | TransformDefinition.SupportOneInput;
    public TransformSide Side { get; set; }
    public Guid BindedLeftClip { get; set; }
    public Guid BindedRightClip { get; set; }
    public uint Duration { get; set; }

    public IPicture GetFrame(IPicture input, double progress, int targetWidth, int targetHeight)
    {
        float opacity = (float)Math.Clamp(Side == TransformSide.Left ? progress : 1 - progress, 0, 1);
        return EffectRuntimeDefaults.Execute("TransformFade", "FadeOpacity", EffectImplementType.IPicture,
            new() { ["Opacity"] = opacity }, effect =>
            {
                effect.Parameters["Opacity"] = opacity;
                return ((INormalEffect)effect).Render(input, targetWidth, targetHeight);
            });
    }
}
