using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.Text;

namespace projectFrameCut.ApplicationAPIBase.Text;

public interface ITextClipStyleUIProvider : projectFrameCut.Render.RenderAPIBase.Plugins.IExtensibleObject
{
    bool ShowDefaultTextEditor => true;
    bool ShowLayoutModePicker => true;
    bool ShowFontPicker => true;
    PropertyPanelBuilder CreateUI(ITextClipStyleProvider source);
    (int newWidth, int newHeight) HandlePropertyPanelChange(ITextClipStyleProvider source, PropertyPanelPropertyChangedEventArgs args);
    (string name, string description) GetLocalizedFieldInfo(ITextClipStyleProvider source, string fieldId, string locale) =>
        (Effect.EffectProviderDisplayDefaults.ResolveLocalized($"TextStyle_{source.TypeName}_{fieldId}", fieldId, locale),
            Effect.EffectProviderDisplayDefaults.ResolveLocalized($"Description_TextStyle_{source.TypeName}_{fieldId}",
                source.SettableFields.GetValueOrDefault(fieldId)?.Remarks ?? "", locale));
}
