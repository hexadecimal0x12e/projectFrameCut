using System.Text.Json;

namespace projectFrameCut.Render.VectorContent;

public static class ParameterExtensions
{
    public static float GetFloat(this IDictionary<string, object> parameters, string key, float defaultValue = 0f)
    {
        if (!parameters.TryGetValue(key, out var value) || value is null)
        {
            return defaultValue;
        }

        return value switch
        {
            float f => f,
            double d => (float)d,
            int i => i,
            long l => l,
            uint u => u,
            ushort us => us,
            decimal m => (float)m,
            JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetSingle(),
            JsonElement je when je.ValueKind == JsonValueKind.String && float.TryParse(je.GetString(), out var parsed) => parsed,
            _ => defaultValue,
        };
    }

    public static ushort GetUShort(this IDictionary<string, object> parameters, string key, ushort defaultValue = 0)
    {
        var value = parameters.GetFloat(key, defaultValue);
        return (ushort)Math.Clamp((int)Math.Round(value), ushort.MinValue, ushort.MaxValue);
    }

    public static bool GetBool(this IDictionary<string, object> parameters, string key, bool defaultValue = false)
    {
        if (!parameters.TryGetValue(key, out var value) || value is null)
        {
            return defaultValue;
        }

        return value switch
        {
            bool b => b,
            JsonElement je when je.ValueKind == JsonValueKind.True => true,
            JsonElement je when je.ValueKind == JsonValueKind.False => false,
            JsonElement je when je.ValueKind == JsonValueKind.String && bool.TryParse(je.GetString(), out var parsed) => parsed,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => defaultValue,
        };
    }
}

