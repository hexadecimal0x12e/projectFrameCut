using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

public static class DynamicEffectBindings
{
    public static bool IsGraphProvider(IEffectProvider p) => p is not ClipArgumentProvider && p.TypeOfEffect is
        EffectType.NormalEffect or EffectType.ContinuousEffect or EffectType.ClipPositionProvider
        or EffectType.ContinuousClipPositionProvider or EffectType.NonIPictureOutputValueProvider or EffectType.VectorPictureEffect;

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
        if (providers.Values.Any(p => p is IClipValueEndpointProvider or IMultipleOutputEffectProvider || p.GetFinalOutputFieldId() is not null
            || InputFields(p).Values.Any(f => f.FieldType.HasFlag(EffectArgumentFieldType.Any))
            || p.GetOutputFields().Values.Any(f => f.FieldType.HasFlag(EffectArgumentFieldType.Any))
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
        if (provider is IClipValueEndpointProvider) return false;
        types ??= new(providers);
        seen ??= [];
        if (!seen.Add(provider.Id)) return false;
        if (provider is not IMultipleOutputEffectProvider && provider.TypeOfEffect != EffectType.NonIPictureOutputValueProvider
            || provider.GetOutputFields().Keys.Any(key => types.GetOutputType(provider, key).IsContent()
                || types.GetOutputType(provider, key).HasFlag(EffectArgumentFieldType.Any))
            || InputFields(provider).Any(p => p.Key == EffectProviderAnchorExtensions.InputKey || p.Value.FieldType.IsContent()
                || p.Value is StaticEffectArgumentField { Value: projectFrameCut.Drawing.Base.IPicture or projectFrameCut.Drawing.Vector.VectorPicture }
                    or DynamicEffectParamField { StaticFallbackValue: projectFrameCut.Drawing.Base.IPicture or projectFrameCut.Drawing.Vector.VectorPicture })) return false;
        foreach (var binding in provider.EnumerateFieldBindings())
        {
            if (binding.Value == IEffectProvider.NoConnectionGUID.ToString()) continue;
            if (binding.Value is ValueProviderFrameContext.BuiltInFrameProviderId or ValueProviderFrameContext.BuiltInProgressProviderId) continue;
            if (!EffectProviderOutputExtensions.TryParseOutputSourceId(binding.Value, out var id, out var outputId)
                || !providers.TryGetValue(id, out var source) || !source.TryGetOutputField(outputId, out _)
                || !CanInlineValue(source, providers, new(seen), types)) return false;
        }
        return true;
    }

    public static IReadOnlyList<EffectBindingHelper.BindingDiagnostic> Normalize(IDictionary<Guid, IEffectProvider> providers)
    {
        var diagnostics = new List<EffectBindingHelper.BindingDiagnostic>();
        foreach (var p in providers.Values)
        {
            var fields = InputFields(p);
            var state = new Dictionary<string, string>(p.AnchorsBindingState);
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

    public static IReadOnlyList<EffectBindingHelper.BindingDiagnostic> Validate(IReadOnlyDictionary<Guid, IEffectProvider> providers, EffectFieldTypeResolution? types = null)
    {
        types ??= new(providers);
        var errors = types.Diagnostics.Select(d => new EffectBindingHelper.BindingDiagnostic(d.ProviderId, d.Code, d.Message)).ToList();
        foreach (var group in providers.Values.Where(p => p.IsFinalOutputSource()).GroupBy(p => p.GetPipeline()))
        {
            if (group.Count() > 1) errors.Add(new(null, "MultipleFinalOutputs", $"Multiple providers feed {group.Key} output."));
            foreach (var p in group)
                if (!p.TryGetOutputField(p.GetFinalOutputFieldId(), out var output)
                    || !EffectFieldTypes.AreCompatible(types.GetOutputType(p, output.Id), p.GetPipeline().GetContentType())
                    || p is ClipArgumentProvider or IClipValueEndpointProvider { IsExport: true } || p.TypeOfEffect == EffectType.Transform)
                    errors.Add(new(p.Id, "InvalidFinalOutput", $"Provider {p.Id} cannot produce the final picture."));
        }
        if (providers.Values.OfType<ClipArgumentProvider>().Count() > 1)
            errors.Add(new(null, "MultipleClipArguments", "A clip can only have one argument provider."));

        foreach (var p in providers.Values)
        {
            var fields = InputFields(p);
            if (p is IMultipleOutputEffectProvider multiple)
            {
                if (!IsGraphProvider(p)) errors.Add(new(p.Id, "UnsupportedMultipleOutputStage", $"Provider {p.Id} must use a content effect graph."));
                if (multiple.OutFields.Count == 0 || multiple.OutFields.Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Key != x.Value.Id))
                    errors.Add(new(p.Id, "InvalidOutputPorts", $"Provider {p.Id} must declare nonempty output IDs matching their descriptors."));
            }
            bool WrongContent(EffectArgumentFieldType type) => type.IsContent()
                && !EffectFieldTypes.AreCompatible(type, p.GetPipeline().GetContentType());
            if (IsGraphProvider(p) && (p.GetOutputFields().Keys.Any(key => WrongContent(types.GetOutputType(p, key)))
                || fields.Keys.Any(key => WrongContent(types.GetInputType(p, key)))))
                errors.Add(new(p.Id, "UnsupportedGraphInput", $"Provider {p.Id} declares content from another pipeline."));
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
                if (binding.Value is ValueProviderFrameContext.BuiltInFrameProviderId or ValueProviderFrameContext.BuiltInProgressProviderId)
                {
                    if (!EffectFieldTypes.AreCompatible(EffectArgumentFieldType.Numeric, inputType))
                        errors.Add(new(p.Id, "IncompatibleInput", $"Built-in source cannot feed '{binding.Key}' on {p.Id}."));
                    continue;
                }
                if (binding.Value == IEffectProvider.InputAnchorGUID.ToString())
                {
                    if (!EffectFieldTypes.AreCompatible(p.GetPipeline().GetContentType(), inputType)
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
                var outputType = types.GetOutputType(source, output.Id);
                if (!outputType.HasFlag(EffectArgumentFieldType.Any) && !EffectFieldTypes.AreCompatible(outputType, inputType))
                    errors.Add(new(p.Id, "IncompatibleInput", $"Provider {id} cannot feed '{binding.Key}' on {p.Id}."));
                bool nativeSource = p.GetPipeline() == EffectPipeline.NativeContent && source.GetPipeline() == EffectPipeline.NativeContent
                    && binding.Key == EffectProviderAnchorExtensions.InputKey && !IsGraphProvider(source);
                if (IsGraphProvider(p) && (!IsGraphProvider(source) && !nativeSource || p.GetPipeline() != source.GetPipeline())
                    || !IsGraphProvider(p) && IsGraphProvider(source) && !CanInlineValue(source, providers, types: types))
                    errors.Add(new(p.Id, "CrossStageBinding", $"Binding '{binding.Key}' on {p.Id} crosses picture-processing stages."));
                if (p.GetPipeline() == EffectPipeline.NativeContent && source.GetPipeline() == EffectPipeline.NativeContent
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
                if (providers.ContainsKey(source)) Visit(source);
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
