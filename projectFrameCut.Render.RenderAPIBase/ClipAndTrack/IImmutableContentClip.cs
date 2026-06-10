using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Drawing.Vector.ImportExport;
using System;
using System.Collections.Generic;
using System.Text;
using System.Transactions;

namespace projectFrameCut.Render.RenderAPIBase.ClipAndTrack
{
    public interface IImmutableContentClip : IClip
    {
        public IPicture GetContent(int width, int height, bool forceResize, IPicture.PicturePixelMode targetPPB);

        IPicture IClip.GetFrameRelativeToStartPointOfSource(uint frameIndex, int requiredWidth, int requiredHeight, bool forceResize, IPicture.PicturePixelMode targetPPB) => GetContent(requiredWidth, requiredHeight, forceResize, targetPPB);

        IPicture IClip.GetFrame(uint targetFrame, int targetWidth, int targetHeight, bool forceResize, IPicture.PicturePixelMode targetPPB) => GetContent(targetWidth, targetHeight, forceResize, targetPPB);

    }
    public interface IImmutableVectorContentClip : IVectorContentClip, IImmutableContentClip
    {
        public VectorPicture GetVectorPicture(int requiredWidth, int requiredHeight);

        VectorPicture IVectorContentClip.GetVectorPictureRelativeToStartPointOfSource(uint frameIndex, int requiredWidth, int requiredHeight)
            => GetVectorPicture(requiredWidth, requiredHeight);


        IPicture IImmutableContentClip.GetContent(int width, int height, bool forceResize, IPicture.PicturePixelMode targetPPB)
        {
            return VectorToIPicture.Convert(GetVectorPicture(height, height), width, height, forceResize, GlobalDefaultAntiAliasMode).ToBitPerPixel(targetPPB);
        }
    }
}
