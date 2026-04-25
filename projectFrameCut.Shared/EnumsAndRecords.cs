using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Drawing.Processing;

namespace projectFrameCut.Shared
{
    public enum ClipMode
    {
        VideoClip,
        PhotoClip,
        SolidColorClip,
        TextClip,
        ExtendClip,
        AudioClip,
        SubtitleClip,
        MarkingClip,
        TransformClip,
        Special = -1
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

    [Flags]
    public enum EffectType
    {
        NormalEffect,
        ContinuousEffect,
        BindableEffect,
        AudioNormalEffect,
        AudioContinuousEffect,
        AudioBindableEffect,
        SpeedVarianceProvider,
        NotSpecified = -1,
    }

    public enum EffectTarget
    {
        Video,
        Audio,
        SpeedVariance,
        ColorAdjustment,
        NotSpecified = -1,
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

    public enum TransformType
    {
        /// <summary>
        /// Represents a transform with two input, 
        /// and get only the last frame of left and first frame of right one.
        /// </summary>
        SingleFrameTransform,
        /// <summary>
        /// Represents a transform with only one input, 
        /// which usually is the last frame of binded clip.
        /// </summary>
        OneInputSingleFrameTransform,
        /// <summary>
        /// Represents a transform with two input, 
        /// Continuously get the frame from two sources and render it by the progress.
        /// </summary>
        ContinuousTransform,
        /// <summary>
        /// To do in future.
        /// </summary>
        AudioTransform
    }

    public enum TextLanguage
    {
        Unknown,
        English,
        Chinese,
        Japanese,
        Korean,
        Russian,
        Thai,
        Arabic
    }

    public record TextClipEntry
    {
        // Core text
        public string text { get; set; }
        public int x { get; set; }
        public int y { get; set; }

        // Font
        public string fontFamily { get; set; }
        public float fontSize { get; set; }
        public FontStyle fontStyle { get; init; } = FontStyle.Regular;
        public bool UseVerticalLayout { get; set; } = false;
        public bool KeepNonCJKTextAsHorizontal { get; set; } = false;

        // Fill color (0..65535 each)
        public ushort r { get; set; }
        public ushort g { get; set; }
        public ushort b { get; set; }
        public float? a { get; set; }

        // Alignment and wrapping
        public HorizontalAlignment horizontalAlignment { get; init; } = HorizontalAlignment.Left;
        public VerticalAlignment verticalAlignment { get; init; } = VerticalAlignment.Top;
        public float? wrappingWidth { get; init; } = null; // when set, enables wrapping within this width

        // Layout and metrics
        public bool applyKerning { get; init; } = true;
        public float lineSpacing { get; init; } = 1.0f; // multiplier for line height

        // Appearance
        public float rotation { get; init; } = 0f; // degrees clockwise

        // Stroke / outline (optional)
        public float? strokeWidth { get; init; } = null;
        public ushort strokeR { get; init; } = 0;
        public ushort strokeG { get; init; } = 0;
        public ushort strokeB { get; init; } = 0;

        // Additional: DPI for font rasterization (nullable, uses default when null)
        public float? dpi { get; init; } = null;

        // Use in UI only, not for rendering
        public bool ShouldInSubtrack { get; set; } = false;
        public string StyleId { get; set; } = "";
        public string? SampleText { get; set; } = null;
        public TextLanguage Language { get; set; } = TextLanguage.Unknown;

        public TextClipEntry()
        {

        }

        public TextClipEntry(string text, int x, int y, string fontFamily, float fontSize, ushort r, ushort g, ushort b, float? a = null)//compactable to older versions
        {
            this.text = text ?? throw new ArgumentNullException(nameof(text));
            this.x = x;
            this.y = y;
            this.fontFamily = fontFamily ?? throw new ArgumentNullException(nameof(fontFamily));
            this.fontSize = fontSize;
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }

        public static string LocalizeLanguageName(TextLanguage language)
        {
            return language switch
            {
                TextLanguage.English => "English",
                TextLanguage.Chinese => "中文",
                TextLanguage.Japanese => "日本語",
                TextLanguage.Korean => "한국어",
                TextLanguage.Russian => "Русский",
                TextLanguage.Thai => "ไทย",
                TextLanguage.Arabic => "العربية",
                _ => "?"
            };
        }

        public static TextLanguage FromLocaliedString(string input)
        {
            return input switch
            {
                "English" => TextLanguage.English,
                "中文" => TextLanguage.Chinese,
                "日本語" => TextLanguage.Japanese,
                "한국어" => TextLanguage.Korean,
                "Русский" => TextLanguage.Russian,
                "ไทย" => TextLanguage.Thai,
                "العربية" => TextLanguage.Arabic,
                _ => TextLanguage.Unknown
            };
        }
    }

    public record struct AcceleratorInfo(uint index, string name, string Type);

    /// <summary>
    /// Determines the method to degrade HDR image to SDR when the renderer or display does not support HDR.
    /// </summary>
    public enum HDRImageDegradeToSDRMode
    {
        /// <summary>
        /// Normalize the pixels from the <see cref="IHDRPicture{T}.Brightness"/> channel to the range of RGB channels.
        /// </summary>
        NormalizeBrightnessToRGB,
        /// <summary>
        /// Overlay a black mask which has <see cref="IPicture{T}.a"/> channel from <see cref="IHDRPicture{T}.Brightness"/> to the RGB(A) channels.
        /// </summary>
        OverlayMaskFromBrightness,
        /// <summary>
        /// Discard the <see cref="IHDRPicture{T}.Brightness"/> channel away.
        /// </summary>
        DiscardBrightnessChannel,
        /// <summary>
        /// Throw a <see cref="InvalidOperationException"/> when degrade operation occurs. Similar behavior when <see cref="IPicture.AllowPixelModeDowngrade"/> is false and you call <see cref="IPicture.ToBitPerPixel(int)"/> smaller than source's <see cref="IPicture.bitPerPixel"/>.
        /// </summary>
        DisallowDowngrade
    }
}
