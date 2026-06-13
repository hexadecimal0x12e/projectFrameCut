using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Drawing.Vector.ImportExport;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.RenderAPIBase.ClipAndTrack
{
    public interface IVectorContentClip : IClip
    {
        public static AntiAliasMode GlobalDefaultAntiAliasMode { get; set; } = AntiAliasMode.SSAA4x;

        public static IVectorPictureRasterizer GlobalDefaultRasterizer { get; set; } = new CPUVectorPictureRasterizer();

        public AntiAliasMode? ClipAntiAliasMode { get; set; } 

        public VectorPicture GetVectorPictureRelativeToStartPointOfSource(uint frameIndex, int requiredWidth, int requiredHeight);
    }
}
