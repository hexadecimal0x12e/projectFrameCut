using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Services;
using System.Globalization;
using System.Text.Json;

namespace projectFrameCut.ApplicationPluginBase.Text;

internal static class TextStyleProviderSettableFieldHelper
{
    internal static EffectArgumentFieldDescriptor StringField(
        string id,
        string displayName,
        string description,
        string defaultValue) => new()
        {
            Id = id,
            TypeName = "TextClipStyleField",
            FieldType = EffectArgumentFieldType.String,
            DefaultValue = defaultValue,
            MinValue = "",
            MaxValue = "",
            Remarks = description
        };

    internal static EffectArgumentFieldDescriptor NumericField(
        string id,
        string displayName,
        string description,
        float? defaultValue,
        float? min = null,
        float? max = null,
        bool mandatory = true)
    {
        var fieldType = EffectArgumentFieldType.Numeric;
        if (min.HasValue) fieldType |= EffectArgumentFieldType.HasMinValue;
        if (max.HasValue) fieldType |= EffectArgumentFieldType.HasMaxValue;
        if (mandatory) fieldType |= EffectArgumentFieldType.Mandatory;

        return new EffectArgumentFieldDescriptor
        {
            Id = id,
            TypeName = "TextClipStyleField",
            FieldType = fieldType,
            DefaultValue = defaultValue?.ToString(CultureInfo.InvariantCulture) ?? "",
            MinValue = min?.ToString(CultureInfo.InvariantCulture) ?? "",
            MaxValue = max?.ToString(CultureInfo.InvariantCulture) ?? "",
            Remarks = description
        };
    }

    internal static EffectArgumentFieldDescriptor IntegerField(
        string id,
        string displayName,
        string description,
        int defaultValue,
        int? min = null,
        int? max = null,
        bool mandatory = true)
    {
        var fieldType = EffectArgumentFieldType.Integer;
        if (min.HasValue) fieldType |= EffectArgumentFieldType.HasMinValue;
        if (max.HasValue) fieldType |= EffectArgumentFieldType.HasMaxValue;
        if (mandatory) fieldType |= EffectArgumentFieldType.Mandatory;

        return new EffectArgumentFieldDescriptor
        {
            Id = id,
            TypeName = "TextClipStyleField",
            FieldType = fieldType,
            DefaultValue = defaultValue.ToString(CultureInfo.InvariantCulture),
            MinValue = min?.ToString(CultureInfo.InvariantCulture) ?? "",
            MaxValue = max?.ToString(CultureInfo.InvariantCulture) ?? "",
            Remarks = description
        };
    }

    internal static EffectArgumentFieldDescriptor BooleanField(
        string id,
        string displayName,
        string description,
        bool defaultValue) => new()
        {
            Id = id,
            TypeName = "TextClipStyleField",
            FieldType = EffectArgumentFieldType.Boolean,
            DefaultValue = defaultValue.ToString().ToLowerInvariant(),
            MinValue = "",
            MaxValue = "",
            Remarks = description
        };

    internal static EffectArgumentFieldDescriptor EnumField(
        string id,
        string displayName,
        string description,
        string defaultValue,
        string[] presetOptions) => new()
        {
            Id = id,
            TypeName = "TextClipStyleField",
            FieldType = EffectArgumentFieldType.String,
            DefaultValue = defaultValue,
            MinValue = "",
            MaxValue = "",
            PresetOptions = presetOptions,
            Remarks = description
        };

    internal static EffectArgumentFieldDescriptor ColorField(
        string id,
        string displayName,
        string description,
        string defaultValue) => new()
        {
            Id = id,
            TypeName = "TextClipStyleField",
            FieldType = EffectArgumentFieldType.Color,
            DefaultValue = defaultValue,
            MinValue = "",
            MaxValue = "",
            Remarks = "Use a 16-bit RGBA JSON object with r, g, b and optional a."
        };

