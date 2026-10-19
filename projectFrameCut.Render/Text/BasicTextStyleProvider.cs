using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Drawing.Text.FontHelper;
using projectFrameCut.Drawing.Text.Typology;
using projectFrameCut.Render.ClipsAndTracks.Text;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Text;
using projectFrameCut.Shared;
using System.Globalization;
using System.Diagnostics;
using Color = projectFrameCut.Render.Text.TextStyleColor;
using Colors = projectFrameCut.Render.Text.TextStyleColors;
using TextAlignment = projectFrameCut.Drawing.Text.Entry.TextAlignment;

namespace projectFrameCut.Render.Text
{
    public class BasicTextStyleProvider : TextClipStyleProviderBase
    {
        protected const string TextKey = "Text";
        protected const string FontKey = "FontFamily";
        protected const string SizeKey = "FontSize";
        protected const string ColorKey = "Color";
        protected const string FontStyleKey = "FontStyle";
        protected const string HorizontalAlignmentKey = "HorizontalAlignment";
        protected const string VerticalAlignmentKey = "VerticalAlignment";
        protected const string WrappingWidthKey = "WrappingWidth";
        protected const string ApplyKerningKey = "ApplyKerning";
        protected const string LineSpacingKey = "LineSpacing";
        protected const string RotationKey = "Rotation";
        protected const string StrokeWidthKey = "StrokeWidth";
        protected const string StrokeColorKey = "StrokeColor";
        protected const string DpiKey = "Dpi";
        protected const string UseVerticalLayoutKey = "UseVerticalLayout";
        protected const string KeepNonCJKTextAsHorizontalKey = "KeepNonCJKTextAsHorizontal";
        public const string ManualSizeKey = "TextStyleManualSize";
        public const string LayoutModeKey = "LayoutMode";
        protected const string FixedHeightValueKey = "FixedHeightValue";

        private TextStyleParameters? parameterView;
        private TextStyleParameters _parameters => parameterView ??= this.GetParameters();

        public BasicTextStyleProvider()
        {
            BasicText = DefaultText;
            EnsureDefaults();
        }

        public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public override string TypeName => "Basic";

        protected virtual string DefaultText => "Text";

        protected virtual float DefaultFontSize => 120f;

        public override string BasicText
        {
            get => _parameters.TryGetValue(TextKey, out var text) ? text : DefaultText;
            set => _parameters[TextKey] = value;
        }

        public override bool AllowFreeRatioResize => LayoutMode switch
        {
            TextClipLayoutMode.FixedSize => false,
            _ => true,
        };

        public override bool IsHorizontalResizable => LayoutMode switch
        {
            TextClipLayoutMode.FixedSize or TextClipLayoutMode.FixedHeight => false,
            _ => true,
        };

        public override bool IsVerticalResizable => LayoutMode switch
        {
            TextClipLayoutMode.FixedSize or TextClipLayoutMode.FixedWidth => false,
            _ => true,
        };

        public override bool CanSnapWhileResizing => false;

        public override TextClipLayoutMode LayoutMode
        {
            get => ParseLayoutMode(GetOrDefault(LayoutModeKey, "FillClip"), TextClipLayoutMode.FillClip);
            set
            {
                var oldMode = LayoutMode;
                _parameters[LayoutModeKey] = value.ToString();

                if (oldMode != value && value == TextClipLayoutMode.FixedWidth)
                {
                    if (!_parameters.ContainsKey(WrappingWidthKey) || string.IsNullOrWhiteSpace(_parameters[WrappingWidthKey]))
                    {
                        var measured = TextStyleRenderHelper.MeasureBounds(BuildEntries(), 1920, 1080);
                        _parameters[WrappingWidthKey] = Math.Max(100, (int)Math.Ceiling(measured.Width)).ToString(CultureInfo.InvariantCulture);
                    }
                }
                else if (oldMode != value && value == TextClipLayoutMode.FixedHeight)
                {
                    if (!_parameters.ContainsKey(FixedHeightValueKey) || string.IsNullOrWhiteSpace(_parameters[FixedHeightValueKey]))
                    {
                        var measured = TextStyleRenderHelper.MeasureBounds(BuildEntries(), 1920, 1080);
                        _parameters[FixedHeightValueKey] = Math.Max(20, (int)Math.Ceiling(measured.Height)).ToString(CultureInfo.InvariantCulture);
                    }
                }
            }
        }

