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
    {
        var stateBindings = new Dictionary<string, string>(bindings ?? provider.AnchorsBindingState);
        bool IsBound(string key) => stateBindings.TryGetValue(key, out var source) && source != IEffectProvider.NoConnectionGUID.ToString();
        var fields = provider.InFields.ToDictionary(p => p.Key, p => (IEffectArgumentField)p.Value);
        foreach (var field in provider.Fields) fields[field.Key] = field.Value;
        foreach (var key in fields.Keys.Where(EffectProviderAnchorExtensions.IsOutputBindingKey).ToArray()) fields.Remove(key);
        return new()
        {
            Id = provider.Id, TypeName = provider.TypeName, FromPlugin = provider.FromPlugin, Enabled = provider.Enabled,
            Name = provider.Name, TypeOfEffect = provider.TypeOfEffect, Target = provider.Target,
            DynamicParameters = Array.AsReadOnly(fields.Where(p => p.Value.IsDynamicAtRenderTime || IsBound(p.Key)).Select(p => p.Key).ToArray()),
            Parameters = new ReadOnlyDictionary<string, object?>(fields.ToDictionary(p => p.Key, p => p.Value switch
            {
                StaticEffectArgumentField f => f.Value,
                DynamicEffectParamField f => f.StaticFallbackValue,
                EffectArgumentFieldDescriptor f => EffectFieldTypes.DefaultValue(f),
                _ => p.Value.IsDynamicAtRenderTime ? EffectFieldTypes.DefaultValue(p.Value) : p.Value.GetGetter()(),
            })),
            Fields = new ReadOnlyDictionary<string, EffectArgumentFieldDescriptor>(fields.ToDictionary(p => p.Key,
                p => Describe(p.Key, p.Value) with { IsDynamic = p.Value.IsDynamic || IsBound(p.Key) })),
            Outputs = new ReadOnlyDictionary<string, EffectArgumentFieldDescriptor>(provider.GetOutputFields().ToDictionary(p => p.Key, p => p.Value with { })),
            Bindings = new ReadOnlyDictionary<string, string>(stateBindings),
            Metadata = new ReadOnlyDictionary<string, object>(new Dictionary<string, object>(provider.MetaData)),
        };
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
    public IReadOnlyList<Diagnostic> Diagnostics => diagnostics;

    public EffectFieldTypeResolution(IReadOnlyDictionary<Guid, IEffectProvider> providers,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>>? bindingOverrides = null)
        : this(providers, bindingOverrides, null) { }

    private EffectFieldTypeResolution(IReadOnlyDictionary<Guid, IEffectProvider> providers,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>>? bindingOverrides,
        Dictionary<Guid, EffectProviderTypeState>? snapshots)
    {
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
                inputs[key] = GetSourceType(source, providers);
            }
            var context = new EffectFieldTypeContext
            {
                State = state,
                BoundProviders = new ReadOnlyDictionary<string, EffectProviderTypeState>(sources),
                InputTypes = new ReadOnlyDictionary<string, EffectArgumentFieldType>(inputs),
            };
            foreach (var (key, field) in state.Fields) Resolve(key, true, field);
            foreach (var (key, field) in state.Outputs) Resolve(key, false, field);
            contexts[id] = context;
            visiting.Remove(id);

            void Resolve(string key, bool input, EffectArgumentFieldDescriptor field)
            {
                var type = field.FieldType;
                if (type.HasFlag(EffectArgumentFieldType.Any))
                {
                    try
                    {
                        type = providers[id].ResolveFieldType(key, input, context);
                        if (type.BaseType() == EffectArgumentFieldType.Unknown || type.HasFlag(EffectArgumentFieldType.Any)) type = EffectArgumentFieldType.Any;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(ex, $"Resolve {(input ? "input" : "output")} '{key}' on provider {id}", this);
                        diagnostics.Add(new(id, "InvalidFieldType", $"Cannot resolve field '{key}' on provider {id}: {ex.Message}"));
                        type = EffectArgumentFieldType.Any;
                    }
                }
                types[(id, input, key)] = type;
            }
        }
        foreach (var id in providers.Keys) Visit(id);
    }

    public static Task<EffectFieldTypeResolution> ResolveAsync(IReadOnlyDictionary<Guid, IEffectProvider> providers,
        CancellationToken cancellationToken = default, IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>>? bindingOverrides = null)
    {
        var copy = providers.ToDictionary();
        var states = copy.ToDictionary(p => p.Key, p => EffectProviderTypeState.Capture(p.Value, bindingOverrides?.GetValueOrDefault(p.Key)));
        return Task.Run(() => new EffectFieldTypeResolution(copy, null, states), cancellationToken);
    }

    public bool AreInputsCompatible(IEffectProvider provider) => contexts[provider.Id].InputTypes.All(p =>
        p.Value.HasFlag(EffectArgumentFieldType.Any) || EffectFieldTypes.AreCompatible(p.Value, GetInputType(provider, p.Key)));

    public EffectArgumentFieldType GetInputType(IEffectProvider provider, string fieldId) =>
        types.GetValueOrDefault((provider.Id, true, fieldId), EffectArgumentFieldType.Unknown);

    public EffectArgumentFieldType GetOutputType(IEffectProvider provider, string? outputId = null) =>
        TryGetOutputField(provider, outputId, out var field)
            ? types.GetValueOrDefault((provider.Id, false, field.Id), EffectArgumentFieldType.Any) : EffectArgumentFieldType.Any;

    public bool TryGetOutputField(IEffectProvider provider, string? outputId, out EffectArgumentFieldDescriptor field)
    {
        if (!contexts.TryGetValue(provider.Id, out var context)) return provider.TryGetOutputField(outputId, out field);
        field = null!;
        if (provider is IMultipleOutputEffectProvider && outputId is null) return false;
        outputId ??= context.State.Outputs.Keys.SingleOrDefault();
        return outputId is not null && context.State.Outputs.TryGetValue(outputId, out field!);
    }

    public EffectArgumentFieldDescriptor GetOutputField(IEffectProvider provider) =>
        TryGetOutputField(provider, null, out var field) ? field : throw new InvalidOperationException($"Select an output port on provider {provider.Id}.");

    public EffectFieldTypeContext GetContext(Guid providerId) => contexts[providerId];

    private EffectArgumentFieldType GetSourceType(string source, IReadOnlyDictionary<Guid, IEffectProvider> providers)
    {
        if (source == IEffectProvider.InputAnchorGUID.ToString()) return EffectArgumentFieldType.IPicture;
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
