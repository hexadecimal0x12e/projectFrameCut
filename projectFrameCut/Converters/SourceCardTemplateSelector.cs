using projectFrameCut.ViewModels;

namespace projectFrameCut.Converters;

public sealed class SourceCardTemplateSelector : DataTemplateSelector
{
    public DataTemplate AssetTemplate { get; set; } = null!;
    public DataTemplate ExternalSourceTemplate { get; set; } = null!;
    public DataTemplate AddTemplate { get; set; } = null!;

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container) => item switch
    {
        AssetItemViewModel => AssetTemplate,
        RpcVideoSourceItemViewModel => ExternalSourceTemplate,
        AddSourceCardViewModel => AddTemplate,
        _ => throw new ArgumentException("Unknown source card type.", nameof(item))
    };
}