        private Dictionary<string, EffectArgumentFieldDescriptor>? settableFields;
        public override Dictionary<string, EffectArgumentFieldDescriptor> SettableFields => settableFields ??= CreateFields();

        private Dictionary<string, EffectArgumentFieldDescriptor> CreateFields()
        {
            return new Dictionary<string, EffectArgumentFieldDescriptor>
            {
                [TextKey] = TextStyleFieldHelper.StringField(TextKey, "Text", "Text content", DefaultText),
                [FontKey] = TextStyleFieldHelper.EnumField(FontKey, "Font Family", "Font used to render the text", "HarmonyOS Sans SC Medium", Array.Empty<string>()),
                [SizeKey] = TextStyleFieldHelper.NumericField(SizeKey, "Font Size", "Font size in canvas pixels", DefaultFontSize, 1f),
                [ColorKey] = TextStyleFieldHelper.ColorField(ColorKey, "Text Color", "Text fill color", "#FFFFFF"),
                [FontStyleKey] = TextStyleFieldHelper.EnumField(FontStyleKey, "Font Style", "Font weight and slant", ClipFontStyle.Regular.ToString(), Enum.GetNames<ClipFontStyle>()),
                [HorizontalAlignmentKey] = TextStyleFieldHelper.EnumField(HorizontalAlignmentKey, "Horizontal Alignment", "Horizontal text alignment", ClipHorizontalAlignment.Left.ToString(), Enum.GetNames<ClipHorizontalAlignment>()),
                [VerticalAlignmentKey] = TextStyleFieldHelper.EnumField(VerticalAlignmentKey, "Vertical Alignment", "Vertical text alignment", ClipVerticalAlignment.Top.ToString(), Enum.GetNames<ClipVerticalAlignment>()),
                [LayoutModeKey] = TextStyleFieldHelper.EnumField(LayoutModeKey, "Layout Mode", "How text is sized relative to the clip", TextClipLayoutMode.FillClip.ToString(), GetSettableLayoutModes()),
                [FixedHeightValueKey] = TextStyleFieldHelper.NumericField(FixedHeightValueKey, "Fixed Height", "Fixed text layout height in pixels", null, 1f, mandatory: false),
                [WrappingWidthKey] = TextStyleFieldHelper.NumericField(WrappingWidthKey, "Wrapping Width", "Optional text wrapping width in canvas pixels", null, 1f, mandatory: false),
                [ApplyKerningKey] = TextStyleFieldHelper.BooleanField(ApplyKerningKey, "Apply Kerning", "Apply font kerning", true),
                [LineSpacingKey] = TextStyleFieldHelper.NumericField(LineSpacingKey, "Line Spacing", "Line spacing multiplier", 1f),
                [RotationKey] = TextStyleFieldHelper.NumericField(RotationKey, "Rotation", "Text rotation in degrees", 0f),
                [StrokeWidthKey] = TextStyleFieldHelper.NumericField(StrokeWidthKey, "Stroke Width", "Optional text stroke width", null, 0f, mandatory: false),
                [StrokeColorKey] = TextStyleFieldHelper.ColorField(StrokeColorKey, "Stroke Color", "Text stroke color", "#000000"),
                [DpiKey] = TextStyleFieldHelper.NumericField(DpiKey, "DPI", "Optional text rendering DPI", null, 1f, mandatory: false),
                [UseVerticalLayoutKey] = TextStyleFieldHelper.BooleanField(UseVerticalLayoutKey, "Use Vertical Layout", "Lay out text vertically", false),
                [KeepNonCJKTextAsHorizontalKey] = TextStyleFieldHelper.BooleanField(KeepNonCJKTextAsHorizontalKey, "Keep Non-CJK Text Horizontal", "Keep non-CJK runs horizontal in vertical layout", false)
            };
        }

