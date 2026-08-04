using Microsoft.Maui.Controls;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Plugin;
using System;
using System.Collections.Generic;

namespace projectFrameCut.ApplicationPluginBase.Effect
{
    /// <summary>
    /// UI helpers shared by the App-layer effect providers and UI wrappers.
    /// Pure value conversions live in <see cref="EffectParamConvert"/> (Render core).
    /// </summary>
    internal static class EffectProviderHelper
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
            return parameters.TryGetValue(key, out var raw) && EffectParamConvert.TryConvertToInt(raw, out var value) ? value : fallback;
        }

        public static float GetFloat(IDictionary<string, object> parameters, string key, float fallback)
        {
            return parameters.TryGetValue(key, out var raw) && EffectParamConvert.TryConvertToFloat(raw, out var value) ? value : fallback;
        }

        public static ushort GetUShort(IDictionary<string, object> parameters, string key, ushort fallback)
        {
            return parameters.TryGetValue(key, out var raw) && EffectParamConvert.TryConvertToUShort(raw, out var value) ? value : fallback;
        }

        public static bool GetBool(IDictionary<string, object> parameters, string key, bool fallback)
        {
            return parameters.TryGetValue(key, out var raw) && EffectParamConvert.TryConvertToBool(raw, out var value) ? value : fallback;
        }

        public static string GetString(IDictionary<string, object> parameters, string key, string fallback)
        {
            if (!parameters.TryGetValue(key, out var raw))
            {
                return fallback;
            }

            raw = EffectParamConvert.Normalize(raw);
            return raw?.ToString() ?? fallback;
        }

        public static bool TrySetInt(IDictionary<string, object> parameters, string key, object? value)
        {
            if (!EffectParamConvert.TryConvertToInt(value, out var parsed))
            {
                return false;
            }

            parameters[key] = parsed;
            return true;
        }

        public static bool TrySetFloat(IDictionary<string, object> parameters, string key, object? value)
        {
            if (!EffectParamConvert.TryConvertToFloat(value, out var parsed))
            {
                return false;
            }

            parameters[key] = parsed;
            return true;
        }

        public static bool TrySetUShort(IDictionary<string, object> parameters, string key, object? value)
        {
            if (!EffectParamConvert.TryConvertToUShort(value, out var parsed))
            {
                return false;
            }

            parameters[key] = parsed;
            return true;
        }

        public static bool TrySetBool(IDictionary<string, object> parameters, string key, object? value)
        {
            if (!EffectParamConvert.TryConvertToBool(value, out var parsed))
            {
                return false;
            }

            parameters[key] = parsed;
            return true;
        }

        #region Fields-based helpers (IEffectProvider.Fields)

        public static int GetFieldInt(Dictionary<string, IEffectArgumentField> fields, string key, int fallback)
        {
            return fields.TryGetValue(key, out var field) && field is StaticEffectArgumentField sf
                && EffectParamConvert.TryConvertToInt(sf.Value, out var v) ? v : fallback;
        }

        public static float GetFieldFloat(Dictionary<string, IEffectArgumentField> fields, string key, float fallback)
        {
            return fields.TryGetValue(key, out var field) && field is StaticEffectArgumentField sf
                && EffectParamConvert.TryConvertToFloat(sf.Value, out var v) ? v : fallback;
        }

        public static ushort GetFieldUShort(Dictionary<string, IEffectArgumentField> fields, string key, ushort fallback)
        {
            return fields.TryGetValue(key, out var field) && field is StaticEffectArgumentField sf
                && EffectParamConvert.TryConvertToUShort(sf.Value, out var v) ? v : fallback;
        }

        public static bool GetFieldBool(Dictionary<string, IEffectArgumentField> fields, string key, bool fallback)
        {
            return fields.TryGetValue(key, out var field) && field is StaticEffectArgumentField sf
                && EffectParamConvert.TryConvertToBool(sf.Value, out var v) ? v : fallback;
        }

        public static string GetFieldString(Dictionary<string, IEffectArgumentField> fields, string key, string fallback)
        {
            if (!fields.TryGetValue(key, out var field) || field is not StaticEffectArgumentField sf)
                return fallback;
            var raw = EffectParamConvert.Normalize(sf.Value);
            return raw?.ToString() ?? fallback;
        }

        public static object? GetFieldRawValue(Dictionary<string, IEffectArgumentField> fields, string key)
        {
            if (fields.TryGetValue(key, out var field) && field is StaticEffectArgumentField sf)
                return sf.Value;
            return null;
        }

        public static void SetFieldValue(Dictionary<string, IEffectArgumentField> fields, string key, object value, EffectArgumentFieldType fieldType)
        {
            fields[key] = new StaticEffectArgumentField(value, fieldType);
        }

        #endregion
    }
}
