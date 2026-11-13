using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.RenderAPIBase.Text;

public interface ITextClipStyleProvider : IExtensibleObject
{
    new string FromPlugin { get; }
    new string TypeName { get; }
    string BasicText { get; set; }
    Dictionary<string, IEffectArgumentField> Fields { get; set; }
    Dictionary<string, object> MetaData { get; set; }
    Dictionary<string, EffectArgumentFieldDescriptor> SettableFields { get; }
    bool AllowFreeRatioResize { get; }
    bool IsHorizontalResizable { get; }
    bool IsVerticalResizable { get; }
    bool CanSnapWhileResizing { get; }
    TextClipLayoutMode LayoutMode { get; set; }
    TextEntry[] BuildEntries();
    /// <summary>Exports portable style state. The host persists the associated provider graph for dynamic bindings.</summary>
    TextClipStyleState ExportStyle() => TextClipStyleState.Export(this);
    void ImportStyle(TextClipStyleState state) => TextClipStyleState.Import(this, state);
    bool HandleSettableFieldsChange(EffectArgumentFieldDescriptor field, object value, out string feedback);
    void HandleClipResize(bool isInRatio, int TargetX, int TargetY, int TargetWidth, int TargetHeight);
    ClipPositionTuple GetViewRect(int canvasWidth, int canvasHeight);
}
