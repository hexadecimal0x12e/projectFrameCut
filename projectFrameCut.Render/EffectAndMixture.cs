using projectFrameCut.Render;
using projectFrameCut.VideoMakeEngine;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
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
        public bool Enabled { get; set; }
        public int Index { get; set; }      
        [JsonIgnore]
        public List<string> ParametersNeeded { get; }
        [JsonIgnore]
        public Dictionary<string, string> ParametersType { get; }
        [JsonIgnore]
        public bool NeedAComputer { get; }

        public IEffect WithParameters(Dictionary<string, object> parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight);
    }

    public interface IMixture
    {
        public Dictionary<string, object> Parameters { get; }

        public IPicture Mix(IPicture basePicture, IPicture topPicture, IComputer computer);
    }

    public interface IComputer
    {
        public float[][] Compute(float[][] args);
    }

    public class EffectAndMixtureJSONStructure
    {
        public bool IsMixture { get; set; } = false;    
        public string TypeName { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public int Index { get; set; } = 1;
        public Dictionary<string, object>? Parameters { get; set; }
    }

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

        private static Dictionary<string,Type> _effectTypeCache = null!;

        [RequiresUnreferencedCode("Effect enumeration may not preserve all information.")]
        public static IDictionary<string, Type> EnumerateEffectsAndNames()
        {
            if(_effectTypeCache is not null) return _effectTypeCache;
            var results = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            var effectInterface = typeof(IEffect);

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var asm in assemblies)
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray()!;
                }
                catch
                {
                    continue; 
                }

                foreach (var t in types)
                {
                    if (t is null) continue;
                    if (t.IsAbstract) continue;
                    if (!effectInterface.IsAssignableFrom(t)) continue;

                    try
                    {
                        if (Activator.CreateInstance(t) is IEffect inst)
                        {
                            results.Add(inst.TypeName,t);
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }
            }
            _effectTypeCache = results;
            return results;
        }


        public static IEnumerable<string> GetEffectTypes() => _effectTypeCache is not null ? _effectTypeCache.Keys : EnumerateEffectsAndNames().Keys;

    }
}
