using projectFrameCut.Drawing.Base;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

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
        VectorCanvasClip,
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

    public enum EffectType
    {
        NormalEffect,
        ContinuousEffect,
        [Obsolete("All BindableEffect is deprecated. Use EffectProvider, and mark a EffectProvider with a not IPicture output as NonIPictureOutputValueProvider.")]
        BindableEffect,
        AudioNormalEffect,
        AudioContinuousEffect,
        AudioBindableEffect,
        SpeedVarianceProvider,
        ClipPositionProvider,
        ContinuousClipPositionProvider,
        MixtureProvider,
        TextEffect,
        ContinuousTextEffect,
        SourceReplacement,
        NonIPictureOutputValueProvider,
        NotSpecified = -1,
    }

    [Flags]
    public enum EffectTarget
    {
        NotSpecified = -1,
        Video = 2,
        Audio = 4,
        SpeedVariance = 8,
        Mixture = 16,
        ColorAdjustment = 32,
        Text = 64,
        SourceReplacement = 128,
        ValueProvider = 256,

        IsKeyFramed = 1 << 16,
        IsNotVisibleInEffectEditor = 1 << 17,
        IsNotVisibleInNewEffectSelector = 1 << 18,
        InternalUse = 1 << 19,
    }

    [Obsolete("All BindableEffect is deprecated, no longer be processed and will be removed in API V9, please use EffectProvider with a dynamic EffectParamField instead.")]
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
        None = -1,
        NotSpecified,
        IPicture,
        [Obsolete("ImageSharp-based effects are deprecated and this enum is kept for backward compatibility only. Please use IPicture-based implementations instead.", false)]
        ImageSharp_Deprecated,
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

    [Obsolete("Use TextEntry (from projectFrameCut.Drawing.Text.Entry) instead. TextClipEntry is kept for backward compatibility with serialized data.")]
    public record TextClipEntry
    {
        // Core text
        public string text { get; set; }
        public int x { get; set; }
        public int y { get; set; }

        // Font
        public string fontFamily { get; set; }
        public float fontSize { get; set; }
        public ClipFontStyle fontStyle { get; init; } = ClipFontStyle.Regular;
        public bool UseVerticalLayout { get; set; } = false;
        public bool KeepNonCJKTextAsHorizontal { get; set; } = false;

        // Fill color (0..65535 each)
        public ushort r { get; set; }
        public ushort g { get; set; }
        public ushort b { get; set; }
        public float? a { get; set; }

        // Alignment and wrapping
        public ClipHorizontalAlignment horizontalAlignment { get; init; } = ClipHorizontalAlignment.Left;
        public ClipVerticalAlignment verticalAlignment { get; init; } = ClipVerticalAlignment.Top;
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

        // When false, BuildEntriesForTargetSize skips font-size and wrapping-width
        // scaling (used by FixedWidth / FixedHeight / FixedSize layout modes where
        // the provider already produces entries in the correct coordinate space).
        public bool ScaleWithTarget { get; init; } = true;

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
    /// A tuple representing the position of a clip on the target canvas. The position is represented by a tuple of (X, Y, Width, Height).
    /// </summary>
    /// <param name="TargetX">The X coordinate of the clip's position.</param>
    /// <param name="TargetY">The Y coordinate of the clip's position.</param>
    /// <param name="TargetWidth">The width of the clip.</param>
    /// <param name="TargetHeight">The height of the clip.</param>
    /// <param name="IsDelta">Indicates whether the position is a delta relative to the previous position.</param>
    public record struct ClipPositionTuple(int TargetX, int TargetY, int TargetWidth, int TargetHeight, bool IsDelta);

}
