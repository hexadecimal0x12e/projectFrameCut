using projectFrameCut.Shared;
using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationPluginBase.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Text;
using projectFrameCut.Render.Text;
using projectFrameCut.Services;

namespace projectFrameCut.ApplicationPluginBase.Text;

public class TextClipStyleUIProvider : ITextClipStyleUIProvider
{
    public virtual (string name, string description) GetLocalizedFieldInfo(ITextClipStyleProvider source, string fieldId, string locale)
    {
        var field = source.SettableFields.GetValueOrDefault(fieldId);
        var key = fieldId switch
        {
            "Text" => "TextOption_Content",
            "FontFamily" => "TextOption_Font",
            "FontSize" => "TextStyle_Field_FontSize",
            "Color" => "TextOption_Color",
            "FontStyle" => "TextOption_Style",
            "HorizontalAlignment" => "TextOption_HorizonOption",
            "VerticalAlignment" => "TextOption_VerticalOption",
            "LayoutMode" => "TextOption_LayoutMode",
            "FixedHeightValue" => "TextOption_LayoutMode_FixedHeight",
            "WrappingWidth" => "TextOption_WrapW",
            "ApplyKerning" => "TextOption_Kerning",
            "LineSpacing" => "TextOption_LineSpacing",
            "Rotation" => "TextOption_Rotation",
            "StrokeWidth" => "TextStyle_Field_StrokeWidth",
            "StrokeColor" => "TextStyle_Field_StrokeColor",
            "UseVerticalLayout" => "TextOption_UseVerticalLayout",
            "KeepNonCJKTextAsHorizontal" => "TextOption_KeepNonCJKHorizontal",
            "PinyinFontSizeRatio" => "TextStyle_Field_PinyinFontSizeRatio",
            "PinyinColor" => "TextStyle_Field_PinyinColor",
            "CharSpacing" => "TextStyle_Field_CharSpacing",
            _ => $"_{fieldId}",
        };
        var locales = ISimpleLocalizerBase.GetMapping();
        var loc = locales.GetValueOrDefault(locale) ?? locales.GetValueOrDefault("en-US");
        var name = fieldId == "Dpi" ? "DPI" : field?.DisplayName ?? fieldId;
        if (loc?.IsItemExist(key) == true) name = loc.DynamicLookup(key, name);
        return (EffectProviderDisplayDefaults.ResolveLocalized($"TextStyle_{source.TypeName}_{fieldId}",
                EffectProviderDisplayDefaults.ResolveLocalized(field?.LocalizedNameKey ?? key, name, locale), locale),
            EffectProviderDisplayDefaults.ResolveLocalized($"Description_TextStyle_{source.TypeName}_{fieldId}", field?.Remarks ?? "", locale));
    }

    private readonly Label glyphWarning = new()
    {
        TextColor = Microsoft.Maui.Graphics.Colors.OrangeRed,
        FontSize = 12,
        IsVisible = false,
    };

    private void UpdateGlyphWarning(ITextClipStyleProvider source)
    {
        if (!source.SettableFields.ContainsKey("FontSize")
            || !source.GetParameters().TryGetValue("FontFamily", out var font)) return;
        EffectParamConvert.TryConvertToFloat(TextClipStyles.ReadValue(source, "FontSize"), out float size);
        glyphWarning.Text = TextServices.GetMissingGlyphWarning(font, source.BasicText, size);
        glyphWarning.IsVisible = !string.IsNullOrWhiteSpace(glyphWarning.Text);
    }

    public virtual PropertyPanelBuilder CreateUI(ITextClipStyleProvider source)
    {
        var panel = new PropertyPanelBuilder();
        if (source.SettableFields.ContainsKey("FontSize"))
        {
            UpdateGlyphWarning(source);
            panel.AddCustomChild(glyphWarning);
        }
        var editor = new ClipArgumentProvider();
        editor.MetaData[TextClipStyles.FromKey] = source.FromPlugin;
        editor.MetaData[TextClipStyles.TypeKey] = source.TypeName;
        editor.Attach(source.SettableFields.ToDictionary(p => p.Key, p => new ClipArgumentFieldDescriptor
        {
            Id = p.Key, FieldType = p.Value.FieldType, DefaultValue = p.Value.DefaultValue,
            MinValue = p.Value.MinValue, MaxValue = p.Value.MaxValue, PresetOptions = p.Value.PresetOptions,
            Remarks = p.Value.Remarks, ReadValue = _ => TextClipStyles.ReadValue(source, p.Key),
        }), key => TextClipStyles.ReadValue(source, key));
        EffectProviderUIHelper.BuildUI(editor, panel, null,
            source.SettableFields.Keys.Except(["Text", "FontFamily", "LayoutMode"]));
        return panel;
    }

    public virtual (int newWidth, int newHeight) HandlePropertyPanelChange(ITextClipStyleProvider source, PropertyPanelPropertyChangedEventArgs args)
    {
        string key = args.Id.StartsWith("EffectProviderField|", StringComparison.Ordinal)
            ? args.Id[(args.Id.LastIndexOf('|') + 1)..] : args.Id;
        if (source.SettableFields.TryGetValue(key, out var field)
            && !source.HandleSettableFieldsChange(field, args.Value ?? "", out var feedback))
        {
            Logger.Log($"Text style '{source.TypeName}', field '{key}': {feedback}", "warn");
            return (0, 0);
        }
        if (key is "Text" or "FontFamily" or "FontSize") UpdateGlyphWarning(source);
        var rect = source.GetViewRect(1920, 1080);
        return (rect.TargetWidth, rect.TargetHeight);
    }
}
