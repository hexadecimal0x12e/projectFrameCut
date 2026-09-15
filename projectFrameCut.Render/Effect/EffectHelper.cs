
using projectFrameCut.Render.ClipsAndTracks;
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

        public static void ResolveClipEffects(IClip target)
        {
            ArgumentNullException.ThrowIfNull(target);

            target.EffectProvidersInstances = [];
            target.EffectsInstances = [];
            target.SpeedVarianceProviderInstance = null;
            target.MixtureInstance = null;
            target.AlternativeSource = null;

            if (target.EffectProviders is not { Length: > 0 } providersJson)
            {
                try
                {
                    var existingEffects = CreateExistingEffects(target.Effects);
                    ApplyResolvedEffects(target, existingEffects.Values);
                    return;
                }
                catch (Exception ex)
                {
                    ClipInitializationFailure.Mark(target, "ResolveEffect", ex);
                    throw;
                }
            }
            else
            {

            }

            Dictionary<Guid, IEffectProvider> providers;
            IReadOnlyList<EffectBindingHelper.BindingDiagnostic> restoreDiagnostics;
            try
            {
                providers = EffectBindingHelper.MigrateToEffectProviders(
                    providersJson,
                    null,
                    out restoreDiagnostics);
                target.EffectProvidersInstances = providers.Values.ToArray();
            }
            catch (Exception ex)
            {
                ClipInitializationFailure.Mark(target, "ResolveEffectProvider", ex);
                throw;
            }

            EffectBindingHelper.BindingDiagnostic[] bindingDiagnostics;
            try
            {
                bindingDiagnostics = restoreDiagnostics
                    .Concat(EffectBindingHelper.ValidateBindings(providers))
                    .Distinct()
                    .ToArray();
            }
            catch (Exception ex)
            {
                ClipInitializationFailure.Mark(target, "ResolveBinding", ex);
                throw;
            }
            if (bindingDiagnostics.Length > 0)
            {
                var exception = new InvalidOperationException(
                    $"Invalid effect binding graph:{Environment.NewLine}" +
                    string.Join(Environment.NewLine, bindingDiagnostics.Select(d => $"- [{d.Code}] {d.Message}")));
                ClipInitializationFailure.Mark(target, "ResolveBinding", exception);
                throw exception;
            }

            Dictionary<string, IEffect> existingProviderEffects;
            try
            {
                existingProviderEffects = CreateExistingEffects(target.Effects);
            }
            catch (Exception ex)
            {
                ClipInitializationFailure.Mark(target, "ResolveEffect", ex);
                throw;
            }

            Dictionary<string, IEffect> rebuiltEffects;
            try
            {
                rebuiltEffects = EffectBindingHelper.RebuildAllEffects(providers, existingProviderEffects)
                    ?? new Dictionary<string, IEffect>();
            }
            catch (Exception ex)
            {
                ClipInitializationFailure.Mark(target, "ResolveBinding", ex);
                throw;
            }

            try
            {
                ApplyResolvedEffects(target, rebuiltEffects.Values);
            }
            catch (Exception ex)
            {
                ClipInitializationFailure.Mark(target, "ResolveEffect", ex);
                throw;
            }
        }

        private static Dictionary<string, IEffect> CreateExistingEffects(
            EffectAndMixtureJSONStructure[]? structures)
        {
            var result = new Dictionary<string, IEffect>();
            if (structures is null) return result;

            foreach (var structure in structures)
            {
                var implementType = ForcePreferToType
                    ?? (structure.ImplementType == EffectImplementType.NotSpecified
                        ? DefaultImplementsType.GetValueOrDefault(
                            $"{structure.FromPlugin}.{structure.TypeName}",
                            EffectImplementType.NotSpecified)
                        : structure.ImplementType);
                var effect = PluginManager.CreateEffect(structure, implementType);
                result[structure.Name ?? effect.Id] = effect;
            }

            return result;
        }

        private static void ApplyResolvedEffects(IClip target, IEnumerable<IEffect> resolvedEffects)
        {
            var effects = new List<IEffect>();
            foreach (var effect in resolvedEffects.OrderBy(e => e.Index))
            {
                switch (effect)
                {
                    case IValueProviderEffect:
                        continue;
                    case ISpeedVarianceProvider speedVariance:
                        if (target.SpeedVarianceProviderInstance is not null)
                            throw new InvalidOperationException("Multiple SpeedVarianceProvider effects found.");
                        target.SpeedVarianceProviderInstance = speedVariance;
                        break;
                    case IMixture mixture:
                        if (target.MixtureInstance is not null)
                            throw new InvalidOperationException("Multiple MixtureProvider effects found.");
                        target.MixtureInstance = mixture;
                        break;
                    case ISourceReplacementEffect alternativeSource:
                        if (target.AlternativeSource is not null)
                            throw new InvalidOperationException("Multiple SourceReplacement effects found.");
                        target.AlternativeSource = alternativeSource;
                        break;
                    default:
                        effects.Add(effect);
                        break;
                }
            }

            foreach (var effect in effects)
            {
                if (!string.IsNullOrWhiteSpace(effect.BindedEffectProvidingSystemID))
                    effect.Initialize();
            }
            target.EffectsInstances = effects.ToArray();
        }

        public static (IEffect[] Effects, ISpeedVarianceProvider? SpeedVarianceProvider) GetEffectsInstancesAndSpeedVariance(EffectAndMixtureJSONStructure[]? Effects)
        {
            var (effects, provider, _, _) = GetEffectsInstancesSpeedVarianceAndMixture(Effects);
            return (effects, provider);
        }

        public static (IEffect[] Effects, ISpeedVarianceProvider? SpeedVarianceProvider, IMixture? Mixture, ISourceReplacementEffect? AlternativeSource) GetEffectsInstancesSpeedVarianceAndMixture(EffectAndMixtureJSONStructure[]? Effects)
        {
            if (Effects is null || Effects.Length == 0)
            {
                return (Array.Empty<IEffect>(), null, null, null);
            }
            List<IEffect> effects = new();
            bool haveSpeedVarProvider = false;
            bool haveMixture = false;
            bool haveAlternativeSource = false;
            ISpeedVarianceProvider? provider = null;
            IMixture? mixture = null;
            ISourceReplacementEffect? alternativeSource = null;
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
                else if (e is ISourceReplacementEffect s)
                {
                    if (haveAlternativeSource) throw new InvalidOperationException("Multiple SourceReplacement effects found.");
                    haveAlternativeSource = true;
                    alternativeSource = s;
                }
                else
                {
                    effects.Add(e);
                }
            }

            return (effects.Where(c => c.Enabled).OrderBy(c => c.Index).ToArray(), provider, mixture, alternativeSource);
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

        /// <summary>
        /// All effect providers registered across the loaded plugins, keyed by effect type name.
        /// The value is a factory that creates a fresh provider instance.
        /// </summary>
        public static Dictionary<string, Func<IEffectProvider>> EffectsProviderEnum =>
                PluginManager.LoadedPlugins.Values
                .SelectMany(p => p.EffectProviderProvider)
                .GroupBy(kv => kv.Key)
                .ToDictionary(g => g.Key, g => g.First().Value);

        public static IEnumerable<string> GetEffectTypes() => EffectsProviderEnum.Keys;

        /// <summary>
        /// 从 <see cref="IClip"/> 构建渲染用的扁平效果列表。
        /// 优先走 provider 路径：用 <see cref="EffectBindingHelper.MigrateToEffectProviders"/> 还原
        /// <see cref="IClip.EffectProviders"/> 的绑定数据，再用 <see cref="EffectBindingHelper.RebuildAllEffects"/>
        /// 重建出含 <c>Func&lt;object&gt;</c> 动态参数的效果（值提供器被内联进消费者字段并从独立管线剔除）。
        /// 无 provider 数据（旧项目）时回退到静态 <see cref="IClip.Effects"/> 路径。
        /// </summary>
        /// <remarks>
        /// <see cref="PluginManager.CreateEffect"/> 内部的 <c>StripBindings</c> 会剥掉 <c>__Binding_*</c> 键与
        /// <see cref="Func{T}"/>/<see cref="Lazy{T}"/> 动态值，因此渲染层必须经 provider 重建才能保留动态绑定。
        /// 独立（未被内联）的值提供器与 <see cref="ISourceReplacementEffect"/> 会被显式过滤，避免渲染循环抛异常。
        /// </remarks>
        public static IEffect[] GetClipEffectsInstances(IClip clip, bool syncClipState = true)
        {
            ArgumentNullException.ThrowIfNull(clip);

            if (syncClipState)
            {
                var target = clip;
                ResolveClipEffects(target);
                return (target.EffectsInstances ?? [])
                    .Where(effect => effect.Enabled)
                    .OrderBy(effect => effect.Index)
                    .ToArray();
            }

            if (clip.EffectProviders is not { Length: > 0 })
                return BuildFromStaticEffects(clip, syncClipState);

            // 1) 从静态 JSON 数组构建 existingEffects（key = stru.Name，与 RebuildAllEffects 的 {providerId}_{subIdx} 键不冲突）。
            //    手动效果（无 BindedEffectGroupID）会被 RebuildAllEffects 原样保留。
            var existing = new Dictionary<string, IEffect>();
            if (clip.Effects is not null)
            {
                foreach (var stru in clip.Effects)
                {
                    var imp = ForcePreferToType
                        ?? (stru.ImplementType == EffectImplementType.NotSpecified
                            ? DefaultImplementsType.GetValueOrDefault($"{stru.FromPlugin}.{stru.TypeName}", EffectImplementType.NotSpecified)
                            : stru.ImplementType);
                    var e = PluginManager.CreateEffect(stru, imp); // CreateEffect 内部会调 Initialize()
                    existing[stru.Name ?? e.Id] = e;
                }
            }

            // 2) 从 EffectProviders 还原 provider 实例（RestoreProviderFields 恢复动态字段的 BoundProviderId）。
            Dictionary<Guid, IEffectProvider> providers;
            try
            {
                providers = EffectBindingHelper.MigrateToEffectProviders(clip.EffectProviders, null);
            }
            catch
            {
                return BuildFromStaticEffects(clip, syncClipState);
            }

            // 3) 重建：内联值提供器 → 拓扑排序 → 每个 provider.Build()（不走 CreateEffect，绑定字段产出 Func<object>）。
            Dictionary<string, IEffect>? rebuilt;
            try
            {
                rebuilt = EffectBindingHelper.RebuildAllEffects(providers, existing);
            }
            catch
            {
                return BuildFromStaticEffects(clip, syncClipState); // provider 图成环 / 未知类型不崩渲染
            }
            if (rebuilt is null || rebuilt.Count == 0)
                return BuildFromStaticEffects(clip, syncClipState);

            // 4) 拆出特殊实例（与 GetEffectsInstancesSpeedVarianceAndMixture 相同的三分类语义）。
            var plain = new List<IEffect>();
            ISpeedVarianceProvider? sv = null;
            IMixture? mix = null;
            ISourceReplacementEffect? alt = null;
            bool haveSv = false, haveMix = false, haveAlt = false;
            foreach (var effect in rebuilt.Values.OrderBy(e => e.Index))
            {
                switch (effect)
                {
                    case IValueProviderEffect:
                        continue; // 未内联的独立值提供器绝不进渲染管线（Render/Timeline 会 throw）
                    case ISpeedVarianceProvider p:
                        if (haveSv) throw new InvalidOperationException("Multiple SpeedVarianceProvider effects found.");
                        haveSv = true;
                        sv = p;
                        continue;
                    case IMixture m:
                        if (haveMix) throw new InvalidOperationException("Multiple MixtureProvider effects found.");
                        haveMix = true;
                        mix = m;
                        continue;
                    case ISourceReplacementEffect s:
                        if (haveAlt) throw new InvalidOperationException("Multiple SourceReplacement effects found.");
                        haveAlt = true;
                        alt = s;
                        continue; // Timeline 对 SourceReplacement 会 throw，必须剔除
                    default:
                        plain.Add(effect);
                        break;
                }
            }

            // 5) provider.Build() 不调 Initialize()，须补；手动效果已由 CreateEffect 初始化，不重复调。
            foreach (var e in plain)
            {
                if (!string.IsNullOrWhiteSpace(e.BindedEffectProvidingSystemID)) e.Initialize();
            }

            // 6) 回写 clip 状态，使绑定在 speed variance / mixture / source-replacement / 文本与音频效果中也生效。
            if (syncClipState)
            {
                clip.SpeedVarianceProviderInstance = sv;
                clip.MixtureInstance = mix;
                clip.AlternativeSource = alt;
                clip.EffectsInstances = plain.ToArray();
            }

            return plain.Where(e => e.Enabled).OrderBy(e => e.Index).ToArray();
        }

        /// <summary>
        /// 旧项目回退路径：从静态 <see cref="IClip.Effects"/> 构建（等价于 <see cref="ReInit"/> 的提取语义），
        /// 并额外过滤独立值提供器，杜绝渲染循环 throw。
        /// </summary>
        private static IEffect[] BuildFromStaticEffects(IClip clip, bool syncClipState)
        {
            var (effects, sv, mix, alt) = GetEffectsInstancesSpeedVarianceAndMixture(clip.Effects);
            var result = effects.Where(e => e is not IValueProviderEffect).ToArray();
            if (syncClipState)
            {
                clip.SpeedVarianceProviderInstance = sv;
                clip.MixtureInstance = mix;
                clip.AlternativeSource = alt;
                clip.EffectsInstances = result;
            }
            return result;
        }

    }
}

