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
        var output = input.Clone();
        float opacity = (float)Math.Clamp(Side == TransformSide.Left ? progress : 1 - progress, 0, 1);
        float[] alpha = new float[output.Pixels];
        var inputAlpha = input.GetSpecificChannel(IPicture.ChannelId.Alpha) as float[];
        for (int i = 0; i < alpha.Length; i++) alpha[i] = (inputAlpha?[i] ?? 1) * opacity;
        if (output is IPicture<byte> p8)
        {
            p8.a = alpha;
            p8.HasAlphaChannel = true;
        }
        else if (output is IPicture<ushort> p16)
        {
            p16.a = alpha;
            p16.HasAlphaChannel = true;
        }
        else throw new NotSupportedException("Fade requires an 8-bit or 16-bit picture.");
        return output;
    }
}
