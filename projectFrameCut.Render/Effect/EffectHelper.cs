
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

        private static long configurationRevision;
        public static long ConfigurationRevision => Volatile.Read(ref configurationRevision);
        public static void InvalidateImplementations() => Interlocked.Increment(ref configurationRevision);

        public static void ReleaseClipEffects(IClip clip)
        {
            foreach (var provider in (clip.EffectProvidersInstances ?? []).OfType<ClipArgumentProvider>())
                provider.ClearRuntimeFields();
            foreach (var effect in (clip.EffectsInstances ?? []).Concat(new IEffect?[] { clip.MixtureInstance, clip.SpeedVarianceProviderInstance, clip.AlternativeSource }).OfType<IDisposable>().Distinct(ReferenceEqualityComparer.Instance))
                ((IDisposable)effect).Dispose();
            clip.EffectsInstances = [];
            clip.MixtureInstance = null;
            clip.SpeedVarianceProviderInstance = null;
            clip.AlternativeSource = null;
        }

        public static EffectImplementType? GetPicturePreference(string typeName) => ForcePreferToType is { } type &&
            RenderAPIBase.Plugins.IPluginBase.EffectImplementations.GetImplementTypes(typeName).Any(x => x is EffectImplementType.IPicture or EffectImplementType.HwAcceleration)
                ? type : null;

        public static Dictionary<string, EffectImplementType> DefaultImplementsType = new();

        public static void ResolveClipEffects(IClip target)
        {
            ArgumentNullException.ThrowIfNull(target);

            ReleaseClipEffects(target);
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

            Dictionary<Guid, IEffectProvider> providers;
            IReadOnlyList<EffectBindingHelper.BindingDiagnostic> restoreDiagnostics;
            try
            {
                providers = EffectBindingHelper.MigrateToEffectProviders(
                    providersJson,
                    null,
                    out restoreDiagnostics);
                target.EffectProvidersInstances = providers.Values.ToArray();
                foreach (var provider in providers.Values.OfType<ClipArgumentProvider>()) provider.Attach(target);
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
                foreach (var effect in existingProviderEffects.Values.OfType<IDisposable>()) effect.Dispose();
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

        private static Dictionary<string, IEffect> CreateExistingEffects(EffectAndMixtureJSONStructure[]? structures)
        {
            var result = new Dictionary<string, IEffect>();
            try
            {
                foreach (var structure in structures ?? [])
                {
                    var implementType = GetPicturePreference(structure.TypeName)
                        ?? (structure.ImplementType == EffectImplementType.NotSpecified
                            ? DefaultImplementsType.GetValueOrDefault($"{structure.FromPlugin}.{structure.TypeName}", EffectImplementType.NotSpecified)
                            : structure.ImplementType);
                    var effect = PluginManager.CreateEffect(structure, implementType);
                    var key = structure.Name ?? effect.Id;
                    if (result.TryGetValue(key, out var previous) && previous is IDisposable disposable) disposable.Dispose();
                    result[key] = effect;
                }
                return result;
            }
            catch
            {
                foreach (var effect in result.Values.OfType<IDisposable>()) effect.Dispose();
                throw;
            }
        }

        private static void ApplyResolvedEffects(IClip target, IEnumerable<IEffect> resolvedEffects)
        {
            var all = resolvedEffects.ToArray();
            var effects = new List<IEffect>();
            ISpeedVarianceProvider? speed = null;
            IMixture? mixture = null;
            ISourceReplacementEffect? alternative = null;
            try
            {
                foreach (var effect in all.OrderBy(e => e.Index))
                {
                    switch (effect)
                    {
                        case IValueProviderEffect:
                            break;
                        case ISpeedVarianceProvider sv:
                            if (speed is not null) throw new InvalidOperationException("Multiple SpeedVarianceProvider effects found.");
                            speed = sv;
                            break;
                        case IMixture m:
                            if (mixture is not null) throw new InvalidOperationException("Multiple MixtureProvider effects found.");
                            mixture = m;
                            break;
                        case ISourceReplacementEffect alt:
                            if (alternative is not null) throw new InvalidOperationException("Multiple SourceReplacement effects found.");
                            alternative = alt;
                            break;
                        default:
                            effects.Add(effect);
                            break;
                    }
                }
                foreach (var effect in effects)
                {
                    if (!string.IsNullOrWhiteSpace(effect.BindedEffectProvidingSystemID)) effect.Initialize();
                }
            }
            catch
            {
                foreach (var effect in all.OfType<IDisposable>()) effect.Dispose();
                throw;
            }
            foreach (var effect in all.OfType<IValueProviderEffect>().OfType<IDisposable>()) effect.Dispose();
            target.EffectsInstances = effects.ToArray();
            target.SpeedVarianceProviderInstance = speed;
            target.MixtureInstance = mixture;
            target.AlternativeSource = alternative;
        }

        public static (IEffect[] Effects, ISpeedVarianceProvider? SpeedVarianceProvider) GetEffectsInstancesAndSpeedVariance(EffectAndMixtureJSONStructure[]? Effects)
        {
            var (effects, provider, mixture, alternative) = GetEffectsInstancesSpeedVarianceAndMixture(Effects);
            (mixture as IDisposable)?.Dispose();
            (alternative as IDisposable)?.Dispose();
            return (effects, provider);
        }

        public static (IEffect[] Effects, ISpeedVarianceProvider? SpeedVarianceProvider, IMixture? Mixture, ISourceReplacementEffect? AlternativeSource) GetEffectsInstancesSpeedVarianceAndMixture(EffectAndMixtureJSONStructure[]? Effects)
        {
            if (Effects is null || Effects.Length == 0)
            {
                return (Array.Empty<IEffect>(), null, null, null);
            }
            List<IEffect> all = new();
            List<IEffect> effects = new();
            ISpeedVarianceProvider? provider = null;
            IMixture? mixture = null;
            ISourceReplacementEffect? alternativeSource = null;
            try
            {
                foreach (var item in Effects)
                {
                    var e = PluginManager.CreateEffect(item, GetPicturePreference(item.TypeName) ?? (item.ImplementType == EffectImplementType.NotSpecified ? DefaultImplementsType.GetValueOrDefault($"{item.FromPlugin}.{item.TypeName}", EffectImplementType.NotSpecified) : item.ImplementType));
                    all.Add(e);
                    if (e is ISpeedVarianceProvider p)
                    {
                        if (provider is not null) throw new InvalidOperationException("Multiple SpeedVarianceProvider effects found.");
                        provider = p;
                    }
                    else if (e is IMixture m)
                    {
                        if (mixture is not null) throw new InvalidOperationException("Multiple MixtureProvider effects found.");
                        mixture = m;
                    }
                    else if (e is ISourceReplacementEffect s)
                    {
                        if (alternativeSource is not null) throw new InvalidOperationException("Multiple SourceReplacement effects found.");
                        alternativeSource = s;
                    }
                    else
                    {
                        effects.Add(e);
                    }
                }
                foreach (var effect in all.Where(e => !e.Enabled).OfType<IDisposable>()) effect.Dispose();
                return (effects.Where(c => c.Enabled).OrderBy(c => c.Index).ToArray(), provider, mixture, alternativeSource);
            }
            catch
            {
                foreach (var effect in all.OfType<IDisposable>()) effect.Dispose();
                throw;
            }
        }
        public static IEffect[] GetEffectsInstances(EffectAndMixtureJSONStructure[]? Effects)
        {
            if (Effects is null || Effects.Length == 0)
            {
                return Array.Empty<IEffect>();
            }
            List<IEffect> effects = new();
            try
            {
                foreach (var item in Effects)
                {
                    effects.Add(PluginManager.CreateEffect(item, GetPicturePreference(item.TypeName) ?? (item.ImplementType == EffectImplementType.NotSpecified ? DefaultImplementsType.GetValueOrDefault($"{item.FromPlugin}.{item.TypeName}", EffectImplementType.NotSpecified) : item.ImplementType)));
                }
                foreach (var effect in effects.Where(e => !e.Enabled).OfType<IDisposable>()) effect.Dispose();
                return effects.Where(c => c.Enabled).OrderBy(c => c.Index).ToArray();
            }
            catch
            {
                foreach (var effect in effects.OfType<IDisposable>()) effect.Dispose();
                throw;
            }
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

            var existing = CreateExistingEffects(clip.Effects);
            Dictionary<string, IEffect>? rebuilt = null;
            try
            {
                var providers = EffectBindingHelper.MigrateToEffectProviders(clip.EffectProviders, null);
                foreach (var provider in providers.Values.OfType<ClipArgumentProvider>()) provider.Attach(clip);
                rebuilt = EffectBindingHelper.RebuildAllEffects(providers, existing) ?? [];
                var result = rebuilt.Values.Where(e => e.Enabled && e is not (IValueProviderEffect or ISpeedVarianceProvider or IMixture or ISourceReplacementEffect))
                    .OrderBy(e => e.Index).ToArray();
                foreach (var effect in result)
                {
                    if (!string.IsNullOrWhiteSpace(effect.BindedEffectProvidingSystemID)) effect.Initialize();
                }
                var retained = result.ToHashSet(ReferenceEqualityComparer.Instance);
                foreach (var effect in rebuilt.Values)
                {
                    if (!retained.Contains(effect) && effect is IDisposable disposable) disposable.Dispose();
                }
                return result;
            }
            catch (Exception ex)
            {
                foreach (var effect in (rebuilt ?? existing).Values.OfType<IDisposable>()) effect.Dispose();
                Logger.Log(ex, "Rebuild clip effects");
                throw;
            }
        }

        /// <summary>
        /// 旧项目回退路径：从静态 <see cref="IClip.Effects"/> 构建（等价于 <see cref="ReInit"/> 的提取语义），
        /// 并额外过滤独立值提供器，杜绝渲染循环 throw。
        /// </summary>
        private static IEffect[] BuildFromStaticEffects(IClip clip, bool syncClipState)
        {
            var (effects, sv, mix, alt) = GetEffectsInstancesSpeedVarianceAndMixture(clip.Effects);
            var result = effects.Where(e => e is not IValueProviderEffect).ToArray();
            foreach (var effect in effects.OfType<IValueProviderEffect>().OfType<IDisposable>()) effect.Dispose();
            if (syncClipState)
            {
                clip.SpeedVarianceProviderInstance = sv;
                clip.MixtureInstance = mix;
                clip.AlternativeSource = alt;
                clip.EffectsInstances = result;
            }
            else
            {
                (sv as IDisposable)?.Dispose();
                (mix as IDisposable)?.Dispose();
                (alt as IDisposable)?.Dispose();
            }
            return result;
        }

    }
}

