using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.Shared
{
    [Obsolete("This enum is deprecated and will be removed in future versions. We have no plan on custom type of mixturing.")]
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

    public enum AssetType
    {
        Video,
        Audio,
        Image,
        Font,
        Other
    }

    public enum TrackMode
    {
        NormalTrack,
        ExtendTrack,
        SpecialTrack
    }

    public enum EffectType
    {
        NormalEffect,
        ContinuousEffect,
        BindableEffect,
        AudioEffect,
        NotSpecified = -1
    }

    public enum BindableArgumentEffectType
    {
        ValueProvider,
        NoInputValueProvider,
        OneInputValueProcessor,
        ManyInputValueProcessor,
        OneInputResultGenerator,
        ManyInputResultGenerator,
        ContinuousResultGenerator,
    }

    public enum EffectImplementType
    {
        NotSpecified,
        IPicture,
        ImageSharp,
        HwAcceleration,
        Custom1,
        Custom2,
        Custom3,
        Custom4,
        Custom5,
    }

    public record struct AcceleratorInfo(uint index, string name, string Type);
}
