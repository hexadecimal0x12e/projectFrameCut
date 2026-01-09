using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
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
            IEffect effect = item.TypeName switch
            {
                "RemoveColor" => RemoveColorEffect.FromParametersDictionary(EffectArgsHelper.ConvertElementDictToObjectDict(item.Parameters!, RemoveColorEffect.ParametersType)),
                "Place" => PlaceEffect.FromParametersDictionary(EffectArgsHelper.ConvertElementDictToObjectDict(item.Parameters!, PlaceEffect.ParametersType)),
                "Crop" => CropEffect.FromParametersDictionary(EffectArgsHelper.ConvertElementDictToObjectDict(item.Parameters!, CropEffect.ParametersType)),
                "Resize" => ResizeEffect.FromParametersDictionary(EffectArgsHelper.ConvertElementDictToObjectDict(item.Parameters!, ResizeEffect.ParametersType)),
                _ => throw new NotImplementedException($"Effect type '{item.TypeName}' is not implemented."),
            };
            effect.Name = item.Name;
            effect.Index = item.Index;
            effect.Enabled = item.Enabled;
            effect.RelativeHeight = item.RelativeHeight;
            effect.RelativeWidth = item.RelativeWidth;
            return effect;
        }


        public static Dictionary<string, Func<IEffect>> EffectsEnum =>
                PluginManager.LoadedPlugins.Values
                .SelectMany(p => p.EffectProvider
                    .Concat(p.ContinuousEffectProvider)
                    .Concat(p.VariableArgumentEffectProvider))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

        public static IEnumerable<string> GetEffectTypes() => EffectsEnum.Keys;

    }
}
