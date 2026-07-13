using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.ClipsAndTracks.Text;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Render.VectorContent;
using projectFrameCut.Render.VectorContent.Components;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;
using System.Globalization;

namespace projectFrameCut.ApplicationPluginBase.VectorComponentHandler;

/// <summary>
/// Handler for the "Text" component type — integrates
/// <see cref="ITextClipStyleProvider"/> into the vector-component property
/// panel so that users get the same rich text-editing experience they would
/// have when configuring a <see cref="Render.ClipsAndTracks.TextClip"/>.
/// </summary>
public class TextComponentHandler : BaseVectorComponentHandler
{
    public override string TypeName => "Text";
    public override string DisplayName => PPLocalizedResources.VectorContentHandler_Text_DisplayName;
    public override string Icon => "\ue262";
    public override bool HasDefaultHandles => true;

    /// <summary>
    /// Returns per-component resize capability based on the text layout mode.
    /// In FixedSize mode both axes are locked; FixedWidth locks vertical;
    /// FixedHeight locks horizontal. FillClip allows both.
    /// </summary>
    internal static (bool horizontal, bool vertical) GetResizability(TextComponent component)
    {
        var provider = RestoreProvider(component);
        if (provider is null) return (true, true);
        SyncComponentToProvider(component, provider);
        return (provider.IsHorizontalResizable, provider.IsVerticalResizable);
    }

    protected override IVectorComponent CreateComponent() => new TextComponent();

    protected override Dictionary<string, object> GetDefaultParameters() => new()
    {
        ["Text"] = "Text",
        ["FontName"] = "",
        ["FontStyle"] = "Regular",
        ["FontSize"] = 120f,
        ["TextAlignment"] = 0,
        ["CharacterSpacing"] = 0f,
        ["LineSpacing"] = 0.3f,
        ["StrokeThickness"] = 2f,
        ["RelativeX"] = 0.5f,
        ["RelativeY"] = 0.5f,
        ["Rotation"] = 0f,
        ["LayerIndex"] = 0,
        ["FillR"] = (float)ushort.MaxValue,
        ["FillG"] = (float)ushort.MaxValue,
        ["FillB"] = (float)ushort.MaxValue,
        ["FillA"] = 1f,
        ["StrokeR"] = 0f,
        ["StrokeG"] = 0f,
        ["StrokeB"] = 0f,
        ["StrokeA"] = 0f,
        ["TextStyleProvider_FromPlugin"] = Render.Plugin.InternalPluginBase.InternalPluginBaseID,
        ["TextStyleProvider_TypeName"] = "Basic",
    };

    // ═══════════════════════════════════════════════════════════════
    //  Property panel — delegate to ITextClipStyleProvider
    // ═══════════════════════════════════════════════════════════════