    internal static string[] GetAvailableFontNames() =>
        TextServices.LoadedFonts.Values
            .Where(font => font is not null && !string.IsNullOrWhiteSpace(font.FontName))
            .Select(font => font.FontName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(font => font, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal static bool TryNormalizeValue(
        EffectArgumentFieldDescriptor field,
        object? value,
        out object normalizedValue,
        out string feedback)
    {
        var baseType = field.FieldType & (EffectArgumentFieldType)0x7FF;
        var normalizedInput = NormalizeJsonValue(value);

        // Enum fields are String-type with PresetOptions set
        if (baseType == EffectArgumentFieldType.String && field.PresetOptions is { Length: > 0 })
            baseType = EffectArgumentFieldType.String; // handled by TryNormalizeEnum below

        switch (baseType)
        {
            case EffectArgumentFieldType.String when field.PresetOptions is { Length: > 0 }:
                return TryNormalizeEnum(field, normalizedInput, out normalizedValue, out feedback);
            case EffectArgumentFieldType.String:
                normalizedValue = normalizedInput?.ToString() ?? "";
                feedback = "";
                return true;
            case EffectArgumentFieldType.Numeric:
                return TryNormalizeNumeric(field, normalizedInput, out normalizedValue, out feedback);
            case EffectArgumentFieldType.Integer:
                return TryNormalizeInteger(field, normalizedInput, out normalizedValue, out feedback);
            case EffectArgumentFieldType.Boolean:
                return TryNormalizeBoolean(field, normalizedInput, out normalizedValue, out feedback);
            case EffectArgumentFieldType.Color:
                return TryNormalizeColor(field, value, out normalizedValue, out feedback);
            default:
                normalizedValue = "";
                feedback = $"Unsupported field type '{baseType}' for field '{field.Id}'.";
                return false;
        }
    }

    private static bool TryNormalizeNumeric(
        EffectArgumentFieldDescriptor field,
        object? value,
        out object normalizedValue,
        out string feedback)
    {
        if (value is null || value is string text && string.IsNullOrWhiteSpace(text))
        {
            if (!field.FieldType.HasFlag(EffectArgumentFieldType.Mandatory))
            {
                normalizedValue = "";
                feedback = "";
                return true;
            }

            normalizedValue = "";
            feedback = $"A value is required for field '{field.Id}'.";
            return false;
        }

        if (!TryConvertToFloat(value, out var parsed))
        {
            normalizedValue = "";
            feedback = $"Cannot convert value '{value}' to number for field '{field.Id}'.";
            return false;
        }

        if (!ValidateRange(field, parsed, out feedback))
        {
            normalizedValue = "";
            return false;
        }

        normalizedValue = parsed;
        return true;
    }

    private static bool TryNormalizeInteger(
        EffectArgumentFieldDescriptor field,
        object? value,
        out object normalizedValue,
        out string feedback)
    {
        if (!TryConvertToInt(value, out var parsed))
        {
            normalizedValue = "";
            feedback = $"Cannot convert value '{value}' to integer for field '{field.Id}'.";
            return false;
        }

        if (!ValidateRange(field, parsed, out feedback))
        {
            normalizedValue = "";
            return false;
        }

        normalizedValue = parsed;
        return true;
    }

    private static bool TryNormalizeBoolean(
        EffectArgumentFieldDescriptor field,
        object? value,
        out object normalizedValue,
        out string feedback)
    {
        if (value is bool boolean)
        {
            normalizedValue = boolean;
            feedback = "";
            return true;
        }

        if (bool.TryParse(value?.ToString(), out var parsed))
        {
            normalizedValue = parsed;
            feedback = "";
            return true;
        }

        normalizedValue = false;
        feedback = $"Cannot convert value '{value}' to boolean for field '{field.Id}'.";
        return false;
    }

    private static bool TryNormalizeEnum(
        EffectArgumentFieldDescriptor field,
        object? value,
        out object normalizedValue,
        out string feedback)
    {
        var text = value?.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            normalizedValue = "";
            feedback = $"A value is required for enum field '{field.Id}'.";
            return false;
        }

        var canonicalValue = field.PresetOptions?
            .FirstOrDefault(option => string.Equals(option, text, StringComparison.OrdinalIgnoreCase));
        if (canonicalValue is null)
        {
            normalizedValue = "";
            feedback = $"Value '{text}' is not a valid option for field '{field.Id}'. Valid options: {string.Join(", ", field.PresetOptions ?? [])}.";
            return false;
        }

        normalizedValue = canonicalValue;
        feedback = "";
        return true;
    }

    private static bool TryNormalizeColor(
        EffectArgumentFieldDescriptor field,
        object? value,
        out object normalizedValue,
        out string feedback)
    {
        if (value is string text && TryNormalizeHexColor(text, out var hexColor))
        {
            normalizedValue = hexColor;
            feedback = "";
            return true;
        }

        if (!TryGetJsonElement(value, out var element) || element.ValueKind != JsonValueKind.Object
            || !TryGetUShort(element, "r", out var red)
            || !TryGetUShort(element, "g", out var green)
            || !TryGetUShort(element, "b", out var blue))
        {
            normalizedValue = "";
            feedback = $"Value for field '{field.Id}' must be a 16-bit RGBA JSON object.";
            return false;
        }

        var alpha = 1f;
        var hasAlpha = element.TryGetProperty("a", out var alphaElement)
            && alphaElement.ValueKind != JsonValueKind.Null;
        if (hasAlpha && (!alphaElement.TryGetSingle(out alpha)
            || float.IsNaN(alpha) || float.IsInfinity(alpha) || alpha is < 0f or > 1f))
        {
            normalizedValue = "";
            feedback = $"Alpha for field '{field.Id}' must be between 0 and 1, or null.";
            return false;
        }

        var r8 = (byte)Math.Round(red / 65535d * 255d);
        var g8 = (byte)Math.Round(green / 65535d * 255d);
        var b8 = (byte)Math.Round(blue / 65535d * 255d);
        normalizedValue = hasAlpha
            ? $"#{(byte)Math.Round(alpha * 255f):X2}{r8:X2}{g8:X2}{b8:X2}"
            : $"#{r8:X2}{g8:X2}{b8:X2}";
        feedback = "";
        return true;
    }

    private static bool ValidateRange(
        EffectArgumentFieldDescriptor field,
        double value,
        out string feedback)
    {
        if (field.FieldType.HasFlag(EffectArgumentFieldType.HasMinValue)
            && double.TryParse(field.MinValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var min)
            && value < min)
        {
            feedback = $"Value {value} is less than minimum {min} for field '{field.Id}'.";
            return false;
        }

        if (field.FieldType.HasFlag(EffectArgumentFieldType.HasMaxValue)
            && double.TryParse(field.MaxValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var max)
            && value > max)
        {
            feedback = $"Value {value} exceeds maximum {max} for field '{field.Id}'.";
            return false;
        }

        feedback = "";
        return true;
    }

    private static object? NormalizeJsonValue(object? value)
    {
        if (value is JsonDocument document)
            value = document.RootElement;

        if (value is not JsonElement element)
            return value;

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt32(out var integer) => integer,
            JsonValueKind.Number when element.TryGetDouble(out var number) => number,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element
        };
    }