        private static string[] GetSettableLayoutModes() =>
        [
            TextClipLayoutMode.FillClip.ToString(),
            TextClipLayoutMode.FixedWidth.ToString(),
            TextClipLayoutMode.FixedHeight.ToString(),
            TextClipLayoutMode.FixedSize.ToString()
        ];

        /// <summary>
        /// Build TextEntry array. FontSize/X/Y/StrokeThickness are stored in pixel space
        /// and must be normalized via <see cref="TextEntryHelper.NormalizeForTypesetting"/>
        /// before passing to the typesetting engine.
        /// </summary>
        public override TextEntry[] BuildEntries()
        {
            var fontFamily = GetOrDefault(FontKey, "HarmonyOS Sans SC Medium");
            var fontSize = ParseFloat(GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)), DefaultFontSize);
            var colorText = GetOrDefault(ColorKey, "#FFFFFF");
            var color = ParseColorOrFallback(colorText, Colors.White);
            var fontStyle = ParseFontStyle(GetOrDefault(FontStyleKey, ClipFontStyle.Regular.ToString()), ClipFontStyle.Regular);
            var horizontalAlignment = ParseHorizontalAlignment(GetOrDefault(HorizontalAlignmentKey, ClipHorizontalAlignment.Left.ToString()), ClipHorizontalAlignment.Left);
            var verticalAlignment = ParseVerticalAlignment(GetOrDefault(VerticalAlignmentKey, ClipVerticalAlignment.Top.ToString()), ClipVerticalAlignment.Top);

            var text = BasicText;
            float? wrappingWidth = null;

            if (LayoutMode == TextClipLayoutMode.FixedWidth)
            {
                var ww = ParseNullableFloat(GetOrDefault(WrappingWidthKey, string.Empty));
                if (ww.HasValue && ww.Value > 0 && !string.IsNullOrEmpty(text))
                {
                    wrappingWidth = ww;
                }
            }
            else if (LayoutMode == TextClipLayoutMode.FixedHeight)
            {
                wrappingWidth = ParseNullableFloat(GetOrDefault(WrappingWidthKey, string.Empty));
            }

            var applyKerning = ParseBool(GetOrDefault(ApplyKerningKey, bool.TrueString), true);
            var lineSpacing = ParseFloat(GetOrDefault(LineSpacingKey, 1f.ToString(CultureInfo.InvariantCulture)), 1f);
            var rotation = ParseFloat(GetOrDefault(RotationKey, 0f.ToString(CultureInfo.InvariantCulture)), 0f);
            var strokeWidth = ParseNullableFloat(GetOrDefault(StrokeWidthKey, string.Empty));
            var strokeColor = ParseColorOrFallback(GetOrDefault(StrokeColorKey, "#000000"), Colors.Black);
            var dpi = ParseNullableFloat(GetOrDefault(DpiKey, string.Empty));
            var useVerticalLayout = ParseBool(GetOrDefault(UseVerticalLayoutKey, bool.FalseString), false);
            var keepNonCJKTextAsHorizontal = ParseBool(GetOrDefault(KeepNonCJKTextAsHorizontalKey, bool.FalseString), false);

            var scaleWithTarget = LayoutMode == TextClipLayoutMode.FillClip;