    protected override void AddShapeSpecificProperties(
        PropertyPanelBuilder builder, IVectorComponent component)
    {
        var textComponent = (TextComponent)component;
        var provider = RestoreProvider(textComponent);
        if (provider is null) return; // no provider → no text panel

        SyncComponentToProvider(textComponent, provider);
        var providerPanel = provider.BuildPropertyPanel();

        // ── Auto-provided options (same layout as ClipInfoBuilder.BuildTextOptionTab) ──
        builder.AddCollapsibleSection(PPLocalizedResources.VectorContentHandler_Section_Text, b =>
        {
            // ── Layout mode picker ──
            if (provider.ShowLayoutModePicker)
            {
                string[] layoutOptions = { "FillClip", "FixedWidth", "FixedSize" };
                string currentLayout = provider.LayoutMode.ToString();
                b.AddPicker("LayoutMode", PPLocalizedResources.VectorContentHandler_LayoutMode, layoutOptions, currentLayout);
            }

            // ── Text content editor (multi-line, like BuildTextOptionTab) ──
            if (provider.ShowDefaultTextEditor)
            {
                b.AddCustomChild(PPLocalizedResources.VectorContentHandler_Content, invoker =>
                {
                    var editor = new Editor
                    {
                        MinimumHeightRequest = 150,
                        Text = provider.BasicText,
                        IsSpellCheckEnabled = true,
                        IsTextPredictionEnabled = true,
                        Placeholder = PPLocalizedResources.VectorContentHandler_TextPlaceholder
                    };
                    editor.Unfocused += (_, _) => invoker(editor.Text);
                    return editor;
                }, "Text", provider.BasicText);
            }

            // ── Font picker (dropdown from loaded fonts) ──
            if (provider.ShowFontPicker)
            {
                var fontNames = TextServices.LoadedFonts.Values
                    .Where(f => f is not null && !string.IsNullOrWhiteSpace(f.FontName))
                    .Select(f => f.FontName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(f => f)
                    .ToArray();

                if (fontNames.Length > 0)
                {
                    var currentFont = provider.Parameters.TryGetValue("FontFamily", out var fn) ? fn : null;
                    b.AddPicker("FontFamily", PPLocalizedResources.VectorContentHandler_Font, fontNames, currentFont);
                }
            }

            // ── Provider-specific properties ──
            b.AddFromAnother(providerPanel, provider);

        }, defaultExpanded: true);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Property change handling
    // ═══════════════════════════════════════════════════════════════

    public override void HandlePropertyChange(
        IVectorComponent component,
        PropertyPanelPropertyChangedEventArgs args)
    {
        var textComponent = (TextComponent)component;

        // ── Handle Text property directly (and keep provider in sync) ──
        if (args.Id == "Text")
        {
            textComponent.Parameters["Text"] = args.Value?.ToString() ?? "Text";
            var syncProvider = RestoreProvider(textComponent);
            if (syncProvider != null)
            {
                syncProvider.BasicText = args.Value?.ToString() ?? string.Empty;
                syncProvider.Parameters["Text"] = syncProvider.BasicText;
                textComponent.Parameters["TextStyleProvider_Parameters"] =
                    new Dictionary<string, string>(syncProvider.Parameters);
            }
            return;
        }

        // ── Restore provider and forward the change ──
        var provider = RestoreProvider(textComponent);
        if (provider is null)
        {
            base.HandlePropertyChange(component, args);
            return;
        }

        SyncComponentToProvider(textComponent, provider);

        // Font-family registration (same pattern as ClipInfoBuilder)
        if (args.Id == "FontFamily")
        {
            var fontName = args.Value?.ToString();
            if (!string.IsNullOrWhiteSpace(fontName) &&
                !TextClipFontRegistry.TryGetFont(fontName, out _))
            {
                if (TextServices.LoadedFonts.TryGetValue(fontName!, out var fontItem))
                {
                    if (fontItem.InnerFont is not null)
                        TextClipFontRegistry.RegisterFontFace(fontItem.InnerFont);
                    else if (!string.IsNullOrWhiteSpace(fontItem.Path))
                        TextClipFontRegistry.AddFont(fontItem.Path);
                }
            }
        }

        // Handle LayoutMode changes from the auto-provided picker
        if (args.Id == "LayoutMode")
        {
            if (args.Value?.ToString() is { } modeStr &&
                Enum.TryParse<TextClipLayoutMode>(modeStr, true, out var mode))
            {
                provider.LayoutMode = mode;
            }
            SyncProviderToComponent(textComponent, provider);
            textComponent.Parameters["TextStyleProvider_Parameters"] =
                new Dictionary<string, string>(provider.Parameters);
            return;
        }

        provider.HandlePropertyPanelChange(args);

        // ── Sync provider params back to component params ──
        SyncProviderToComponent(textComponent, provider);

        // Store updated provider parameters for persistence.
        textComponent.Parameters["TextStyleProvider_Parameters"] =
            new Dictionary<string, string>(provider.Parameters);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Provider ↔ Component parameter sync helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Read provider metadata and restore the style provider.</summary>
    internal static ITextClipStyleProvider? RestoreProvider(TextComponent component)
    {
        string? fromPlugin = component.Parameters.TryGetValue("TextStyleProvider_FromPlugin", out var fp)
            ? fp?.ToString()
            : null;
        string? typeName = component.Parameters.TryGetValue("TextStyleProvider_TypeName", out var tn)
            ? tn?.ToString()
            : null;

        if (string.IsNullOrWhiteSpace(fromPlugin) || string.IsNullOrWhiteSpace(typeName))
            return null;

        Dictionary<string, string>? providerParams = null;
        if (component.Parameters.TryGetValue("TextStyleProvider_Parameters", out var raw))
        {
            if (raw is Dictionary<string, string> dict)
                providerParams = dict;
            else if (raw is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                try { providerParams = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(je.GetRawText()); }
                catch { }
            }
            else if (raw is string json && !string.IsNullOrWhiteSpace(json))
            {
                try { providerParams = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
                catch { }
            }
        }

        return TextStyleServices.RestoreTextStyleProvider(fromPlugin, typeName, providerParams);
    }

    /// <summary>
    /// Copy the component's typed text parameters into the provider's
    /// string-keyed parameters so the provider sees current values.
    /// </summary>
    internal static void SyncComponentToProvider(TextComponent component, ITextClipStyleProvider provider)
    {
        var p = component.Parameters;

        if (p.TryGetValue("Text", out var text) && text is string s)
            provider.BasicText = s;

        // Fill colour – convert from (R,G,B) ushort channels to hex.
        var fillR = (int)Math.Round(p.GetFloat("FillR", ushort.MaxValue) / ushort.MaxValue * 255f);
        var fillG = (int)Math.Round(p.GetFloat("FillG", ushort.MaxValue) / ushort.MaxValue * 255f);
        var fillB = (int)Math.Round(p.GetFloat("FillB", ushort.MaxValue) / ushort.MaxValue * 255f);
        provider.Parameters["Color"] = $"#{fillR:X2}{fillG:X2}{fillB:X2}";

        // FontSize
        var fontSize = p.GetFloat("FontSize", 120f);
        provider.Parameters["Size"] = fontSize.ToString("F1", CultureInfo.InvariantCulture);

        // Alignment
        var align = (int)p.GetFloat("TextAlignment", 0);
        provider.Parameters["HorizontalAlignment"] = align switch
        {
            0 => "Left",
            1 => "Center",
            2 => "Right",
            _ => "Left",
        };

        // Line spacing
        var lineSpacing = p.GetFloat("LineSpacing", 0.3f);
        provider.Parameters["LineSpacing"] = lineSpacing.ToString("F2", CultureInfo.InvariantCulture);

        // Stroke thickness
        var strokeThickness = p.GetFloat("StrokeThickness", 2f);
        provider.Parameters["StrokeWidth"] = strokeThickness.ToString("F1", CultureInfo.InvariantCulture);

        // Stroke colour
        var strokeR = (int)Math.Round(p.GetFloat("StrokeR", 0f) / ushort.MaxValue * 255f);
        var strokeG = (int)Math.Round(p.GetFloat("StrokeG", 0f) / ushort.MaxValue * 255f);
        var strokeB = (int)Math.Round(p.GetFloat("StrokeB", 0f) / ushort.MaxValue * 255f);
        if (strokeR > 0 || strokeG > 0 || strokeB > 0)
            provider.Parameters["StrokeColor"] = $"#{strokeR:X2}{strokeG:X2}{strokeB:X2}";

        // Font name & style
        if (p.TryGetValue("FontName", out var fn) && fn is string fontName && !string.IsNullOrEmpty(fontName))
            provider.Parameters["FontFamily"] = fontName;
        if (p.TryGetValue("FontStyle", out var fst) && fst is string fontStyle && !string.IsNullOrEmpty(fontStyle))
            provider.Parameters["FontStyle"] = fontStyle;
    }

    /// <summary>
    /// Copy the provider's string-keyed parameters back into the component's
    /// typed parameters so that <see cref="TextComponent.Compute"/> can
    /// build the correct <see cref="Drawing.Text.Entry.TextEntry"/>.
    /// </summary>
    private static void SyncProviderToComponent(TextComponent component, ITextClipStyleProvider provider)
    {
        var p = provider.Parameters;

        // Text
        if (p.TryGetValue("Text", out var text))
            component.Parameters["Text"] = text;

        // Font name
        if (p.TryGetValue("FontFamily", out var fontName))
            component.Parameters["FontName"] = fontName ?? string.Empty;

        // Font style
        if (p.TryGetValue("FontStyle", out var fontStyle))
            component.Parameters["FontStyle"] = fontStyle ?? "Regular";

        // FontSize – string → float
        if (p.TryGetValue("Size", out var sizeStr) &&
            float.TryParse(sizeStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var size))
        {
            component.Parameters["FontSize"] = Math.Clamp(size, 1f, 5000f);
        }

        // TextAlignment – string → int
        if (p.TryGetValue("HorizontalAlignment", out var hAlign))
        {
            component.Parameters["TextAlignment"] = hAlign switch
            {
                "Center" => 1,
                "Right" => 2,
                _ => 0,
            };
        }

        // Line spacing – string → float
        if (p.TryGetValue("LineSpacing", out var lsStr) &&
            float.TryParse(lsStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var ls))
        {
            component.Parameters["LineSpacing"] = Math.Clamp(ls, 0f, 5f);
        }

        // Stroke thickness – string → float
        if (p.TryGetValue("StrokeWidth", out var swStr) &&
            float.TryParse(swStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var sw))
        {
            component.Parameters["StrokeThickness"] = Math.Max(0f, sw);
        }

        // Fill colour – hex → (R, G, B) ushort channels
        if (p.TryGetValue("Color", out var colorHex) && TryParseHexColor(colorHex, out var r, out var g, out var b))
        {
            component.Parameters["FillR"] = r;
            component.Parameters["FillG"] = g;
            component.Parameters["FillB"] = b;
        }

        // Stroke colour
        if (p.TryGetValue("StrokeColor", out var strokeHex) && TryParseHexColor(strokeHex, out var sr, out var sg, out var sb))
        {
            component.Parameters["StrokeR"] = sr;
            component.Parameters["StrokeG"] = sg;
            component.Parameters["StrokeB"] = sb;
            component.Parameters["StrokeA"] = 1f;
        }
    }

    private static bool TryParseHexColor(string? hex, out float r, out float g, out float b)
    {
        r = g = b = 0f;
        if (string.IsNullOrWhiteSpace(hex)) return false;

        hex = hex.TrimStart('#');
        if (hex.Length != 6 && hex.Length != 8) return false;

        try
        {
            var val = Convert.ToUInt32(hex, 16);
            r = (val >> 16) & 0xFF;
            g = (val >> 8) & 0xFF;
            b = val & 0xFF;
            // Scale from 0-255 to 0-65535
            r = r / 255f * ushort.MaxValue;
            g = g / 255f * ushort.MaxValue;
            b = b / 255f * ushort.MaxValue;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