    private static bool TryConvertToFloat(object? value, out float result)
    {
        switch (value)
        {
            case float single when !float.IsNaN(single) && !float.IsInfinity(single):
                result = single;
                return true;
            case double number when !double.IsNaN(number) && !double.IsInfinity(number)
                && number is >= -float.MaxValue and <= float.MaxValue:
                result = (float)number;
                return true;
            case decimal number:
                result = (float)number;
                return true;
            case int integer:
                result = integer;
                return true;
            case long integer:
                result = integer;
                return true;
            default:
                return float.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result)
                    && !float.IsNaN(result)
                    && !float.IsInfinity(result);
        }
    }

    private static bool TryConvertToInt(object? value, out int result)
    {
        switch (value)
        {
            case int integer:
                result = integer;
                return true;
            case long integer when integer is >= int.MinValue and <= int.MaxValue:
                result = (int)integer;
                return true;
            case double number when !double.IsNaN(number) && !double.IsInfinity(number)
                && number == Math.Truncate(number)
                && number is >= int.MinValue and <= int.MaxValue:
                result = (int)number;
                return true;
            default:
                return int.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }
    }

    private static bool TryNormalizeHexColor(string value, out string normalized)
    {
        var text = value.Trim();
        if (text.Length is 7 or 9 && text[0] == '#'
            && uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            normalized = text.ToUpperInvariant();
            return true;
        }

        normalized = "";
        return false;
    }

    private static bool TryGetJsonElement(object? value, out JsonElement element)
    {
        switch (value)
        {
            case JsonDocument document:
                element = document.RootElement;
                return true;
            case JsonElement jsonElement:
                element = jsonElement;
                return true;
            default:
                element = default;
                return false;
        }
    }

    private static bool TryGetUShort(JsonElement element, string propertyName, out ushort value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property)
            && property.TryGetUInt16(out value);
    }
}