            var entry = new TextEntry
            {
                Text = text,
                FontName = fontFamily,
                FontSize = fontSize,
                X = 0,
                Y = 0,
                FontStyle = fontStyle.ToString(),
                FillR = (ushort)Math.Round(color.Red * 65535),
                FillG = (ushort)Math.Round(color.Green * 65535),
                FillB = (ushort)Math.Round(color.Blue * 65535),
                FillA = (float)color.Alpha,
                Alignment = horizontalAlignment switch
                {
                    ClipHorizontalAlignment.Center => TextAlignment.Center,
                    ClipHorizontalAlignment.Right => TextAlignment.Right,
                    _ => TextAlignment.Left,
                },
                LineSpacing = lineSpacing - 1f,
                Rotation = rotation * MathF.PI / 180f,
                StrokeR = (ushort)Math.Round(strokeColor.Red * 65535),
                StrokeG = (ushort)Math.Round(strokeColor.Green * 65535),
                StrokeB = (ushort)Math.Round(strokeColor.Blue * 65535),
                StrokeThickness = strokeWidth ?? 0f,
                StrokeA = strokeWidth.HasValue && strokeWidth.Value > 0f ? 1f : 0f,
            };
            entry.SetUseVerticalLayout(useVerticalLayout);
            entry.SetKeepNonCJKTextAsHorizontal(keepNonCJKTextAsHorizontal);
            entry.SetWrappingWidth(wrappingWidth);
            entry.SetScaleWithTarget(scaleWithTarget);
            entry.SetDpi(dpi);
            entry.SetVerticalAlignment(verticalAlignment);
            entry.SetLayoutMode(LayoutMode);
            if (LayoutMode == TextClipLayoutMode.FixedHeight)
            {
                var fhValue = ParseNullableFloat(GetOrDefault(FixedHeightValueKey, string.Empty));
                if (fhValue.HasValue && fhValue.Value > 0f)
                    entry.SetFixedHeightValue(fhValue.Value);
            }

