using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.RenderAPIBase.Text;

public abstract class TextClipStyleProviderBase : ITextClipStyleProvider
{
    public abstract string FromPlugin { get; }
    public abstract string TypeName { get; }
    public virtual string BasicText { get; set; } = "";
    public Dictionary<string, IEffectArgumentField> Fields { get; set; } = new();
    public Dictionary<string, object> MetaData { get; set; } = new();
    public abstract Dictionary<string, EffectArgumentFieldDescriptor> SettableFields { get; }
    public abstract bool AllowFreeRatioResize { get; }
    public abstract bool IsHorizontalResizable { get; }
    public abstract bool IsVerticalResizable { get; }
    public abstract bool CanSnapWhileResizing { get; }
    public abstract TextClipLayoutMode LayoutMode { get; set; }
    public abstract TextEntry[] BuildEntries();
    public abstract void HandleClipResize(bool isInRatio, int TargetX, int TargetY, int TargetWidth, int TargetHeight);
    public abstract ClipPositionTuple GetViewRect(int canvasWidth, int canvasHeight);

    public virtual bool HandleSettableFieldsChange(EffectArgumentFieldDescriptor field, object value, out string feedback)
    {
        if (!SettableFields.TryGetValue(field.Id, out var descriptor))
        {
            feedback = $"Unknown text style field '{field.Id}'.";
            return false;
        }
        if (!TextStyleFieldHelper.TryNormalizeValue(descriptor, value, out var normalized, out feedback)) return false;
        if (field.Id == "LayoutMode" && Enum.TryParse<TextClipLayoutMode>(normalized.ToString(), out var mode)) LayoutMode = mode;
        else Fields[field.Id] = new StaticEffectArgumentField(normalized, descriptor.FieldType) { Id = field.Id };
        return true;
    }
}
