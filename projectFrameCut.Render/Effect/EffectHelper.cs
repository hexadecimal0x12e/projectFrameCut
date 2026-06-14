
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
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

        public static EffectImplementType? ForcePreferToType = null;

        public static Dictionary<string, EffectImplementType> DefaultImplementsType = new();

        public static (IEffect[] Effects, ISpeedVarianceProvider? SpeedVarianceProvider) GetEffectsInstancesAndSpeedVariance(EffectAndMixtureJSONStructure[]? Effects)
        {
            var (effects, provider, _) = GetEffectsInstancesSpeedVarianceAndMixture(Effects);
            return (effects, provider);
        }

        public static (IEffect[] Effects, ISpeedVarianceProvider? SpeedVarianceProvider, IMixture? Mixture) GetEffectsInstancesSpeedVarianceAndMixture(EffectAndMixtureJSONStructure[]? Effects)
        {
            if (Effects is null || Effects.Length == 0)
            {
                return (Array.Empty<IEffect>(), null, null);
            }
            List<IEffect> effects = new();
            bool haveSpeedVarProvider = false;
            bool haveMixture = false;
            ISpeedVarianceProvider? provider = null;
            IMixture? mixture = null;
            foreach (var item in Effects)
            {
                var e = PluginManager.CreateEffect(item, ForcePreferToType ?? (item.ImplementType == EffectImplementType.NotSpecified ? DefaultImplementsType.GetValueOrDefault($"{item.FromPlugin}.{item.TypeName}", EffectImplementType.NotSpecified) : item.ImplementType));
                if (e is ISpeedVarianceProvider p)
                {
                    if (haveSpeedVarProvider) throw new InvalidOperationException("Multiple SpeedVarianceProvider effects found.");
                    haveSpeedVarProvider = true;
                    provider = p;
                }
                else if (e is IMixture m)
                {
                    if (haveMixture) throw new InvalidOperationException("Multiple MixtureProvider effects found.");
                    haveMixture = true;
                    mixture = m;
                }
                else
                {
                    effects.Add(e);
                }
            }

            return (effects.Where(c => c.Enabled).OrderBy(c => c.Index).ToArray(), provider, mixture);
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
                var e = PluginManager.CreateEffect(item, ForcePreferToType ?? (item.ImplementType == EffectImplementType.NotSpecified ? DefaultImplementsType.GetValueOrDefault($"{item.FromPlugin}.{item.TypeName}", EffectImplementType.NotSpecified) : item.ImplementType));
                effects.Add(e);


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

