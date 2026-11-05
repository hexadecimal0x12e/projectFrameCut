using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

public static class DynamicEffectBindings
{
    public static bool IsGraphEffect(IEffect effect) => effect is IRenderEffectPipeline or NativeDynamicEffectGraph or DynamicMixtureGraph or FullDynamicVectorPipeline or FullDynamicMixturePipeline;

    public static bool IsGraphProvider(IEffectProvider p) => p is RenderOutputProvider
        || p.Stage == EffectStage.Composition && (p.TypeOfEffect == EffectType.MixtureProvider || p.Target.HasFlag(EffectTarget.ValueProvider))
        || p.Stage is EffectStage.Render or EffectStage.VectorContent && p is not ClipArgumentProvider && p.TypeOfEffect is
        EffectType.NormalEffect or EffectType.ContinuousEffect or EffectType.ClipPositionProvider
        or EffectType.ContinuousClipPositionProvider or EffectType.NonIPictureOutputValueProvider or EffectType.VectorPictureEffect
        or EffectType.AudioNormalEffect or EffectType.AudioContinuousEffect;

    public static bool CanBindOutput(IEffectProvider p) => p is not (ClipArgumentProvider or RenderOutputProvider or EffectVariableWriteProvider or IClipValueEndpointProvider { IsExport: true })
        && (p.Target.HasFlag(EffectTarget.ValueProvider)
            || p.Stage is EffectStage.Render or EffectStage.VectorContent or EffectStage.Text or EffectStage.Composition);

    public static Dictionary<string, IEffectArgumentField> InputFields(IEffectProvider p)
    {
        var fields = p.InFields.ToDictionary(x => x.Key, x => (IEffectArgumentField)x.Value);
        foreach (var field in p.Fields) fields[field.Key] = field.Value;
        fields.Remove(EffectProviderAnchorExtensions.OutputKey);
        fields.Remove(EffectProviderAnchorExtensions.OutputFieldKey);
        return fields;
    }

    public static bool RequiresGraph(IReadOnlyDictionary<Guid, IEffectProvider>? providers)
    {
        if (providers is null) return false;
        if (providers.Values.Any(p => p is RenderOutputProvider || p.Stage is EffectStage.Render or EffectStage.Composition)) return true;
        if (providers.Values.Any(p => p is IClipValueEndpointProvider or IMultipleOutputEffectProvider || p.GetFinalOutputFieldId() is not null
            || InputFields(p).Values.Any(f => f.FieldType.HasFlag(EffectArgumentFieldType.Any)
                || f.TypeContract is not null
                || f.FieldType.BaseType() == EffectArgumentFieldType.AudioSample)
            || p.GetOutputFields().Values.Any(f => f.FieldType.HasFlag(EffectArgumentFieldType.Any)
                || f.TypeContract is not null
                || f.FieldType.BaseType() == EffectArgumentFieldType.AudioSample)
            || p.AnchorsBindingState.Values.Contains(ValueProviderFrameContext.BuiltInAudioSampleProviderId)
            || p.AnchorsBindingState.Any(x => !EffectProviderAnchorExtensions.IsOutputBindingKey(x.Key)
                && x.Value.StartsWith("provider://", StringComparison.Ordinal)))) return true;
        var consumers = new Dictionary<string, int>();
        foreach (var p in providers.Values.Where(IsGraphProvider))
        {
            if (p is IMultipleOutputEffectProvider) return true;
            var fields = InputFields(p);
            if ((p.TypeOfEffect is EffectType.NormalEffect or EffectType.ContinuousEffect)
                && (!p.HasMainPictureInput() || !p.OutField.FieldType.IsContent())) return true;
            if (fields.TryGetValue(EffectProviderAnchorExtensions.InputKey, out var main)
                && (!main.FieldType.IsContent() || p.TypeOfEffect == EffectType.NonIPictureOutputValueProvider)) return true;
            if (fields.Any(x => x.Key != EffectProviderAnchorExtensions.InputKey && x.Value.FieldType.IsContent())) return true;
            foreach (var binding in p.EnumerateFieldBindings())
                if (binding.Value == IEffectProvider.InputAnchorGUID.ToString()
                    || EffectProviderOutputExtensions.TryParseOutputSourceId(binding.Value, out var id, out var outputId)
                        && providers.TryGetValue(id, out var source) && source.TryGetOutputField(outputId, out var output)
                        && output.FieldType.IsContent()) return true;
            if (p.HasMainPictureInput())
            {
                var source = p.GetMainInputSource();
                if (source == IEffectProvider.NoConnectionGUID.ToString()) continue;
                consumers[source] = consumers.GetValueOrDefault(source) + 1;
            }
        }
        return consumers.Values.Any(n => n > 1);
    }

