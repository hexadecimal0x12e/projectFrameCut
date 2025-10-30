using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.Shared
{
    public enum EffectType
    {
        Crop,
        Resize,
        RemoveColor,
        ReplaceAlpha,
        ColorCorrection,
    }

    public enum RenderMode
    {
        Overlay,
        Add,
        Minus,
        Multiply,
        RemoveColor
    }

    public enum MyAcceleratorType
    {
        Auto,
        CUDA,
        OpenCL,
        OpenGL,
        Metal,
        CPU
    }
}
