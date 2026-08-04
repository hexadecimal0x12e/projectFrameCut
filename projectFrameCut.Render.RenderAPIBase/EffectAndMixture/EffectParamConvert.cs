using System;
using System.Globalization;
using System.Text.Json;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    /// <summary>
    /// Pure parameter-conversion helpers shared by the Render-side <see cref="EffectProviderBase"/>
    /// and the App-layer UI providers. It has no dependency on any UI/MAUI type so it can live in the
    /// Render core. The same helpers used to live in the App-side <c>EffectProviderHelper</c>; keep them
    /// in sync if you change the behavior here.
    /// </summary>
    public static class EffectParamConvert
    {
        /// <summary>
        /// Normalizes a raw <see cref="JsonElement"/> value (as produced by deserialization) into a plain CLR object.
        /// </summary>
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

        private static bool TryParseDouble(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value)
                || double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value);
        }
    }
}
