using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{

    public interface IComputer
    {
        /// <summary>
        /// Indicates which plugin this computer comes from.
        /// </summary>
        public string FromPlugin { get; }
        /// <summary>
        /// Represents the effect or mixture type name that this computer supports.
        /// </summary>
        public string SupportedEffectOrMixture { get; }
        /// <summary>
        /// Compute the output based on the input arguments.
        /// </summary>
        /// <param name="args">Input data</param>
        /// <returns>output data</returns>
        public object[] Compute(object[] args);
    }

    public class EffectAndMixtureJSONStructure
    {
        public string BelogsToGroupId { get; set; } = string.Empty;
        public bool IsMixture { get; set; } = false;
        public bool IsContinuousEffect { get; set; } = false;
        public bool IsVariableArgumentEffect { get; set; } = false;
        public string FromPlugin { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string TypeName { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public int Index { get; set; } = 1;
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public Dictionary<string, object>? Parameters { get; set; }
    }

    public static class EffectArgsHelper
    {
        /// <summary>
        /// Convert a dictionary with JsonElement values to a dictionary with object values according to the given parameter types.
        /// </summary>
        /// <param name="elements"></param>
        /// <param name="ParametersType"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
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
