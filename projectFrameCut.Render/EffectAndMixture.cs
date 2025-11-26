using projectFrameCut.Render;
using projectFrameCut.VideoMakeEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace projectFrameCut.Shared
{
    public interface IEffect
    {
        public string TypeName { get; }
        public Dictionary<string, object> Parameters { get; }
        public bool Enabled { get; init; }
        public int Index { get; init; }
        [JsonIgnore]
        public List<string> ParametersNeeded { get; }
        [JsonIgnore]
        public Dictionary<string, string> ParametersType { get; }

        public IEffect WithParameters(Dictionary<string, object> parameters);

        public Picture Render(Picture source, IComputer computer);
    }

    public interface IMixture
    {
        public Dictionary<string, object> Parameters { get; }

        public Picture Mix(Picture basePicture, Picture topPicture, IComputer computer);
    }

    public interface IComputer
    {
        public float[][] Compute(float[][] args);
    }

    public class EffectAndMixtureJSONStructure
    {
        public bool IsMixture { get; set; } = false;    
        public string TypeName { get; set; } = string.Empty;
        public Dictionary<string, object>? Parameters { get; set; }
    }

    public static class EffectHelper
    {
        public static IEffect CreateFromJSONStructure(EffectAndMixtureJSONStructure item)
        {
            IEffect effect;
            switch (item.TypeName)
            {
                case "RemoveColorEffect":
                    effect = RemoveColorEffect.FromParametersDictionary(ConvertElementDictToObjectDict(item.Parameters!, RemoveColorEffect.ParametersType));
                    break;
                default:
                    throw new NotImplementedException($"Effect type '{item.TypeName}' is not implemented.");
            }
            return effect;
        }

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
