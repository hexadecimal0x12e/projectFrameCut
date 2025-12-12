using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    public static class EffectArgsHelper
    {
        public static Dictionary<string, object> ConvertElementDictToObjectDict(Dictionary<string, object> elements, Dictionary<string, string> ParametersType)
        {
            var result = new Dictionary<string, object>();
            foreach (var kvp in elements)
            {
                object value = null;
                JsonElement source = (JsonElement)kvp.Value;
                switch (ParametersType[kvp.Key])
                {
                    case "ushort":
                        value = source.GetUInt16();
                        break;
                    case "int":
                        value = source.GetInt32();
                        break;
                    case "float":
                        value = source.GetSingle();
                        break;
                    case "double":
                        value = source.GetDouble();
                        break;
                    case "string":
                        value = source.GetString()!;
                        break;
                    case "bool":
                        value = source.GetBoolean();
                        break;
                    default:
                        throw new NotImplementedException($"Parameter type '{ParametersType[kvp.Key]}' is not implemented.");
                }
                result.Add(kvp.Key, value);
            }
            return result;
        }
    }
}
