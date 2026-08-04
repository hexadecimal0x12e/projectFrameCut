using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Linq;

namespace projectFrameCut.Render.Effect
{
    public static class EffectBindingHelper
    {
        /// <summary>
        /// 将旧的 EffectBundle 数据迁移为新的 <see cref="IEffectProvider"/> 实例集合。
        /// 若已存在新的 EffectProvider 数据则优先使用；否则从 EffectBundles 转换。
        /// 找不到对应工厂的类型会被跳过，保证加载过程稳定。
        /// </summary>
        private static readonly Dictionary<Guid, int> subIdxByProvider = new();
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
                    instance.AnchorsBindingState = p.AnchorsBindingState ?? new Dictionary<string, Guid>();
                    instance.MetaData = p.MetaData ?? new Dictionary<string, object>();
                    RestoreProviderFields(instance, p.Fields);
                    result[instance.Id] = instance;
                }

                InlineValueProvidersIntoConsumers(result);
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
                instance.Fields = new();
                instance.MetaData = new Dictionary<string, object>();
                instance.SetInputAnchor(b.BindedInputId);
                instance.SetOutputAnchor(b.BindedOutputId);
                instance.SetInputAnchors(b.BindedInputIds ?? []);
                foreach (var kvp in b.Parameters ?? new Dictionary<string, object>())
                {
                    instance.Fields[kvp.Key] = new StaticEffectArgumentField(kvp.Value, EffectArgumentFieldType.Unknown);
                }
                result[instance.Id] = instance;
            }

            return result;
        }

        /// <summary>
        /// 从序列化的字段描述重建 provider 的 <see cref="IEffectProvider.Fields"/>。
        /// 与 <see cref="EffectFieldPool.RebuildField"/> 保持相同的判定规则，避免导入后绑定状态丢失。
        /// </summary>
        private static void RestoreProviderFields(IEffectProvider provider, EffectProviderFieldJSONStructure[]? fieldDtos)
        {
            if (fieldDtos is null || fieldDtos.Length == 0) return;

            var fields = new Dictionary<string, IEffectArgumentField>();
            foreach (var dto in fieldDtos)
            {
                if (!Enum.TryParse<EffectArgumentFieldType>(dto.FieldType, ignoreCase: true, out var fieldType))
                    fieldType = EffectArgumentFieldType.Unknown;

                var isDynamic = dto.IsBound || string.Equals(dto.TypeName, "DynamicEffectParamField", StringComparison.Ordinal);
                if (isDynamic)
                {
                    fields[dto.Id] = new DynamicEffectParamField
                    {
                        Id = dto.Id,
                        FieldType = fieldType,
                        BoundProviderId = dto.BoundSourceId,
                        StaticFallbackValue = EffectParamConvert.Normalize(dto.StaticValue),
                        DefaultValue = dto.DefaultValue,
                        MinValue = dto.MinValue,
                        MaxValue = dto.MaxValue,
                        PresetOptions = dto.PresetOptions,
                        Remarks = dto.Remarks,
                    };
                }
                else
                {
                    fields[dto.Id] = new StaticEffectArgumentField
                    {
                        Id = dto.Id,
                        FieldType = fieldType,
                        Value = EffectParamConvert.Normalize(dto.StaticValue) ?? new object(),
                        DefaultValue = dto.DefaultValue,
                        MinValue = dto.MinValue,
                        MaxValue = dto.MaxValue,
                        PresetOptions = dto.PresetOptions,
                        Remarks = dto.Remarks,
                    };
                }
            }

            try
            {
                provider.Fields = fields;
            }
            catch
            {
                // 保留默认字段值：可能缺少必填元数据（如 required 的 FieldType/Value）导致重建失败。
            }
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
        /// 计算当前已被内联进某个消费者字段的值提供器 id 集合。
        /// </summary>
        /// <remarks>
        /// 仅当字段已被内联为 <see cref="IValueProviderEffect"/> 时才认为该值提供器已被剔除出渲染管线。
        /// 仍以 <see cref="DynamicEffectParamField"/> 绑定的值提供器（走 <see cref="ValueProviderFrameContext"/>
        /// 按 id 寻址的机制）必须保留在管线中构建，否则消费者无法读到它的值。
        /// </remarks>
        private static HashSet<Guid> GetInlinedValueProviderIds(IReadOnlyDictionary<Guid, IEffectProvider> providers)
        {
            var inlinedIds = new HashSet<Guid>();
            if (providers.Count == 0) return inlinedIds;

            foreach (var consumer in providers.Values)
            {
                Dictionary<string, IEffectArgumentField>? fields;
                try
                {
                    fields = consumer.Fields;
                }
                catch
                {
                    continue; // 字段物化失败：与 InlineValueProvidersIntoConsumers 保持一致，跳过该消费者。
                }
                if (fields is null) continue;
                foreach (var field in fields.Values)
                {
                    if (field is IValueProviderEffect vpe && Guid.TryParse(vpe.BindedEffectProvidingSystemID, out var sourceId))
                    {
                        inlinedIds.Add(sourceId);
                    }
                }
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
                // 每次重建前刷新内联：将绑定到值提供器的字段内联为 IValueProviderEffect，
                // 让对源 provider 参数的修改能传播到消费者，并据此从本管线剔除已被内联的值提供器。
                InlineValueProvidersIntoConsumers(effectProviders);
                NormalizeProviderPipeline(effectProviders);
                var sortedProviders = SortEffectProviders(effectProviders);
                if (!sortedProviders.ListAny()) return null;
                var inlinedProviderIds = GetInlinedValueProviderIds(effectProviders);
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
                        int subIdx = subIdxByProvider.TryGetValue(bundleData.Id, out var n) ? n : 0;
                        subIdxByProvider[bundleData.Id] = subIdx + 1;
                        effect.Name = $"EffectProvider {bundleData.TypeName}({bundleData.Id}){Environment.NewLine} - Subeffect #{subIdx}";
                        effect.Enabled = bundleData.Target.HasFlag(EffectTarget.ValueProvider)
                            ? bundleData.Enabled
                            : bundleData.Enabled && bundleData.GetOutputAnchor() != IEffectProvider.NoConnectionGUID;
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

                var outputId = bundle.GetOutputAnchor();
                if (IsValidOutputDependency(outputId) && bundles.ContainsKey(outputId) && outputId != bundleId)
                {
                    adjacency[bundleId].Add(outputId);
                    incoming[outputId]++;
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

        /// <summary>
        /// Yields the value-provider bundle Guids that this bundle's parameters bind to
        /// (via the reserved <c>__Binding_*</c> keys). Built-in sources like <c>builtin://frame</c>
        /// are not Guids and are ignored here.
        /// </summary>
        public static IEnumerable<Guid> GetBoundProviderDependencyIds(IEffectProvider bundle)
        {
            if (bundle.Fields is null) yield break;
            foreach (var kvp in bundle.Fields)
            {
                if (kvp.Value is DynamicEffectParamField df && df.BoundProviderId is { } sourceId)
                {
                    if (Guid.TryParse(sourceId, out var g)) yield return g;
                }
            }
        }

        public static IEnumerable<Guid> GetInputDependencyIds(IEffectProvider bundle)
        {
            if (bundle.HasMultiInputAnchors())
            {
                if (bundle.GetInputAnchors() is null) yield break;
                foreach (var id in bundle.GetInputAnchors())
                {
                    if (IsValidInputDependency(id)) yield return id;
                }
                yield break;
            }

            if (IsValidInputDependency(bundle.GetInputAnchor()))
            {
                yield return bundle.GetInputAnchor();
                yield break;
            }

            if (bundle.GetInputAnchors() is not null && bundle.GetInputAnchors().Count > 0 && IsValidInputDependency(bundle.GetInputAnchors()[0]))
            {
                // DraftEffectBindingView may store single-input connections in BindedInputIds[0].
                yield return bundle.GetInputAnchors()[0];
            }
        }

        public static bool IsValidInputDependency(Guid id)
        {
            return id != IEffectProvider.NoConnectionGUID && id != IEffectProvider.InputAnchorGUID;
        }

        public static bool IsValidOutputDependency(Guid id)
        {
            return id != IEffectProvider.NoConnectionGUID && id != IEffectProvider.OutputAnchorGUID;
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
            var lastProvider = effectProviders.Values
                .FirstOrDefault(b => b.GetOutputAnchor() == IEffectProvider.OutputAnchorGUID
                                   && AreTargetsCompatible(b.Target, effectTarget)
                                   && b.Id != newProvider.Id);

            if (lastProvider != null)
            {
                lastProvider.SetOutputAnchor(newProvider.Id);
                newProvider.SetInputAnchor(lastProvider.Id);
                newProvider.SetOutputAnchor(IEffectProvider.OutputAnchorGUID);
            }
            else
            {
                newProvider.SetInputAnchor(IEffectProvider.InputAnchorGUID);
                newProvider.SetOutputAnchor(IEffectProvider.OutputAnchorGUID);
            }
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

        /// <summary>
        /// 验证并修复所有 EffectProvider 的连接一致性：
        /// - 自身连接 → 断开
        /// - 单向连接（A→B 但 B 没有指回 A）→ 断开
        /// - 扇入（多个 bundle 的输入指向同一个 source）→ 只保留第一个
        /// </summary>
        public static void ValidateAndFixProviderConnections(IReadOnlyDictionary<Guid, IEffectProvider>? effectProviders)
        {
            if (effectProviders == null || effectProviders.Count == 0) return;
            var bundles = effectProviders;

            foreach (var bundle in bundles.Values)
            {
                // 自身连接
                if (bundle.GetInputAnchor() == bundle.Id)
                {
                    bundle.SetInputAnchor(IEffectProvider.NoConnectionGUID);
                    if (bundle.GetInputAnchors() is not null && bundle.GetInputAnchors().Count > 0)
                        bundle.GetInputAnchors()[0] = IEffectProvider.NoConnectionGUID;
                }
                if (bundle.GetOutputAnchor() == bundle.Id)
                {
                    bundle.SetOutputAnchor(IEffectProvider.NoConnectionGUID);
                }

                // 单输入：BindedInputId 指向的 bundle 必须将其 BindedOutputId 指回自己
                if (!bundle.HasMultiInputAnchors())
                {
                    if (IsValidInputDependency(bundle.GetInputAnchor()))
                    {
                        if (!bundles.TryGetValue(bundle.GetInputAnchor(), out var src) || src.GetOutputAnchor() != bundle.Id)
                        {
                            bundle.SetInputAnchor(IEffectProvider.NoConnectionGUID);
                            if (bundle.GetInputAnchors() is not null && bundle.GetInputAnchors().Count > 0)
                                bundle.GetInputAnchors()[0] = IEffectProvider.NoConnectionGUID;
                        }
                    }
                }

                // 多输入：逐个检查 BindedInputIds
                if (bundle.GetInputAnchors() is not null && bundle.HasMultiInputAnchors())
                {
                    for (int i = 0; i < bundle.GetInputAnchors().Count; i++)
                    {
                        var id = bundle.GetInputAnchors()[i];
                        if (IsValidInputDependency(id))
                        {
                            if (!bundles.TryGetValue(id, out var src) || src.GetOutputAnchor() != bundle.Id)
                                bundle.GetInputAnchors()[i] = IEffectProvider.NoConnectionGUID;
                        }
                    }
                }

                // BindedOutputId 指向的 bundle 必须将其 BindedInputId/BindedInputIds 指回自己
                if (IsValidOutputDependency(bundle.GetOutputAnchor()))
                {
                    if (!bundles.TryGetValue(bundle.GetOutputAnchor(), out var tgt))
                    {
                        bundle.SetOutputAnchor(IEffectProvider.NoConnectionGUID);
                    }
                    else
                    {
                        bool pointsBack = tgt.GetInputAnchor() == bundle.Id
                            || (tgt.GetInputAnchors()?.Contains(bundle.Id) ?? false);
                        if (!pointsBack)
                            bundle.SetOutputAnchor(IEffectProvider.NoConnectionGUID);
                    }
                }
            }

            // 扇入修复：不允许两个 bundle 的 BindedInputId 指向同一个 source
            var usedOutputs = new Dictionary<Guid, Guid>();
            foreach (var bundle in bundles.Values)
            {
                if (!bundle.HasMultiInputAnchors())
                {
                    if (IsValidInputDependency(bundle.GetInputAnchor()))
                    {
                        if (usedOutputs.TryGetValue(bundle.GetInputAnchor(), out var firstConsumer))
                        {
                            bundle.SetInputAnchor(IEffectProvider.NoConnectionGUID);
                            if (bundle.GetInputAnchors() is not null && bundle.GetInputAnchors().Count > 0)
                                bundle.GetInputAnchors()[0] = IEffectProvider.NoConnectionGUID;
                        }
                        else
                        {
                            usedOutputs[bundle.GetInputAnchor()] = bundle.Id;
                        }
                    }
                }
                else if (bundle.GetInputAnchors() is not null)
                {
                    for (int i = 0; i < bundle.GetInputAnchors().Count; i++)
                    {
                        var id = bundle.GetInputAnchors()[i];
                        if (IsValidInputDependency(id))
                        {
                            if (usedOutputs.TryGetValue(id, out var firstConsumer))
                                bundle.GetInputAnchors()[i] = IEffectProvider.NoConnectionGUID;
                            else
                                usedOutputs[id] = bundle.Id;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 规范化 Provider 管线：若检测到并行链，将其合并为单链。
        /// 仅在确实存在并行链时才执行重排，避免覆盖用户已手动配置好的单链顺序。
        /// 内部 Effect（ColorAdjustment、Crop 等）排在前面，用户 Effect 排在后面。
        /// Mixture 和 SpeedVariance 断开连接（它们在渲染时由系统直接提取，不参与绑定管线）。
        /// </summary>
        public static void NormalizeProviderPipeline(IReadOnlyDictionary<Guid, IEffectProvider>? effectProviders)
        {
            if (effectProviders == null || effectProviders.Count <= 1) return;
            var bundles = effectProviders;

            var pipelineProviders = new List<IEffectProvider>();
            var detachedProviders = new List<IEffectProvider>();

            foreach (var b in bundles.Values)
            {
                if (b.Target.HasFlag(EffectTarget.SpeedVariance)
                    || b.Target.HasFlag(EffectTarget.Mixture)
                    || b.Target.HasFlag(EffectTarget.ValueProvider))
                    detachedProviders.Add(b);
                else
                    pipelineProviders.Add(b);
            }

            foreach (var b in detachedProviders)
            {
                b.SetInputAnchor(IEffectProvider.NoConnectionGUID);
                b.SetOutputAnchor(IEffectProvider.NoConnectionGUID);
                if (b.GetInputAnchors() != null)
                {
                    for (int i = 0; i < b.GetInputAnchors().Count; i++)
                        b.GetInputAnchors()[i] = IEffectProvider.NoConnectionGUID;
                }
            }

            if (pipelineProviders.Count <= 1) return;

            var directToInputCount = pipelineProviders.Count(b =>
               b.GetInputAnchor() == IEffectProvider.InputAnchorGUID ||
               (b.GetInputAnchors()?.Contains(IEffectProvider.InputAnchorGUID) ?? false));

            if (directToInputCount <= 1) return;

            var sorted = pipelineProviders
                .OrderBy(b => b.Target.HasFlag(EffectTarget.ColorAdjustment) ? 0 : 1)
                .ThenBy(b => b.Target.HasFlag(EffectTarget.IsNotVisibleInEffectEditor) ? 0 : 1)
                .ToList();

            for (int i = 0; i < sorted.Count; i++)
            {
                var b = sorted[i];
                b.SetInputAnchor(i == 0 ? IEffectProvider.InputAnchorGUID : sorted[i - 1].Id);
                b.SetOutputAnchor(i == sorted.Count - 1 ? IEffectProvider.OutputAnchorGUID : sorted[i + 1].Id);

            }
        }
    }
}
