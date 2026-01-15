using projectFrameCut.Render.Effect;
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
        public  static IEffect PickFromEffectCombinations(List<Func<IEffect>> EffectCombinations, EffectImplementType preferredType)
        {
            foreach (var item in EffectCombinations)
            {
                var instance = item();
                if (instance.ImplementType == preferredType) return instance;
            }
            return EffectCombinations[0]();
        }


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

        public static Dictionary<string, Func<IEffect>> EffectsEnum =>
                PluginManager.LoadedPlugins.Values
                .SelectMany(p =>
                    p.EffectFactoryProvider.Select(kv => new KeyValuePair<string, Func<IEffect>>(kv.Key, () => kv.Value.BuildWithDefaultType()))
                        .Concat(p.ContinuousEffectFactoryProvider.Select(kv => new KeyValuePair<string, Func<IEffect>>(kv.Key, () => kv.Value.BuildWithDefaultType())))
                        .Concat(p.VariableArgumentEffectFactoryProvider.Select(kv => new KeyValuePair<string, Func<IEffect>>(kv.Key, () => kv.Value.BuildWithDefaultType())))
                        .Concat(p.EffectProvider)
                        .Concat(p.ContinuousEffectProvider)
                        .Concat(p.VariableArgumentEffectProvider))
                .DistinctBy(kv => kv.Value().TypeName)
                .GroupBy(kv => kv.Key)
                .ToDictionary(g => g.Key, g => g.First().Value);

        public static Dictionary<string, IEffectFactory> EffectsFactoriesEnum =>
                PluginManager.LoadedPlugins.Values
                .SelectMany(p => p.EffectFactoryProvider
                        .Concat(p.ContinuousEffectFactoryProvider)
                        .Concat(p.VariableArgumentEffectFactoryProvider))
                .GroupBy(kv => kv.Key)
                .ToDictionary(g => g.Key, g => g.First().Value);

        public static IEnumerable<string> GetEffectTypes() => EffectsEnum.Keys;

    }
}
