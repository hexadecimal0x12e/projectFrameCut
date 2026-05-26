using Microsoft.Maui.Graphics;
using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationPluginBase.DynamicPreviewProvider;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace projectFrameCut.ApplicationPluginBase.Text
{
    public class BasicTextStyleProvider : ITextClipStyleProvider
    {
        protected const string TextKey = "Text";
        protected const string FontKey = "FontFamily";
        protected const string SizeKey = "FontSize";
        protected const string ColorKey = "Color";
        public const string ManualSizeKey = "TextStyleManualSize";

        private Dictionary<string, string> _parameters = new();

        public BasicTextStyleProvider()
        {
            EnsureDefaults();
        }

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public virtual string TypeName => "Basic";

        protected virtual string DefaultText => "Text";

        protected virtual float DefaultFontSize => 50f;

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
            var fontFamily = GetOrDefault(FontKey, "Arial");
            var fontSize = ParseFloat(GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)), DefaultFontSize);
            var colorText = GetOrDefault(ColorKey, "#FFFFFF");
            var color = ParseColorOrFallback(colorText, Colors.White);

            return new[]
            {
                new TextClipEntry
                {
                    text = BasicText,
                    x = 0,
                    y = 0,
                    fontFamily = fontFamily,
                    fontSize = fontSize,
                    r = (ushort)Math.Round(color.Red * 65535),
                    g = (ushort)Math.Round(color.Green * 65535),
                    b = (ushort)Math.Round(color.Blue * 65535),
                    a = (float)color.Alpha
                }
            };
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
            panel.AddSlider(SizeKey, "Font Size", 50, 500, size, eventCallMode: SliderUpdateEventCallMode.OnMouseUp);
            panel.AddEntry(ColorKey, "Color", GetOrDefault(ColorKey, "#FFFFFF"), "#FFFFFF");
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
                case ColorKey:
                    _parameters[ColorKey] = args.Value?.ToString() ?? "#FFFFFF";
                    break;
            }
            var rect = TextClipMeasureHelper.MeasureBounds(BuildEntries());
            return (_parameters, (int)rect.Width, (int)rect.Height);
        }

        public Dictionary<string, string> HandleClipResize(bool isInRatio, int TargetX, int TargetY, int TargetWidth, int TargetHeight)
        {
            EnsureDefaults();

            if (TargetWidth <= 0 || TargetHeight <= 0)
            {
                return new Dictionary<string, string>(_parameters);
            }

            var currentRect = GetViewRect(TargetWidth, TargetHeight);
            if (currentRect.TargetWidth <= 0 || currentRect.TargetHeight <= 0)
            {
                return new Dictionary<string, string>(_parameters);
            }

            var currentFontSize = ParseFloat(
                GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)),
                DefaultFontSize);

            var scaleX = (double)TargetWidth / currentRect.TargetWidth;
            var scaleY = (double)TargetHeight / currentRect.TargetHeight;
            if (double.IsNaN(scaleX) || double.IsInfinity(scaleX) || scaleX <= 0)
            {
                return new Dictionary<string, string>(_parameters);
            }

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
            {
                return new ClipPositionTuple(0, 0, Math.Max(1, canvasWidth), Math.Max(1, canvasHeight), false);
            }

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
            {
                return new ClipPositionTuple(0, 0, Math.Max(1, canvasWidth), Math.Max(1, canvasHeight), false);
            }

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
        }

        private static (int width, int height) EstimateEntrySize(TextClipEntry entry)
        {
            try
            {
                var rect = TextClipMeasureHelper.MeasureBounds([entry]);
                return (Math.Max(1, (int)Math.Round(rect.Width)), Math.Max(1, (int)Math.Round(rect.Height)));
            }
            catch
            {
                var text = entry.text ?? string.Empty;
                var fontSize = entry.fontSize > 0 ? entry.fontSize : 24f;
                var width = Math.Max(1, (int)Math.Round(text.Length * fontSize * 0.6f));
                var height = Math.Max(1, (int)Math.Round(fontSize * 1.2f));
                return (width, height);
            }
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
            {
                return fallback;
            }

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

    public sealed class TitleTextStyleProvider : BasicTextStyleProvider
    {
        public override string TypeName => "Title";

        protected override string DefaultText => "Title";

        protected override float DefaultFontSize => 64f;
    }
}
