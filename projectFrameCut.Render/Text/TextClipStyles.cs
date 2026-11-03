using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Text;
using System.Text.Json;

namespace projectFrameCut.Render.Text;

public static class TextClipStyles
{
    public const string ArgumentPrefix = "TextStyle.";
    public const string FromKey = "TextStyleProvider_FromPlugin";
    public const string TypeKey = "TextStyleProvider_TypeName";
    public const string ParametersKey = "TextStyleProvider_Parameters";
    public const string FieldsKey = "TextStyleProvider_Fields";
    public const string MetaDataKey = "TextStyleProvider_MetaData";
    public const string StateKey = "TextStyleProvider_State";
    public const string TextKey = "TextStyleProvider_Text";

    public static ITextClipStyleProvider? Create(string fromPlugin, string typeName)
    {
        if (!PluginManager.LoadedPlugins.TryGetValue(fromPlugin, out var plugin)
            || !plugin.TextClipStyleProvider.TryGetValue(typeName, out var factory)) return null;
        return factory();
    }

    public static ITextClipStyleProvider? Restore(IReadOnlyDictionary<string, object>? data)
    {
        if (data is null) return null;
        if (data.TryGetValue(StateKey, out var raw))
        {
            var state = raw is TextClipStyleState s ? s
                : JsonSerializer.Deserialize<TextClipStyleState>(raw is JsonElement j ? j.GetRawText() : JsonSerializer.Serialize(raw))
                    ?? throw new InvalidOperationException("Empty text style state.");
            var restored = Create(state.FromPlugin, state.TypeName);
            if (restored is null) return null;
            restored.ImportStyle(state);
            if (data.TryGetValue(TextKey, out var text) && !state.Bindings.ContainsKey("Text")) restored.BasicText = text.ToString() ?? "";
            return restored;
        }
        string from = data.GetValueOrDefault(FromKey)?.ToString() ?? "";
        string type = data.GetValueOrDefault(TypeKey)?.ToString() ?? "";
        var source = Create(from, type);
        if (source is null) return null;
        if (ReadDictionary(data.GetValueOrDefault(ParametersKey)) is { } legacy)
            source.RestoreParameters(legacy.ToDictionary(p => p.Key, p => p.Value.ToString() ?? ""));
        if (ReadDictionary(data.GetValueOrDefault(MetaDataKey)) is { } metadata) source.MetaData = metadata;
        if (ReadDictionary(data.GetValueOrDefault(FieldsKey)) is { } fields)
            foreach (var (key, value) in fields)
                if (source.SettableFields.TryGetValue(key, out var descriptor)
                    && TextStyleFieldHelper.TryNormalizeValue(descriptor, value, out var normalized, out _))
                    source.Fields[key] = new StaticEffectArgumentField(normalized, descriptor.FieldType) { Id = key };
        return source;
    }

    public static object ReadValue(ITextClipStyleProvider source, string key)
    {
        var descriptor = source.SettableFields[key];
        if (source.Fields.TryGetValue(key, out var field)) return field.GetGetter()();
        return TextStyleFieldHelper.TryNormalizeValue(descriptor, descriptor.DefaultValue, out var value, out _)
            ? value : descriptor.DefaultValue;
    }

    public static void Save(ITextClipStyleProvider source, IDictionary<string, object> data)
    {
        data[FromKey] = source.FromPlugin;
        data[TypeKey] = source.TypeName;
        data[StateKey] = source.ExportStyle();
        if (source.Fields.TryGetValue("Text", out var textField) && textField.IsDynamic) data.Remove(TextKey);
        else data[TextKey] = source.BasicText;
        data.Remove(FieldsKey);
        data.Remove(MetaDataKey);
        data.Remove(ParametersKey);
    }

    public static ITextClipStyleProvider Copy(ITextClipStyleProvider source)
    {
        var copy = Create(source.FromPlugin, source.TypeName)
            ?? throw new InvalidOperationException($"Text style '{source.TypeName}' is unavailable.");
        copy.ImportStyle(source.ExportStyle());
        if (!source.Fields.TryGetValue("Text", out var textField) || !textField.IsDynamic) copy.BasicText = source.BasicText;
        return copy;
    }

    private static Dictionary<string, object>? ReadDictionary(object? raw)
    {
        if (raw is Dictionary<string, object> values) return values;
        if (raw is Dictionary<string, string> strings) return strings.ToDictionary(p => p.Key, p => (object)p.Value);
        if (raw is null) return null;
        var json = raw is JsonElement element ? element : JsonSerializer.SerializeToElement(raw);
        if (json.ValueKind == JsonValueKind.String) json = JsonSerializer.Deserialize<JsonElement>(json.GetString()!);
        return json.ValueKind == JsonValueKind.Object ? json.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? p.Value.Clone() : EffectParamConvert.Normalize(p.Value) ?? "") : null;
    }
}
