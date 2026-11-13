using projectFrameCut.Drawing.Base;

namespace projectFrameCut.Render.RenderAPIBase.ClipAndTrack;

public interface IRotatableClip : IClip
{
    IPicture GetUnrotatedFrame(uint frameIndex, int width, int height, IPicture.PicturePixelMode pixelMode);
}
