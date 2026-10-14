using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Context;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

public sealed class DynamicEffectGraph : INormalEffect, IDisposable
{
    private sealed record Node(IEffectProvider Provider, Dictionary<string, IEffectArgumentField> Fields,
        Dictionary<string, string> Bindings, IEffect[] Effects, bool Enabled);
    private readonly Dictionary<Guid, Node> nodes;
    private readonly string? finalSource;
    private readonly InlineValueGraph? inlineValues;
    private readonly EffectFieldTypeResolution types;
    private readonly EffectPipeline pipeline;
    private readonly HashSet<Guid> contentSources;
    private readonly HashSet<Guid> conditionalNodes;
    private readonly object sync = new();
    private bool initialized;
    private bool disposed;
    private int activeFrames;

    public DynamicEffectGraph(IReadOnlyDictionary<Guid, IEffectProvider> providers, IReadOnlyDictionary<Guid, IEffect[]> effects)
        : this(providers, effects, null) { }

    internal DynamicEffectGraph(IReadOnlyDictionary<Guid, IEffectProvider> providers, IReadOnlyDictionary<Guid, IEffect[]> effects,
        InlineValueGraph? inlineValues, EffectFieldTypeResolution? resolvedTypes = null, EffectPipeline pipeline = EffectPipeline.Picture)
    {
        this.pipeline = pipeline;
        this.inlineValues = inlineValues;
        types = resolvedTypes ?? inlineValues?.Types ?? new(providers);
        foreach (var provider in providers.Values) provider.ValidateOutputFields();
        nodes = providers.Values.Where(p => p.GetPipeline() == pipeline && DynamicEffectBindings.IsGraphProvider(p)).ToDictionary(p => p.Id,
            p => new Node(p, DynamicEffectBindings.InputFields(p), new(p.AnchorsBindingState), effects.GetValueOrDefault(p.Id) ?? [], p.Enabled));
        finalSource = providers.Values.FirstOrDefault(p => p.GetPipeline() == pipeline && DynamicEffectBindings.IsGraphProvider(p) && p.IsFinalOutputSource())?.GetFinalOutputSourceId();
        contentSources = providers.Values.Where(p => p.GetPipeline() == EffectPipeline.NativeContent && !DynamicEffectBindings.IsGraphProvider(p))
            .Select(p => p.Id).ToHashSet();
        conditionalNodes = nodes.Values.Where(n => n.Provider is ConditionComputeNodeEffectProvider).Select(n => n.Provider.Id).ToHashSet();
        if (conditionalNodes.Count > 0)
        {
            var consumers = nodes.Keys.ToDictionary(id => id, _ => new List<Guid>());
            foreach (var (id, node) in nodes)
                foreach (var binding in node.Bindings.Where(p => !EffectProviderAnchorExtensions.IsOutputBindingKey(p.Key)))
                    if (EffectProviderOutputExtensions.TryParseOutputSourceId(binding.Value, out var upstream, out _)
                        && consumers.TryGetValue(upstream, out var list)) list.Add(id);
            var pending = new Queue<Guid>(conditionalNodes);
            while (pending.TryDequeue(out var id))
                foreach (var consumer in consumers[id])
                    if (conditionalNodes.Add(consumer)) pending.Enqueue(consumer);
        }
        inlineValues?.AddOwner();
    }

    public string TypeName => "__DynamicEffectGraph__";
    public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Dynamic effect graph";
    public bool Enabled { get; set; } = true;
    public int Index { get; set; }
    public bool IsReorderable => false;
    public int RelativeWidth { get; set; }
    public int RelativeHeight { get; set; }
    public string? BindedEffectProvidingSystemID { get; set; } = "__DynamicEffectGraph__";
    public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
    public Dictionary<string, object> Parameters { get; } = new();
    public IEnumerable<IEffect> NodeEffects => nodes.Values.SelectMany(n => n.Effects);
    public IEffect WithParameters(Dictionary<string, object> parameters) => throw new NotSupportedException("Rebuild the provider graph instead.");

