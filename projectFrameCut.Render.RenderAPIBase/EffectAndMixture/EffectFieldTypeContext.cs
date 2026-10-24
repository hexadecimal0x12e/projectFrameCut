using System.Collections.ObjectModel;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

public sealed class EffectProviderTypeState
{
    public Guid Id { get; init; }
    public string TypeName { get; init; } = "";
    public string FromPlugin { get; init; } = "";
    public string Name { get; init; } = "";
    public EffectType TypeOfEffect { get; init; }
    public EffectTarget Target { get; init; }
    public bool Enabled { get; init; }
    public IReadOnlyList<string> DynamicParameters { get; init; } = [];
    public IReadOnlyDictionary<string, object?> Parameters { get; init; } = new Dictionary<string, object?>();
    public IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> Fields { get; init; } = new Dictionary<string, EffectArgumentFieldDescriptor>();
    public IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> Outputs { get; init; } = new Dictionary<string, EffectArgumentFieldDescriptor>();
    public IReadOnlyDictionary<string, string> Bindings { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();

    public static EffectProviderTypeState Capture(IEffectProvider provider, IReadOnlyDictionary<string, string>? bindings = null)
        => PrepareCapture(provider, bindings)();

    internal static Func<EffectProviderTypeState> PrepareCapture(IEffectProvider provider, IReadOnlyDictionary<string, string>? bindings = null)
    {
        var stateBindings = new Dictionary<string, string>(bindings ?? provider.AnchorsBindingState);
        bool IsBound(string key) => stateBindings.TryGetValue(key, out var source) && source != IEffectProvider.NoConnectionGUID.ToString();
        var fields = provider.InFields.ToDictionary(p => p.Key, p => (IEffectArgumentField)p.Value);
        foreach (var field in provider.Fields) fields[field.Key] = field.Value;
        foreach (var key in fields.Keys.Where(EffectProviderAnchorExtensions.IsOutputBindingKey).ToArray()) fields.Remove(key);
        var info = (provider.Id, provider.TypeName, provider.FromPlugin, provider.Enabled, provider.Name, provider.TypeOfEffect, provider.Target);
        var dynamicParameters = Array.AsReadOnly(fields.Where(p => p.Value.IsDynamicAtRenderTime || IsBound(p.Key)).Select(p => p.Key).ToArray());
        var parameterReaders = fields.ToDictionary(p => p.Key, p => ReadParameter(p.Value));
        var descriptors = new ReadOnlyDictionary<string, EffectArgumentFieldDescriptor>(fields.ToDictionary(p => p.Key,
            p => Describe(p.Key, p.Value) with { IsDynamic = p.Value.IsDynamic || IsBound(p.Key) }));
        var outputs = new ReadOnlyDictionary<string, EffectArgumentFieldDescriptor>(provider.GetOutputFields().ToDictionary(p => p.Key, p => p.Value with { }));
        var metadata = new ReadOnlyDictionary<string, object>(new Dictionary<string, object>(provider.MetaData));
        return () => new()
        {
            Id = info.Id, TypeName = info.TypeName, FromPlugin = info.FromPlugin, Enabled = info.Enabled,
            Name = info.Name, TypeOfEffect = info.TypeOfEffect, Target = info.Target,
            DynamicParameters = dynamicParameters,
            Parameters = new ReadOnlyDictionary<string, object?>(parameterReaders.ToDictionary(p => p.Key, p => p.Value())),
            Fields = descriptors,
            Outputs = outputs,
            Bindings = new ReadOnlyDictionary<string, string>(stateBindings),
            Metadata = metadata,
        };

        static Func<object?> ReadParameter(IEffectArgumentField field)
        {
            if (field is not StaticEffectArgumentField and not DynamicEffectParamField and not EffectArgumentFieldDescriptor
                && !field.IsDynamicAtRenderTime) return field.GetGetter();
            object? value = field switch
            {
                StaticEffectArgumentField f => f.Value,
                DynamicEffectParamField f => f.StaticFallbackValue,
                _ => EffectFieldTypes.DefaultValue(field),
            };
            return () => value;
        }
    }

    private static EffectArgumentFieldDescriptor Describe(string id, IEffectArgumentField field) => new()
    {
        Id = id, TypeName = field.TypeName, FromPlugin = field.FromPlugin, FieldType = field.FieldType,
        IsDynamic = field.IsDynamic, DefaultValue = field.DefaultValue, MinValue = field.MinValue,
        MaxValue = field.MaxValue, PresetOptions = field.PresetOptions?.ToArray(), Remarks = field.Remarks,
    };
}

/// <summary>Editing-time state only; resolving a type must not evaluate bound effects.</summary>
public sealed class EffectFieldTypeContext
{
    public required EffectProviderTypeState State { get; init; }
    public IReadOnlyDictionary<string, object?> Parameters => State.Parameters;
    public IReadOnlyDictionary<string, EffectProviderTypeState> BoundProviders { get; init; } = new Dictionary<string, EffectProviderTypeState>();
    public IReadOnlyDictionary<string, EffectArgumentFieldType> InputTypes { get; init; } = new Dictionary<string, EffectArgumentFieldType>();
}

/// <summary>A type snapshot shared by binding validation and a built render graph.</summary>
public sealed class EffectFieldTypeResolution
{
    public sealed record Diagnostic(Guid ProviderId, string Code, string Message);
    private readonly Dictionary<(Guid Provider, bool Input, string Field), EffectArgumentFieldType> types = new();
    private readonly Dictionary<Guid, EffectFieldTypeContext> contexts = new();
    private readonly List<Diagnostic> diagnostics = new();
    private readonly IReadOnlyDictionary<Guid, IEffectProvider> providers;
    private readonly EffectFieldTypeResolution? parent;
    private Dictionary<Guid, List<Guid>>? consumers;
    private readonly Dictionary<Guid, HashSet<Guid>> dependents = new();
    public IReadOnlyList<Diagnostic> Diagnostics => diagnostics;

