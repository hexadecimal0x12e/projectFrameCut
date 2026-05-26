using Microsoft.Maui.Graphics;
using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Services;
using TinyPinyin;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using projectFrameCut.ApplicationAPIBase.Helpers;

namespace projectFrameCut.ApplicationPluginBase.Text
{
    public class PinyinTextStyleProvider : ITextClipStyleProvider
    {
        protected const string TextKey = "Text";
        protected const string FontKey = "FontFamily";
        protected const string SizeKey = "FontSize";
        protected const string ColorKey = "Color";
        protected const string PinyinFontSizeRatioKey = "PinyinFontSizeRatio";
        protected const string PinyinColorKey = "PinyinColor";
        protected const string SpacingKey = "CharSpacing";
        public const string ManualSizeKey = "PinyinManualSize";

        private Dictionary<string, string> _parameters = new();

        public PinyinTextStyleProvider()
        {
            EnsureDefaults();
        }

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public virtual string TypeName => "Pinyin";

        protected virtual string DefaultText => "拼音";

        protected virtual float DefaultFontSize => 64f;

        protected virtual float DefaultPinyinFontSizeRatio => 0.4f;

        protected virtual int DefaultSpacing => 8;

        public string BasicText
        {
            get => GetOrDefault(TextKey, DefaultText);
            set => _parameters[TextKey] = value ?? string.Empty;
        }

        public Dictionary<string, string> Parameters
        {
            get => _parameters;
            set
            {
                _parameters = value ?? new Dictionary<string, string>();
                EnsureDefaults();
            }
        }

        public bool AllowFreeRatioResize => true;

        public TextClipEntry[] BuildEntries()
        {
            var text = BasicText;
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<TextClipEntry>();

            var fontFamily = GetOrDefault(FontKey, "Arial");
            var fontSize = ParseFloat(GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)), DefaultFontSize);
            var pinyinRatio = ParseFloat(GetOrDefault(PinyinFontSizeRatioKey, DefaultPinyinFontSizeRatio.ToString(CultureInfo.InvariantCulture)), DefaultPinyinFontSizeRatio);
            var pinyinFontSize = fontSize * pinyinRatio;
            var charSpacing = ParseInt(GetOrDefault(SpacingKey, DefaultSpacing.ToString()), DefaultSpacing);
            var colorText = GetOrDefault(ColorKey, "#FFFFFF");
            var pinyinColorText = GetOrDefault(PinyinColorKey, colorText);
            var color = ParseColorOrFallback(colorText, Colors.White);
            var pinyinColor = ParseColorOrFallback(pinyinColorText, color);

            var baseLineY = (int)(pinyinFontSize * 1.3f + 2);
            var entries = new List<TextClipEntry>();
            var currentX = 0;
            var sentenceLanguage = TextHelper.DetectTextLanguage(text);
            foreach (var c in text)
            {
                var isCJK = IsCJKCharacter(c);
                if (isCJK)
                {
                    var rawPinyin = TaskHelper.SyncWait(() => TextServices.GetHowToPronuce(c.ToString(), sentenceLanguage), cancellationToken: CancellationToken.None);
                    var isKnown = rawPinyin.Length > 0 && rawPinyin != c.ToString();
                    if (isKnown)
                    {
                        var estCharWidth = (int)(fontSize * 0.7f);
                        var estPinyinWidth = (int)(rawPinyin.Length * pinyinFontSize * 0.6f);
                        var columnWidth = Math.Max(estCharWidth, estPinyinWidth) + charSpacing;
                        var centerX = currentX + columnWidth / 2;

                        entries.Add(new TextClipEntry
                        {
                            text = rawPinyin,
                            x = centerX,
                            y = 0,
                            fontFamily = fontFamily,
                            fontSize = pinyinFontSize,
                            horizontalAlignment = SixLabors.Fonts.HorizontalAlignment.Center,
                            r = (ushort)Math.Round(pinyinColor.Red * 65535),
                            g = (ushort)Math.Round(pinyinColor.Green * 65535),
                            b = (ushort)Math.Round(pinyinColor.Blue * 65535),
                            a = (float)pinyinColor.Alpha
                        });

                        entries.Add(new TextClipEntry
                        {
                            text = c.ToString(),
                            x = centerX,
                            y = baseLineY,
                            fontFamily = fontFamily,
                            fontSize = fontSize,
                            horizontalAlignment = SixLabors.Fonts.HorizontalAlignment.Center,
                            r = (ushort)Math.Round(color.Red * 65535),
                            g = (ushort)Math.Round(color.Green * 65535),
                            b = (ushort)Math.Round(color.Blue * 65535),
                            a = (float)color.Alpha
                        });

                        currentX += columnWidth;
                    }
                    else
                    {
                        var estWidth = (int)(fontSize * 0.7f) + charSpacing;
                        entries.Add(new TextClipEntry
                        {
                            text = c.ToString(),
                            x = currentX,
                            y = baseLineY,
                            fontFamily = fontFamily,
                            fontSize = fontSize,
                            r = (ushort)Math.Round(color.Red * 65535),
                            g = (ushort)Math.Round(color.Green * 65535),
                            b = (ushort)Math.Round(color.Blue * 65535),
                            a = (float)color.Alpha
                        });
                        currentX += estWidth;
                    }
                }
                else
                {
                    var estWidth = c == ' '
                        ? (int)(fontSize * 0.3f) + charSpacing
                        : (int)(fontSize * 0.5f) + charSpacing;

                    entries.Add(new TextClipEntry
                    {
                        text = c.ToString(),
                        x = currentX,
                        y = baseLineY,
                        fontFamily = fontFamily,
                        fontSize = fontSize,
                        r = (ushort)Math.Round(color.Red * 65535),
                        g = (ushort)Math.Round(color.Green * 65535),
                        b = (ushort)Math.Round(color.Blue * 65535),
                        a = (float)color.Alpha
                    });
                    currentX += estWidth;
                }
            }