    public static bool CanInlineValue(IEffectProvider provider, IReadOnlyDictionary<Guid, IEffectProvider> providers, HashSet<Guid>? seen = null,
        EffectFieldTypeResolution? types = null)
    {
        if (provider is EffectVariableProvider or ConditionalLoopNodeEffectProvider) return false;
        if (provider is IClipValueEndpointProvider { IsExport: false }) return true;
        types ??= new(providers);
        return CanInlineValue(provider, providers, types, seen is null ? [] : new(seen), new());
    }

    private static bool CanInlineValue(IEffectProvider provider, IReadOnlyDictionary<Guid, IEffectProvider> providers,
        EffectFieldTypeResolution types, HashSet<Guid> visiting, Dictionary<Guid, bool> cache)
    {
        if (visiting.Contains(provider.Id)) return false;
        if (cache.TryGetValue(provider.Id, out var result)) return result;
        visiting.Add(provider.Id);
        try
        {
            if (provider is IClipValueEndpointProvider { IsExport: false }) return cache[provider.Id] = true;
            if (provider is EffectVariableProvider or ConditionalLoopNodeEffectProvider or IClipValueEndpointProvider { IsExport: true }
                || !provider.Target.HasFlag(EffectTarget.ValueProvider)
                || provider.GetOutputFields().Keys.Any(key => types.GetOutputType(provider, key).IsContent()
                    || types.GetOutputType(provider, key).HasFlag(EffectArgumentFieldType.Any))
                || InputFields(provider).Any(p => types.GetInputType(provider, p.Key).IsContent()
                    || p.Value is StaticEffectArgumentField { Value: projectFrameCut.Drawing.Base.IPicture or projectFrameCut.Drawing.Vector.VectorPicture }
                        or DynamicEffectParamField { StaticFallbackValue: projectFrameCut.Drawing.Base.IPicture or projectFrameCut.Drawing.Vector.VectorPicture }))
                return cache[provider.Id] = false;
            foreach (var binding in provider.EnumerateFieldBindings())
            {
                if (binding.Value == IEffectProvider.NoConnectionGUID.ToString()
                    || ValueProviderFrameContext.IsBuiltInSource(binding.Value)) continue;
                if (!EffectProviderOutputExtensions.TryParseOutputSourceId(binding.Value, out var id, out var outputId)
                    || !providers.TryGetValue(id, out var source) || !source.TryGetOutputField(outputId, out _)
                    || !CanInlineValue(source, providers, types, visiting, cache)) return cache[provider.Id] = false;
            }
            return cache[provider.Id] = true;
        }
        finally { visiting.Remove(provider.Id); }
    }

