using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

internal sealed class InlineValueGraph(IReadOnlyDictionary<Guid, IEffectProvider> providers, EffectFieldTypeResolution? resolvedTypes = null)
{
    public bool IsDeterministic => providers.Values.All(p => !p.Enabled || p.IsDeterministic) && Effects.All(e => e.IsDeterministic);
    public EffectFieldTypeResolution Types { get; } = resolvedTypes ?? new(providers);
    private sealed record Node(IEffectProvider Provider, Dictionary<string, IEffectArgumentField> Fields,
        Dictionary<string, string> Bindings, IEffect[] Effects, bool Enabled);
    private readonly Dictionary<Guid, Node> nodes = new();
    private readonly HashSet<IEffect> initialized = new(ReferenceEqualityComparer.Instance);
    private readonly object sync = new();
    private int activeCalls;
    private bool disposed;
    private Action? release;
    private int owners;
    public IEnumerable<IEffect> Effects => nodes.Values.SelectMany(n => n.Effects);
    public IEffect[]? FindEffects(Guid id) => nodes.GetValueOrDefault(id)?.Effects;
    public bool Contains(Guid id) => nodes.ContainsKey(id);
    public void AddOwner()
    {
        lock (sync) owners++;
    }

    public IEffect[] GetEffects(Guid id)
    {
        if (!nodes.TryGetValue(id, out var node))
        {
            var provider = providers[id];
            provider.ValidateOutputFields();
            var effects = provider.Enabled ? EffectBindingHelper.BuildProviderEffects(provider, Types) : [];
            foreach (var effect in effects) effect.BindedEffectProvidingSystemID = provider.Id.ToString();
            try
            {
                if (provider.Enabled) DynamicEffectBindings.ValidateMultipleOutputEffects(provider, effects);
            }
            catch
            {
                foreach (var effect in effects.OfType<IDisposable>()) effect.Dispose();
                throw;
            }
            node = new(provider, DynamicEffectBindings.InputFields(provider), new(provider.AnchorsBindingState), effects, provider.Enabled);
            nodes[id] = node;
            foreach (var dependency in provider.AnchorsBindingState.Where(p => !EffectProviderAnchorExtensions.IsOutputBindingKey(p.Key))
                .Select(p => EffectProviderOutputExtensions.TryParseOutputSourceId(p.Value, out var id, out _) ? id : Guid.Empty)
                .Where(providers.ContainsKey).Distinct()) GetEffects(dependency);
        }
        return node.Effects;
    }

    public void InitializeEffect(IEffect effect)
    {
        lock (initialized)
        {
            if (initialized.Contains(effect)) return;
            effect.Initialize();
            initialized.Add(effect);
        }
    }

    public IEffectArgumentField CreateField(string source, string fieldId, EffectArgumentFieldDescriptor output, object? fallback)
        => new ValueField(this, source, fieldId, output, fallback);

    public void ReleaseWhenIdle(Action releaseEffects)
    {
        lock (sync)
        {
            if (disposed) return;
            release += releaseEffects;
            if (--owners > 0) return;
            disposed = true;
            if (activeCalls > 0) return;
        }
        release?.Invoke();
    }

