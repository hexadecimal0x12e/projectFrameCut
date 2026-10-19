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

    public static ITextClipStyleProvider? Create(string fromPlugin, string typeName)
    {
        if (!PluginManager.LoadedPlugins.TryGetValue(fromPlugin, out var plugin)
            || !plugin.TextClipStyleProvider.TryGetValue(typeName, out var factory)) return null;
        return factory();
    }

    public static ITextClipStyleProvider? Restore(IReadOnlyDictionary<string, object>? data)
    {
        if (data is null) return null;
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
        data[FieldsKey] = source.Fields.Where(p => !p.Value.IsDynamic)
            .ToDictionary(p => p.Key, p => p.Value.GetGetter()());
        data[MetaDataKey] = new Dictionary<string, object>(source.MetaData);
        data.Remove(ParametersKey);
    }

    public static ITextClipStyleProvider Copy(ITextClipStyleProvider source)
    {
        var copy = Create(source.FromPlugin, source.TypeName)
            ?? throw new InvalidOperationException($"Text style '{source.TypeName}' is unavailable.");
        copy.MetaData = new(source.MetaData);
        copy.Fields = source.Fields.ToDictionary(p => p.Key, p => (IEffectArgumentField)new StaticEffectArgumentField(
            p.Value.GetGetter()(), p.Value.FieldType) { Id = p.Key });
        copy.BasicText = source.BasicText;
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