    public static IReadOnlyList<EffectBindingHelper.BindingDiagnostic> Normalize(IDictionary<Guid, IEffectProvider> providers)
    {
        if (providers.Values.Any(p => p.TypeOfEffect == EffectType.MixtureProvider))
            Compose.MixtureBindings.EnsureSources(providers);
        var diagnostics = new List<EffectBindingHelper.BindingDiagnostic>();
        foreach (var p in providers.Values)
        {
            var fields = InputFields(p);
            var state = new Dictionary<string, string>(p.AnchorsBindingState);
            if (p.TypeOfEffect == EffectType.SpeedVarianceProvider)
            {
                if (state.TryGetValue(EffectProviderAnchorExtensions.InputKey, out var source) && source != IEffectProvider.NoConnectionGUID.ToString()
                    || p.IsFinalOutputSource())
                    Logger.LogDiagnostic($"Cleared picture anchors on {p.TypeName} provider {p.Id}.");
                state[EffectProviderAnchorExtensions.InputKey] = IEffectProvider.NoConnectionGUID.ToString();
                state[EffectProviderAnchorExtensions.OutputKey] = IEffectProvider.NoConnectionGUID.ToString();
                state.Remove(EffectProviderAnchorExtensions.OutputFieldKey);
            }
            foreach (var key in state.Keys.ToArray())
            {
                if (key == EffectProviderAnchorExtensions.OutputFieldKey) continue;
                if (EffectProviderOutputExtensions.TryParseOutputSourceId(state[key], out var id, out var outputId))
                    state[key] = outputId is null ? id.ToString() : EffectProviderOutputExtensions.CreateOutputSourceId(id, outputId);
                else state[key] = state[key] switch
                {
                    "__Builtin_frame" => ValueProviderFrameContext.BuiltInFrameProviderId,
                    "__Builtin_progress" => ValueProviderFrameContext.BuiltInProgressProviderId,
                    _ => state[key],
                };
                if (key != EffectProviderAnchorExtensions.InputKey && !EffectProviderAnchorExtensions.IsOutputBindingKey(key) && !fields.ContainsKey(key))
                {
                    state.Remove(key);
                    diagnostics.Add(new(p.Id, "UnknownBindingKey", $"Removed unknown binding '{key}' from {p.Id}."));
                }
            }
            p.AnchorsBindingState = state;
        }
        return diagnostics;
    }