    public void Initialize()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (initialized) return;
            foreach (var effect in NodeEffects)
            {
                if (inlineValues is null) effect.Initialize();
                else inlineValues.InitializeEffect(effect);
            }
            initialized = true;
        }
    }

    public IPicture Render(IPicture source, int targetWidth, int targetHeight) => Evaluate(source, null,
        (uint)Convert.ToSingle(ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInFrameProviderId) ?? 0f),
        targetWidth, targetHeight, targetWidth, targetHeight, new(0, 0, targetWidth, targetHeight, false)).Picture;

    public sealed record FrameResult(IPicture Picture, ClipPositionTuple Position, bool PreserveAspect);
    private sealed record Result(object? Value, ClipPositionTuple Position, bool PreserveAspect,
        IReadOnlyDictionary<string, object?>? Outputs = null);
    private sealed record FrameKey(DynamicEffectGraph Graph, Guid Clip, uint Frame, int Width, int Height,
        int RelativeWidth, int RelativeHeight, int SourceWidth, int SourceHeight, Type SourceType,
        ClipPositionTuple Position, bool PreserveAspect, bool ProcessFromCanvas, bool TransformInput);
    internal sealed record ContentFrameResult(object Content, ClipPositionTuple Position, bool PreserveAspect);
    private sealed record CachedFrame(ContentFrameResult Frame, IReadOnlyDictionary<Guid, object> Exports);

    public FrameResult Evaluate(IPicture source, IClip? clip, uint frame, int width, int height, int relativeWidth,
        int relativeHeight, ClipPositionTuple initialPosition, bool preserveAspect = true, bool processFromCanvas = false,
        CancellationToken cancellationToken = default, Action<IEffect, object?>? afterNode = null)
    {
        var result = EvaluateContent(source, clip, frame, width, height, relativeWidth, relativeHeight,
            initialPosition, preserveAspect, processFromCanvas, cancellationToken, afterNode);
        return new((IPicture)result.Content, result.Position, result.PreserveAspect);
    }

    internal ContentFrameResult EvaluateContent(object source, IClip? clip, uint frame, int width, int height, int relativeWidth,
        int relativeHeight, ClipPositionTuple initialPosition, bool preserveAspect = true, bool processFromCanvas = false,
        CancellationToken cancellationToken = default, Action<IEffect, object?>? afterNode = null, float? clipProgress = null,
        float? vectorProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var endpoints = clip is not null && ClipValueFrameContext.Current?.FrameIndex == frame && clip.ContainsFrame(frame)
            && nodes.Values.Any(n => n.Provider is IClipValueEndpointProvider)
            ? ClipValueFrameContext.Current : null;
        var key = endpoints is null ? null : new FrameKey(this, clip!.Id, frame, width, height, relativeWidth,
            relativeHeight, (source as IPicture)?.Width ?? width, (source as IPicture)?.Height ?? height,
            source.GetType(), initialPosition, preserveAspect, processFromCanvas, endpoints.IsTransformInput);
        if (key is not null && afterNode is null && endpoints!.TryGetComputed<CachedFrame>(key, out var cachedFrame))
        {
            foreach (var (id, value) in cachedFrame!.Exports) endpoints.Publish(clip!, frame, id, value);
            return cachedFrame.Frame with { Content = CopyContent(cachedFrame.Frame.Content) };
        }
        lock (sync)
        {
            Initialize();
            activeFrames++;
        }
        var results = new Dictionary<Guid, Result>();
        var visiting = new HashSet<Guid>();
        var activation = new Dictionary<(Guid, string), bool?>();
        var checkingActivation = new HashSet<(Guid, string, bool)>();
        var conditions = new Dictionary<Guid, bool>();
        var mainInputs = new Dictionary<Guid, Result>();
        var pictures = new HashSet<IPicture>(ReferenceEqualityComparer.Instance);
        object? output = null;
        bool returned = false;
        var previousBuffer = IRenderContext.CurrentFrameBuffer;
        float progress = clipProgress ?? (clip is not null && clip.GetEffectiveDuration() > 0
            ? Math.Clamp((float)((long)frame - clip.StartFrame) / clip.GetEffectiveDuration(), 0, 1)
            : Convert.ToSingle(ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInProgressProviderId) ?? 0f));
        using var scope = ValueProviderFrameContext.PushFrame(frame, progress, reuseComputed: true);

        IPicture Track(IPicture p)
        {
            if (!ReferenceEquals(p, source)) pictures.Add(p);
            return p;
        }
        object? Copy(object? value) => value is IPicture p ? Track(CopyPicture(p))
            : value is VectorPicture v ? VectorPictureEffectProcessing.Copy(v) : value;
        float? Progress(IEffect effect)
        {
            if (effect is IVectorPictureEffect) return vectorProgress ?? progress;
            if (effect is not IContinuousEffect continuous || !continuous.IsScoped && clip is null) return progress;
            long start = continuous.IsScoped ? continuous.StartPoint : clip?.StartFrame ?? 0;
            long end = continuous.IsScoped ? continuous.EndPoint : start + (clip?.GetEffectiveDuration() ?? 1);
            return end > start && frame >= start && frame < end ? Math.Clamp((float)(frame - start) / (end - start), 0, 1) : null;
        }
        Result Inactive(Guid id)
        {
            if (results.TryGetValue(id, out var cached)) return cached;
            Logger.LogDiagnostic($"Skipped inactive provider {id}, clip {clip?.Id}, frame {frame}.");
            return results[id] = new(EffectBranchState.Inactive, initialPosition, preserveAspect,
                nodes[id].Provider is IMultipleOutputEffectProvider
                    ? nodes[id].Provider.GetOutputFields().Keys.ToDictionary(key => key, _ => (object?)EffectBranchState.Inactive) : null);
        }
        Result MainInput(Node node, bool useDefault)
        {
            if (mainInputs.TryGetValue(node.Provider.Id, out var cached)) return cached;
            Result input;
            try { input = Resolve(node.Bindings.GetValueOrDefault(EffectProviderAnchorExtensions.InputKey)); }
            catch (Exception ex) when (useDefault && ex is not OperationCanceledException)
            {
                Logger.Log(ex, $"Resolve upstream of provider {node.Provider.Id}; using default value, clip {clip?.Id}, frame {frame}", this);
                input = new(null, initialPosition, preserveAspect);
            }
            return mainInputs[node.Provider.Id] = input;
        }
        bool Condition(Node node, string key)
        {
            if (conditions.TryGetValue(node.Provider.Id, out bool cached)) return cached;
            var field = node.Fields[key];
            var input = Resolve(node.Bindings.GetValueOrDefault(key));
            var value = EffectFieldTypes.ConvertInput(field.FieldType, input.Value ?? Fallback(field));
            types.ValidateValue(node.Provider, key, true, field, value, frame);
            if (value is not bool condition) throw new InvalidOperationException($"Provider {node.Provider.Id} requires a bool condition, frame {frame}.");
            Logger.LogDiagnostic($"Condition provider {node.Provider.Id} selected {condition}, clip {clip?.Id}, frame {frame}.");
            return conditions[node.Provider.Id] = condition;
        }
        bool IsSourceActive(string? sourceId) => CheckActivation(sourceId, true) is true;
        // A null probe defers availability checks that need data until other dependencies have been checked.
        bool? CheckActivation(string? sourceId, bool resolveAvailability)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!EffectProviderOutputExtensions.TryParseOutputSourceId(sourceId, out var id, out var outputId)
                || !nodes.TryGetValue(id, out var node) || !conditionalNodes.Contains(id)) return true;
            if (!types.TryGetOutputField(node.Provider, outputId, out var field))
                throw new InvalidOperationException($"Provider {id} requires a valid output port in '{sourceId}'.");
            if (results.TryGetValue(id, out var result))
                return !ReferenceEquals(result.Outputs is null ? result.Value : result.Outputs.GetValueOrDefault(field.Id), EffectBranchState.Inactive);
            var key = (id, field.Id);
            if (activation.TryGetValue(key, out var cached) && (cached.HasValue || !resolveAvailability)) return cached;
            var checkingKey = (id, field.Id, resolveAvailability);
            if (!checkingActivation.Add(checkingKey)) throw new InvalidOperationException($"Effect cycle at {id}.");
            try
            {
                bool? active;
                bool hasEffects = node.Enabled && node.Effects.Any(e => Progress(e).HasValue);
                var main = node.Bindings.GetValueOrDefault(EffectProviderAnchorExtensions.InputKey);
                if (node.Provider is ConditionComputeNodeEffectProvider)
                {
                    if (!hasEffects) return activation[key] = false;
                    var inputActive = CheckActivation(main, resolveAvailability);
                    if (inputActive is false) return activation[key] = false;
                    var conditionActive = CheckActivation(node.Bindings.GetValueOrDefault(ConditionComputeNodeEffectProvider.ConditionKey), resolveAvailability);
                    if (conditionActive is false) return activation[key] = false;
                    if (conditionActive is null) return activation[key] = null;
                    active = field.Id == (Condition(node, ConditionComputeNodeEffectProvider.ConditionKey)
                        ? ConditionComputeNodeEffectProvider.TrueOutputKey : ConditionComputeNodeEffectProvider.FalseOutputKey)
                        ? inputActive : false;
                }
                else if (hasEffects && node.Provider is IsUpstreamAvailableOrDefaultEffectProvider)
                {
                    if (!resolveAvailability) return activation[key] = null;
                    var value = MainInput(node, true).Value;
                    active = value is not null && !ReferenceEquals(value, EffectBranchState.Inactive)
                        || IsSourceActive(node.Bindings.GetValueOrDefault("DefaultValue"));
                }
                else if (hasEffects && node.Provider is IfConditionNodeEffectProvider)
                {
                    active = CheckActivation(node.Bindings.GetValueOrDefault(IfConditionNodeEffectProvider.ConditionKey), resolveAvailability);
                    if (active is true)
                        active = CheckActivation(node.Bindings.GetValueOrDefault(
                            Condition(node, IfConditionNodeEffectProvider.ConditionKey) ? "TrueValue" : "FalseValue"), resolveAvailability);
                }
                else
                {
                    var bindings = node.Bindings.Where(p => !EffectProviderAnchorExtensions.IsOutputBindingKey(p.Key)
                        && node.Fields.ContainsKey(p.Key) && (hasEffects || p.Key == EffectProviderAnchorExtensions.InputKey));
                    active = true;
                    foreach (var binding in bindings)
                    {
                        var state = CheckActivation(binding.Value, false);
                        if (state is false) return activation[key] = false;
                        if (state is null) active = null;
                    }
                    if (active is null && resolveAvailability)
                        active = bindings.All(p => IsSourceActive(p.Value));
                }
                return activation[key] = active;
            }
            finally { checkingActivation.Remove(checkingKey); }
        }
        Result Resolve(string? id)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (id == IEffectProvider.InputAnchorGUID.ToString()) return new(source, initialPosition, preserveAspect);
            if (id is ValueProviderFrameContext.BuiltInFrameProviderId or ValueProviderFrameContext.BuiltInProgressProviderId)
                return new(ValueProviderFrameContext.Get(id), initialPosition, preserveAspect);
            if (!EffectProviderOutputExtensions.TryParseOutputSourceId(id, out var guid, out var outputId)) return new(null, initialPosition, preserveAspect);
            if (!nodes.TryGetValue(guid, out var node)) return new(pipeline == EffectPipeline.NativeContent && contentSources.Contains(guid)
                ? source : null, initialPosition, preserveAspect);
            if (!types.TryGetOutputField(node.Provider, outputId, out var field))
                throw new InvalidOperationException($"Provider {guid} requires a valid output port in '{id}'.");
            if (types.GetOutputType(node.Provider, field.Id).HasFlag(EffectArgumentFieldType.Any))
                throw new InvalidOperationException($"Provider {guid} output '{field.Id}' has an unresolved type, frame {frame}.");
            if (!IsSourceActive(id))
                return node.Provider is ConditionComputeNodeEffectProvider
                    ? new(EffectBranchState.Inactive, initialPosition, preserveAspect) : Inactive(guid);
            if (inlineValues?.Contains(guid) == true)
                return new(inlineValues.GetValue(id!), initialPosition, preserveAspect);
            var result = EvaluateNode(guid);
            return result.Outputs is null ? result : result with { Value = result.Outputs.GetValueOrDefault(field.Id) };
        }
        object? Fallback(IEffectArgumentField field)
        {
            object? value = field switch
            {
                StaticEffectArgumentField f => f.Value,
                DynamicEffectParamField f => f.StaticFallbackValue,
                EffectArgumentFieldDescriptor => EffectFieldTypes.DefaultValue(field),
                _ => field.GetGetter()(),
            };
            return field.FieldType.IsContent() && !EffectFieldTypes.Accepts(field.FieldType, value) ? null : value;
        }
        Result EvaluateNode(Guid id)
        {
            if (results.TryGetValue(id, out var cached)) return cached;
            if (!visiting.Add(id)) throw new InvalidOperationException($"Effect cycle at {id}.");
            var node = nodes[id];
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (node.Provider is IClipValueEndpointProvider { IsExport: false } endpointInput)
                {
                    if (!node.Enabled || clip is null || ClipValueFrameContext.Current is null)
                        throw new ClipValueBindingException($"Endpoint input {id} on clip {clip?.Id}, frame {frame}, is unavailable.");
                    var imported = Copy(ClipValueFrameContext.Current.Read(clip, frame, endpointInput));
                    var field = types.GetOutputField(node.Provider);
                    types.ValidateValue(node.Provider, field.Id, false, field, imported, frame);
                    ValueProviderFrameContext.Set(id.ToString(), imported);
                    ValueProviderFrameContext.Set(EffectProviderOutputExtensions.CreateOutputSourceId(id, field.Id), imported);
                    foreach (var effect in node.Effects) afterNode?.Invoke(effect, imported);
                    return results[id] = new(imported, initialPosition, preserveAspect);
                }
                bool multiple = node.Provider is IMultipleOutputEffectProvider;
                if (multiple && (!node.Enabled || node.Effects.Length == 0 || !Progress(node.Effects[^1]).HasValue))
                    return results[id] = new(null, initialPosition, preserveAspect, new Dictionary<string, object?>());
                bool useDefault = node.Provider is IsUpstreamAvailableOrDefaultEffectProvider;
                var activeEffects = node.Enabled ? node.Effects.Select(e => (Effect: e, Progress: Progress(e)))
                    .Where(e => e.Progress.HasValue).ToArray() : [];
                var input = MainInput(node, useDefault && activeEffects.Length > 0);
                if (useDefault && activeEffects.Length > 0 && (input.Value is null || ReferenceEquals(input.Value, EffectBranchState.Inactive)))
                    input = Resolve(node.Bindings.GetValueOrDefault("DefaultValue"));
                if (ReferenceEquals(input.Value, EffectBranchState.Inactive)) return Inactive(id);
                if (node.Provider is IClipValueEndpointProvider { IsExport: true } && input.Value is null)
                    throw new ClipValueBindingException($"Endpoint {id} on clip {clip?.Id}, frame {frame}, has no source value.");
                var position = input.Value is IPicture ? input.Position : initialPosition;
                bool aspect = input.Value is IPicture ? input.PreserveAspect : preserveAspect;
                node.Fields.TryGetValue(EffectProviderAnchorExtensions.InputKey, out var main);
                object? value = input.Value ?? (main is null || useDefault ? null : Fallback(main));
                IReadOnlyDictionary<string, object?>? outputs = null;
                if (activeEffects.Length == 0)
                {
                    if (main is null || (main.FieldType.HasFlag(EffectArgumentFieldType.Any)
                        ? !EffectFieldTypes.Accepts(types.GetOutputType(node.Provider), value)
                        : !EffectFieldTypes.AreCompatible(types.GetInputType(node.Provider, EffectProviderAnchorExtensions.InputKey),
                            types.GetOutputType(node.Provider)))) value = null;
                }
                else
                {
                    if (main is not null) value = EffectFieldTypes.ConvertInput(main.FieldType, value);
                    if (main is not null && value is null && main.FieldType.IsContent())
                        throw new InvalidOperationException($"Invalid primary input on provider {id}.");
                    if (main is not null) types.ValidateValue(node.Provider, EffectProviderAnchorExtensions.InputKey, true, main, value, frame);
                    string? selectedKey = node.Provider is IfConditionNodeEffectProvider
                        ? (Condition(node, IfConditionNodeEffectProvider.ConditionKey) ? "TrueValue" : "FalseValue") : null;
                    var selectedInput = selectedKey is null ? null : Resolve(node.Bindings.GetValueOrDefault(selectedKey));
                    if (ReferenceEquals(selectedInput?.Value, EffectBranchState.Inactive)) return Inactive(id);
                    if (selectedInput?.Value is IPicture)
                    {
                        position = selectedInput.Position;
                        aspect = selectedInput.PreserveAspect;
                    }
                    var parameters = new Dictionary<string, object?>();
                    var values = new Dictionary<string, object?>();
                    foreach (var (key, field) in node.Fields.Where(p => p.Key != EffectProviderAnchorExtensions.InputKey))
                    {
                        if (useDefault && key == "DefaultValue") continue;
                        if (selectedKey is not null && (key is "TrueValue" or "FalseValue") && key != selectedKey) continue;
                        var binding = node.Bindings.GetValueOrDefault(key);
                        object? parameter;
                        if (node.Provider is ConditionComputeNodeEffectProvider && key == ConditionComputeNodeEffectProvider.ConditionKey
                            || node.Provider is IfConditionNodeEffectProvider && key == IfConditionNodeEffectProvider.ConditionKey)
                            parameter = Condition(node, key);
                        else
                        {
                            var resolved = key == selectedKey ? selectedInput!.Value : Resolve(binding).Value;
                            if (ReferenceEquals(resolved, EffectBranchState.Inactive)) return Inactive(id);
                            parameter = key == selectedKey || node.Provider is EqualityComparisonNodeEffectProvider && (key is "A" or "B")
                                ? resolved : resolved ?? Fallback(field);
                        }
                        // Numeric comparison retains integer/decimal precision instead of coercing operands to float.
                        if (node.Provider is not NumericComparisonNodeEffectProvider || (key is not "A" and not "B"))
                            parameter = EffectFieldTypes.ConvertInput(field.FieldType, parameter);
                        types.ValidateValue(node.Provider, key, true, field, parameter, frame);
                        parameters[key] = Copy(parameter);
                        if (!string.IsNullOrEmpty(binding)) values[binding] = parameters[key];
                    }
                    value = Copy(value);
                    using var nodeScope = ValueProviderFrameContext.PushValues(values);
                    foreach (var (effect, nodeProgress) in activeEffects)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (processFromCanvas && effect.CanProcessFromCanvas && value is IPicture picture
                                && position.TargetWidth > 0 && position.TargetHeight > 0
                                && (picture.Width != position.TargetWidth || picture.Height != position.TargetHeight))
                            value = Track(picture is IHDRPicture<ushort> hdr
                                ? hdr.Resize(position.TargetWidth, position.TargetHeight, aspect)
                                : picture.Resize(position.TargetWidth, position.TargetHeight, aspect));
                        IRenderContext.CurrentFrameBuffer = value as IPicture;
                        var context = new EffectExecutionContext
                        {
                            Input = value, Parameters = parameters, Clip = clip, FrameIndex = frame,
                            Progress = nodeProgress!.Value, ClipProgress = progress, TargetWidth = width, TargetHeight = height,
                            RelativeWidth = relativeWidth, RelativeHeight = relativeHeight, CancellationToken = cancellationToken,
                        };
                        object? computed;
                        if (effect is IMultipleOutputEffect multi)
                        {
                            outputs = multi.ComputeOutputs(context) ?? throw new InvalidOperationException($"Provider {id} returned no output map.");
                            foreach (var pictureOutput in outputs.Values.OfType<IPicture>()) Track(pictureOutput);
                            computed = outputs;
                        }
                        else computed = effect.Compute(context);
                        if (effect is IClipPositionProvider or IContinuousClipPositionProvider)
                        {
                            position = ApplyPosition(position, computed is ClipPositionTuple pos ? pos
                                : throw new InvalidOperationException($"Position effect {effect.Id} did not return a position."));
                            if (effect is IContinuousClipPositionProvider cp) aspect &= cp.PreserveAspectRatio;
                        }
                        else value = computed;
                        if (value is IPicture rendered) Track(rendered);
                    }
                }
                if (multiple) DynamicEffectBindings.ValidateOutputValues(node.Provider, outputs!, types, frame);
                else
                {
                    var field = types.GetOutputField(node.Provider);
                    types.ValidateValue(node.Provider, field.Id, false, field, value, frame);
                }
                if (activeEffects.Length > 0 && afterNode is not null)
                {
                    object? observed = multiple ? outputs : value;
                    if (outputs is not null && outputs.Values.Any(v => ReferenceEquals(v, EffectBranchState.Inactive)))
                        observed = outputs.ToDictionary(p => p.Key, p => ReferenceEquals(p.Value, EffectBranchState.Inactive) ? null : p.Value);
                    afterNode(activeEffects[^1].Effect, observed);
                }
                var result = new Result(multiple ? null : value, position, aspect, outputs);
                results[id] = result;
                if (outputs is not null)
                    foreach (var (key, outputValue) in outputs)
                        ValueProviderFrameContext.Set(EffectProviderOutputExtensions.CreateOutputSourceId(id, key),
                            ReferenceEquals(outputValue, EffectBranchState.Inactive) ? null : outputValue);
                else
                {
                    ValueProviderFrameContext.Set(id.ToString(), value);
                    var field = types.GetOutputField(node.Provider);
                    if (!string.IsNullOrWhiteSpace(field.Id))
                        ValueProviderFrameContext.Set(EffectProviderOutputExtensions.CreateOutputSourceId(id, field.Id), value);
                }
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Log(ex, $"Compute effect provider {id} of clip {clip?.Id}, frame {frame}", this);
                throw;
            }
            finally { visiting.Remove(id); }
        }
        try
        {
            var exported = new Dictionary<Guid, object>();
            foreach (var (id, node) in nodes)
                if (node.Provider is IClipValueEndpointProvider { IsExport: true } && node.Enabled
                    && node.Bindings.GetValueOrDefault(EffectProviderAnchorExtensions.InputKey) is { } binding
                    && binding != IEffectProvider.NoConnectionGUID.ToString())
                {
                    var value = Resolve(id.ToString()).Value;
                    if (ReferenceEquals(value, EffectBranchState.Inactive)) continue;
                    if (value is null) throw new ClipValueBindingException($"Endpoint {id} on clip {clip?.Id}, frame {frame}, produced no value.");
                    exported[id] = value;
                }
            var result = finalSource is not null ? Resolve(finalSource) : new Result(source, initialPosition, preserveAspect);
            cancellationToken.ThrowIfCancellationRequested();
            if (!EffectFieldTypes.Accepts(pipeline.GetContentType(), result.Value) || result.Value is null)
                throw new InvalidOperationException($"Dynamic {pipeline} output on clip {clip?.Id} has invalid content.");
            output = result.Value;
            if (endpoints is not null)
            {
                var snapshots = exported.ToDictionary(p => p.Key, p => endpoints.Snapshot(p.Value));
                foreach (var (id, value) in snapshots) endpoints.Publish(clip!, frame, id, value);
                endpoints.SetComputed(key!, new CachedFrame(new ContentFrameResult(endpoints.Snapshot(output), result.Position, result.PreserveAspect), snapshots));
            }
            returned = true;
            return new(output, result.Position, result.PreserveAspect);
        }
        finally
        {
            IRenderContext.CurrentFrameBuffer = previousBuffer;
            try
            {
                foreach (var picture in pictures)
                {
                    if (returned && ReferenceEquals(picture, output)) continue;
                    try { picture.Dispose(); }
                    catch (Exception ex) { Logger.Log(ex, "Release dynamic effect frame", this); }
                }
            }
            finally
            {
                lock (sync)
                    if (--activeFrames == 0 && disposed) ReleaseEffects();
            }
        }
    }

    public static IPicture CopyPicture(IPicture source)
    {
        var clone = source.Clone();
        try
        {
            var result = PictureEffectChannels.PreserveHdr(clone, source);
            if (!ReferenceEquals(clone, result)) clone.Dispose();
            return result;
        }
        catch
        {
            clone.Dispose();
            throw;
        }
    }

    private static object CopyContent(object value) => value is IPicture picture ? CopyPicture(picture)
        : value is VectorPicture vector ? VectorPictureEffectProcessing.Copy(vector) : value;

    private static ClipPositionTuple ApplyPosition(ClipPositionTuple current, ClipPositionTuple value) => value.IsDelta
        ? new(current.TargetX + value.TargetX, current.TargetY + value.TargetY, current.TargetWidth + value.TargetWidth,
            current.TargetHeight + value.TargetHeight, false, current.Rotation + value.Rotation)
        : value;

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            if (activeFrames == 0) ReleaseEffects();
        }
    }

    private void ReleaseEffects()
    {
        if (inlineValues is null) ReleaseEffectsCore();
        else inlineValues.ReleaseWhenIdle(ReleaseEffectsCore);
    }

    private void ReleaseEffectsCore()
    {
        foreach (var effect in NodeEffects.OfType<IDisposable>().Distinct<IDisposable>(ReferenceEqualityComparer.Instance))
        {
            try { effect.Dispose(); }
            catch (Exception ex) { Logger.Log(ex, "Release dynamic effect instance", this); }
        }
    }
}
