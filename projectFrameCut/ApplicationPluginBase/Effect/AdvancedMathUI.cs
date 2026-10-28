using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.ApplicationPluginBase.Effect;

public sealed class AdvancedMathUI(IEffectProvider inner) : EffectProviderUI(inner)
{
    public override (Dictionary<string, object>? newParams, Dictionary<string, IEffectArgumentField>? newFields)
        HandlePropertyPanelChange(IEffectProvider source, PropertyPanelPropertyChangedEventArgs args)
    {
        string id = EffectProviderUIHelper.FieldId(Inner, args.Id);
        if (id is not ("A" or "B" or "C")) return base.HandlePropertyPanelChange(source, args);
        var fields = Inner.Fields;
        try
        {
            EffectProviderHelper.SetFieldValue(fields, id, AdvancedMathEffectProvider.ParseNumber(args.Value ?? "0"), EffectArgumentFieldType.Numeric);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            return (null, null);
        }
        Inner.Fields = fields;
        return (null, fields);
    }
}
