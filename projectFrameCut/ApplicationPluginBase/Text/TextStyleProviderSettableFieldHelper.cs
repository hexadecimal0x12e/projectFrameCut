using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.Services;
using System.Globalization;
using System.Text.Json;
using static projectFrameCut.ApplicationAPIBase.Effect.EffectBundleSettableFields;

namespace projectFrameCut.ApplicationPluginBase.Text;

internal static class TextStyleProviderSettableFieldHelper
{
    internal static EffectBundleSettableFields StringField(
        string id,
        string displayName,
        string description,
        string defaultValue) => new()
        {
            Id = id,
            DisplayName = displayName,
            Description = description,
            ValueType = FieldType.String,
            DefaultValue = defaultValue,
            MinValue = "",
            MaxValue = ""
        };

    internal static EffectBundleSettableFields NumericField(
        string id,
        string displayName,
        string description,
        float? defaultValue,
        float? min = null,
        float? max = null,
        bool mandatory = true)
    {
        var valueType = FieldType.Numeric;
        if (min.HasValue) valueType |= FieldType.HasMinValue;
        if (max.HasValue) valueType |= FieldType.HasMaxValue;
        if (mandatory) valueType |= FieldType.Mandatory;

        return new EffectBundleSettableFields
        {
            Id = id,
            DisplayName = displayName,
            Description = description,
            ValueType = valueType,
            DefaultValue = defaultValue?.ToString(CultureInfo.InvariantCulture) ?? "",
            MinValue = min?.ToString(CultureInfo.InvariantCulture) ?? "",
            MaxValue = max?.ToString(CultureInfo.InvariantCulture) ?? ""
        };
    }

    internal static EffectBundleSettableFields IntegerField(
        string id,
        string displayName,
        string description,
        int defaultValue,
        int? min = null,
        int? max = null,
        bool mandatory = true)
    {
        var valueType = FieldType.Integer;
        if (min.HasValue) valueType |= FieldType.HasMinValue;
        if (max.HasValue) valueType |= FieldType.HasMaxValue;
        if (mandatory) valueType |= FieldType.Mandatory;

        return new EffectBundleSettableFields
        {
            Id = id,
            DisplayName = displayName,
            Description = description,
            ValueType = valueType,
            DefaultValue = defaultValue.ToString(CultureInfo.InvariantCulture),
            MinValue = min?.ToString(CultureInfo.InvariantCulture) ?? "",
            MaxValue = max?.ToString(CultureInfo.InvariantCulture) ?? ""
        };
    }

    internal static EffectBundleSettableFields BooleanField(
        string id,
        string displayName,
        string description,
        bool defaultValue) => new()
        {
            Id = id,
            DisplayName = displayName,
            Description = description,
            ValueType = FieldType.Boolean,
            DefaultValue = defaultValue.ToString().ToLowerInvariant(),
            MinValue = "",
            MaxValue = ""
        };

    internal static EffectBundleSettableFields EnumField(
        string id,
        string displayName,
        string description,
        string defaultValue,
        string[] presetOptions) => new()
        {
            Id = id,
            DisplayName = displayName,
            Description = description,
            ValueType = FieldType.Enum,
            DefaultValue = defaultValue,
            MinValue = "",
            MaxValue = "",
            PresetOptions = presetOptions
        };

