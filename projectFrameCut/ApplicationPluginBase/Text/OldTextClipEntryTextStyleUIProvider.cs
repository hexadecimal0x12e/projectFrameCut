using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Render.ClipsAndTracks.Text;
using projectFrameCut.Render.RenderAPIBase.Text;
using projectFrameCut.Shared;
using System.Text.Json;

namespace projectFrameCut.ApplicationPluginBase.Text;

internal sealed class OldTextClipEntryTextStyleUIProvider : ITextClipStyleUIProvider
{
    private const string EntriesJsonKey = "TextEntriesJson";
    public bool ShowDefaultTextEditor => false;
    public bool ShowLayoutModePicker => false;
    public bool ShowFontPicker => false;

    private static List<TextClipEntry> LoadEntries(ITextClipStyleProvider source) =>
        source.GetParameters().TryGetValue(EntriesJsonKey, out var json)
            ? JsonSerializer.Deserialize<List<TextClipEntry>>(json) ?? new() : new();

    public PropertyPanelBuilder CreateUI(ITextClipStyleProvider source)
    {
        var fonts = projectFrameCut.Services.TextServices.LoadedFonts.Select(c => c.Value);
        var entries = LoadEntries(source);

        var ppb = new PropertyPanelBuilder();

        ppb.AddCustomChild((invoker) =>
        {
            var entriesContainer = new Microsoft.Maui.Controls.VerticalStackLayout { Spacing = 8 };

            void UpdateStoredEntries()
            {
                try
                {
                    source.GetParameters()[EntriesJsonKey] = System.Text.Json.JsonSerializer.Serialize(entries);
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
        }, "TextEntries", System.Text.Json.JsonSerializer.Serialize(LoadEntries(source)));

        return ppb;
    }


    public (int newWidth, int newHeight) HandlePropertyPanelChange(ITextClipStyleProvider source, PropertyPanelPropertyChangedEventArgs args)
    {
        if (args.Id == "TextEntries")
            source.MetaData[EntriesJsonKey] = args.Value is string text ? text : JsonSerializer.Serialize(args.Value);
        var rect = source.GetViewRect(1920, 1080);
        return (rect.TargetWidth, rect.TargetHeight);
    }
}