    public EffectFieldTypeResolution(IReadOnlyDictionary<Guid, IEffectProvider> providers,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>>? bindingOverrides = null)
        : this(providers, bindingOverrides, null) { }

    private EffectFieldTypeResolution(IReadOnlyDictionary<Guid, IEffectProvider> providers,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>>? bindingOverrides,
        Dictionary<Guid, EffectProviderTypeState>? snapshots)
    {
        this.providers = providers;
        var states = snapshots ?? providers.ToDictionary(p => p.Key,
            p => EffectProviderTypeState.Capture(p.Value, bindingOverrides?.GetValueOrDefault(p.Key)));
        var visiting = new HashSet<Guid>();
        void Visit(Guid id)
        {
            if (contexts.ContainsKey(id)) return;
            if (!visiting.Add(id))
            {
                diagnostics.Add(new(id, "BindingCycle", $"Effect field type cycle at provider {id}."));
                return;
            }
            var state = states[id];
            var sources = new Dictionary<string, EffectProviderTypeState>();
            var inputs = new Dictionary<string, EffectArgumentFieldType>();
            foreach (var (key, source) in state.Bindings.Where(p => !EffectProviderAnchorExtensions.IsOutputBindingKey(p.Key)))
            {
                if (EffectProviderOutputExtensions.TryParseOutputSourceId(source, out var sourceId, out _) && states.TryGetValue(sourceId, out var upstream))
                {
                    Visit(sourceId);
                    sources[key] = upstream;
                }
                inputs[key] = source == IEffectProvider.InputAnchorGUID.ToString()
                    ? providers[id].GetPipeline().GetContentType() : GetSourceType(source, providers);
            }
            var context = new EffectFieldTypeContext
            {
                State = state,
                BoundProviders = new ReadOnlyDictionary<string, EffectProviderTypeState>(sources),
                InputTypes = new ReadOnlyDictionary<string, EffectArgumentFieldType>(inputs),
            };
            ResolveFields(providers[id], context);
            contexts[id] = context;
            visiting.Remove(id);
        }
        foreach (var id in providers.Keys) Visit(id);
    }

    private void ResolveFields(IEffectProvider provider, EffectFieldTypeContext context)
    {
        foreach (var (key, field) in context.State.Fields) Resolve(key, true, field);
        foreach (var (key, field) in context.State.Outputs) Resolve(key, false, field);
        void Resolve(string key, bool input, EffectArgumentFieldDescriptor field)
        {
            var type = field.FieldType;
            if (type.HasFlag(EffectArgumentFieldType.Any))
            {
                try
                {
                    type = provider.ResolveFieldType(key, input, context);
                    if (type.BaseType() == EffectArgumentFieldType.Unknown || type.HasFlag(EffectArgumentFieldType.Any)) type = EffectArgumentFieldType.Any;
                }
                catch (Exception ex)
                {
                    Logger.Log(ex, $"Resolve {(input ? "input" : "output")} '{key}' on provider {provider.Id}", this);
                    diagnostics.Add(new(provider.Id, "InvalidFieldType", $"Cannot resolve field '{key}' on provider {provider.Id}: {ex.Message}"));
                    type = EffectArgumentFieldType.Any;
                }
            }
            types[(provider.Id, input, key)] = type;
        }
    }

