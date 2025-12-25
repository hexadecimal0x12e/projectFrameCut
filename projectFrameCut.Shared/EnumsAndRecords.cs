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
        RemoveColor,
        ExtendMixture,
    }

    public enum ClipMode
    {
        VideoClip,
        PhotoClip,
        SolidColorClip,
        TextClip,
        ExtendClip,
        AudioClip,
        SubtitleClip,
        Special
    }

    public enum TrackMode
    {
        NormalTrack,
        ExtendTrack,
        SpecialTrack
    }

    public enum EffectType
    {
        Crop,
        Resize,
        RemoveColor,
        ReplaceAlpha,
        ColorCorrection,
        ExtendEffect,
    }

    public record struct AcceleratorInfo(uint index, string name, string Type);
}