    internal static EffectBundleSettableFields ColorField(
        string id,
        string displayName,
        string description,
        string defaultValue) => new()
        {
            Id = id,
            DisplayName = displayName,
            Description = description,
            ValueType = FieldType.Color,
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
        EffectBundleSettableFields field,
        object? value,
        out object normalizedValue,
        out string feedback)
    {
        var baseType = field.ValueType & (FieldType)0x7FF;
        var normalizedInput = NormalizeJsonValue(value);

        switch (baseType)
        {
            case FieldType.String:
                normalizedValue = normalizedInput?.ToString() ?? "";
                feedback = "";
                return true;
            case FieldType.Numeric:
                return TryNormalizeNumeric(field, normalizedInput, out normalizedValue, out feedback);
            case FieldType.Integer:
                return TryNormalizeInteger(field, normalizedInput, out normalizedValue, out feedback);
            case FieldType.Boolean:
                return TryNormalizeBoolean(field, normalizedInput, out normalizedValue, out feedback);
            case FieldType.Enum:
                return TryNormalizeEnum(field, normalizedInput, out normalizedValue, out feedback);
            case FieldType.Color:
                return TryNormalizeColor(field, value, out normalizedValue, out feedback);
            default:
                normalizedValue = "";
                feedback = $"Unsupported field type '{baseType}' for field '{field.DisplayName}'.";
                return false;
        }
    }

    private static bool TryNormalizeNumeric(
        EffectBundleSettableFields field,
        object? value,
        out object normalizedValue,
        out string feedback)
    {
        if (value is null || value is string text && string.IsNullOrWhiteSpace(text))
        {
            if (!field.ValueType.HasFlag(FieldType.Mandatory))
            {
                normalizedValue = "";
                feedback = "";
                return true;
            }

            normalizedValue = "";
            feedback = $"A value is required for field '{field.DisplayName}'.";
            return false;
        }

        if (!TryConvertToFloat(value, out var parsed))
        {
            normalizedValue = "";
            feedback = $"Cannot convert value '{value}' to number for field '{field.DisplayName}'.";
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
        EffectBundleSettableFields field,
        object? value,
        out object normalizedValue,
        out string feedback)
    {
        if (!TryConvertToInt(value, out var parsed))
        {
            normalizedValue = "";
            feedback = $"Cannot convert value '{value}' to integer for field '{field.DisplayName}'.";
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
        EffectBundleSettableFields field,
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
        feedback = $"Cannot convert value '{value}' to boolean for field '{field.DisplayName}'.";
        return false;
    }

    private static bool TryNormalizeEnum(
        EffectBundleSettableFields field,
        object? value,
        out object normalizedValue,
        out string feedback)
    {
        var text = value?.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            normalizedValue = "";
            feedback = $"A value is required for enum field '{field.DisplayName}'.";
            return false;
        }

        var canonicalValue = field.PresetOptions?
            .FirstOrDefault(option => string.Equals(option, text, StringComparison.OrdinalIgnoreCase));
        if (canonicalValue is null)
        {
            normalizedValue = "";
            feedback = $"Value '{text}' is not a valid option for field '{field.DisplayName}'. Valid options: {string.Join(", ", field.PresetOptions ?? [])}.";
            return false;
        }

        normalizedValue = canonicalValue;
        feedback = "";
        return true;
    }

    private static bool TryNormalizeColor(
        EffectBundleSettableFields field,
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
            feedback = $"Value for field '{field.DisplayName}' must be a 16-bit RGBA JSON object.";
            return false;
        }

        var alpha = 1f;
        var hasAlpha = element.TryGetProperty("a", out var alphaElement)
            && alphaElement.ValueKind != JsonValueKind.Null;
        if (hasAlpha && (!alphaElement.TryGetSingle(out alpha)
            || float.IsNaN(alpha) || float.IsInfinity(alpha) || alpha is < 0f or > 1f))
        {
            normalizedValue = "";
            feedback = $"Alpha for field '{field.DisplayName}' must be between 0 and 1, or null.";
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
        EffectBundleSettableFields field,
        double value,
        out string feedback)
    {
        if (field.ValueType.HasFlag(FieldType.HasMinValue)
            && double.TryParse(field.MinValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var min)
            && value < min)
        {
            feedback = $"Value {value} is less than minimum {min} for field '{field.DisplayName}'.";
            return false;
        }

        if (field.ValueType.HasFlag(FieldType.HasMaxValue)
            && double.TryParse(field.MaxValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var max)
            && value > max)
        {
            feedback = $"Value {value} exceeds maximum {max} for field '{field.DisplayName}'.";
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