    private static EffectProviderTypeState WithBinding(EffectProviderTypeState current, string fieldId, string source)
    {
        var bindings = new Dictionary<string, string>(current.Bindings) { [fieldId] = source };
        var fields = new Dictionary<string, EffectArgumentFieldDescriptor>(current.Fields)
        {
            [fieldId] = current.Fields[fieldId] with { IsDynamic = current.Fields[fieldId].IsDynamic || source != IEffectProvider.NoConnectionGUID.ToString() },
        };
        return new EffectProviderTypeState
        {
            Id = current.Id, TypeName = current.TypeName, FromPlugin = current.FromPlugin, Name = current.Name,
            TypeOfEffect = current.TypeOfEffect, Target = current.Target, Enabled = current.Enabled,
            Parameters = current.Parameters, Fields = fields, Outputs = current.Outputs, Metadata = current.Metadata,
            Bindings = bindings, DynamicParameters = source == IEffectProvider.NoConnectionGUID.ToString()
                ? current.DynamicParameters : current.DynamicParameters.Append(fieldId).Distinct().ToArray(),
        };
    }

    private EffectFieldTypeResolution(EffectFieldTypeResolution parent, IEffectProvider provider, EffectProviderTypeState state)
    {
        this.parent = parent;
        providers = parent.providers;
        var sources = new Dictionary<string, EffectProviderTypeState>();
        var inputs = new Dictionary<string, EffectArgumentFieldType>();
        foreach (var (key, value) in state.Bindings.Where(p => !EffectProviderAnchorExtensions.IsOutputBindingKey(p.Key)))
        {
            if (EffectProviderOutputExtensions.TryParseOutputSourceId(value, out var id, out _) && providers.ContainsKey(id))
                sources[key] = parent.GetContext(id).State;
            inputs[key] = value == IEffectProvider.InputAnchorGUID.ToString()
                ? provider.GetPipeline().GetContentType() : parent.GetSourceType(value, providers);
        }
        var context = new EffectFieldTypeContext { State = state, BoundProviders = sources, InputTypes = inputs };
        diagnostics.AddRange(parent.Diagnostics.Where(d => d.ProviderId != provider.Id));
        ResolveFields(provider, context);
        contexts[provider.Id] = context;
    }

    /// <summary>Resolves only the edited provider, reusing the upstream type snapshot.</summary>
    public EffectFieldTypeResolution? ResolveBindingCandidate(IEffectProvider provider, string fieldId, string source)
    {
        if (!GetContext(provider.Id).State.Fields.ContainsKey(fieldId)) return null;
        if (EffectProviderOutputExtensions.TryParseOutputSourceId(source, out var id, out _)
            && (id == provider.Id || GetDependents(provider.Id).Contains(id))) return null;
        var state = WithBinding(GetContext(provider.Id).State, fieldId, source);
        if (Diagnostics.Count > 0)
        {
            var snapshots = providers.Keys.ToDictionary(key => key, key => GetContext(key).State);
            snapshots[provider.Id] = state;
            return new(providers, null, snapshots);
        }
        return new(this, provider, state);
    }

    private HashSet<Guid> GetDependents(Guid providerId)
    {
        if (dependents.TryGetValue(providerId, out var result)) return result;
        if (consumers is null)
        {
            consumers = providers.Keys.ToDictionary(id => id, _ => new List<Guid>());
            foreach (var id in providers.Keys)
                foreach (var binding in GetContext(id).State.Bindings.Where(p => !EffectProviderAnchorExtensions.IsOutputBindingKey(p.Key)))
                    if (EffectProviderOutputExtensions.TryParseOutputSourceId(binding.Value, out var source, out _)
                        && consumers.TryGetValue(source, out var list)) list.Add(id);
        }
        result = new HashSet<Guid>();
        var pending = new Stack<Guid>();
        pending.Push(providerId);
        while (pending.TryPop(out var id))
            if (result.Add(id))
                foreach (var child in consumers[id]) pending.Push(child);
        dependents[providerId] = result;
        return result;
    }

