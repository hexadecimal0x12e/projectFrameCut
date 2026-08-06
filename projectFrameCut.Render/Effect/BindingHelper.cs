using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace projectFrameCut.Render.Effect
{
    public static class EffectBindingHelper
    {
        /// <summary>
        /// 将旧的 EffectBundle 数据迁移为新的 <see cref="IEffectProvider"/> 实例集合。
        /// 若已存在新的 EffectProvider 数据则优先使用；否则从 EffectBundles 转换。
        /// 找不到对应工厂的类型会被跳过，保证加载过程稳定。
        /// </summary>
        public static Dictionary<Guid, IEffectProvider> MigrateToEffectProviders(
            EffectProviderJSONStructure[]? effectProviders,
            EffectBundleJSONStructure[]? effectBundles)
        {
            var result = new Dictionary<Guid, IEffectProvider>();
            var factories = EffectHelper.EffectsProviderEnum;

            if (effectProviders is { Length: > 0 })
            {
                foreach (var p in effectProviders)
                {
                    if (!factories.TryGetValue(p.TypeName, out var factory))
                    {
                        continue;
                    }

                    var instance = factory();
                    instance.Id = p.Id;
                    instance.Enabled = p.Enabled;
                    instance.Name = p.Name;
                    instance.AnchorsBindingState = p.AnchorsBindingState ?? new Dictionary<string, string>();
                    instance.MetaData = p.MetaData ?? new Dictionary<string, object>();
                    RestoreStaticFields(instance, p.StaticFields);
                    result[instance.Id] = instance;
                }

                NormalizeStoredBindings(result);
                MaterializeFields(result.Values);
                return result;
            }

            if (effectBundles is null)
            {
                return result;
            }

            foreach (var b in effectBundles)
            {
                if (!factories.TryGetValue(b.BundleTypeName, out var factory))
                {
                    continue;
                }

                var instance = factory();
                instance.Id = b.Id;
                instance.Enabled = b.Enabled;
                instance.Name = b.Name;
                instance.MetaData = new Dictionary<string, object>();
                if (instance.HasMainPictureInput())
                    instance.SetMainInputSource(b.BindedInputId);
                else
                    instance.DisconnectMainInput();
                instance.SetFinalOutputSource(
                    instance.OutField.FieldType.HasFlag(EffectArgumentFieldType.IPicture)
                    && b.BindedOutputId == IEffectProvider.OutputAnchorGUID);
                var legacyFields = new Dictionary<string, IEffectArgumentField>();
                foreach (var kvp in b.Parameters ?? new Dictionary<string, object>())
                {
                    legacyFields[kvp.Key] = new StaticEffectArgumentField(kvp.Value, EffectArgumentFieldType.Unknown);
                }
                instance.Fields = legacyFields;
                result[instance.Id] = instance;
            }

            NormalizeStoredBindings(result);
            MaterializeFields(result.Values);
            return result;
        }

        /// <summary>
        /// Restores provider-owned static values from the provider JSON structure.
        /// Field metadata comes from the provider factory; only the persisted value is replaced.
        /// </summary>
        private static void RestoreStaticFields(IEffectProvider provider, Dictionary<string, object>? staticFields)
        {
            if (staticFields is null || staticFields.Count == 0) return;

            Dictionary<string, IEffectArgumentField> fields;
            try
            {
                fields = provider.Fields;
            }
            catch
            {
                return;
            }

            foreach (var (fieldId, rawValue) in staticFields)
            {
                if (!fields.TryGetValue(fieldId, out var descriptor)) continue;
                fields[fieldId] = new StaticEffectArgumentField
                {
                    Id = fieldId,
                    FieldType = descriptor.FieldType,
                    Value = EffectParamConvert.Normalize(rawValue) ?? new object(),
                    DefaultValue = descriptor.DefaultValue,
                    MinValue = descriptor.MinValue,
                    MaxValue = descriptor.MaxValue,
                    PresetOptions = descriptor.PresetOptions,
                    Remarks = descriptor.Remarks,
                };
            }

            provider.Fields = fields;
        }

        /// <summary>
        /// 将绑定到另一个 <see cref="IEffectProvider"/> 字段的值提供器构建为 <see cref="IValueProviderEffect"/>
        /// 并直接注入为该字段；返回所有被内联消费、应当从渲染管线中剔除的 provider 的 id。
        /// </summary>
        /// <remarks>
        /// 每次调用都会重建值提供器并刷新注入字段，保证对源 provider 参数的修改能传播到消费者。
        /// 被内联的值提供器不再被 <see cref="RebuildAllEffects"/> 构建，从而不会进入渲染管线。
        /// </remarks>
        private static HashSet<Guid> InlineValueProvidersIntoConsumers(IReadOnlyDictionary<Guid, IEffectProvider> providers)
        {
            var inlinedIds = new HashSet<Guid>();
            if (providers.Count == 0) return inlinedIds;

            foreach (var consumer in providers.Values)
            {
                if (consumer.Fields is null) continue;
                Dictionary<string, IEffectArgumentField>? fields;
                try
                {
                    fields = consumer.Fields;
                }
                catch
                {
                    continue; // 字段物化失败（例如缺少必填元数据），跳过该消费者，保证加载稳定。
                }

                foreach (var fieldKey in fields.Keys.ToList())
                {
                    var field = fields[fieldKey];
                    Guid sourceId;
                    if (field is DynamicEffectParamField df)
                    {
                        if (!Guid.TryParse(df.BoundProviderId, out sourceId)) continue;
                    }
                    else if (field is IValueProviderEffect vpe && Guid.TryParse(vpe.BindedEffectProvidingSystemID, out sourceId))
                    {
                        // 已内联过：仍走刷新路径，以同步源 provider 的最新参数。
                    }
                    else
                    {
                        continue;
                    }

                    if (!providers.TryGetValue(sourceId, out var source) || !source.Target.HasFlag(EffectTarget.ValueProvider)) continue;

                    IValueProviderEffect? injected = null;
                    try
                    {
                        injected = source.Build().OfType<IValueProviderEffect>().FirstOrDefault();
                    }
                    catch
                    {
                        injected = null; // 值提供器构建失败：保留原绑定，避免内联一个不可用的实例。
                    }
                    if (injected is null) continue; // 该 provider 无法构建出可内联的值提供器，保留原有绑定。

                    injected.BindedEffectProvidingSystemID = sourceId.ToString();
                    fields[fieldKey] = injected;
                    inlinedIds.Add(sourceId);
                }

                // EffectProviderBase.Fields 的 getter 每次返回新物化字典，必须整体 set 才能持久化。
                consumer.Fields = fields;
            }

            return inlinedIds;
        }

        /// <summary>
        /// 移除指定 provider 生成的所有 effect 条目（key 为 <c>{id}_{subIdx}</c> 或直接等于 id 字符串）。
        /// 用于将被内联的值提供器从渲染管线中剔除时，清理其残留的旧 effect。
        /// </summary>
        private static void RemoveProviderEffects(Dictionary<string, IEffect> effects, Guid providerId)
        {
            var idString = providerId.ToString();
            string prefix = idString + "_";
            foreach (var key in effects.Keys.ToList())
            {
                if (key == idString || key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    effects.Remove(key);
                }
            }
        }

        public static Dictionary<string, IEffect>? RebuildAllEffects(
            IReadOnlyDictionary<Guid, IEffectProvider>? effectProviders,
            Dictionary<string, IEffect>? existingEffects)
        {
            var newEffects = new Dictionary<string, IEffect>();
            int globalIndex = 0;

            // Preserve manually-added effects (those without a BindedEffectProvidingSystemID) from the current Effects.
            if (existingEffects != null)
            {
                foreach (var kvp in existingEffects)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Value.BindedEffectProvidingSystemID))
                    {
                        newEffects[kvp.Key] = kvp.Value;
                        if (kvp.Value.Index >= globalIndex)
                            globalIndex = kvp.Value.Index + 1;
                    }
                }
            }

            if (effectProviders != null)
            {
                MaterializeFields(effectProviders.Values);
                var activePictureProviders = GetActivePictureProviderIds(effectProviders);
                var inlinedProviderIds = InlineValueProvidersIntoConsumers(effectProviders);
                var sortedProviders = SortEffectProviders(effectProviders);
                if (!sortedProviders.ListAny()) return null;
                foreach (var bundleData in sortedProviders.Where(b => b.Enabled))
                {
                    if (bundleData is not IEffectProvider provider)
                    {
                        throw new InvalidOperationException($"{bundleData.TypeName} is not IEffectProvider");
                    }

                    // 已被内联进某个消费者字段的值提供器：跳过构建，使其不再进入渲染管线。
                    // 同时清除其可能残留的旧 effect 条目，避免渲染时仍执行它的值提取分支。
                    if (inlinedProviderIds.Contains(bundleData.Id))
                    {
                        RemoveProviderEffects(newEffects, bundleData.Id);
                        continue;
                    }

                    var imp = EffectHelper.ForcePreferToType
                        ?? EffectHelper.DefaultImplementsType.GetValueOrDefault($"{provider.FromPlugin}.{provider.TypeName}", EffectImplementType.NotSpecified);
                    provider.MetaData[EffectProviderBase.ImplementTypeParameterKey] = imp;
                    IEffect[] effects;
                    try
                    {
                        effects = provider.Build();
                    }
                    finally
                    {
                        provider.MetaData.Remove(EffectProviderBase.ImplementTypeParameterKey);
                    }

                    for (int i = 0; i < effects.Length; i++)
                    {
                        var effect = effects[i];
                        int subIdx = i;
                        effect.Name = $"EffectProvider {bundleData.TypeName}({bundleData.Id}){Environment.NewLine} - Subeffect #{subIdx}";
                        var detached = bundleData.Target.HasFlag(EffectTarget.ValueProvider)
                            || bundleData.Target.HasFlag(EffectTarget.Mixture)
                            || bundleData.Target.HasFlag(EffectTarget.SpeedVariance);
                        effect.Enabled = detached
                            ? bundleData.Enabled
                            : bundleData.Enabled && activePictureProviders.Contains(bundleData.Id);
                        effect.Index = globalIndex++;
                        effect.BindedEffectProvidingSystemID = bundleData.Id.ToString();
                        string key = $"{bundleData.Id}_{subIdx}";
                        if (newEffects.TryGetValue(key, out var previousEffect))
                        {
                            if (effect.RelativeWidth <= 0 && previousEffect.RelativeWidth > 0)
                            {
                                effect.RelativeWidth = previousEffect.RelativeWidth;
                            }

                            if (effect.RelativeHeight <= 0 && previousEffect.RelativeHeight > 0)
                            {
                                effect.RelativeHeight = previousEffect.RelativeHeight;
                            }
                        }
                        if (!Guid.TryParse(effect.Id, out _))
                        {
                            Log($"Effect Provider {bundleData.TypeName}({bundleData.Id})'s subeffect #{subIdx} has invalid Id '{effect.Id}', generating a new one.", "warn");
                            effect.Id = Guid.NewGuid().ToString();
                        }
                        newEffects[key] = effect;
                    }
                }

            }
            return newEffects
                .Where(e => string.IsNullOrWhiteSpace(e.Value.BindedEffectProvidingSystemID)
                           || (effectProviders?.ContainsKey(Guid.TryParse(e.Value.BindedEffectProvidingSystemID, out var g) ? g : Guid.Empty) ?? false))
                .ToDictionary();
        }

        /// <summary>
        /// 对 <see cref="IEffectProvider"/> 集合按输入/输出连接关系进行拓扑排序。
        /// </summary>
        public static List<IEffectProvider> SortEffectProviders(IReadOnlyDictionary<Guid, IEffectProvider> bundles)
        {
            var ordered = bundles.ToList();
            var adjacency = new Dictionary<Guid, List<Guid>>();
            var incoming = new Dictionary<Guid, int>();

            foreach (var kvp in ordered)
            {
                adjacency[kvp.Key] = new List<Guid>();
                incoming[kvp.Key] = 0;
            }

            foreach (var kvp in ordered)
            {
                var bundle = kvp.Value;
                var bundleId = kvp.Key;

                foreach (var inputId in GetInputDependencyIds(bundle))
                {
                    if (!bundles.ContainsKey(inputId) || inputId == bundleId) continue;
                    adjacency[inputId].Add(bundleId);
                    incoming[bundleId]++;
                }

                // Dynamic parameter bindings: the consumer depends on the value-provider bundle it
                // binds to, so the provider is built earlier and gets a lower Index in the render chain.
                foreach (var boundProviderId in GetBoundProviderDependencyIds(bundle))
                {
                    if (!bundles.ContainsKey(boundProviderId) || boundProviderId == bundleId) continue;
                    adjacency[boundProviderId].Add(bundleId);
                    incoming[bundleId]++;
                }

            }

            var queue = new Queue<Guid>(ordered.Where(kvp => incoming[kvp.Key] == 0).Select(kvp => kvp.Key));
            var result = new List<IEffectProvider>(ordered.Count);
            var visited = new HashSet<Guid>();

            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (!visited.Add(id)) continue;
                result.Add(bundles[id]);

                foreach (var next in adjacency[id])
                {
                    incoming[next]--;
                    if (incoming[next] == 0) queue.Enqueue(next);
                }
            }

            if (result.Count < ordered.Count)
            {
                var cycleIds = ordered.Where(kvp => !visited.Contains(kvp.Key)).Select(kvp => kvp.Key);
                throw new InvalidOperationException($"Effect bundle graph has a cycle. Unresolved ids: {string.Join(", ", cycleIds)}");
            }

            return result;
        }

        public sealed record BindingDiagnostic(Guid? ProviderId, string Code, string Message);

        /// <summary>Returns provider dependencies declared by stored value-field bindings.</summary>
        public static IEnumerable<Guid> GetBoundProviderDependencyIds(IEffectProvider provider)
        {
            foreach (var binding in provider.EnumerateFieldBindings())
                if (Guid.TryParse(binding.Value, out var id)) yield return id;
        }

        public static IEnumerable<Guid> GetInputDependencyIds(IEffectProvider provider)
        {
            var source = provider.GetMainInputSource();
            if (Guid.TryParse(source, out var id) && IsProviderReference(id)) yield return id;
        }

        private static bool IsProviderReference(Guid id) =>
            id != IEffectProvider.NoConnectionGUID
            && id != IEffectProvider.InputAnchorGUID
            && id != IEffectProvider.OutputAnchorGUID;

        private static string CanonicalizeSourceIdentifier(string sourceId)
        {
            if (Guid.TryParse(sourceId, out var guid)) return guid.ToString();
            return sourceId switch
            {
                "__Builtin_frame" => ValueProviderFrameContext.BuiltInFrameProviderId,
                "__Builtin_progress" => ValueProviderFrameContext.BuiltInProgressProviderId,
                _ => sourceId,
            };
        }

        /// <summary>
        /// Normalizes legacy two-way picture bindings and legacy field bindings into each target
        /// provider's own AnchorsBindingState. Ambiguous graphs are preserved for validation.
        /// </summary>
        public static IReadOnlyList<BindingDiagnostic> NormalizeStoredBindings(IDictionary<Guid, IEffectProvider>? providers)
        {
            var diagnostics = new List<BindingDiagnostic>();
            if (providers is null) return diagnostics;
            var legacyOutputs = new List<(Guid Source, Guid Target)>();
            var legacyFieldSources = new Dictionary<(Guid ProviderId, string FieldId), string>();

            foreach (var provider in providers.Values)
            {
                var fields = provider.Fields ?? [];
                var state = new Dictionary<string, string>(provider.AnchorsBindingState ?? []);
                foreach (var key in state.Keys.ToList())
                    state[key] = CanonicalizeSourceIdentifier(state[key]);

                foreach (var (fieldId, field) in fields)
                {
                    var legacySource = field switch
                    {
                        DynamicEffectParamField dynamicField => dynamicField.BoundProviderId,
                        IValueProviderEffect valueEffect => valueEffect.BindedEffectProvidingSystemID,
                        _ => null,
                    };
                    if (!string.IsNullOrWhiteSpace(legacySource))
                        legacyFieldSources[(provider.Id, fieldId)] = CanonicalizeSourceIdentifier(legacySource);
                    if (!state.ContainsKey(fieldId) && !string.IsNullOrWhiteSpace(legacySource))
                        state[fieldId] = CanonicalizeSourceIdentifier(legacySource);
                }

                foreach (var key in state.Keys.ToList())
                {
                    if (key == EffectProviderAnchorExtensions.InputKey || key == EffectProviderAnchorExtensions.OutputKey || fields.ContainsKey(key))
                        continue;
                    state.Remove(key);
                    diagnostics.Add(new(provider.Id, "UnknownBindingKey", $"Removed unknown binding key '{key}' from provider {provider.Id}."));
                }

                var hasPictureInput = provider.HasMainPictureInput();
                if (!hasPictureInput)
                {
                    var declaredPictureInputs = provider.InFields
                        .Where(field => field.Value.FieldType.HasFlag(EffectArgumentFieldType.IPicture))
                        .Select(field => field.Key)
                        .ToList();
                    if (declaredPictureInputs.Count != 0)
                    {
                        diagnostics.Add(new(provider.Id, "UnsupportedPictureInputs",
                            $"Provider {provider.Id} declares unsupported picture inputs: {string.Join(", ", declaredPictureInputs)}."));
                    }
                    state[EffectProviderAnchorExtensions.InputKey] = IEffectProvider.NoConnectionGUID.ToString();
                    state[EffectProviderAnchorExtensions.OutputKey] = IEffectProvider.NoConnectionGUID.ToString();
                    provider.AnchorsBindingState = state;
                    continue;
                }

                if (!state.TryGetValue(EffectProviderAnchorExtensions.InputKey, out var input) || string.IsNullOrWhiteSpace(input))
                    state[EffectProviderAnchorExtensions.InputKey] = IEffectProvider.NoConnectionGUID.ToString();

                if (state.TryGetValue(EffectProviderAnchorExtensions.OutputKey, out var oldOutput)
                    && Guid.TryParse(oldOutput, out var oldOutputId)
                    && IsProviderReference(oldOutputId))
                {
                    legacyOutputs.Add((provider.Id, oldOutputId));
                    state[EffectProviderAnchorExtensions.OutputKey] = IEffectProvider.NoConnectionGUID.ToString();
                }
                else if (oldOutput != IEffectProvider.OutputAnchorGUID.ToString())
                {
                    state[EffectProviderAnchorExtensions.OutputKey] = IEffectProvider.NoConnectionGUID.ToString();
                }

                provider.AnchorsBindingState = state;
            }

            var duplicatedFieldBindings = providers.Values
                .SelectMany(provider => provider.EnumerateFieldBindings()
                    .Select(binding => (Provider: provider, binding.Key, binding.Value)))
                .GroupBy(item => (item.Key, item.Value))
                .Where(group => group.Count() > 1);
            foreach (var group in duplicatedFieldBindings)
            {
                var evidencedTargets = group
                    .Where(item => legacyFieldSources.TryGetValue((item.Provider.Id, item.Key), out var legacySource)
                        && legacySource == item.Value)
                    .Select(item => item.Provider.Id)
                    .Distinct()
                    .ToList();
                if (evidencedTargets.Count == 1)
                {
                    foreach (var item in group.Where(item => item.Provider.Id != evidencedTargets[0]))
                    {
                        item.Provider.ClearFieldBinding(item.Key);
                        diagnostics.Add(new(item.Provider.Id, "MisplacedDuplicatedBinding",
                            $"Removed duplicated binding for field '{item.Key}' from provider {item.Provider.Id}."));
                    }
                }
                else
                {
                    diagnostics.Add(new(null, "AmbiguousDuplicatedBinding",
                        $"Binding '{group.Key.Key}' -> '{group.Key.Value}' is duplicated across providers and has no unique legacy target."));
                }
            }

            foreach (var group in legacyOutputs.GroupBy(edge => edge.Target))
            {
                if (!providers.TryGetValue(group.Key, out var target))
                {
                    foreach (var edge in group)
                        diagnostics.Add(new(edge.Source, "DanglingLegacyOutput", $"Removed legacy output reference from {edge.Source} to missing provider {edge.Target}."));
                    continue;
                }
                if (target.GetMainInputSource() != IEffectProvider.NoConnectionGUID.ToString()) continue;
                var candidates = group.Select(edge => edge.Source).Distinct().ToList();
                if (candidates.Count == 1)
                    target.SetMainInputSource(candidates[0]);
                else
                    diagnostics.Add(new(group.Key, "AmbiguousLegacyInput", $"Provider {group.Key} has multiple legacy input candidates: {string.Join(", ", candidates)}."));
            }

            foreach (var provider in providers.Values)
            {
                var input = provider.GetMainInputSource();
                if (input != IEffectProvider.NoConnectionGUID.ToString()
                    && input != IEffectProvider.InputAnchorGUID.ToString()
                    && (!Guid.TryParse(input, out var inputId) || !providers.ContainsKey(inputId)))
                {
                    provider.DisconnectMainInput();
                    diagnostics.Add(new(provider.Id, "DanglingPictureInput", $"Disconnected missing picture source '{input}' from provider {provider.Id}."));
                }

                foreach (var binding in provider.EnumerateFieldBindings().ToList())
                {
                    if (binding.Value.StartsWith("builtin://", StringComparison.Ordinal)) continue;
                    if (Guid.TryParse(binding.Value, out var sourceId)
                        && providers.ContainsKey(sourceId))
                        continue;
                    provider.ClearFieldBinding(binding.Key);
                    diagnostics.Add(new(provider.Id, "DanglingFieldBinding", $"Removed missing source '{binding.Value}' from field '{binding.Key}' on provider {provider.Id}."));
                }
            }

            return diagnostics;
        }

        /// <summary>Validates stored bindings without mutating them.</summary>
        public static IReadOnlyList<BindingDiagnostic> ValidateBindings(IReadOnlyDictionary<Guid, IEffectProvider>? providers)
        {
            var diagnostics = new List<BindingDiagnostic>();
            if (providers is null) return diagnostics;

            var finalProviders = providers.Values.Where(p => p.IsFinalOutputSource()).ToList();
            if (finalProviders.Count > 1)
                diagnostics.Add(new(null, "MultipleFinalOutputs", $"Multiple providers are connected to final output: {string.Join(", ", finalProviders.Select(p => p.Id))}."));

            foreach (var provider in providers.Values)
            {
                var pictureInputs = provider.InFields
                    .Where(field => field.Value.FieldType.HasFlag(EffectArgumentFieldType.IPicture))
                    .Select(field => field.Key)
                    .ToList();
                if (pictureInputs.Count != 0 && !provider.HasMainPictureInput())
                    diagnostics.Add(new(provider.Id, "UnsupportedPictureInputs", $"Provider {provider.Id} must use '__Input__' as its only picture input."));

                var input = provider.GetMainInputSource();
                if (input == IEffectProvider.NoConnectionGUID.ToString() || input == IEffectProvider.InputAnchorGUID.ToString())
                    continue;
                if (!Guid.TryParse(input, out var sourceId) || !providers.TryGetValue(sourceId, out var source))
                {
                    diagnostics.Add(new(provider.Id, "DanglingPictureInput", $"Provider {provider.Id} references missing picture source '{input}'."));
                    continue;
                }
                if (sourceId == provider.Id)
                    diagnostics.Add(new(provider.Id, "SelfPictureBinding", $"Provider {provider.Id} uses itself as picture input."));
                if (source.Target.HasFlag(EffectTarget.ValueProvider))
                    diagnostics.Add(new(provider.Id, "InvalidPictureSource", $"Value provider {sourceId} cannot be used as a picture input."));
            }

            foreach (var provider in providers.Values)
            {
                var seen = new HashSet<Guid>();
                var current = provider;
                while (true)
                {
                    if (!seen.Add(current.Id))
                    {
                        diagnostics.Add(new(provider.Id, "PictureCycle", $"Picture binding cycle detected from provider {provider.Id}."));
                        break;
                    }
                    var source = current.GetMainInputSource();
                    if (!Guid.TryParse(source, out var sourceId) || !providers.TryGetValue(sourceId, out var next)) break;
                    current = next;
                }
            }

            foreach (var provider in providers.Values)
            {
                foreach (var binding in provider.EnumerateFieldBindings())
                {
                    if (!provider.Fields.ContainsKey(binding.Key))
                        diagnostics.Add(new(provider.Id, "UnknownFieldBinding", $"Provider {provider.Id} does not own field '{binding.Key}'."));
                    if (binding.Value.StartsWith("builtin://", StringComparison.Ordinal)) continue;
                    if (Guid.TryParse(binding.Value, out var sourceId))
                    {
                        if (providers.TryGetValue(sourceId, out var fieldSource))
                        {
                            if (!fieldSource.Target.HasFlag(EffectTarget.ValueProvider))
                                diagnostics.Add(new(provider.Id, "InvalidFieldSource", $"Field '{binding.Key}' references non-value provider {sourceId}."));
                            continue;
                        }
                    }
                    diagnostics.Add(new(provider.Id, "DanglingFieldBinding", $"Field '{binding.Key}' references missing source '{binding.Value}'."));
                }
            }

            if (finalProviders.Count == 1)
            {
                var current = finalProviders[0];
                var seen = new HashSet<Guid>();
                while (seen.Add(current.Id))
                {
                    var source = current.GetMainInputSource();
                    if (source == IEffectProvider.InputAnchorGUID.ToString()) break;
                    if (!Guid.TryParse(source, out var sourceId) || !providers.TryGetValue(sourceId, out var next))
                    {
                        diagnostics.Add(new(finalProviders[0].Id, "BrokenActivePath", $"Final provider {finalProviders[0].Id} does not have a complete path to the source picture."));
                        break;
                    }
                    current = next;
                }
            }

            return diagnostics.Distinct().ToList();
        }

        public static HashSet<Guid> GetActivePictureProviderIds(IReadOnlyDictionary<Guid, IEffectProvider>? providers)
        {
            if (providers is null || providers.Count == 0) return [];
            var errors = ValidateBindings(providers);
            if (errors.Count != 0)
                throw new InvalidOperationException("Invalid effect binding graph: " + string.Join(" ", errors.Select(e => e.Message)));

            var finalProvider = providers.Values.SingleOrDefault(p => p.IsFinalOutputSource());
            if (finalProvider is null) return [];

            var result = new HashSet<Guid>();
            var current = finalProvider;
            while (result.Add(current.Id))
            {
                var source = current.GetMainInputSource();
                if (source == IEffectProvider.InputAnchorGUID.ToString()) return result;
                current = providers[Guid.Parse(source)];
            }
            throw new InvalidOperationException($"Picture binding graph contains a cycle ending at provider {current.Id}.");
        }

        /// <summary>
        /// 将新添加的 EffectProvider 自动接入到输出链中：插在距离输出画面最近的同Target Provider 与输出画面之间。
        /// </summary>
        public static void AutoConnectProviderToOutput(
            IDictionary<Guid, IEffectProvider>? effectProviders,
            IEffectProvider newProvider,
            EffectTarget effectTarget)
        {
            if (effectProviders is null) throw new ArgumentNullException(nameof(effectProviders));
            if (!newProvider.HasMainPictureInput())
            {
                newProvider.DisconnectMainInput();
                newProvider.SetFinalOutputSource(false);
                return;
            }
            var lastProvider = effectProviders.Values
                .FirstOrDefault(b => b.IsFinalOutputSource()
                                   && AreTargetsCompatible(b.Target, effectTarget)
                                   && b.Id != newProvider.Id);

            foreach (var provider in effectProviders.Values) provider.SetFinalOutputSource(false);
            if (lastProvider != null)
            {
                newProvider.SetMainInputSource(lastProvider.Id);
            }
            else
            {
                newProvider.SetMainInputSource(IEffectProvider.InputAnchorGUID);
            }
            newProvider.SetFinalOutputSource(true);
        }

        /// <summary>Inserts a picture provider immediately after the source picture, preserving every fan-out branch.</summary>
        public static void AutoConnectProviderToInput(
            IDictionary<Guid, IEffectProvider> effectProviders,
            IEffectProvider newProvider)
        {
            if (!newProvider.HasMainPictureInput())
                return;

            var roots = effectProviders.Values
                .Where(provider => provider.Id != newProvider.Id
                    && provider.GetMainInputSource() == IEffectProvider.InputAnchorGUID.ToString())
                .ToList();
            var hasFinalOutput = effectProviders.Values
                .Any(provider => provider.Id != newProvider.Id && provider.IsFinalOutputSource());
            newProvider.SetMainInputSource(IEffectProvider.InputAnchorGUID);
            newProvider.SetFinalOutputSource(!hasFinalOutput);
            foreach (var root in roots) root.SetMainInputSource(newProvider.Id);
        }

        /// <summary>
        /// 判断两个 EffectTarget 是否兼容（可连接）。
        /// </summary>
        public static bool AreTargetsCompatible(EffectTarget a, EffectTarget b)
        {
            var aBase = a & ~(EffectTarget.IsKeyFramed | EffectTarget.IsNotVisibleInEffectEditor | EffectTarget.IsNotVisibleInNewEffectSelector);
            var bBase = b & ~(EffectTarget.IsKeyFramed | EffectTarget.IsNotVisibleInEffectEditor | EffectTarget.IsNotVisibleInNewEffectSelector);
            if (aBase == EffectTarget.NotSpecified || bBase == EffectTarget.NotSpecified)
                return true;
            return (aBase & bBase) != 0;
        }

        /// <summary>Materializes runtime Fields exclusively from stored binding configuration.</summary>
        public static void MaterializeFields(IEnumerable<IEffectProvider> providers)
        {
            foreach (var provider in providers)
            {
                var fields = provider.Fields ?? [];
                foreach (var fieldKey in fields.Keys.ToList())
                {
                    var field = fields[fieldKey];
                    object? fallback = field switch
                    {
                        StaticEffectArgumentField staticField => staticField.Value,
                        DynamicEffectParamField dynamicField => dynamicField.StaticFallbackValue,
                        _ => field.DefaultValue,
                    };

                    if (provider.TryGetFieldBinding(fieldKey, out var sourceId))
                        fields[fieldKey] = new DynamicEffectParamField
                        {
                            Id = fieldKey,
                            FieldType = field.FieldType,
                            BoundProviderId = sourceId,
                            StaticFallbackValue = fallback,
                            DefaultValue = field.DefaultValue,
                            MinValue = field.MinValue,
                            MaxValue = field.MaxValue,
                            PresetOptions = field.PresetOptions,
                            Remarks = field.Remarks,
                        };
                    else if (field is not StaticEffectArgumentField)
                        fields[fieldKey] = new StaticEffectArgumentField
                        {
                            Id = fieldKey,
                            FieldType = field.FieldType,
                            Value = EffectParamConvert.Normalize(fallback) ?? new object(),
                            DefaultValue = field.DefaultValue,
                            MinValue = field.MinValue,
                            MaxValue = field.MaxValue,
                            PresetOptions = field.PresetOptions,
                            Remarks = field.Remarks,
                        };
                }
                provider.Fields = fields;
            }
        }

        public static void SetFinalOutput(IDictionary<Guid, IEffectProvider> providers, Guid? providerId)
        {
            if (providerId.HasValue
                && (!providers.TryGetValue(providerId.Value, out var selected)
                    || !selected.OutField.FieldType.HasFlag(EffectArgumentFieldType.IPicture)))
            {
                throw new ArgumentException($"Provider '{providerId}' cannot be used as the final picture output.", nameof(providerId));
            }

            foreach (var provider in providers.Values)
                provider.SetFinalOutputSource(providerId.HasValue && provider.Id == providerId.Value);
        }

        public static void RemoveReferencesTo(IEnumerable<IEffectProvider> providers, string sourceId)
        {
            foreach (var provider in providers)
            {
                if (provider.GetMainInputSource() == sourceId) provider.DisconnectMainInput();
                foreach (var binding in provider.EnumerateFieldBindings().Where(b => b.Value == sourceId).ToList())
                    provider.ClearFieldBinding(binding.Key);
            }
        }

        public static bool RemoveProvider(IDictionary<Guid, IEffectProvider> providers, Guid providerId)
        {
            if (!providers.ContainsKey(providerId)) return false;
            RemoveReferencesTo(providers.Values, providerId.ToString());
            return providers.Remove(providerId);
        }

        private const string InputNodeId = "INPUT";
        private const string OutputNodeId = "OUTPUT";

        /// <summary>
        /// 生成一张描述该 Clip 的 EffectProvider 渲染绑定关系的 Mermaid 图。
        /// 反映 <see cref="IEffectProvider.Build()"/> 实际构建 effect 时使用的 <see cref="IEffectProvider.Fields"/>
        /// 绑定关系（动态字段绑定 / 内联值提供器），以及 <see cref="IEffectProvider.AnchorsBindingState"/>
        /// 中的输入/输出锚点连接。图中包含"输入（源画面）"与"输出（最终画面）"两个终端节点，
        /// 连接线上标注接收侧的锚点键 / 字段 Id。
        /// </summary>
        /// <remarks>
        /// 图以 <see cref="IEffectProvider.Build()"/> 的角度绘制，而非 <see cref="RebuildAllEffects"/> 的角度，
        /// 因此能反映内联后的真实绑定状态，但无法表达渲染后实际子效果条目的顺序。
        /// </remarks>
        public static string GenerateRenderTimeMermaidDiagram(IReadOnlyDictionary<Guid, IEffectProvider>? effectProviders)
        {
            if (effectProviders is null || effectProviders.Count == 0)
                return "graph TD;\n    empty[\"无 EffectProvider 数据\"];";

            var sb = new StringBuilder();
            sb.AppendLine("graph TD;");

            var nodeByProvider = AssignNodes(sb, effectProviders.Values, p =>
            {
                var displayName = string.IsNullOrWhiteSpace(p.Name) ? p.TypeName : p.Name;
                var flags = p.Target.HasFlag(EffectTarget.ValueProvider) ? " (值提供器)" : "";
                if (!p.Enabled) flags += " (已禁用)";
                return $"{p.TypeName}: {displayName}{flags}";
            });
            AppendTerminalNodes(sb);
            AppendAnchorEdges(sb, effectProviders, nodeByProvider);
            AppendFieldBindingEdges(sb, effectProviders, nodeByProvider, useRenderTimeFields: true);
            return sb.ToString();
        }

        /// <summary>
        /// 生成一张描述该 Clip 的 EffectProvider 存储绑定关系的 Mermaid 图。
        /// 直接反映规范化后的 <see cref="IEffectProvider.AnchorsBindingState"/>：
        /// 主图片输入、最终输出标记，以及以普通 field id 为 key 的值来源配置。
        /// 同样包含"输入（源画面）"/"输出（最终画面）"终端节点，连接线上标注锚点键 / 字段 Id。
        /// </summary>
        /// <remarks>
        /// 该图表示磁盘/保存模型，不反映渲染时内联后的字段绑定状态。
        /// </remarks>
        public static string GenerateStoredMermaidDiagram(IReadOnlyDictionary<Guid, IEffectProvider>? effectProviders)
        {
            if (effectProviders is null || effectProviders.Count == 0)
                return "graph TD;\n    empty[\"无 EffectProvider 数据\"];";

            var sb = new StringBuilder();
            sb.AppendLine("graph TD;");

            var nodeByProvider = AssignNodes(sb, effectProviders.Values, p => p.TypeName);
            AppendTerminalNodes(sb);
            AppendAnchorEdges(sb, effectProviders, nodeByProvider);
            AppendFieldBindingEdges(sb, effectProviders, nodeByProvider, useRenderTimeFields: false);
            return sb.ToString();
        }

        /// <summary>
        /// 为每个 provider 生成节点定义，返回 <see cref="Guid"/> → Mermaid 节点 id 的映射。
        /// </summary>
        private static Dictionary<Guid, string> AssignNodes(StringBuilder sb, IEnumerable<IEffectProvider> providers, Func<IEffectProvider, string> labelBuilder)
        {
            var nodeByProvider = new Dictionary<Guid, string>();
            int counter = 0;
            foreach (var p in providers)
            {
                nodeByProvider[p.Id] = "P" + counter++;
                sb.AppendLine($"    {nodeByProvider[p.Id]}[\"{Escape(labelBuilder(p))}\"];");
            }
            return nodeByProvider;
        }

        /// <summary>
        /// 定义"输入（源画面）"与"输出（最终画面）"两个终端节点。
        /// </summary>
        private static void AppendTerminalNodes(StringBuilder sb)
        {
            sb.AppendLine($"    {InputNodeId}[\"输入 (源画面)\"];");
            sb.AppendLine($"    {OutputNodeId}[\"输出 (最终画面)\"];");
        }

        /// <summary>
        /// 根据输入/输出锚点连接生成边：输入锚 → 本 Provider（标注接收锚点键），
        /// 本 Provider → 输出锚（标注输出键 <c>__Output__</c>），并连接到终端节点。
        /// 输出锚直接指向 <see cref="IEffectProvider.OutputAnchorGUID"/> 的节点连到"输出"。
        /// </summary>
        private static void AppendAnchorEdges(StringBuilder sb, IReadOnlyDictionary<Guid, IEffectProvider> effectProviders, Dictionary<Guid, string> nodeByProvider)
        {
            var anchorLines = new List<string>();
            foreach (var p in effectProviders.Values)
            {
                var input = p.GetMainInputSource();
                if (input == IEffectProvider.InputAnchorGUID.ToString())
                {
                    anchorLines.Add($"    {InputNodeId} -->|\"{Escape(EffectProviderAnchorExtensions.InputKey)}\"| {nodeByProvider[p.Id]};");
                }
                else if (Guid.TryParse(input, out var sourceId) && effectProviders.ContainsKey(sourceId))
                {
                    anchorLines.Add($"    {nodeByProvider[sourceId]} -->|\"{Escape(EffectProviderAnchorExtensions.InputKey)}\"| {nodeByProvider[p.Id]};");
                }

                if (p.IsFinalOutputSource())
                {
                    anchorLines.Add($"    {nodeByProvider[p.Id]} -->|\"{Escape(EffectProviderAnchorExtensions.OutputKey)}\"| {OutputNodeId};");
                }
            }

            if (!effectProviders.Values.Any(p => p.IsFinalOutputSource()))
                anchorLines.Add($"    {InputNodeId} -->|\"Picture\"| {OutputNodeId};");

            foreach (var line in anchorLines.Distinct())
                sb.AppendLine(line);
        }

        /// <summary>
        /// 根据字段绑定生成边。渲染时使用 <see cref="IEffectProvider.Fields"/>（反映内联后的动态绑定），
        /// 存储时使用 <see cref="IEffectProvider.AnchorsBindingState"/> 中以普通字段 Id 为 key 的条目。
        /// 连接线上标注字段 Id，以及绑定模式（动态 / 内联）。
        /// </summary>
        private static void AppendFieldBindingEdges(StringBuilder sb, IReadOnlyDictionary<Guid, IEffectProvider> effectProviders, Dictionary<Guid, string> nodeByProvider, bool useRenderTimeFields)
        {
            var fieldEdges = new List<string>();
            foreach (var consumer in effectProviders.Values)
            {
                var sourceIdAndFieldId = new List<(Guid SourceId, string FieldId, string Mode)>();
                if (useRenderTimeFields)
                {
                    Dictionary<string, IEffectArgumentField>? fields;
                    try
                    {
                        fields = consumer.Fields;
                    }
                    catch
                    {
                        continue; // 字段物化失败：与 InlineValueProvidersIntoConsumers 保持一致。
                    }
                    if (fields is null) continue;

                    foreach (var field in fields.Values)
                    {
                        string? sourceIdString = field switch
                        {
                            DynamicEffectParamField df => df.BoundProviderId,
                            IValueProviderEffect vpe => vpe.BindedEffectProvidingSystemID,
                            _ => null,
                        };
                        if (sourceIdString is null) continue;
                        if (Guid.TryParse(sourceIdString, out var sourceId)
                            && effectProviders.TryGetValue(sourceId, out var source)
                            && source.Target.HasFlag(EffectTarget.ValueProvider))
                        {
                            sourceIdAndFieldId.Add((sourceId, field.Id, field is IValueProviderEffect ? "内联" : "动态"));
                        }
                    }
                }
                else
                {
                    if (consumer.AnchorsBindingState is null) continue;
                    foreach (var kvp in consumer.AnchorsBindingState)
                    {
                        if (kvp.Key == EffectProviderAnchorExtensions.InputKey
                            || kvp.Key == EffectProviderAnchorExtensions.OutputKey)
                            continue;
                        if (Guid.TryParse(kvp.Value, out var sourceId)
                            && effectProviders.TryGetValue(sourceId, out var source)
                            && source.Target.HasFlag(EffectTarget.ValueProvider))
                        {
                            sourceIdAndFieldId.Add((sourceId, kvp.Key, "动态"));
                        }
                    }
                }

                foreach (var (sourceId, fieldId, mode) in sourceIdAndFieldId)
                {
                    fieldEdges.Add($"    {nodeByProvider[sourceId]} -->|\"{Escape(fieldId)} ({mode})\"| {nodeByProvider[consumer.Id]};");
                }
            }

            foreach (var line in fieldEdges.Distinct())
                sb.AppendLine(line);
        }

        /// <summary>
        /// 转义 Mermaid 标签文本中需要转义的字符（引号、方括号、大括号）。
        /// </summary>
        private static string Escape(string text)
        {
            if (text is null) return "";
            return text
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("[", "\\[")
                .Replace("]", "\\]")
                .Replace("{", "\\{")
                .Replace("}", "\\}");
        }
    }
}
