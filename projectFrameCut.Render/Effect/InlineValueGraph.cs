using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

internal sealed class InlineValueGraph(IReadOnlyDictionary<Guid, IEffectProvider> providers)
{
    private sealed record Node(IEffectProvider Provider, Dictionary<string, IEffectArgumentField> Fields,
        Dictionary<string, string> Bindings, IEffect[] Effects, bool Enabled);
    private readonly Dictionary<Guid, Node> nodes = new();
    private readonly HashSet<IEffect> initialized = new(ReferenceEqualityComparer.Instance);
    private readonly object sync = new();
    private int activeCalls;
    private bool disposed;
    private Action? release;
    public IEnumerable<IEffect> Effects => nodes.Values.SelectMany(n => n.Effects);
    public IEffect[]? FindEffects(Guid id) => nodes.GetValueOrDefault(id)?.Effects;
    public bool Contains(Guid id) => nodes.ContainsKey(id);

    public IEffect[] GetEffects(Guid id)
    {
        if (!nodes.TryGetValue(id, out var node))
        {
            var provider = providers[id];
            var effects = provider.Enabled ? EffectBindingHelper.BuildProviderEffects(provider) : [];
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
            foreach (var dependency in EffectBindingHelper.GetBoundProviderDependencyIds(provider).Distinct()) GetEffects(dependency);
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
            disposed = true;
            if (activeCalls > 0)
            {
                release = releaseEffects;
                return;
            }
        }
        releaseEffects();
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
        if (source is ValueProviderFrameContext.BuiltInFrameProviderId or ValueProviderFrameContext.BuiltInProgressProviderId)
            return ValueProviderFrameContext.Get(source);
        if (!EffectProviderOutputExtensions.TryParseOutputSourceId(source, out var id, out var outputId)
            || !nodes.TryGetValue(id, out var node) || !node.Provider.TryGetOutputField(outputId, out var output)) return null;
        return ValueProviderFrameContext.GetOrCompute(node, () => Compute(node)).GetValueOrDefault(output.Id);
    }

    private IReadOnlyDictionary<string, object?> Compute(Node node)
    {
        if (!node.Enabled) return new Dictionary<string, object?>();
        uint frame = (uint)Convert.ToSingle(ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInFrameProviderId) ?? 0f);
        float progress = Convert.ToSingle(ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInProgressProviderId) ?? 0f);
        var parameters = new Dictionary<string, object?>();
        var values = new Dictionary<string, object?>();
        var pictures = new HashSet<IPicture>(ReferenceEqualityComparer.Instance);
        try
        {
            foreach (var (key, field) in node.Fields)
            {
                string? binding = node.Bindings.GetValueOrDefault(key);
                object? fallback = field switch
                {
                    StaticEffectArgumentField f => f.Value,
                    DynamicEffectParamField f => f.StaticFallbackValue,
                    _ => EffectFieldTypes.DefaultValue(field),
                };
                parameters[key] = EffectFieldTypes.ConvertInput(field.FieldType, binding is null ? fallback : Resolve(binding) ?? fallback);
                if (parameters[key] is null && field.FieldType.HasFlag(EffectArgumentFieldType.Mandatory)
                    || !EffectFieldTypes.Accepts(field.FieldType, parameters[key]))
                    throw new InvalidOperationException($"Invalid input '{key}' on provider {node.Provider.Id}.");
                if (binding is not null) values[binding] = parameters[key];
            }
            using var scope = ValueProviderFrameContext.PushValues(values);
            object? value = null;
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
                };
                if (effect is IMultipleOutputEffect multiple)
                {
                    var outputs = multiple.ComputeOutputs(context) ?? throw new InvalidOperationException($"Provider {node.Provider.Id} returned no output map.");
                    foreach (var picture in outputs.Values.OfType<IPicture>()) pictures.Add(picture);
                    DynamicEffectBindings.ValidateOutputValues(node.Provider, outputs);
                    return outputs;
                }
                value = effect.Compute(context);
                if (value is IPicture p) pictures.Add(p);
            }
            var result = new Dictionary<string, object?> { [node.Provider.OutField.Id] = value };
            DynamicEffectBindings.ValidateOutputValues(node.Provider, result);
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
        public string DefaultValue { get; set; } = output.DefaultValue;
        public string MinValue { get; set; } = output.MinValue;
        public string MaxValue { get; set; } = output.MaxValue;
        public string[]? PresetOptions { get; set; } = output.PresetOptions;
        public string? Remarks { get; set; } = output.Remarks;
        public void Initialize() { }
        public Func<object> GetGetter() => () => graph.GetValue(source) ?? fallback!;
        public IEffect WithParameters(Dictionary<string, object> parameters) => throw new NotSupportedException();
    }
}
