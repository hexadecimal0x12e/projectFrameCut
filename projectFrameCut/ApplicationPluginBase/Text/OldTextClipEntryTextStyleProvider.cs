using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationPluginBase.DynamicPreviewProvider;
using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Render.ClipsAndTracks.Text;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace projectFrameCut.ApplicationPluginBase.Text
{
    internal class OldTextClipEntryTextStyleProvider : ITextClipStyleProvider
    {
        private const string EntriesJsonKey = "TextEntriesJson";

        private Dictionary<string, string> _parameters = new();

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public string TypeName => "OldTextClipEntry";

        public string BasicText { get; set; } = string.Empty;

        public Dictionary<string, string> Parameters
        {
            get => _parameters;
            set
            {
                _parameters = value ?? new Dictionary<string, string>();
            }
        }

        public bool AllowFreeRatioResize => false;
        public bool IsHorizontalResizable => false;
        public bool IsVerticalResizable => false;
        public bool CanSnapWhileResizing => false;

        public TextClipLayoutMode LayoutMode { get; set; }

        public Dictionary<string, EffectArgumentFieldDescriptor> SettableFields => [];

        public bool HandleSettableFieldsChange(EffectArgumentFieldDescriptor field, object value, out string feedback)
        {
            feedback = "The legacy text style has no settable fields.";
            return false;
        }

        private List<TextClipEntry> LoadOldEntriesFromParameters()
        {
            if (Parameters.TryGetValue(EntriesJsonKey, out var json) && !string.IsNullOrWhiteSpace(json))
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

        public TextEntry[] BuildEntries()
        {
            var oldEntries = LoadOldEntriesFromParameters();
            return TextEntryMigration.MigrateFromTextClipEntries(oldEntries).ToArray();
        }

        public PropertyPanelBuilder BuildPropertyPanel()
        {
            var fonts = projectFrameCut.Services.TextServices.LoadedFonts.Select(c => c.Value);
            var entries = LoadOldEntriesFromParameters();

            var ppb = new PropertyPanelBuilder();

            ppb.AddCustomChild((invoker) =>
            {
                var entriesContainer = new Microsoft.Maui.Controls.VerticalStackLayout { Spacing = 8 };

                void UpdateStoredEntries()
                {
                    try
                    {
                        Parameters[EntriesJsonKey] = System.Text.Json.JsonSerializer.Serialize(entries);
                    }
                    catch { }
                    invoker(entries);
                }

                void RebuildEntriesUI()
                {
                    entriesContainer.Children.Clear();
                    for (int i = 0; i < entries.Count; i++)
                    {
                        int idx = i;
                        var e = entries[idx];
                        var view = projectFrameCut.DraftStuff.ClipInfoBuilder.BuildTextEntryUI(e, idx, fonts,
                            (id, newE) => { entries[id] = newE; UpdateStoredEntries(); },
                            (id) => { entries.RemoveAt(id); UpdateStoredEntries(); RebuildEntriesUI(); },
                            entries.Count > 1,
                            false, null, null);
                        entriesContainer.Children.Add(view);
                    }

                    var addBtn = new Microsoft.Maui.Controls.Button
                    {
                        Text = LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources.TextOption_AddAEntry,
                        HorizontalOptions = LayoutOptions.Fill,
                        CornerRadius = 8,
                        FontAttributes = FontAttributes.Bold,
                        Margin = new Microsoft.Maui.Thickness(0, 4, 0, 0)
                    };
                    addBtn.Clicked += (s, e) =>
                    {
                        var value = new TextClipEntry
                        {
                            text = "",
                            x = 0, y = 0,
                            fontFamily = TextClipFontRegistry.GetAllFonts().FirstOrDefault()?.UniqueName ?? TextClipFontRegistry.FallbackFamilyName ?? "HarmonyOS Sans SC Medium",
                            fontSize = 24f,
                            r = 65535, g = 65535, b = 65535,
                            a = 1f
                        };
                        entries.Add(value);
                        UpdateStoredEntries();
                        RebuildEntriesUI();
                    };
                    entriesContainer.Children.Add(addBtn);
                }

                RebuildEntriesUI();

                var scroll = new Microsoft.Maui.Controls.ScrollView { Content = entriesContainer, VerticalOptions = LayoutOptions.Start };
                return scroll;
            }, "TextEntries", System.Text.Json.JsonSerializer.Serialize(LoadOldEntriesFromParameters()));

            return ppb;
        }

        public ClipPositionTuple GetViewRect(int canvasWidth, int canvasHeight)
        {
            var entries = BuildEntries();
            try
            {
                var rect = TextMeasureHelper.MeasureBounds(entries, canvasWidth, canvasHeight);
                return new ClipPositionTuple((int)Math.Round(rect.X), (int)Math.Round(rect.Y), Math.Max(1, (int)Math.Ceiling(rect.Width)), Math.Max(1, (int)Math.Ceiling(rect.Height)), false);
            }
            catch
            {
                return new ClipPositionTuple(0, 0, Math.Max(1, canvasWidth), Math.Max(1, canvasHeight), false);
            }
        }

        public Dictionary<string, string> HandleClipResize(bool isInRatio, int TargetX, int TargetY, int TargetWidth, int TargetHeight)
            => new Dictionary<string, string>(Parameters);

        public (Dictionary<string, string> newParams, int newWidth, int newHeight) HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            if (args.Id == "TextEntries")
            {
                try
                {
                    if (args.Value is System.Collections.IEnumerable enumerable)
                    {
                        var list = new System.Collections.Generic.List<TextClipEntry>();
                        foreach (var item in enumerable)
                        {
                            if (item is TextClipEntry te) list.Add(te);
                        }
                        Parameters[EntriesJsonKey] = System.Text.Json.JsonSerializer.Serialize(list);
                        var migrated = TextEntryMigration.MigrateFromTextClipEntries(list);
                        var rect = TextMeasureHelper.MeasureBounds(migrated, 1920, 1080);
                        return (new Dictionary<string, string>(Parameters), Math.Max(1, (int)Math.Ceiling(rect.Width)), Math.Max(1, (int)Math.Ceiling(rect.Height)));
                    }
                    else if (args.Value is string js)
                    {
                        Parameters[EntriesJsonKey] = js;
                        var des = System.Text.Json.JsonSerializer.Deserialize<TextClipEntry[]>(js) ?? Array.Empty<TextClipEntry>();
                        var migrated = TextEntryMigration.MigrateFromTextClipEntries(des);
                        var rect = TextMeasureHelper.MeasureBounds(migrated, 1920, 1080);
                        return (new Dictionary<string, string>(Parameters), Math.Max(1, (int)Math.Ceiling(rect.Width)), Math.Max(1, (int)Math.Ceiling(rect.Height)));
                    }
                }
                catch { }
            }
            return (new Dictionary<string, string>(Parameters), 0, 0);
        }
    }
}