    public object? GetValue(string source)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            activeCalls++;
        }
        try { return Resolve(source); }
        finally
        {
            Action? releaseEffects = null;
            lock (sync)
                if (--activeCalls == 0 && disposed)
                {
                    releaseEffects = release;
                    release = null;
                }
            releaseEffects?.Invoke();
        }
    }

    private object? Resolve(string source)
    {
        if (ValueProviderFrameContext.IsBuiltInSource(source))
            return ValueProviderFrameContext.Get(source);
        if (!EffectProviderOutputExtensions.TryParseOutputSourceId(source, out var id, out var outputId)
            || !nodes.TryGetValue(id, out var node) || !Types.TryGetOutputField(node.Provider, outputId, out var output)) return null;
        if (node.Provider is ConditionComputeNodeEffectProvider
            && output.Id != (ReadCondition(node, ConditionComputeNodeEffectProvider.ConditionKey)
                ? ConditionComputeNodeEffectProvider.TrueOutputKey : ConditionComputeNodeEffectProvider.FalseOutputKey))
            return EffectBranchState.Inactive;
        return ValueProviderFrameContext.GetOrCompute(node, () => Compute(node)).GetValueOrDefault(output.Id);
    }

    private object? ReadInput(Node node, string key)
    {
        if (!node.Fields.TryGetValue(key, out var field)) return null;
        object? fallback = field switch
        {
            StaticEffectArgumentField f => f.Value,
            DynamicEffectParamField f => f.StaticFallbackValue,
            _ => EffectFieldTypes.DefaultValue(field),
        };
        return node.Bindings.TryGetValue(key, out var binding) ? Resolve(binding) ?? fallback : fallback;
    }

    private bool ReadCondition(Node node, string key) => EffectParamConvert.TryConvertToBool(ReadInput(node, key), out var condition)
        ? condition : throw new InvalidOperationException($"Input '{key}' on provider {node.Provider.Id} must be boolean.");

    private IReadOnlyDictionary<string, object?> Compute(Node node)
    {
        if (!node.Enabled) return new Dictionary<string, object?>();
        uint frame = (uint)Convert.ToSingle(ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInFrameProviderId) ?? 0f);
        float progress = Convert.ToSingle(ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInProgressProviderId) ?? 0f);
        if (node.Provider is IClipValueEndpointProvider { IsExport: false } input)
            return new Dictionary<string, object?>
            {
                [input.OutField.Id] = (ClipValueFrameContext.Current
                    ?? throw new ClipValueBindingException($"Endpoint {input.Id} requires a frame context.")).Read(frame, input),
            };
        var parameters = new Dictionary<string, object?>();
        var values = new Dictionary<string, object?>();
        var pictures = new HashSet<IPicture>(ReferenceEqualityComparer.Instance);
        try
        {
            string? selected = node.Provider is IfConditionNodeEffectProvider
                ? ReadCondition(node, IfConditionNodeEffectProvider.ConditionKey) ? "TrueValue" : "FalseValue" : null;
            foreach (var (key, field) in node.Fields.OrderBy(p => p.Key == EffectProviderAnchorExtensions.InputKey ? 0 : 1))
            {
                if (selected is not null && key is "TrueValue" or "FalseValue" && key != selected) continue;
                if (node.Provider is IsUpstreamAvailableOrDefaultEffectProvider && key == "DefaultValue"
                    && parameters.GetValueOrDefault(EffectProviderAnchorExtensions.InputKey) is not null) continue;
                string? binding = node.Bindings.GetValueOrDefault(key);
                object? inputValue = node.Provider is IsUpstreamAvailableOrDefaultEffectProvider
                    && key == EffectProviderAnchorExtensions.InputKey
                    ? binding is null ? null : Resolve(binding) : ReadInput(node, key);
                if (ReferenceEquals(inputValue, EffectBranchState.Inactive))
                {
                    if (node.Provider is IsUpstreamAvailableOrDefaultEffectProvider && key == EffectProviderAnchorExtensions.InputKey)
                        inputValue = null;
                    else return node.Provider.GetOutputFields().Keys.ToDictionary(k => k, _ => (object?)EffectBranchState.Inactive);
                }
                parameters[key] = node.Provider is AdvancedMathEffectProvider && (key is "A" or "B" or "C")
                    ? inputValue : EffectFieldTypes.ConvertInput(field.FieldType, inputValue, false);
                if (parameters[key] is null && field.FieldType.HasFlag(EffectArgumentFieldType.Mandatory)
                    || !EffectFieldTypes.Accepts(Types.GetInputType(node.Provider, key), parameters[key]))
                    throw new InvalidOperationException($"Invalid input '{key}' on provider {node.Provider.Id}.");
                if (binding is not null) values[binding] = parameters[key];
            }
            using var scope = ValueProviderFrameContext.PushValues(values);
            var inputTypes = node.Fields.Keys.ToDictionary(key => key, key => Types.GetInputType(node.Provider, key));
            foreach (var field in node.Fields)
                if (node.Bindings.TryGetValue(field.Key, out var binding)) inputTypes[binding] = Types.GetInputType(node.Provider, field.Key);
            object? value = parameters.GetValueOrDefault(EffectProviderAnchorExtensions.InputKey);
            foreach (var effect in node.Effects)
            {
                float nodeProgress = progress;
                if (effect is IContinuousEffect { IsScoped: true } continuous && (frame < continuous.StartPoint || frame >= continuous.EndPoint))
                {
                    if (effect is IMultipleOutputEffect) return new Dictionary<string, object?>();
                    continue;
                }
                if (effect is IContinuousEffect { IsScoped: true } scoped)
                    nodeProgress = Math.Clamp(((float)frame - scoped.StartPoint) / (scoped.EndPoint - scoped.StartPoint), 0, 1);
                InitializeEffect(effect);
                var context = new EffectExecutionContext
                {
                    Input = value, Parameters = parameters, FrameIndex = frame, Progress = nodeProgress, ClipProgress = progress,
                    InputTypes = inputTypes, Clip = ValueProviderFrameContext.CurrentClip,
                    CancellationToken = ValueProviderFrameContext.CancellationToken,
                };
                if (effect is IMultipleOutputEffect multiple)
                {
                    var outputs = (FrameHashContext.Current is { } hashes
                        ? hashes.ComputeEffect(effect, context, () => EffectPictureProcessing.Compute(effect, context, values), node.Provider.IsDeterministic, node.Provider, Array.IndexOf(node.Effects, effect))
                        : EffectPictureProcessing.Compute(effect, context, values)) as IReadOnlyDictionary<string, object?>
                        ?? throw new InvalidOperationException($"Provider {node.Provider.Id} returned no output map.");
                    foreach (var picture in outputs.Values.OfType<IPicture>()) pictures.Add(picture);
                    DynamicEffectBindings.ValidateOutputValues(node.Provider, outputs, Types, frame);
                    ValueProviderFrameContext.NodeObserver?.Invoke(effect, outputs);
                    return outputs;
                }
                value = FrameHashContext.Current is { } frameHashes
                    ? frameHashes.ComputeEffect(effect, context, () => EffectPictureProcessing.Compute(effect, context, values), node.Provider.IsDeterministic, node.Provider, Array.IndexOf(node.Effects, effect))
                    : EffectPictureProcessing.Compute(effect, context, values);
                if (value is IPicture p) pictures.Add(p);
                ValueProviderFrameContext.NodeObserver?.Invoke(effect, value);
            }
            var result = new Dictionary<string, object?> { [Types.GetOutputField(node.Provider).Id] = value };
            DynamicEffectBindings.ValidateOutputValues(node.Provider, result, Types, frame);
            return result;
        }
        catch (Exception ex)
        {
            Logger.Log(ex, $"Compute inline provider {node.Provider.Id}, frame {frame}", this);
            throw;
        }
        finally
        {
            foreach (var picture in pictures) picture.Dispose();
        }
    }

    private sealed class ValueField(InlineValueGraph graph, string source, string fieldId,
        EffectArgumentFieldDescriptor output, object? fallback) : IValueProviderEffect
    {
        public bool IsDeterministic => graph.IsDeterministic;

        public string Id { get; set; } = fieldId;
        public string TypeName => "__InlineOutputField__";
        public string FromPlugin => output.FromPlugin;
        public string Name { get; set; } = fieldId;
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public bool IsReorderable => false;
        public string? BindedEffectProvidingSystemID { get; set; } = source;
        public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
        public Dictionary<string, object> Parameters { get; } = new();
        public bool IsDynamic => true;
        public bool IsDynamicAtRenderTime => true;
        public EffectArgumentFieldType FieldType => output.FieldType;
        public object? DataFormat => output.DataFormat;
        public string DefaultValue { get; set; } = output.DefaultValue;
        public string MinValue { get; set; } = output.MinValue;
        public string MaxValue { get; set; } = output.MaxValue;
        public string[]? PresetOptions { get; set; } = output.PresetOptions;
        public string? Remarks { get; set; } = output.Remarks;
        public void Initialize() { }
        public Func<object> GetGetter() => () => graph.GetValue(source) is { } value
            && !ReferenceEquals(value, EffectBranchState.Inactive)
                ? value is IAudioSamples<float> audio ? EffectFieldTypes.CopyAudio(audio) : value : fallback!;
        public IEffect WithParameters(Dictionary<string, object> parameters) => throw new NotSupportedException();
    }
}
