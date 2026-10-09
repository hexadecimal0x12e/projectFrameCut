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
        public IPicture GetContent(int width, int height, IPicture.PicturePixelMode targetPPB);

        IPicture IClip.GetFrameRelativeToStartPointOfSource(uint frameIndex, int requiredWidth, int requiredHeight, IPicture.PicturePixelMode targetPPB) => GetContent(requiredWidth, requiredHeight, targetPPB);

        IPicture IClip.GetFrame(uint targetFrame, int targetWidth, int targetHeight, IPicture.PicturePixelMode targetPPB) => GetContent(targetWidth, targetHeight, targetPPB);

    }
    public interface IImmutableVectorContentClip : IVectorContentClip, IImmutableContentClip
    {
        bool IVectorContentClip.ProcessesVectorPictureEffects => true;

        IPicture IClip.GetFrameRelativeToStartPointOfSource(uint frameIndex, int width, int height, IPicture.PicturePixelMode ppb)
            => Rasterize(frameIndex, width, height, ppb);

        IPicture IClip.GetFrame(uint frame, int width, int height, IPicture.PicturePixelMode ppb)
            => Rasterize(((IClip)this).GetRelativeFrameIndex(frame) ?? 0, width, height, ppb);

        private IPicture Rasterize(uint frame, int width, int height, IPicture.PicturePixelMode ppb)
        {
            var raster = GlobalDefaultRasterizer.Convert(GetVectorPictureRelativeToStartPointOfSource(frame, width, height),
                width, height, true, ClipAntiAliasMode ?? GlobalDefaultAntiAliasMode);
            if (raster.BitPerPixel == ppb) return raster;
            try { return raster.ToBitPerPixel(ppb); }
            finally { raster.Dispose(); }
        }

        public VectorPicture GetVectorPicture(int requiredWidth, int requiredHeight);

        VectorPicture IVectorContentClip.GetVectorPictureRelativeToStartPointOfSource(uint frameIndex, int requiredWidth, int requiredHeight)
            => EffectAndMixture.VectorPictureEffectProcessing.Process(this, GetVectorPicture(requiredWidth, requiredHeight), frameIndex);

        IPicture IImmutableContentClip.GetContent(int width, int height, IPicture.PicturePixelMode targetPPB)
        {
            return Rasterize(0, width, height, targetPPB);
        }
    }
}
