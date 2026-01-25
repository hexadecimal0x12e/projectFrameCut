using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.Render.Effect
{
    public static class EffectHelper
    {
        public static double GetContinuesEffectProgress(uint index, int startPoint, int endPoint)
        {
            if (endPoint <= startPoint) return 1.0;
            if (index < startPoint) return 0.0;
            if (index >= endPoint) return 1.0;
            return (double)(index - startPoint) / (endPoint - startPoint);
        }

        public static double GetContinuesEffectProgress(this IContinuousEffect effect, uint index)
        {
            if (effect.EndPoint <= effect.StartPoint) return 1.0;
            if (index < effect.StartPoint) return 0.0;
            if (index >= effect.EndPoint) return 1.0;
            return (double)(index - effect.StartPoint) / (effect.EndPoint - effect.StartPoint);
        }

        public static IEffect PickFromEffectCombinations(List<Func<IEffect>> EffectCombinations, EffectImplementType preferredType)
        {
            foreach (var item in EffectCombinations)
            {
                var instance = item();
                if (instance.ImplementType == preferredType) return instance;
            }
            return EffectCombinations[0]();
        }

        public static Dictionary<string, EffectImplementType> DefaultImplementsType = new();

        public static IEffect[] GetEffectsInstances(EffectAndMixtureJSONStructure[]? Effects)
        {
            if (Effects is null || Effects.Length == 0)
            {
                return Array.Empty<IEffect>();
            }
            List<IEffect> effects = new();
            foreach (var item in Effects)
            {
                effects.Add(PluginManager.CreateEffect(item, DefaultImplementsType.GetValueOrDefault($"{item.FromPlugin}.{item.TypeName}", EffectImplementType.NotSpecified)));
            }
            return effects.Where(c => c.Enabled).OrderBy(c => c.Index).ToArray();
        }

        public static Dictionary<string, Func<IEffect>> EffectsEnum =>
                PluginManager.LoadedPlugins.Values
                .SelectMany(p =>
                       p.EffectProvider
                        .Concat(p.ContinuousEffectProvider)
                        .Concat(p.BindableArgumentEffectProvider))
                .DistinctBy(kv => kv.Value().TypeName)
                .GroupBy(kv => kv.Key)
                .ToDictionary(g => g.Key, g => g.First().Value);

        public static Dictionary<string, IEffectFactory> EffectsFactoriesEnum =>
                PluginManager.LoadedPlugins.Values
                .SelectMany(p => p.EffectFactoryProvider
                        .Concat(p.ContinuousEffectFactoryProvider)
                        .Concat(p.BindableArgumentEffectFactoryProvider))
                .GroupBy(kv => kv.Key)
                .ToDictionary(g => g.Key, g => g.First().Value);

        public static IEnumerable<string> GetEffectTypes() => EffectsEnum.Keys;

    }
}
