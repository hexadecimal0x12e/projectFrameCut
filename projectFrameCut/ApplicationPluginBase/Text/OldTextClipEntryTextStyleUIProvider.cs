using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Render.ClipsAndTracks.Text;
using projectFrameCut.Render.RenderAPIBase.Text;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
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
        List<TextEntry>? native = null;
        if (source.GetParameters().TryGetValue(EntriesJsonKey, out var json))
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement is { ValueKind: JsonValueKind.Array } array && array.GetArrayLength() > 0
                && array[0].TryGetProperty("FontName", out _))
            {
                native = JsonSerializer.Deserialize<List<TextEntry>>(json);
                foreach (var entry in native ?? [])
                    entry.ExtraData = entry.ExtraData.ToDictionary(p => p.Key, p => EffectParamConvert.Normalize(p.Value) ?? "");
            }
        }
        var entries = native?.Select(ToLegacy).ToList() ?? LoadEntries(source);

        var ppb = new PropertyPanelBuilder();

        ppb.AddCustomChild((invoker) =>
        {
            var entriesContainer = new Microsoft.Maui.Controls.VerticalStackLayout { Spacing = 8 };

            void UpdateStoredEntries()
            {
                try
                {
                    source.GetParameters()[EntriesJsonKey] = JsonSerializer.Serialize((object?)native ?? entries);
                }
                catch (Exception ex) { Logger.Log(ex, "Update text style entries", typeof(OldTextClipEntryTextStyleUIProvider)); }
                invoker((object?)native ?? entries);
            }

            void RebuildEntriesUI()
            {
                entriesContainer.Children.Clear();
                for (int i = 0; i < entries.Count; i++)
                {
                    int idx = i;
                    var e = entries[idx];
                    var view = projectFrameCut.DraftStuff.ClipInfoBuilder.BuildTextEntryUI(e, idx, fonts,
                        (id, newE) =>
                        {
                            if (native is not null) native[id] = UpdateNative(native[id], newE);
                            entries[id] = newE;
                            UpdateStoredEntries();
                        },
                        (id) => { entries.RemoveAt(id); native?.RemoveAt(id); UpdateStoredEntries(); RebuildEntriesUI(); },
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
                    native?.Add(TextEntryMigration.MigrateFromTextClipEntry(value));
                    UpdateStoredEntries();
                    RebuildEntriesUI();
                };
                entriesContainer.Children.Add(addBtn);
            }

            RebuildEntriesUI();

            var scroll = new Microsoft.Maui.Controls.ScrollView { Content = entriesContainer, VerticalOptions = LayoutOptions.Start };
            return scroll;
        }, "TextEntries", JsonSerializer.Serialize((object?)native ?? entries));

        return ppb;
    }

    private static TextClipEntry ToLegacy(TextEntry e) => new()
    {
        text = e.Text, fontFamily = e.FontName, fontSize = e.FontSize,
        x = (int)Math.Round(e.X), y = (int)Math.Round(e.Y),
        fontStyle = Enum.TryParse<ClipFontStyle>(e.FontStyle, out var style) ? style : ClipFontStyle.Regular,
        r = e.FillR, g = e.FillG, b = e.FillB, a = (float)e.FillA,
        strokeR = e.StrokeR, strokeG = e.StrokeG, strokeB = e.StrokeB, strokeWidth = e.StrokeThickness,
        rotation = e.Rotation * 180f / MathF.PI, lineSpacing = e.LineSpacing + 1,
        horizontalAlignment = Enum.TryParse<ClipHorizontalAlignment>(e.Alignment.ToString(), out var alignment) ? alignment : ClipHorizontalAlignment.Left,
        verticalAlignment = e.GetVerticalAlignment(), wrappingWidth = e.GetWrappingWidth(),
        ScaleWithTarget = e.GetScaleWithTarget(), dpi = e.GetDpi(),
        UseVerticalLayout = e.GetUseVerticalLayout(), KeepNonCJKTextAsHorizontal = e.GetKeepNonCJKTextAsHorizontal(),
    };

    private static TextEntry UpdateNative(TextEntry original, TextClipEntry value)
    {
        var previous = ToLegacy(original);
        var edited = TextEntryMigration.MigrateFromTextClipEntry(value);
        var entry = original with
        {
            Text = edited.Text, FontName = edited.FontName, FontSize = edited.FontSize,
            X = previous.x == value.x ? original.X : edited.X, Y = previous.y == value.y ? original.Y : edited.Y,
            FontStyle = previous.fontStyle == value.fontStyle ? original.FontStyle : edited.FontStyle,
            FillR = edited.FillR, FillG = edited.FillG, FillB = edited.FillB, FillA = edited.FillA,
            StrokeR = edited.StrokeR, StrokeG = edited.StrokeG, StrokeB = edited.StrokeB,
            StrokeThickness = edited.StrokeThickness,
            StrokeA = previous.strokeWidth == value.strokeWidth ? original.StrokeA : edited.StrokeA,
            Rotation = previous.rotation == value.rotation ? original.Rotation : edited.Rotation,
            LineSpacing = previous.lineSpacing == value.lineSpacing ? original.LineSpacing : edited.LineSpacing,
            Alignment = previous.horizontalAlignment == value.horizontalAlignment ? original.Alignment : edited.Alignment,
            ExtraData = new(original.ExtraData),
        };
        entry.SetVerticalAlignment(value.verticalAlignment);
        entry.SetWrappingWidth(value.wrappingWidth);
        entry.SetScaleWithTarget(value.ScaleWithTarget);
        entry.SetDpi(value.dpi);
        entry.SetUseVerticalLayout(value.UseVerticalLayout);
        entry.SetKeepNonCJKTextAsHorizontal(value.KeepNonCJKTextAsHorizontal);
        return entry;
    }


    public (int newWidth, int newHeight) HandlePropertyPanelChange(ITextClipStyleProvider source, PropertyPanelPropertyChangedEventArgs args)
    {
        if (args.Id == "TextEntries")
            source.MetaData[EntriesJsonKey] = args.Value is string text ? text : JsonSerializer.Serialize(args.Value);
        var rect = source.GetViewRect(1920, 1080);
        return (rect.TargetWidth, rect.TargetHeight);
    }
}
