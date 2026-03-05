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
    }

    public record struct AcceleratorInfo(uint index, string name, string Type);
}
