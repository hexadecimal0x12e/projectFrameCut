using projectFrameCut.Render.Plugins;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
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
        public static IEffect[] GetEffectsInstances(EffectAndMixtureJSONStructure[]? Effects)
        {
            if (Effects is null || Effects.Length == 0)
            {
                return Array.Empty<IEffect>();
            }
            List<IEffect> effects = new();
            foreach (var item in Effects)
            {
                effects.Add(PluginManager.CreateEffect(item));
            }
            return effects.Where(c => c.Enabled).OrderBy(c => c.Index).ToArray();
        }

        public static IEffect CreateFromJSONStructure(EffectAndMixtureJSONStructure item)
        {
            IEffect effect;
            switch (item.TypeName)
            {
                case "RemoveColor":
                    effect = RemoveColorEffect.FromParametersDictionary(EffectArgsHelper.ConvertElementDictToObjectDict(item.Parameters!, RemoveColorEffect.ParametersType));
                    break;
                case "Place":
                    effect = PlaceEffect.FromParametersDictionary(EffectArgsHelper.ConvertElementDictToObjectDict(item.Parameters!, PlaceEffect.ParametersType));
                    break;
                case "Crop":
                    effect = CropEffect.FromParametersDictionary(EffectArgsHelper.ConvertElementDictToObjectDict(item.Parameters!, CropEffect.ParametersType));
                    break;
                case "Resize":
                    effect = ResizeEffect.FromParametersDictionary(EffectArgsHelper.ConvertElementDictToObjectDict(item.Parameters!, ResizeEffect.ParametersType));
                    break;
                default:
                    throw new NotImplementedException($"Effect type '{item.TypeName}' is not implemented.");
            }
            effect.Name = item.Name;
            effect.Index = item.Index;
            effect.Enabled = item.Enabled;
            return effect;
        }


        public static Dictionary<string, Func<IEffect>> EffectsEnum => PluginManager.LoadedPlugins.Values.SelectMany(p => p.EffectProvider).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        public static IEnumerable<string> GetEffectTypes() => EffectsEnum.Keys;

    }
}
