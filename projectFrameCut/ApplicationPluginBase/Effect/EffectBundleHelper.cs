using Microsoft.Maui.Controls;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.Plugin;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace projectFrameCut.ApplicationPluginBase.Effect
{
    internal static class EffectBundleHelper
    {
        public static string L(string key, string fallback)
        {
            return PluginManager.GetLocalizationItem(key, fallback);
        }

        public static string ParamLabel(string parameterName)
        {
            return L($"_{parameterName}", parameterName);
        }

        public static void AddNumericEntry(PropertyPanelBuilder panel, string id, string title, string value, string placeholder)
        {
            panel.AddEntry(id, title, value, placeholder, entry => entry.Keyboard = Keyboard.Numeric);
        }

        public static int GetInt(IDictionary<string, object> parameters, string key, int fallback)
        {
            return parameters.TryGetValue(key, out var raw) && TryConvertToInt(raw, out var value) ? value : fallback;
        }

        public static float GetFloat(IDictionary<string, object> parameters, string key, float fallback)
        {
            return parameters.TryGetValue(key, out var raw) && TryConvertToFloat(raw, out var value) ? value : fallback;
        }

        public static ushort GetUShort(IDictionary<string, object> parameters, string key, ushort fallback)
        {
            return parameters.TryGetValue(key, out var raw) && TryConvertToUShort(raw, out var value) ? value : fallback;
        }

        public static bool GetBool(IDictionary<string, object> parameters, string key, bool fallback)
        {
            return parameters.TryGetValue(key, out var raw) && TryConvertToBool(raw, out var value) ? value : fallback;
        }

        public static string GetString(IDictionary<string, object> parameters, string key, string fallback)
        {
            if (!parameters.TryGetValue(key, out var raw))
            {
                return fallback;
            }

            raw = Normalize(raw);
            return raw?.ToString() ?? fallback;
        }

        public static bool TrySetInt(IDictionary<string, object> parameters, string key, object? value)
        {
            if (!TryConvertToInt(value, out var parsed))
            {
                return false;
            }

            parameters[key] = parsed;
            return true;
        }

        public static bool TrySetFloat(IDictionary<string, object> parameters, string key, object? value)
        {
            if (!TryConvertToFloat(value, out var parsed))
            {
                return false;
            }

            parameters[key] = parsed;
            return true;
        }

        public static bool TrySetUShort(IDictionary<string, object> parameters, string key, object? value)
        {
            if (!TryConvertToUShort(value, out var parsed))
            {
                return false;
            }

            parameters[key] = parsed;
            return true;
        }

        public static bool TrySetBool(IDictionary<string, object> parameters, string key, object? value)
        {
            if (!TryConvertToBool(value, out var parsed))
            {
                return false;
            }

            parameters[key] = parsed;
            return true;
        }

        public static bool TryConvertToInt(object? raw, out int value)
        {
            raw = Normalize(raw);
            switch (raw)
            {
                case int i:
                    value = i;
                    return true;
                case short s:
                    value = s;
                    return true;
                case long l when l <= int.MaxValue && l >= int.MinValue:
                    value = (int)l;
                    return true;
                case uint ui when ui <= int.MaxValue:
                    value = (int)ui;
                    return true;
                case ushort us:
                    value = us;
                    return true;
                case byte b:
                    value = b;
                    return true;
                case sbyte sb:
                    value = sb;
                    return true;
                case float f when !float.IsNaN(f) && !float.IsInfinity(f):
                    value = (int)Math.Round(f, MidpointRounding.AwayFromZero);
                    return true;
                case double d when !double.IsNaN(d) && !double.IsInfinity(d):
                    value = (int)Math.Round(d, MidpointRounding.AwayFromZero);
                    return true;
                case decimal m when m <= int.MaxValue && m >= int.MinValue:
                    value = (int)Math.Round(m, MidpointRounding.AwayFromZero);
                    return true;
                case string text:
                    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                        || int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value))
                    {
                        return true;
                    }

                    if (TryParseDouble(text, out var parsedDouble)
                        && parsedDouble <= int.MaxValue
                        && parsedDouble >= int.MinValue)
                    {
                        value = (int)Math.Round(parsedDouble, MidpointRounding.AwayFromZero);
                        return true;
                    }
                    break;
            }

            value = 0;
            return false;
        }

        public static bool TryConvertToFloat(object? raw, out float value)
        {
            raw = Normalize(raw);
            switch (raw)
            {
                case float f when !float.IsNaN(f) && !float.IsInfinity(f):
                    value = f;
                    return true;
                case double d when !double.IsNaN(d) && !double.IsInfinity(d):
                    value = (float)d;
                    return true;
                case decimal m:
                    value = (float)m;
                    return true;
                case int i:
                    value = i;
                    return true;
                case long l:
                    value = l;
                    return true;
                case string text:
                    if (float.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value)
                        || float.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value))
                    {
                        return true;
                    }
                    break;
            }

            value = 0f;
            return false;
        }

        public static bool TryConvertToUShort(object? raw, out ushort value)
        {
            if (TryConvertToInt(raw, out var intValue) && intValue >= ushort.MinValue && intValue <= ushort.MaxValue)
            {
                value = (ushort)intValue;
                return true;
            }

            value = 0;
            return false;
        }

        public static bool TryConvertToBool(object? raw, out bool value)
        {
            raw = Normalize(raw);
            switch (raw)
            {
                case bool b:
                    value = b;
                    return true;
                case int i:
                    value = i != 0;
                    return true;
                case long l:
                    value = l != 0;
                    return true;
                case string text:
                    if (bool.TryParse(text, out value))
                    {
                        return true;
                    }

                    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue)
                        || int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out intValue))
                    {
                        value = intValue != 0;
                        return true;
                    }
                    break;
            }

            value = false;
            return false;
        }

        public static object? Normalize(object? raw)
        {
            if (raw is not JsonElement element)
            {
                return raw;
            }

            return element.ValueKind switch
            {
                JsonValueKind.Number => element.TryGetInt64(out var intVal)
                    ? intVal
                    : element.TryGetDouble(out var doubleVal)
                        ? doubleVal
                        : element.ToString(),
                JsonValueKind.String => element.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => element.ToString(),
            };
        }

        private static bool TryParseDouble(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value)
                || double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value);
        }

        #region SettableFields Helpers

        internal static EffectBundleSettableFields FloatField(
            string id,
            string displayName,
            string description,
            float defaultValue,
            float? min = null,
            float? max = null,
            string? remarks = null)
        {
            var valueType = EffectBundleSettableFields.FieldType.Numeric;
            if (min.HasValue) valueType |= EffectBundleSettableFields.FieldType.HasMinValue;
            if (max.HasValue) valueType |= EffectBundleSettableFields.FieldType.HasMaxValue;

            return new EffectBundleSettableFields
            {
                Id = id,
                DisplayName = displayName,
                Description = description,
                ValueType = valueType,
                DefaultValue = defaultValue.ToString(CultureInfo.InvariantCulture),
                MinValue = min?.ToString(CultureInfo.InvariantCulture) ?? "",
                MaxValue = max?.ToString(CultureInfo.InvariantCulture) ?? "",
                Remarks = remarks ?? ""
            };
        }

        internal static EffectBundleSettableFields IntField(
            string id,
            string displayName,
            string description,
            int defaultValue,
            int? min = null,
            int? max = null,
            string? remarks = null)
        {
            var valueType = EffectBundleSettableFields.FieldType.Integer;
            if (min.HasValue) valueType |= EffectBundleSettableFields.FieldType.HasMinValue;
            if (max.HasValue) valueType |= EffectBundleSettableFields.FieldType.HasMaxValue;

            return new EffectBundleSettableFields
            {
                Id = id,
                DisplayName = displayName,
                Description = description,
                ValueType = valueType,
                DefaultValue = defaultValue.ToString(CultureInfo.InvariantCulture),
                MinValue = min?.ToString(CultureInfo.InvariantCulture) ?? "",
                MaxValue = max?.ToString(CultureInfo.InvariantCulture) ?? "",
                Remarks = remarks ?? ""
            };
        }

        internal static EffectBundleSettableFields UShortField(
            string id,
            string displayName,
            string description,
            ushort defaultValue,
            ushort? min = null,
            ushort? max = null,
            string? remarks = null)
        {
            var valueType = EffectBundleSettableFields.FieldType.UnsignedInteger;
            if (min.HasValue) valueType |= EffectBundleSettableFields.FieldType.HasMinValue;
            if (max.HasValue) valueType |= EffectBundleSettableFields.FieldType.HasMaxValue;

            return new EffectBundleSettableFields
            {
                Id = id,
                DisplayName = displayName,
                Description = description,
                ValueType = valueType,
                DefaultValue = defaultValue.ToString(CultureInfo.InvariantCulture),
                MinValue = min?.ToString(CultureInfo.InvariantCulture) ?? "",
                MaxValue = max?.ToString(CultureInfo.InvariantCulture) ?? "",
                Remarks = remarks ?? ""
            };
        }

        internal static EffectBundleSettableFields BoolField(
            string id,
            string displayName,
            string description,
            bool defaultValue,
            string? remarks = null)
        {
            return new EffectBundleSettableFields
            {
                Id = id,
                DisplayName = displayName,
                Description = description,
                ValueType = EffectBundleSettableFields.FieldType.Boolean,
                DefaultValue = defaultValue ? "true" : "false",
                MinValue = "",
                MaxValue = "",
                Remarks = remarks ?? ""
            };
        }

        internal static EffectBundleSettableFields StringField(
            string id,
            string displayName,
            string description,
            string defaultValue,
            string? remarks = null)
        {
            return new EffectBundleSettableFields
            {
                Id = id,
                DisplayName = displayName,
                Description = description,
                ValueType = EffectBundleSettableFields.FieldType.String,
                DefaultValue = defaultValue,
                MinValue = "",
                MaxValue = "",
                Remarks = remarks ?? ""
            };
        }

        internal static EffectBundleSettableFields EnumField(
            string id,
            string displayName,
            string description,
            string defaultValue,
            string[] presetOptions,
            string? remarks = null)
        {
            return new EffectBundleSettableFields
            {
                Id = id,
                DisplayName = displayName,
                Description = description,
                ValueType = EffectBundleSettableFields.FieldType.Enum,
                DefaultValue = defaultValue,
                MinValue = "",
                MaxValue = "",
                PresetOptions = presetOptions,
                Remarks = remarks ?? ""
            };
        }

        internal static bool HandleSettableFieldChange(
            Dictionary<string, object> parameters,
            EffectBundleSettableFields field,
            object value,
            out string feedback)
        {
            if (field is null)
            {
                feedback = "Field definition is null.";
                return false;
            }

            if (parameters is null)
            {
                feedback = "Parameters dictionary is null.";
                return false;
            }

            // Extract the base type (bits 0-10 cover all type flags)
            var baseType = field.ValueType & (EffectBundleSettableFields.FieldType)0x7FF;

            switch (baseType)
            {
                case EffectBundleSettableFields.FieldType.Numeric:
                    if (!TryConvertToFloat(value, out var floatVal))
                    {
                        feedback = $"Cannot convert value '{value}' to number for field '{field.DisplayName}'.";
                        return false;
                    }
                    if (field.ValueType.HasFlag(EffectBundleSettableFields.FieldType.HasMinValue)
                        && float.TryParse(field.MinValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var fMin)
                        && floatVal < fMin)
                    {
                        feedback = $"Value {floatVal} is less than minimum {fMin} for field '{field.DisplayName}'.";
                        return false;
                    }
                    if (field.ValueType.HasFlag(EffectBundleSettableFields.FieldType.HasMaxValue)
                        && float.TryParse(field.MaxValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var fMax)
                        && floatVal > fMax)
                    {
                        feedback = $"Value {floatVal} exceeds maximum {fMax} for field '{field.DisplayName}'.";
                        return false;
                    }
                    parameters[field.Id] = floatVal;
                    feedback = "";
                    return true;

                case EffectBundleSettableFields.FieldType.Integer:
                    if (!TryConvertToInt(value, out var intVal))
                    {
                        feedback = $"Cannot convert value '{value}' to integer for field '{field.DisplayName}'.";
                        return false;
                    }
                    if (field.ValueType.HasFlag(EffectBundleSettableFields.FieldType.HasMinValue)
                        && int.TryParse(field.MinValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iMin)
                        && intVal < iMin)
                    {
                        feedback = $"Value {intVal} is less than minimum {iMin} for field '{field.DisplayName}'.";
                        return false;
                    }
                    if (field.ValueType.HasFlag(EffectBundleSettableFields.FieldType.HasMaxValue)
                        && int.TryParse(field.MaxValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iMax)
                        && intVal > iMax)
                    {
                        feedback = $"Value {intVal} exceeds maximum {iMax} for field '{field.DisplayName}'.";
                        return false;
                    }
                    parameters[field.Id] = intVal;
                    feedback = "";
                    return true;

                case EffectBundleSettableFields.FieldType.UnsignedInteger:
                    if (!TryConvertToUShort(value, out var usVal))
                    {
                        feedback = $"Cannot convert value '{value}' to unsigned integer for field '{field.DisplayName}'.";
                        return false;
                    }
                    if (field.ValueType.HasFlag(EffectBundleSettableFields.FieldType.HasMinValue)
                        && ushort.TryParse(field.MinValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var usMin)
                        && usVal < usMin)
                    {
                        feedback = $"Value {usVal} is less than minimum {usMin} for field '{field.DisplayName}'.";
                        return false;
                    }
                    if (field.ValueType.HasFlag(EffectBundleSettableFields.FieldType.HasMaxValue)
                        && ushort.TryParse(field.MaxValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var usMax)
                        && usVal > usMax)
                    {
                        feedback = $"Value {usVal} exceeds maximum {usMax} for field '{field.DisplayName}'.";
                        return false;
                    }
                    parameters[field.Id] = usVal;
                    feedback = "";
                    return true;

                case EffectBundleSettableFields.FieldType.Boolean:
                    if (!TryConvertToBool(value, out var boolVal))
                    {
                        feedback = $"Cannot convert value '{value}' to boolean for field '{field.DisplayName}'.";
                        return false;
                    }
                    parameters[field.Id] = boolVal;
                    feedback = "";
                    return true;

                case EffectBundleSettableFields.FieldType.String:
                case EffectBundleSettableFields.FieldType.CustomType:
                    parameters[field.Id] = value?.ToString() ?? "";
                    feedback = "";
                    return true;

                case EffectBundleSettableFields.FieldType.Enum:
                    var strValue = value?.ToString();
                    if (strValue is null)
                    {
                        feedback = $"Value cannot be null for enum field '{field.DisplayName}'.";
                        return false;
                    }
                    if (field.PresetOptions is not null && Array.IndexOf(field.PresetOptions, strValue) < 0)
                    {
                        feedback = $"Value '{strValue}' is not a valid option for field '{field.DisplayName}'. Valid options: {string.Join(", ", field.PresetOptions)}.";
                        return false;
                    }
                    parameters[field.Id] = strValue;
                    feedback = "";
                    return true;

                case EffectBundleSettableFields.FieldType.KeyFrames:
                case EffectBundleSettableFields.FieldType.Color:
                case EffectBundleSettableFields.FieldType.Size:
                case EffectBundleSettableFields.FieldType.Position:
                    feedback = $"Field type '{baseType}' for '{field.DisplayName}' requires custom handling in HandleSettableFieldsChange.";
                    return false;

                default:
                    feedback = $"Unsupported field type '{baseType}' for field '{field.DisplayName}'.";
                    return false;
            }
        }

        #endregion
    }
}