    public static IReadOnlyList<EffectBindingHelper.BindingDiagnostic> Validate(IReadOnlyDictionary<Guid, IEffectProvider> providers, EffectFieldTypeResolution? types = null, bool forward = false)
    {
        types ??= new(providers);
        var errors = types.Diagnostics.Where(d => !(forward && d.Code == "BindingCycle"
            && providers.TryGetValue(d.ProviderId, out var cyclic) && IsGraphProvider(cyclic) && DynamicEffectCalculation.Supports(cyclic.Stage))).Select(d => new EffectBindingHelper.BindingDiagnostic(d.ProviderId, d.Code, d.Message)).ToList();
        var inlineable = new Dictionary<Guid, bool>();
        var inlineVisiting = new HashSet<Guid>();
        foreach (var group in providers.Values.Where(p => p.IsFinalOutputSource()).GroupBy(p => p.Stage))
        {
            if (group.Count() > 1) errors.Add(new(null, "MultipleFinalOutputs", $"Multiple providers feed {group.Key} output."));
            foreach (var p in group)
                if (!p.TryGetOutputField(p.GetFinalOutputFieldId(), out var output)
                    || !EffectFieldTypes.AreCompatible(types.GetOutputType(p, output.Id), p.Stage.GetContentType())
                    || !CanBindOutput(p))
                    errors.Add(new(p.Id, "InvalidFinalOutput", $"Provider {p.Id} cannot produce the final picture."));
        }
        if (providers.Values.OfType<ClipArgumentProvider>().Count() > 1)
            errors.Add(new(null, "MultipleClipArguments", "A clip can only have one argument provider."));

        bool TimingDependsOnOutput(IEffectProvider source, bool speed, HashSet<Guid> seen)
        {
            if (!seen.Add(source.Id)) return false;
            if (speed && source is IClipValueEndpointProvider { IsExport: false } input && providers.ContainsKey(input.SourceEndpointId)) return true;
            return source.AnchorsBindingState.Where(p => !EffectProviderAnchorExtensions.IsOutputBindingKey(p.Key))
                .Any(p => p.Value == ValueProviderFrameContext.BuiltInAudioSampleProviderId
                    || EffectProviderOutputExtensions.TryParseOutputSourceId(p.Value, out var id, out _) && providers.TryGetValue(id, out var upstream)
                        && TimingDependsOnOutput(upstream, speed, seen));
        }
        bool DependsOnGlobalMix(string? source, HashSet<Guid> seen)
        {
            if (source == ValueProviderFrameContext.BuiltInAudioSampleProviderId) return true;
            if (!EffectProviderOutputExtensions.TryParseOutputSourceId(source, out var id, out _)
                || !seen.Add(id) || !providers.TryGetValue(id, out var upstream) || !upstream.Enabled) return false;
            return upstream.AnchorsBindingState.Where(b => !EffectProviderAnchorExtensions.IsOutputBindingKey(b.Key))
                .Any(b => DependsOnGlobalMix(b.Value, seen));
        }
        foreach (var output in providers.Values.OfType<RenderOutputProvider>().Where(p => p.Enabled))
            foreach (var field in new[] { RenderOutputProvider.AudioKey, RenderOutputProvider.ShouldRenderKey })
                if (DependsOnGlobalMix(output.AnchorsBindingState.GetValueOrDefault(field), []))
                    errors.Add(new(output.Id, "AudioDependencyCycle", "Global mixed audio cannot depend on itself through clip audio or visibility."));
        foreach (var p in providers.Values)
        {
            try { EffectStages.Validate(p); }
            catch (Exception ex) { errors.Add(new(p.Id, "UnsupportedStage", ex.Message)); }
            if (p is ConditionalLoopNodeEffectProvider { Enabled: true } && !forward)
                errors.Add(new(p.Id, "LoopRequiresDynamicCalculation", "Conditional loops require dynamic calculation."));
            if (p is EffectVariableProvider variable)
            {
                if (p.Enabled && (!forward || !DynamicEffectCalculation.Supports(p.Stage)))
                    errors.Add(new(p.Id, "VariableRequiresDynamicCalculation", "Variables require dynamic calculation in a supported stage."));
                if (!EffectVariableProvider.SupportsType(variable.ValueType))
                    errors.Add(new(p.Id, "InvalidVariableType", $"Variable {p.Id} has an unsupported value type."));
                if (variable.VariableId == Guid.Empty || providers.Values.OfType<EffectVariableProvider>()
                    .Any(v => v.VariableId == variable.VariableId && (v.Stage != variable.Stage || v.ValueType != variable.ValueType)))
                    errors.Add(new(p.Id, "InvalidVariableReference", $"Variable endpoint {p.Id} has an incompatible variable."));
            }
            if (p is IClipValueEndpointProvider { IsExport: false } input
                && providers.GetValueOrDefault(input.SourceEndpointId) is IClipValueEndpointProvider { IsExport: true } endpointOutput
                && !endpointOutput.CanPassTo(p.Stage, true, providers.Values))
                errors.Add(new(p.Id, "InvalidStageEndpoint", $"Endpoint {p.Id} does not follow source stage {endpointOutput.Stage}."));
            var fields = InputFields(p);
            if (p.TypeOfEffect == EffectType.MixtureProvider
                && (!fields.TryGetValue(MixtureProviderBase.UpperInputKey, out var upper) || !upper.FieldType.HasFlag(EffectArgumentFieldType.IPicture)
                    || !fields.TryGetValue(MixtureProviderBase.LowerInputKey, out var lower) || !lower.FieldType.HasFlag(EffectArgumentFieldType.IPicture)
                    || !p.OutField.FieldType.HasFlag(EffectArgumentFieldType.IPicture)))
                errors.Add(new(p.Id, "InvalidMixturePorts", $"Mixture {p.Id} must declare upper and lower picture inputs and a picture output."));
            if (p is IMultipleOutputEffectProvider multiple)
            {
                if (!IsGraphProvider(p) && !CanInlineValue(p, providers, types: types))
                    errors.Add(new(p.Id, "UnsupportedMultipleOutputStage", $"Provider {p.Id} cannot produce values in stage {p.Stage}."));
                if (multiple.OutFields.Count == 0 || multiple.OutFields.Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Key != x.Value.Id))
                    errors.Add(new(p.Id, "InvalidOutputPorts", $"Provider {p.Id} must declare nonempty output IDs matching their descriptors."));
            }
            bool WrongContent(EffectArgumentFieldType type) => type.IsContent()
                && !EffectFieldTypes.AreCompatible(type, p.Stage.GetContentType());
            if (IsGraphProvider(p) && (p.GetOutputFields().Keys.Any(key => WrongContent(types.GetOutputType(p, key)))
                || fields.Keys.Any(key => WrongContent(types.GetInputType(p, key)))))
                errors.Add(new(p.Id, "UnsupportedGraphInput", $"Provider {p.Id} declares content from another stage."));
            foreach (var binding in p.AnchorsBindingState.Where(x => !EffectProviderAnchorExtensions.IsOutputBindingKey(x.Key)))
            {
                if (binding.Value == IEffectProvider.NoConnectionGUID.ToString()) continue;
                if (!fields.TryGetValue(binding.Key, out var field))
                {
                    errors.Add(new(p.Id, "UnknownFieldBinding", $"Provider {p.Id} has no input '{binding.Key}'."));
                    continue;
                }
                if (field.FieldType.HasFlag(EffectArgumentFieldType.CannotBeDynamic))
                    errors.Add(new(p.Id, "FieldCannotBeDynamic", $"Input '{binding.Key}' on {p.Id} cannot be bound."));
                var inputType = types.GetInputType(p, binding.Key);
                if (ValueProviderFrameContext.IsBuiltInSource(binding.Value))
                {
                    var sourceType = ValueProviderFrameContext.SourceType(binding.Value);
                    if (binding.Value is ValueProviderFrameContext.ClipAudioProviderId or ValueProviderFrameContext.ClipPositionProviderId
                        && p.Stage != EffectStage.Render)
                        errors.Add(new(p.Id, "InvalidRenderSource", "Clip audio and position sources belong to the render stage."));
                    if (sourceType == EffectArgumentFieldType.AudioSample && !ValueProviderFrameContext.SupportsAudioSample(p.Stage))
                        errors.Add(new(p.Id, "UnsupportedAudioSampleStage", $"Final mixed audio cannot feed stage {p.Stage} on {p.Id}."));
                    if (!EffectFieldTypes.AreCompatible(sourceType, inputType, null, types.GetInputContract(p, binding.Key)))
                        errors.Add(new(p.Id, "IncompatibleInput", $"Built-in source cannot feed '{binding.Key}' on {p.Id}."));
                    continue;
                }
                if (binding.Value == IEffectProvider.InputAnchorGUID.ToString())
                {
                    if (!EffectFieldTypes.AreCompatible(p.Stage.GetContentType(), inputType, null, types.GetInputContract(p, binding.Key))
                        || !IsGraphProvider(p) && binding.Key != EffectProviderAnchorExtensions.InputKey)
                        errors.Add(new(p.Id, "IncompatibleInput", $"Source picture cannot feed '{binding.Key}' on {p.Id}."));
                    continue;
                }
                if (!EffectProviderOutputExtensions.TryParseOutputSourceId(binding.Value, out var id, out var outputId) || !providers.TryGetValue(id, out var source))
                {
                    errors.Add(new(p.Id, "DanglingFieldBinding", $"Missing source '{binding.Value}' for '{binding.Key}' on {p.Id}."));
                    continue;
                }
                if (!source.TryGetOutputField(outputId, out var output))
                {
                    errors.Add(new(p.Id, "UnknownOutputPort", $"Source {id} requires a valid output port for '{binding.Key}' on {p.Id}."));
                    continue;
                }
                if (source is IClipValueEndpointProvider { IsExport: true })
                    errors.Add(new(p.Id, "EndpointIsNotLocalSource", "Export endpoints can only be read through another clip's endpoint input."));
                else if (!CanBindOutput(source))
                    errors.Add(new(p.Id, "UnsupportedBindingSource", $"Provider {id} cannot feed '{binding.Key}' on {p.Id}."));
                if (p.TypeOfEffect is EffectType.SpeedVarianceProvider or EffectType.Transform
                    && (!CanInlineValue(source, providers, types: types) || TimingDependsOnOutput(source, p.TypeOfEffect == EffectType.SpeedVarianceProvider, [])))
                    errors.Add(new(p.Id, "CompositionTimingDependency", "Timing and transition parameters cannot depend on the composition picture graph."));
                var outputType = types.GetOutputType(source, output.Id);
                if (!outputType.HasFlag(EffectArgumentFieldType.Any) && !EffectFieldTypes.AreCompatible(outputType, inputType,
                    types.GetOutputContract(source, output.Id), types.GetInputContract(p, binding.Key)))
                    errors.Add(new(p.Id, "IncompatibleInput", $"Provider {id} cannot feed '{binding.Key}' on {p.Id}."));
                if (p.Stage != source.Stage)
                    errors.Add(new(p.Id, "CrossStageBinding", $"Binding '{binding.Key}' on {p.Id} crosses picture-processing stages."));
                if (p.Stage is EffectStage.Text or EffectStage.VectorContent && p.Stage == source.Stage
                    && binding.Key == EffectProviderAnchorExtensions.InputKey && !source.CanConnectContent(p, outputId, types))
                    errors.Add(new(p.Id, "IncompatibleContentInput", $"Provider {id} cannot feed native content on {p.Id}."));
            }
        }
        var visiting = new HashSet<Guid>();
        var visited = new HashSet<Guid>();
        void Visit(Guid id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id))
            {
                errors.Add(new(id, "BindingCycle", $"Effect input cycle at provider {id}."));
                return;
            }
            foreach (var source in providers[id].AnchorsBindingState.Where(p => !EffectProviderAnchorExtensions.IsOutputBindingKey(p.Key))
                .Select(p => EffectProviderOutputExtensions.TryParseOutputSourceId(p.Value, out var next, out _) ? next : IEffectProvider.NoConnectionGUID).Distinct())
                if (providers.ContainsKey(source) && !(forward && DynamicEffectCalculation.Supports(providers[id].Stage)
                    && providers[source].Stage == providers[id].Stage && IsGraphProvider(providers[id]) && IsGraphProvider(providers[source]))) Visit(source);
            visiting.Remove(id);
            visited.Add(id);
        }
        foreach (var id in providers.Keys) Visit(id);
        return errors.Distinct().ToArray();
    }

    public static void ValidateOutputValues(IEffectProvider provider, IReadOnlyDictionary<string, object?> outputs,
        EffectFieldTypeResolution? types = null, uint? frame = null)
    {
        types ??= new(new Dictionary<Guid, IEffectProvider> { [provider.Id] = provider });
        var fields = types.GetContext(provider.Id).State.Outputs;
        foreach (var key in outputs.Keys)
            if (!fields.ContainsKey(key)) throw new InvalidOperationException($"Provider {provider.Id} returned unknown output '{key}'.");
        foreach (var (key, field) in fields)
        {
            if (!outputs.TryGetValue(key, out var value))
                throw new InvalidOperationException($"Provider {provider.Id} returned a missing output '{key}', frame {frame}.");
            if (ReferenceEquals(value, EffectBranchState.Inactive)) continue;
            if (provider is IMultipleOutputEffectProvider && types.GetOutputType(provider, key).HasFlag(EffectArgumentFieldType.Any))
            {
                if (value is null && field.FieldType.HasFlag(EffectArgumentFieldType.Mandatory))
                    throw new InvalidOperationException($"Provider {provider.Id} returned no value for mandatory output '{key}', frame {frame}.");
                continue;
            }
            types.ValidateValue(provider, key, false, field, value, frame);
        }
    }

    public static void ValidateMultipleOutputEffects(IEffectProvider provider, IEffect[] effects)
    {
        if (provider is IMultipleOutputEffectProvider && (effects.Length == 0 || effects[^1] is not IMultipleOutputEffect
            || effects.Take(effects.Length - 1).Any(e => e is IMultipleOutputEffect)))
            throw new InvalidOperationException($"Provider {provider.Id} must end with exactly one multiple-output effect.");
        if (provider is not IMultipleOutputEffectProvider && effects.Any(e => e is IMultipleOutputEffect))
            throw new InvalidOperationException($"Provider {provider.Id} must declare its multiple output ports.");
    }
}
