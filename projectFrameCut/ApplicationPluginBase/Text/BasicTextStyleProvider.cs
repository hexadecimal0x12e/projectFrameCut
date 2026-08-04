using Microsoft.Maui.Graphics;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationAPIBase.Views.Pickers;
using projectFrameCut.ApplicationPluginBase.DynamicPreviewProvider;
using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Drawing.Text.FontHelper;
using projectFrameCut.Drawing.Text.Typology;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using Microsoft.Maui.Controls.Shapes;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;
using System.Diagnostics;
using TextAlignment = projectFrameCut.Drawing.Text.Entry.TextAlignment;

namespace projectFrameCut.ApplicationPluginBase.Text
{
    public class BasicTextStyleProvider : ITextClipStyleProvider
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

        private Dictionary<string, string> _parameters = new();

        public BasicTextStyleProvider()
        {
            BasicText = DefaultText;
            EnsureDefaults();
        }

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public virtual string TypeName => "Basic";

        protected virtual string DefaultText => "Text";

        protected virtual float DefaultFontSize => 120f;

        public string BasicText { get; set; }

        public Dictionary<string, string> Parameters
        {
            get => _parameters;
            set
            {
                _parameters = value ?? new Dictionary<string, string>();
                EnsureDefaults();
                if (_parameters.TryGetValue(TextKey, out var t) && !string.IsNullOrWhiteSpace(t))
                    BasicText = t;
            }
        }

        public bool AllowFreeRatioResize => LayoutMode switch
        {
            TextClipLayoutMode.FixedSize => false,
            _ => true,
        };

        public bool IsHorizontalResizable => LayoutMode switch
        {
            TextClipLayoutMode.FixedSize or TextClipLayoutMode.FixedHeight => false,
            _ => true,
        };

        public bool IsVerticalResizable => LayoutMode switch
        {
            TextClipLayoutMode.FixedSize or TextClipLayoutMode.FixedWidth => false,
            _ => true,
        };

        public bool CanSnapWhileResizing => false;

