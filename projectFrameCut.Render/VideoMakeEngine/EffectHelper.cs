using projectFrameCut.Render.Plugins;
using projectFrameCut.Shared;
using projectFrameCut.VideoMakeEngine;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.Render.VideoMakeEngine
{
    public static class EffectHelper
    {
        public static IEffect CreateFromJSONStructure(EffectAndMixtureJSONStructure item)
        {
            IEffect effect;
            switch (item.TypeName)
            {
                case "RemoveColor":
                    effect = RemoveColorEffect.FromParametersDictionary(ConvertElementDictToObjectDict(item.Parameters!, RemoveColorEffect.ParametersType));
                    break;
                case "Place":
                    effect = PlaceEffect.FromParametersDictionary(ConvertElementDictToObjectDict(item.Parameters!, PlaceEffect.ParametersType));
                    break;
                case "Crop":
                    effect = CropEffect.FromParametersDictionary(ConvertElementDictToObjectDict(item.Parameters!, CropEffect.ParametersType));
                    break;
                case "Resize":
                    effect = ResizeEffect.FromParametersDictionary(ConvertElementDictToObjectDict(item.Parameters!, ResizeEffect.ParametersType));
                    break;
                default:
                    throw new NotImplementedException($"Effect type '{item.TypeName}' is not implemented.");
            }
            effect.Name = item.Name;
            effect.Index = item.Index;
            effect.Enabled = item.Enabled;
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


        public static Dictionary<string, Func<IEffect>> EffectsEnum => PluginManager.LoadedPlugins.Values.SelectMany(p => p.EffectProvider).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        public static IEnumerable<string> GetEffectTypes() => EffectsEnum.Keys;

    }
}
