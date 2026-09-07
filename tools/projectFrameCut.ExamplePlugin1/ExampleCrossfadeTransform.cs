using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace SomePublisher;

public sealed class ExampleCrossfadeTransform : IContinuousTransform
{
    public ExampleCrossfadeTransform(Guid left, Guid right)
    {
        BindedLeftClip = left;
        BindedRightClip = right;
    }

    public string FromPlugin => ExamplePluginConstants.PluginId;
    public string TypeName => "ExampleCrossfade";
    public string Name { get; init; } = "Example crossfade";
    public Guid BindedLeftClip { get; set; }
    public Guid BindedRightClip { get; set; }
    public uint Duration { get; set; } = 30;
    public string? NeedComputer => null;

    public IPicture GetFrame(IPicture left, IPicture right, double progress, IComputer? computer, int targetWidth, int targetHeight)
    {
        progress = Math.Clamp(progress, 0, 1);
        if (left is IPicture<byte> l8 && right is IPicture<byte> r8)
        {
            var result = new Picture8bpp(l8.Width, l8.Height);
            for (int i = 0; i < result.Pixels; i++)
            {
                result.r[i] = Blend(l8.r[i], r8.r[i], progress);
                result.g[i] = Blend(l8.g[i], r8.g[i], progress);
                result.b[i] = Blend(l8.b[i], r8.b[i], progress);
            }
            return result;
        }

        if (left is IPicture<ushort> l16 && right is IPicture<ushort> r16)
        {
            var result = new Picture16bpp(l16.Width, l16.Height);
            for (int i = 0; i < result.Pixels; i++)
            {
                result.r[i] = Blend(l16.r[i], r16.r[i], progress);
                result.g[i] = Blend(l16.g[i], r16.g[i], progress);
                result.b[i] = Blend(l16.b[i], r16.b[i], progress);
            }
            return result;
        }

        throw new NotSupportedException("ExampleCrossfade requires matching 8-bit or 16-bit inputs.");
    }

    private static byte Blend(byte left, byte right, double progress) => (byte)Math.Clamp((int)Math.Round(left * (1 - progress) + right * progress), 0, byte.MaxValue);
    private static ushort Blend(ushort left, ushort right, double progress) => (ushort)Math.Clamp((int)Math.Round(left * (1 - progress) + right * progress), 0, ushort.MaxValue);
}