        public TextClipLayoutMode LayoutMode
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
                        var measured = TextMeasureHelper.MeasureBounds(BuildEntries(), 1920, 1080);
                        _parameters[WrappingWidthKey] = Math.Max(100, (int)Math.Ceiling(measured.Width)).ToString(CultureInfo.InvariantCulture);
                    }
                }
                else if (oldMode != value && value == TextClipLayoutMode.FixedHeight)
                {
                    if (!_parameters.ContainsKey(FixedHeightValueKey) || string.IsNullOrWhiteSpace(_parameters[FixedHeightValueKey]))
                    {
                        var measured = TextMeasureHelper.MeasureBounds(BuildEntries(), 1920, 1080);
                        _parameters[FixedHeightValueKey] = Math.Max(20, (int)Math.Ceiling(measured.Height)).ToString(CultureInfo.InvariantCulture);
                    }
                }
            }
        }

        public Dictionary<string, EffectArgumentFieldDescriptor> SettableFields
        {
            get
            {
                var fontNames = TextStyleProviderSettableFieldHelper.GetAvailableFontNames();
                return new Dictionary<string, EffectArgumentFieldDescriptor>
                {
                    [TextKey] = TextStyleProviderSettableFieldHelper.StringField(TextKey, "Text", "Text content", DefaultText),
                    [FontKey] = TextStyleProviderSettableFieldHelper.EnumField(FontKey, "Font Family", "Font used to render the text", "HarmonyOS Sans SC Medium", fontNames),
                    [SizeKey] = TextStyleProviderSettableFieldHelper.NumericField(SizeKey, "Font Size", "Font size in canvas pixels", DefaultFontSize, 1f),
                    [ColorKey] = TextStyleProviderSettableFieldHelper.ColorField(ColorKey, "Text Color", "Text fill color", "#FFFFFF"),
                    [FontStyleKey] = TextStyleProviderSettableFieldHelper.EnumField(FontStyleKey, "Font Style", "Font weight and slant", ClipFontStyle.Regular.ToString(), Enum.GetNames<ClipFontStyle>()),
                    [HorizontalAlignmentKey] = TextStyleProviderSettableFieldHelper.EnumField(HorizontalAlignmentKey, "Horizontal Alignment", "Horizontal text alignment", ClipHorizontalAlignment.Left.ToString(), Enum.GetNames<ClipHorizontalAlignment>()),
                    [VerticalAlignmentKey] = TextStyleProviderSettableFieldHelper.EnumField(VerticalAlignmentKey, "Vertical Alignment", "Vertical text alignment", ClipVerticalAlignment.Top.ToString(), Enum.GetNames<ClipVerticalAlignment>()),
                    [LayoutModeKey] = TextStyleProviderSettableFieldHelper.EnumField(LayoutModeKey, "Layout Mode", "How text is sized relative to the clip", TextClipLayoutMode.FillClip.ToString(), GetSettableLayoutModes()),
                    [WrappingWidthKey] = TextStyleProviderSettableFieldHelper.NumericField(WrappingWidthKey, "Wrapping Width", "Optional text wrapping width in canvas pixels", null, 1f, mandatory: false),
                    [ApplyKerningKey] = TextStyleProviderSettableFieldHelper.BooleanField(ApplyKerningKey, "Apply Kerning", "Apply font kerning", true),
                    [LineSpacingKey] = TextStyleProviderSettableFieldHelper.NumericField(LineSpacingKey, "Line Spacing", "Line spacing multiplier", 1f),
                    [RotationKey] = TextStyleProviderSettableFieldHelper.NumericField(RotationKey, "Rotation", "Text rotation in degrees", 0f),
                    [StrokeWidthKey] = TextStyleProviderSettableFieldHelper.NumericField(StrokeWidthKey, "Stroke Width", "Optional text stroke width", null, 0f, mandatory: false),
                    [StrokeColorKey] = TextStyleProviderSettableFieldHelper.ColorField(StrokeColorKey, "Stroke Color", "Text stroke color", "#000000"),
                    [DpiKey] = TextStyleProviderSettableFieldHelper.NumericField(DpiKey, "DPI", "Optional text rendering DPI", null, 1f, mandatory: false),
                    [UseVerticalLayoutKey] = TextStyleProviderSettableFieldHelper.BooleanField(UseVerticalLayoutKey, "Use Vertical Layout", "Lay out text vertically", false),
                    [KeepNonCJKTextAsHorizontalKey] = TextStyleProviderSettableFieldHelper.BooleanField(KeepNonCJKTextAsHorizontalKey, "Keep Non-CJK Text Horizontal", "Keep non-CJK runs horizontal in vertical layout", false)
                };
            }
        }

        public bool HandleSettableFieldsChange(EffectArgumentFieldDescriptor field, object value, out string feedback)
        {
            if (field is null || !SettableFields.TryGetValue(field.Id, out var canonicalField))
            {
                feedback = field is null
                    ? "Field definition is null."
                    : $"Unknown settable field '{field.Id}' for text style '{TypeName}'.";
                return false;
            }

            if (!TextStyleProviderSettableFieldHelper.TryNormalizeValue(
                canonicalField, value, out var normalizedValue, out feedback))
            {
                return false;
            }

            if (canonicalField.Id == LayoutModeKey)
            {
                LayoutMode = Enum.Parse<TextClipLayoutMode>((string)normalizedValue);
            }
            else
            {
                HandlePropertyPanelChange(new PropertyPanelPropertyChangedEventArgs(
                    canonicalField.Id, normalizedValue, null));
            }

            feedback = "";
            return true;
        }

        private static string[] GetSettableLayoutModes() =>
        [
            TextClipLayoutMode.FillClip.ToString(),
            TextClipLayoutMode.FixedWidth.ToString(),
            TextClipLayoutMode.FixedSize.ToString()
        ];

        /// <summary>
        /// Build TextEntry array. FontSize/X/Y/StrokeThickness are stored in pixel space
        /// and must be normalized via <see cref="TextEntryHelper.NormalizeForTypesetting"/>
        /// before passing to the typesetting engine.
        /// </summary>
        public TextEntry[] BuildEntries()
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

        Label glyphWarning = new Label
        {
            TextColor = Colors.OrangeRed,
            FontSize = 12,
            IsVisible = false,
            LineBreakMode = LineBreakMode.WordWrap
        };

        void UpdateGlyphWarning()
        {
            var warning = TextServices.GetMissingGlyphWarning(GetOrDefault(FontKey, "HarmonyOS Sans SC Medium"), BasicText, ParseFloat(GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)), DefaultFontSize));
            glyphWarning.Text = warning;
            glyphWarning.IsVisible = !string.IsNullOrWhiteSpace(warning);
        }

        public PropertyPanelBuilder BuildPropertyPanel()
        {
            var panel = new PropertyPanelBuilder();
            var fontSize = ParseFloat(GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)), DefaultFontSize);
            var currentFontStyle = ParseFontStyle(GetOrDefault(FontStyleKey, ClipFontStyle.Regular.ToString()), ClipFontStyle.Regular);

            panel.AddCustomChild(glyphWarning);
            // BasicText editor is managed centrally by ClipInfoBuilder.
            UpdateGlyphWarning();
            panel.AddEntry(SizeKey, PPLocalizedResources.TextOption_Size, fontSize.ToString(), "18", c => { c.Keyboard = Keyboard.Numeric; if (LayoutMode is TextClipLayoutMode.FillClip or TextClipLayoutMode.FixedHeight) { c.IsReadOnly = true; c.TextColor = Colors.Gray; } }, EntryUpdateEventCallMode.OnUnfocusedAndValueChanged);
            panel.AddPicker(FontStyleKey, PPLocalizedResources.TextOption_Style, new[] { ClipFontStyle.Regular.ToString(), ClipFontStyle.Bold.ToString(), ClipFontStyle.Italic.ToString(), ClipFontStyle.BoldItalic.ToString() }, currentFontStyle.ToString());
            panel.AddCustomChild(PPLocalizedResources.TextOption_Color, invoker => BuildColorPickerField(
                ColorKey,
                GetOrDefault(ColorKey, "#FFFFFF"),
                invoker), ColorKey, GetOrDefault(ColorKey, "#FFFFFF"));
            panel.AddPicker(HorizontalAlignmentKey, PPLocalizedResources.TextOption_HorizonOption, new[] { ClipHorizontalAlignment.Left.ToString(), ClipHorizontalAlignment.Center.ToString(), ClipHorizontalAlignment.Right.ToString() }, GetOrDefault(HorizontalAlignmentKey, ClipHorizontalAlignment.Left.ToString()));
            panel.AddPicker(VerticalAlignmentKey, PPLocalizedResources.TextOption_VerticalOption, new[] { ClipVerticalAlignment.Top.ToString(), ClipVerticalAlignment.Center.ToString(), ClipVerticalAlignment.Bottom.ToString() }, GetOrDefault(VerticalAlignmentKey, ClipVerticalAlignment.Top.ToString()));
            panel.AddEntry(WrappingWidthKey, PPLocalizedResources.TextOption_WrapW, GetOrDefault(WrappingWidthKey, string.Empty), PPLocalizedResources.TextOption_WrapW_Hint);
            panel.AddSwitch(ApplyKerningKey, PPLocalizedResources.TextOption_Kerning, ParseBool(GetOrDefault(ApplyKerningKey, bool.TrueString), true));
            panel.AddEntry(LineSpacingKey, PPLocalizedResources.TextOption_LineSpacing, GetOrDefault(LineSpacingKey, 1f.ToString(CultureInfo.InvariantCulture)), "1.0");
            panel.AddEntry(RotationKey, PPLocalizedResources.TextOption_Rotation, GetOrDefault(RotationKey, 0f.ToString(CultureInfo.InvariantCulture)), "0");
            panel.AddEntry(StrokeWidthKey, PPLocalizedResources.TextOption_Stroke, GetOrDefault(StrokeWidthKey, string.Empty), PPLocalizedResources.TextOption_Stroke_Hint);
            panel.AddCustomChild(PPLocalizedResources.TextOption_Stroke, invoker => BuildColorPickerField(
                StrokeColorKey,
                GetOrDefault(StrokeColorKey, "#000000"),
                invoker), StrokeColorKey, GetOrDefault(StrokeColorKey, "#000000"));
            panel.AddEntry(DpiKey, "DPI", GetOrDefault(DpiKey, string.Empty), string.Empty);
            panel.AddSwitch(UseVerticalLayoutKey, PPLocalizedResources.TextOption_UseVerticalLayout, ParseBool(GetOrDefault(UseVerticalLayoutKey, bool.FalseString), false));
            panel.AddSwitch(KeepNonCJKTextAsHorizontalKey, PPLocalizedResources.TextOption_KeepNonCJKHorizontal, ParseBool(GetOrDefault(KeepNonCJKTextAsHorizontalKey, bool.FalseString), false));
            return panel;
        }

        public (Dictionary<string, string> newParams, int newWidth, int newHeight) HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            switch (args.Id)
            {
                case TextKey:
                    BasicText = args.Value?.ToString() ?? string.Empty;
                    _parameters[TextKey] = BasicText;
                    UpdateGlyphWarning();
                    break;
                case FontKey:
                    _parameters[FontKey] = args.Value?.ToString() ?? "HarmonyOS Sans SC Medium";
                    UpdateGlyphWarning();
                    break;
                case SizeKey:
                    if (TryParseFloat(args.Value, out var size))
                    {
                        _parameters[SizeKey] = size.ToString(CultureInfo.InvariantCulture);
                        _parameters[ManualSizeKey] = "true";
                    }
                    break;
                case ColorKey:
                    _parameters[ColorKey] = args.Value?.ToString() ?? "#FFFFFF";
                    break;
                case FontStyleKey:
                    _parameters[FontStyleKey] = ParseFontStyle(args.Value?.ToString(), ClipFontStyle.Regular).ToString();
                    break;
                case HorizontalAlignmentKey:
                    _parameters[HorizontalAlignmentKey] = ParseHorizontalAlignment(args.Value?.ToString(), ClipHorizontalAlignment.Left).ToString();
                    break;
                case VerticalAlignmentKey:
                    _parameters[VerticalAlignmentKey] = ParseVerticalAlignment(args.Value?.ToString(), ClipVerticalAlignment.Top).ToString();
                    break;
                case WrappingWidthKey:
                    _parameters[WrappingWidthKey] = args.Value?.ToString() ?? string.Empty;
                    break;
                case ApplyKerningKey:
                    _parameters[ApplyKerningKey] = ParseBool(args.Value, true).ToString();
                    break;
                case LineSpacingKey:
                    if (TryParseFloat(args.Value, out var lineSpacing))
                        _parameters[LineSpacingKey] = lineSpacing.ToString(CultureInfo.InvariantCulture);
                    break;
                case RotationKey:
                    if (TryParseFloat(args.Value, out var rotation))
                        _parameters[RotationKey] = rotation.ToString(CultureInfo.InvariantCulture);
                    break;
                case StrokeWidthKey:
                    if (TryParseFloat(args.Value, out var strokeWidth))
                        _parameters[StrokeWidthKey] = strokeWidth.ToString(CultureInfo.InvariantCulture);
                    else
                        _parameters[StrokeWidthKey] = string.Empty;
                    break;
                case StrokeColorKey:
                    _parameters[StrokeColorKey] = args.Value?.ToString() ?? "#000000";
                    break;
                case DpiKey:
                    if (TryParseFloat(args.Value, out var dpi))
                        _parameters[DpiKey] = dpi.ToString(CultureInfo.InvariantCulture);
                    else
                        _parameters[DpiKey] = string.Empty;
                    break;
                case UseVerticalLayoutKey:
                    _parameters[UseVerticalLayoutKey] = ParseBool(args.Value, false).ToString();
                    break;
                case KeepNonCJKTextAsHorizontalKey:
                    _parameters[KeepNonCJKTextAsHorizontalKey] = ParseBool(args.Value, false).ToString();
                    break;
            }
            var rect = TextMeasureHelper.MeasureBounds(BuildEntries(), 1920, 1080);
            var measuredW = Math.Max(1, (int)Math.Ceiling(rect.Width));
            var measuredH = Math.Max(1, (int)Math.Ceiling(rect.Height));

            switch (LayoutMode)
            {
                case TextClipLayoutMode.FixedWidth:
                    var ww = ParseNullableFloat(GetOrDefault(WrappingWidthKey, string.Empty));
                    if (ww.HasValue && ww.Value > 0)
                        return (_parameters, (int)Math.Ceiling(ww.Value), measuredH);
                    return (_parameters, measuredW, measuredH);
                case TextClipLayoutMode.FixedHeight:
                    var fixedH = ParseFloat(GetOrDefault(FixedHeightValueKey, "0"), 0);
                    var fhWrappingW = ParseNullableFloat(GetOrDefault(WrappingWidthKey, string.Empty));
                    var displayW = fhWrappingW.HasValue && fhWrappingW.Value > 0
                        ? (int)Math.Ceiling(fhWrappingW.Value)
                        : measuredW;
                    if (fixedH > 0)
                        return (_parameters, displayW, (int)Math.Ceiling(fixedH));
                    return (_parameters, displayW, measuredH);
                case TextClipLayoutMode.FixedSize:
                    return (_parameters, measuredW, measuredH);
                default:
                    return (_parameters, measuredW, measuredH);
            }
        }

        public Dictionary<string, string> HandleClipResize(bool isInRatio, int TargetX, int TargetY, int TargetWidth, int TargetHeight)
        {
            EnsureDefaults();

            if (TargetWidth <= 0 || TargetHeight <= 0)
                return new Dictionary<string, string>(_parameters);

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
                        return new Dictionary<string, string>(_parameters);

                    var currentFontSize = ParseFloat(
                        GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)),
                        DefaultFontSize);

                    var scaleY = (double)TargetHeight / currentRect.TargetHeight;
                    if (double.IsNaN(scaleY) || double.IsInfinity(scaleY) || scaleY <= 0)
                        return new Dictionary<string, string>(_parameters);

                    var updatedSize = (float)(currentFontSize * scaleY);
                    if (updatedSize > 0)
                        _parameters[SizeKey] = updatedSize.ToString(CultureInfo.InvariantCulture);
                    break;
            }

            return new Dictionary<string, string>(_parameters);
        }

        public ClipPositionTuple GetViewRect(int canvasWidth, int canvasHeight)
        {
            var entries = BuildEntries();
            if (entries.Length == 0)
                return new ClipPositionTuple(0, 0, Math.Max(1, canvasWidth), Math.Max(1, canvasHeight), false);

            try
            {
                // Entries are in project-pixel space. Measure them with the
                // single canonical pipeline against the requested canvas.
                var rect = TextMeasureHelper.MeasureBounds(entries, canvasWidth, canvasHeight);

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

        private (float Width, float Height) MeasureNaturalSize()
        {
            var hadWrapping = _parameters.TryGetValue(WrappingWidthKey, out var saved);
            _parameters.Remove(WrappingWidthKey);
            try
            {
                var entries = BuildEntries();
                var rect = TextMeasureHelper.MeasureBounds(entries, 1920, 1080);
                return ((float)rect.Width, (float)rect.Height);
            }
            finally
            {
                if (hadWrapping)
                    _parameters[WrappingWidthKey] = saved;
            }
        }

        private float MeasureTextHeightAtWidth(float wrappingWidth)
        {
            var hadWrapping = _parameters.TryGetValue(WrappingWidthKey, out var saved);
            _parameters[WrappingWidthKey] = wrappingWidth.ToString(CultureInfo.InvariantCulture);
            try
            {
                var entries = BuildEntries();
                var rect = TextMeasureHelper.MeasureBounds(entries, 1920, 1080);
                return (float)rect.Height;
            }
            finally
            {
                _parameters.Remove(WrappingWidthKey);
                if (hadWrapping)
                    _parameters[WrappingWidthKey] = saved;
            }
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

        private static bool TryParseFloat(object? value, out float result)
        {
            if (value is float f) { result = f; return true; }
            if (value is double d) { result = (float)d; return true; }
            if (float.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            { result = parsed; return true; }
            result = 0f;
            return false;
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

        private View BuildColorPickerField(string parameterKey, string defaultHex, Action<object> invoker)
        {
            var currentHex = GetOrDefault(parameterKey, defaultHex);
            var currentColor = ParseColorOrFallback(currentHex, Colors.White);
            var swatch = new Border
            {
                WidthRequest = 24,
                HeightRequest = 24,
                StrokeShape = new RoundRectangle { CornerRadius = 5 },
                StrokeThickness = 1,
                Stroke = Colors.White.WithAlpha(0.2f),
                Background = new SolidColorBrush(currentColor),
                VerticalOptions = LayoutOptions.Center
            };
            var valueLabel = new Label
            {
                Text = currentHex,
                TextColor = Colors.White,
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.TailTruncation
            };

            async Task OpenPickerAsync()
            {
                var selectedColor = currentColor;
                var picker = new ColorPicker { SelectedColor = selectedColor };
                var pickerPopup = new Popup
                {
                    CanBeDismissedByTappingOutsideOfPopup = true,
                    Content = new Border
                    {
                        StrokeShape = new RoundRectangle { CornerRadius = 12 },
                        Stroke = Colors.White.WithAlpha(0.12f),
                        Background = new SolidColorBrush(Color.FromArgb("#1F2023")),
                        Padding = new Thickness(12),
                        Content = new VerticalStackLayout
                        {
                            Spacing = 10,
                            Children =
                            {
                                picker,
                                new HorizontalStackLayout
                                {
                                    Spacing = 8,
                                    Children =
                                    {
                                        new Button { Text = Localized._Cancel },
                                        new Button { Text = Localized._OK }
                                    }
                                }
                            }
                        }
                    }
                };

                if (pickerPopup.Content is Border popupBorder &&
                    popupBorder.Content is VerticalStackLayout popupStack &&
                    popupStack.Children.Count >= 2 &&
                    popupStack.Children[1] is HorizontalStackLayout buttonRow &&
                    buttonRow.Children.Count >= 2 &&
                    buttonRow.Children[0] is Button cancelButton &&
                    buttonRow.Children[1] is Button okButton)
                {
                    void ApplyColor(Color color)
                    {
                        selectedColor = color;
                        currentColor = color;
                        var hex = ToArgbHex(color);
                        _parameters[parameterKey] = hex;
                        swatch.Background = new SolidColorBrush(color);
                        valueLabel.Text = hex;
                        invoker(hex);
                    }

                    picker.SelectedColorChanged += (_, color) => ApplyColor(color);
                    okButton.Clicked += async (_, _) => { ApplyColor(picker.SelectedColor); await pickerPopup.CloseAsync(); };
                    cancelButton.Clicked += async (_, _) => await pickerPopup.CloseAsync();

                    if (Shell.Current.CurrentPage is DraftPage d)
                        await d.ShowAPopup(picker, null, null, "dialog");
                }
            }

            var tapSurface = new Border
            {
                StrokeShape = new RoundRectangle { CornerRadius = 8 },
                Stroke = Colors.White.WithAlpha(0.12f),
                Background = new SolidColorBrush(Color.FromArgb("#1AFFFFFF")),
                Padding = new Thickness(10, 8),
                Content = new HorizontalStackLayout { Spacing = 10, Children = { swatch, valueLabel } }
            };
            tapSurface.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(async () => await OpenPickerAsync())
            });
            return tapSurface;
        }

        private static string ToArgbHex(Color color)
        {
            var a = (int)Math.Round(color.Alpha * 255);
            var r = (int)Math.Round(color.Red * 255);
            var g = (int)Math.Round(color.Green * 255);
            var b = (int)Math.Round(color.Blue * 255);
            return $"#{a:X2}{r:X2}{g:X2}{b:X2}";
        }
    }

    public sealed class TitleTextStyleProvider : BasicTextStyleProvider
    {
        public override string TypeName => "Title";
        protected override string DefaultText => "Title";
        protected override float DefaultFontSize => 140f;
    }
}
