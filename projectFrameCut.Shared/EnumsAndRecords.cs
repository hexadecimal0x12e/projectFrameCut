using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.Shared
{
    public enum MixtureMode
    {
        Overlay,
        Add,
        Minus,
        Multiply,
        RemoveColor
    }

    public enum EffectType
    {
        Crop,
        Resize,
        RemoveColor,
        ReplaceAlpha,
        ColorCorrection,
    }

    public record struct AcceleratorInfo(uint index, string name, string Type);
}