            return entries.ToArray();
        }

        public PropertyPanelBuilder BuildPropertyPanel()
        {
            var panel = new PropertyPanelBuilder();
            panel.AddEntry(TextKey, "Text", BasicText, "Text");

            var fontOptions = TextServices.LoadedFonts.Keys.OrderBy(c => c).ToArray();
            var currentFont = GetOrDefault(FontKey, "Arial");
            if (fontOptions.Length > 0)
            {
                panel.AddPicker(FontKey, "Font", fontOptions, fontOptions.Contains(currentFont) ? currentFont : fontOptions.First());
            }
            else
            {
                panel.AddEntry(FontKey, "Font", currentFont, "Arial");
            }

            var size = ParseFloat(GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)), DefaultFontSize);
            panel.AddSlider(SizeKey, "Font Size", 20, 400, size, eventCallMode: SliderUpdateEventCallMode.OnMouseUp);

            var pinyinRatio = ParseFloat(GetOrDefault(PinyinFontSizeRatioKey, DefaultPinyinFontSizeRatio.ToString(CultureInfo.InvariantCulture)), DefaultPinyinFontSizeRatio);
            panel.AddSlider(PinyinFontSizeRatioKey, "Pinyin Size Ratio", 0.15f, 0.8f, pinyinRatio, eventCallMode: SliderUpdateEventCallMode.OnMouseUp);

            var spacing = ParseInt(GetOrDefault(SpacingKey, DefaultSpacing.ToString()), DefaultSpacing);
            panel.AddSlider(SpacingKey, "Char Spacing", 0, 40, spacing, eventCallMode: SliderUpdateEventCallMode.OnMouseUp);

            panel.AddEntry(ColorKey, "Char Color", GetOrDefault(ColorKey, "#FFFFFF"), "#FFFFFF");
            panel.AddEntry(PinyinColorKey, "Pinyin Color", GetOrDefault(PinyinColorKey, ""), "");
            return panel;
        }

        public (Dictionary<string, string> newParams, int newWidth, int newHeight) HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            switch (args.Id)
            {
                case TextKey:
                    BasicText = args.Value?.ToString() ?? string.Empty;
                    break;
                case FontKey:
                    _parameters[FontKey] = args.Value?.ToString() ?? "Arial";
                    break;
                case SizeKey:
                    if (TryParseFloat(args.Value, out var size))
                    {
                        _parameters[SizeKey] = size.ToString(CultureInfo.InvariantCulture);
                        _parameters[ManualSizeKey] = "true";
                    }
                    break;
                case PinyinFontSizeRatioKey:
                    if (TryParseFloat(args.Value, out var ratio))
                    {
                        _parameters[PinyinFontSizeRatioKey] = ratio.ToString(CultureInfo.InvariantCulture);
                    }
                    break;
                case SpacingKey:
                    if (int.TryParse(args.Value?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var spacing))
                    {
                        _parameters[SpacingKey] = spacing.ToString(CultureInfo.InvariantCulture);
                    }
                    break;
                case ColorKey:
                    _parameters[ColorKey] = args.Value?.ToString() ?? "#FFFFFF";
                    break;
                case PinyinColorKey:
                    _parameters[PinyinColorKey] = args.Value?.ToString() ?? string.Empty;
                    break;
            }

            var rect = MeasureEntries(BuildEntries());
            return (_parameters, (int)rect.Width, (int)rect.Height);
        }

        public Dictionary<string, string> HandleClipResize(bool isInRatio, int TargetX, int TargetY, int TargetWidth, int TargetHeight)
        {
            EnsureDefaults();

            if (TargetWidth <= 0 || TargetHeight <= 0)
                return new Dictionary<string, string>(_parameters);

            var currentRect = GetViewRect(TargetWidth, TargetHeight);
            if (currentRect.TargetWidth <= 0 || currentRect.TargetHeight <= 0)
                return new Dictionary<string, string>(_parameters);

            var currentFontSize = ParseFloat(
                GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)),
                DefaultFontSize);

            var scaleX = (double)TargetWidth / currentRect.TargetWidth;
            var scaleY = (double)TargetHeight / currentRect.TargetHeight;
            if (double.IsNaN(scaleX) || double.IsInfinity(scaleX) || scaleX <= 0)
                return new Dictionary<string, string>(_parameters);

            double scale = scaleX;
            if (!isInRatio && !double.IsNaN(scaleY) && !double.IsInfinity(scaleY) && scaleY > 0)
            {
                if (Math.Abs(scaleX - scaleY) > 0.0001)
                {
                    scale = (scaleX + scaleY) / 2d;
                }
            }

            var updatedSize = (float)(currentFontSize * scale);
            if (updatedSize > 0)
            {
                _parameters[SizeKey] = updatedSize.ToString(CultureInfo.InvariantCulture);
            }

            return new Dictionary<string, string>(_parameters);
        }

        public ClipPositionTuple GetViewRect(int canvasWidth, int canvasHeight)
        {
            var entries = BuildEntries();
            if (entries.Length == 0)
                return new ClipPositionTuple(0, 0, Math.Max(1, canvasWidth), Math.Max(1, canvasHeight), false);

            double minX = double.MaxValue;
            double minY = double.MaxValue;
            double maxX = double.MinValue;
            double maxY = double.MinValue;

            foreach (var entry in entries)
            {
                var (w, h) = EstimateEntrySize(entry);
                var left = entry.x;
                var top = entry.y;
                var right = entry.x + w;
                var bottom = entry.y + h;

                minX = Math.Min(minX, left);
                minY = Math.Min(minY, top);
                maxX = Math.Max(maxX, right);
                maxY = Math.Max(maxY, bottom);
            }

            if (double.IsInfinity(minX) || double.IsInfinity(minY))
                return new ClipPositionTuple(0, 0, Math.Max(1, canvasWidth), Math.Max(1, canvasHeight), false);

            int width = Math.Max(1, (int)Math.Round(maxX - minX));
            int height = Math.Max(1, (int)Math.Round(maxY - minY));
            return new ClipPositionTuple((int)Math.Round(minX), (int)Math.Round(minY), width, height, false);
        }

        private void EnsureDefaults()
        {
            if (!_parameters.ContainsKey(TextKey)) _parameters[TextKey] = DefaultText;
            if (!_parameters.ContainsKey(FontKey)) _parameters[FontKey] = "Arial";
            if (!_parameters.ContainsKey(SizeKey)) _parameters[SizeKey] = DefaultFontSize.ToString(CultureInfo.InvariantCulture);
            if (!_parameters.ContainsKey(ColorKey)) _parameters[ColorKey] = "#FFFFFF";
            if (!_parameters.ContainsKey(PinyinFontSizeRatioKey)) _parameters[PinyinFontSizeRatioKey] = DefaultPinyinFontSizeRatio.ToString(CultureInfo.InvariantCulture);
            if (!_parameters.ContainsKey(PinyinColorKey)) _parameters[PinyinColorKey] = "";
            if (!_parameters.ContainsKey(SpacingKey)) _parameters[SpacingKey] = DefaultSpacing.ToString(CultureInfo.InvariantCulture);
        }

        private static (int width, int height) EstimateEntrySize(TextClipEntry entry)
        {
            var text = entry.text ?? string.Empty;
            var fontSize = entry.fontSize > 0 ? entry.fontSize : 24f;
            var width = Math.Max(1, (int)Math.Round(text.Length * fontSize * 0.6f));
            var height = Math.Max(1, (int)Math.Round(fontSize * 1.2f));
            return (width, height);
        }

        private static (float Width, float Height) MeasureEntries(TextClipEntry[] entries)
        {
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            bool hasBounds = false;

            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.text)) continue;
                var (w, h) = EstimateEntrySize(entry);
                float left = entry.x, top = entry.y;
                float right = left + w, bottom = top + h;

                if (!hasBounds)
                {
                    minX = left; minY = top; maxX = right; maxY = bottom;
                    hasBounds = true;
                }
                else
                {
                    minX = Math.Min(minX, left);
                    minY = Math.Min(minY, top);
                    maxX = Math.Max(maxX, right);
                    maxY = Math.Max(maxY, bottom);
                }
            }

            if (!hasBounds)
                return (1f, 1f);

            return (Math.Max(1f, maxX - minX) + 15, Math.Max(1f, maxY - minY) + 15);
        }

        private static bool IsCJKCharacter(char c)
        {
            return c >= 0x4E00 && c <= 0x9FFF || c >= 0x3400 && c <= 0x4DBF
                || c >= 0x2F800 && c <= 0x2FA1F || c >= 0x3000 && c <= 0x303F
                || c >= 0xFF00 && c <= 0xFFEF;
        }

        private string GetOrDefault(string key, string fallback)
        {
            return _parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : fallback;
        }

        private static float ParseFloat(string? value, float fallback)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
        }

        private static int ParseInt(string? value, int fallback)
        {
            return int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
        }

        private static bool TryParseFloat(object? value, out float result)
        {
            if (value is float f)
            {
                result = f;
                return true;
            }
            if (value is double d)
            {
                result = (float)d;
                return true;
            }
            if (float.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                result = parsed;
                return true;
            }

            result = 0f;
            return false;
        }

        private static Color ParseColorOrFallback(string? value, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            try
            {
                return Color.FromArgb(value);
            }
            catch
            {
                return fallback;
            }
        }
    }
}