            return [entry];
        }

        public override void HandleClipResize(bool isInRatio, int TargetX, int TargetY, int TargetWidth, int TargetHeight)
        {
            EnsureDefaults();

            if (TargetWidth <= 0 || TargetHeight <= 0)
                return;

            switch (LayoutMode)
            {
                case TextClipLayoutMode.FixedSize:
                    break;

                case TextClipLayoutMode.FixedWidth:
                    _parameters[WrappingWidthKey] = TargetWidth.ToString(CultureInfo.InvariantCulture);
                    break;

                case TextClipLayoutMode.FixedHeight:
                    // In the new pipeline, FixedHeight means "fit the font size
                    // to the box height". The actual font-size fit happens at
                    // render time via TextLayoutModeResolver — we just record
                    // the desired height here. Wrapping width, if set, is
                    // preserved untouched.
                    _parameters[FixedHeightValueKey] = TargetHeight.ToString(CultureInfo.InvariantCulture);
                    break;

                default:
                    // FillClip — scale the font size by the height ratio so the
                    // rendered glyphs grow/shrink with the box.
                    var currentRect = GetViewRect(TargetWidth, TargetHeight);
                    if (currentRect.TargetWidth <= 0 || currentRect.TargetHeight <= 0)
                        return;

                    var currentFontSize = ParseFloat(
                        GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)),
                        DefaultFontSize);

                    var scaleY = (double)TargetHeight / currentRect.TargetHeight;
                    if (double.IsNaN(scaleY) || double.IsInfinity(scaleY) || scaleY <= 0)
                        return;

                    var updatedSize = (float)(currentFontSize * scaleY);
                    if (updatedSize > 0)
                        _parameters[SizeKey] = updatedSize.ToString(CultureInfo.InvariantCulture);
                    break;
            }

        }

        public override ClipPositionTuple GetViewRect(int canvasWidth, int canvasHeight)
        {
            var entries = BuildEntries();
            if (entries.Length == 0)
                return new ClipPositionTuple(0, 0, Math.Max(1, canvasWidth), Math.Max(1, canvasHeight), false);

            try
            {
                // Entries are in project-pixel space. Measure them with the
                // single canonical pipeline against the requested canvas.
                var rect = TextStyleRenderHelper.MeasureBounds(entries, canvasWidth, canvasHeight);

                if (LayoutMode == TextClipLayoutMode.FixedWidth)
                {
                    var ww = ParseNullableFloat(GetOrDefault(WrappingWidthKey, string.Empty));
                    if (ww.HasValue && ww.Value > 0)
                    {
                        return new ClipPositionTuple(
                            (int)Math.Round(rect.X),
                            (int)Math.Round(rect.Y),
                            (int)Math.Ceiling(ww.Value),
                            Math.Max(1, (int)Math.Ceiling(rect.Height)),
                            false);
                    }
                }

                if (LayoutMode == TextClipLayoutMode.FixedHeight)
                {
                    var fixedH = ParseFloat(GetOrDefault(FixedHeightValueKey, "0"), 0);
                    if (fixedH > 0)
                    {
                        return new ClipPositionTuple(
                            (int)Math.Round(rect.X),
                            (int)Math.Round(rect.Y),
                            Math.Max(1, (int)Math.Ceiling(rect.Width)),
                            (int)Math.Ceiling(fixedH),
                            false);
                    }
                }

                return new ClipPositionTuple(
                    (int)Math.Round(rect.X),
                    (int)Math.Round(rect.Y),
                    Math.Max(1, (int)Math.Ceiling(rect.Width)),
                    Math.Max(1, (int)Math.Ceiling(rect.Height)),
                    false);
            }
            catch
            {
                return new ClipPositionTuple(0, 0, Math.Max(1, canvasWidth), Math.Max(1, canvasHeight), false);
            }
        }

        private void EnsureDefaults()
        {
            if (!_parameters.ContainsKey(TextKey)) _parameters[TextKey] = DefaultText;
            if (!_parameters.ContainsKey(FontKey)) _parameters[FontKey] = "HarmonyOS Sans SC Medium";
            if (!_parameters.ContainsKey(SizeKey)) _parameters[SizeKey] = DefaultFontSize.ToString(CultureInfo.InvariantCulture);
            if (!_parameters.ContainsKey(ColorKey)) _parameters[ColorKey] = "#FFFFFF";
            if (!_parameters.ContainsKey(LayoutModeKey)) _parameters[LayoutModeKey] = "FillClip";
        }

        [DebuggerStepThrough()]
        private string GetOrDefault(string key, string fallback)
        {
            return _parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : fallback;
        }

        [DebuggerStepThrough()]
        private static TextClipLayoutMode ParseLayoutMode(string? value, TextClipLayoutMode fallback)
        {
            return Enum.TryParse<TextClipLayoutMode>(value, true, out var parsed) ? parsed : fallback;
        }

        [DebuggerStepThrough()]
        private static float ParseFloat(string? value, float fallback)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
        }

        private static bool ParseBool(object? value, bool fallback)
        {
            if (value is bool b) return b;
            if (bool.TryParse(value?.ToString(), out var parsed)) return parsed;
            return fallback;
        }

        private static float? ParseNullableFloat(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }

        private static ClipFontStyle ParseFontStyle(string? value, ClipFontStyle fallback)
        {
            return Enum.TryParse<ClipFontStyle>(value, true, out var parsed) ? parsed : fallback;
        }

        private static ClipHorizontalAlignment ParseHorizontalAlignment(string? value, ClipHorizontalAlignment fallback)
        {
            return Enum.TryParse<ClipHorizontalAlignment>(value, true, out var parsed) ? parsed : fallback;
        }

        private static ClipVerticalAlignment ParseVerticalAlignment(string? value, ClipVerticalAlignment fallback)
        {
            return Enum.TryParse<ClipVerticalAlignment>(value, true, out var parsed) ? parsed : fallback;
        }

        private static Color ParseColorOrFallback(string? value, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            try { return Color.FromArgb(value); }
            catch { return fallback; }
        }

    }
    public sealed class TitleTextStyleProvider : BasicTextStyleProvider
    {
        public override string TypeName => "Title";
        protected override string DefaultText => "Title";
        protected override float DefaultFontSize => 140f;
    }
}