    public static Task<EffectFieldTypeResolution> ResolveAsync(IReadOnlyDictionary<Guid, IEffectProvider> providers,
        CancellationToken cancellationToken = default, IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>>? bindingOverrides = null)
    {
        var copy = providers.ToDictionary();
        var captures = copy.ToDictionary(p => p.Key, p => EffectProviderTypeState.PrepareCapture(p.Value, bindingOverrides?.GetValueOrDefault(p.Key)));
        return Task.Run(() =>
        {
            var states = new Dictionary<Guid, EffectProviderTypeState>();
            foreach (var (id, capture) in captures)
            {
                cancellationToken.ThrowIfCancellationRequested();
                states[id] = capture();
            }
            return new EffectFieldTypeResolution(copy, null, states);
        }, cancellationToken);
    }

    public bool AreInputsCompatible(IEffectProvider provider) =>
        (ValueProviderFrameContext.SupportsAudioSample(provider.Stage)
            || !GetContext(provider.Id).State.Bindings.Values.Contains(ValueProviderFrameContext.BuiltInAudioSampleProviderId))
        && GetContext(provider.Id).InputTypes.All(p => p.Value.HasFlag(EffectArgumentFieldType.Any)
            || EffectFieldTypes.AreCompatible(p.Value, GetInputType(provider, p.Key)));

    public EffectArgumentFieldType GetInputType(IEffectProvider provider, string fieldId) =>
        GetResolvedType(provider.Id, true, fieldId, EffectArgumentFieldType.Unknown);

    public EffectArgumentFieldType GetOutputType(IEffectProvider provider, string? outputId = null) =>
        TryGetOutputField(provider, outputId, out var field)
            ? GetResolvedType(provider.Id, false, field.Id, EffectArgumentFieldType.Any) : EffectArgumentFieldType.Any;

    private EffectArgumentFieldType GetResolvedType(Guid id, bool input, string field, EffectArgumentFieldType fallback) =>
        types.TryGetValue((id, input, field), out var type) ? type : parent?.GetResolvedType(id, input, field, fallback) ?? fallback;

    public bool TryGetOutputField(IEffectProvider provider, string? outputId, out EffectArgumentFieldDescriptor field)
    {
        if (!contexts.TryGetValue(provider.Id, out var context))
        {
            if (parent is not null) return parent.TryGetOutputField(provider, outputId, out field);
            return provider.TryGetOutputField(outputId, out field);
        }
        field = null!;
        if (provider is IMultipleOutputEffectProvider && outputId is null) return false;
        outputId ??= context.State.Outputs.Keys.SingleOrDefault();
        return outputId is not null && context.State.Outputs.TryGetValue(outputId, out field!);
    }

    public EffectArgumentFieldDescriptor GetOutputField(IEffectProvider provider) =>
        TryGetOutputField(provider, null, out var field) ? field : throw new InvalidOperationException($"Select an output port on provider {provider.Id}.");

    public EffectFieldTypeContext GetContext(Guid providerId)
    {
        if (contexts.TryGetValue(providerId, out var context)) return context;
        return parent is not null ? parent.GetContext(providerId) : contexts[providerId];
    }

    private EffectArgumentFieldType GetSourceType(string source, IReadOnlyDictionary<Guid, IEffectProvider> providers)
    {
        if (source == IEffectProvider.InputAnchorGUID.ToString()) return EffectArgumentFieldType.IPicture;
        if (source == ValueProviderFrameContext.BuiltInAudioSampleProviderId) return EffectArgumentFieldType.AudioSample;
        if (source is ValueProviderFrameContext.BuiltInFrameProviderId or ValueProviderFrameContext.BuiltInProgressProviderId) return EffectArgumentFieldType.Numeric;
        return EffectProviderOutputExtensions.TryParseOutputSourceId(source, out var id, out var outputId) && providers.TryGetValue(id, out var provider)
            ? GetOutputType(provider, outputId) : EffectArgumentFieldType.Any;
    }

    public void ValidateValue(IEffectProvider provider, string fieldId, bool input, IEffectArgumentField field, object? value, uint? frame = null)
    {
        var type = input ? GetInputType(provider, fieldId) : GetOutputType(provider, fieldId);
        if (!input && type.HasFlag(EffectArgumentFieldType.Any))
            throw new InvalidOperationException($"Provider {provider.Id} output '{fieldId}' has an unresolved type, frame {frame}.");
        if (value is null && field.FieldType.HasFlag(EffectArgumentFieldType.Mandatory) || !EffectFieldTypes.Accepts(type, value))
            throw new InvalidOperationException($"Provider {provider.Id} {(input ? "input" : "output")} '{fieldId}' expects {type}, received {value?.GetType().FullName ?? "null"}, frame {frame}.");
    }
}
