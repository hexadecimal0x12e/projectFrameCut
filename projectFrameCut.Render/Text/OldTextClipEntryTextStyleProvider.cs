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
    public class OldTextClipEntryTextStyleProvider : TextClipStyleProviderBase
    {
        private const string EntriesJsonKey = "TextEntriesJson";

        private TextStyleParameters? parameterView;
        private TextStyleParameters _parameters => parameterView ??= this.GetParameters();

        public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public override string TypeName => "OldTextClipEntry";

        public override string BasicText
        {
            get => _parameters.TryGetValue("Text", out var text) ? text : "";
            set => _parameters["Text"] = value;
        }

        public override bool AllowFreeRatioResize => false;
        public override bool IsHorizontalResizable => false;
        public override bool IsVerticalResizable => false;
        public override bool CanSnapWhileResizing => false;

        public override TextClipLayoutMode LayoutMode { get; set; }

        public override Dictionary<string, EffectArgumentFieldDescriptor> SettableFields => [];

        public override bool HandleSettableFieldsChange(EffectArgumentFieldDescriptor field, object value, out string feedback)
        {
            feedback = "The legacy text style has no settable fields.";
            return false;
        }

        private List<TextClipEntry> LoadOldEntriesFromParameters()
        {
            if (_parameters.TryGetValue(EntriesJsonKey, out var json) && !string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    var des = System.Text.Json.JsonSerializer.Deserialize<List<TextClipEntry>>(json);
                    if (des != null) return des;
                }
                catch { }
            }

            var f = TextClipFontRegistry.GetAllFonts().FirstOrDefault()?.UniqueName ?? TextClipFontRegistry.FallbackFamilyName ?? "HarmonyOS Sans SC Medium";
            return new List<TextClipEntry>
            {
                new TextClipEntry
                {
                    text = BasicText ?? string.Empty,
                    x = 0, y = 0,
                    fontFamily = f,
                    fontSize = 24f,
                    r = 65535, g = 65535, b = 65535,
                    a = 1f
                }
            };
        }

        public override TextEntry[] BuildEntries()
        {
            var oldEntries = LoadOldEntriesFromParameters();
            return TextEntryMigration.MigrateFromTextClipEntries(oldEntries).ToArray();
        }

        public override ClipPositionTuple GetViewRect(int canvasWidth, int canvasHeight)
        {
            var entries = BuildEntries();
            try
            {
                var rect = TextStyleRenderHelper.MeasureBounds(entries, canvasWidth, canvasHeight);
                return new ClipPositionTuple((int)Math.Round(rect.X), (int)Math.Round(rect.Y), Math.Max(1, (int)Math.Ceiling(rect.Width)), Math.Max(1, (int)Math.Ceiling(rect.Height)), false);
            }
            catch
            {
                return new ClipPositionTuple(0, 0, Math.Max(1, canvasWidth), Math.Max(1, canvasHeight), false);
            }
        }

        public override void HandleClipResize(bool isInRatio, int TargetX, int TargetY, int TargetWidth, int TargetHeight)
            { }

    }
}
