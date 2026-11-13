using System.Text.Json;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.RenderAPIBase.Text;

public sealed class TextClipStyleState
{
    public int Version { get; set; } = 1;
    public string FromPlugin { get; set; } = "";
    public string TypeName { get; set; } = "";
    public TextClipLayoutMode LayoutMode { get; set; }
    public Dictionary<string, object> Fields { get; set; } = new();
    public Dictionary<string, string> Bindings { get; set; } = new();
    public Dictionary<string, object> MetaData { get; set; } = new();
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = new();

    public static TextClipStyleState Export(ITextClipStyleProvider source)
    {
        var state = new TextClipStyleState
        {
            FromPlugin = source.FromPlugin,
            TypeName = source.TypeName,
            LayoutMode = source.LayoutMode,
            MetaData = Clone(source.MetaData),
        };
        foreach (var (key, field) in source.Fields)
        {
            if (field is DynamicEffectParamField dynamic)
            {
                state.Fields[key] = JsonSerializer.SerializeToElement(dynamic.StaticFallbackValue);
                if (!string.IsNullOrEmpty(dynamic.BoundProviderId)) state.Bindings[key] = dynamic.BoundProviderId;
            }
            else if (field.IsDynamic)
                throw new NotSupportedException($"Text style field '{key}' must implement style export for '{field.TypeName}'.");
            else state.Fields[key] = JsonSerializer.SerializeToElement(field.GetGetter()());
        }
        return state;
    }

    public static void Import(ITextClipStyleProvider source, TextClipStyleState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Version != 1 || state.FromPlugin != source.FromPlugin || state.TypeName != source.TypeName)
            throw new InvalidOperationException("Unsupported text style version or provider.");
        if (!Enum.IsDefined(state.LayoutMode) || state.Fields is null || state.MetaData is null || state.Bindings is null)
            throw new InvalidOperationException("Invalid text style state.");
        if (state.Bindings.Keys.Any(key => !state.Fields.ContainsKey(key)))
            throw new InvalidOperationException("Text style binding has no fallback field.");
        var fields = new Dictionary<string, IEffectArgumentField>();
        foreach (var (key, value) in state.Fields)
        {
            if (!source.SettableFields.TryGetValue(key, out var descriptor))
                throw new InvalidOperationException($"Unknown text style field '{key}'.");
            if (!TextStyleFieldHelper.TryNormalizeValue(descriptor, value, out var normalized, out var feedback))
                throw new ArgumentException($"Text style field '{key}': {feedback}");
            if (state.Bindings.ContainsKey(key) && descriptor.FieldType.HasFlag(EffectArgumentFieldType.CannotBeDynamic))
                throw new ArgumentException($"Text style field '{key}' cannot be dynamic.");
            fields[key] = state.Bindings.TryGetValue(key, out var binding)
                ? new DynamicEffectParamField
                {
                    Id = key,
                    FieldType = descriptor.FieldType,
                    BoundProviderId = binding,
                    StaticFallbackValue = normalized,
                }
                : new StaticEffectArgumentField(normalized, descriptor.FieldType) { Id = key };
        }
        source.Fields = fields;
        source.MetaData = Clone(state.MetaData);
        source.LayoutMode = state.LayoutMode;
    }

    private static Dictionary<string, object> Clone(Dictionary<string, object> data) =>
        JsonSerializer.SerializeToElement(data).EnumerateObject().ToDictionary(p => p.Name,
            p => p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? (object)p.Value.Clone() : EffectParamConvert.Normalize(p.Value) ?? "");
}
