using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System.Collections;
using System.Globalization;

namespace projectFrameCut.Render.RenderAPIBase.Text;

/// <summary>A string view over typed fields and legacy metadata, without a second parameter store.</summary>
public sealed class TextStyleParameters(ITextClipStyleProvider source) : IDictionary<string, string>
{
    public string this[string key]
    {
        get => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);
        set
        {
            if (source.SettableFields.TryGetValue(key, out var descriptor))
            {
                if (!TextStyleFieldHelper.TryNormalizeValue(descriptor, value, out var normalized, out var feedback))
                    throw new ArgumentException(feedback, nameof(value));
                source.Fields[key] = new StaticEffectArgumentField(normalized, descriptor.FieldType) { Id = key };
            }
            else source.MetaData[key] = value;
        }
    }

    public ICollection<string> Keys => source.Fields.Keys.Union(source.MetaData.Keys).ToArray();
    public ICollection<string> Values => Keys.Select(key => this[key]).ToArray();
    public int Count => Keys.Count;
    public bool IsReadOnly => false;
    public bool ContainsKey(string key) => source.Fields.ContainsKey(key) || source.MetaData.ContainsKey(key);
    public bool TryGetValue(string key, out string value)
    {
        object? raw;
        if (source.Fields.TryGetValue(key, out var field)) raw = field.GetGetter()();
        else if (!source.MetaData.TryGetValue(key, out raw))
        {
            value = "";
            return false;
        }
        value = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "";
        return true;
    }

    public void Add(string key, string value) => this[key] = value;
    public bool Remove(string key) => source.Fields.Remove(key) | source.MetaData.Remove(key);
    public void Clear() { source.Fields.Clear(); source.MetaData.Clear(); }
    public void Add(KeyValuePair<string, string> item) => Add(item.Key, item.Value);
    public bool Contains(KeyValuePair<string, string> item) => TryGetValue(item.Key, out var value) && value == item.Value;
    public bool Remove(KeyValuePair<string, string> item) => Contains(item) && Remove(item.Key);
    public void CopyTo(KeyValuePair<string, string>[] array, int arrayIndex)
    {
        foreach (var item in this) array[arrayIndex++] = item;
    }
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => Keys.Select(key => new KeyValuePair<string, string>(key, this[key])).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class TextStyleParameterExtensions
{
    public static TextStyleParameters GetParameters(this ITextClipStyleProvider source) => new(source);

    public static void RestoreParameters(this ITextClipStyleProvider source, IEnumerable<KeyValuePair<string, string>> parameters)
    {
        foreach (var (key, value) in parameters)
        {
            if (source.SettableFields.TryGetValue(key, out var field))
            {
                if (TextStyleFieldHelper.TryNormalizeValue(field, value, out var normalized, out _))
                    source.Fields[key] = new StaticEffectArgumentField(normalized, field.FieldType) { Id = key };
            }
            else source.MetaData[key] = value;
        }
    }
}
