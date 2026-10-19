using projectFrameCut.Shared;
using projectFrameCut.ApplicationAPIBase.Text;
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
