using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.Compose;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Context;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System.Runtime.CompilerServices;

namespace projectFrameCut.Render.Effect;

public sealed class DynamicEffectGraph : IRenderEffectPipeline, IDisposable
{
    public bool IsDeterministic => nodes.Values.All(n => !n.Enabled || n.Provider.IsDeterministic && n.Effects.All(e => e.IsDeterministic)) && (inlineValues?.IsDeterministic ?? true);

    private readonly Dictionary<Guid, DynamicEffectNode> nodes;
    private readonly string? finalSource;
    private readonly RenderOutputProvider? renderOutput;
    public EffectStage Stage => stage;
    public bool AudioNeedsPicture { get; }
    private readonly InlineValueGraph? inlineValues;
    private readonly EffectFieldTypeResolution types;
    private readonly EffectStage stage;
    private readonly HashSet<Guid> contentSources;
    private readonly HashSet<Guid> conditionalNodes;
    private readonly Lock sync = new();
    private bool initialized;
    private bool disposed;
    private int activeFrames;

    internal IReadOnlyDictionary<Guid, DynamicEffectNode> Nodes => nodes;
    internal EffectFieldTypeResolution Types => types;
    internal RenderOutputProvider? RenderOutput => renderOutput;
    internal string? FinalSource => finalSource;
    internal IReadOnlySet<Guid> ContentSources => contentSources;

    public DynamicEffectGraph(IReadOnlyDictionary<Guid, IEffectProvider> providers, IReadOnlyDictionary<Guid, IEffect[]> effects)
        : this(providers, effects, null) { }

    internal DynamicEffectGraph(IReadOnlyDictionary<Guid, IEffectProvider> providers, IReadOnlyDictionary<Guid, IEffect[]> effects,
        InlineValueGraph? inlineValues, EffectFieldTypeResolution? resolvedTypes = null, EffectStage stage = EffectStage.Render)
    {
        this.stage = stage;
        renderOutput = providers.Values.OfType<RenderOutputProvider>().SingleOrDefault();
        this.inlineValues = inlineValues;
        types = resolvedTypes ?? inlineValues?.Types ?? new(providers);
        foreach (var provider in providers.Values) provider.ValidateOutputFields();
        nodes = providers.Values.Where(p => p.Stage == stage && DynamicEffectBindings.IsGraphProvider(p)).ToDictionary(p => p.Id,
            p => new DynamicEffectNode(p, DynamicEffectBindings.InputFields(p), new(p.AnchorsBindingState), effects.GetValueOrDefault(p.Id) ?? [], p.Enabled));
        finalSource = providers.Values.FirstOrDefault(p => p.Stage == stage && DynamicEffectBindings.IsGraphProvider(p) && p.IsFinalOutputSource())?.GetFinalOutputSourceId();
        contentSources = providers.Values.Where(p => p.Stage is EffectStage.Text or EffectStage.VectorContent && !DynamicEffectBindings.IsGraphProvider(p))
            .Select(p => p.Id).ToHashSet();
        var checkingSources = new HashSet<Guid>();
        bool NeedsPicture(string? source)
        {
            if (source == IEffectProvider.InputAnchorGUID.ToString()) return true;
            if (!EffectProviderOutputExtensions.TryParseOutputSourceId(source, out var id, out _)
                || !checkingSources.Add(id) || !nodes.TryGetValue(id, out var node)) return false;
            return node.Bindings.Where(p => !EffectProviderAnchorExtensions.IsOutputBindingKey(p.Key)).Any(p => NeedsPicture(p.Value));
        }
        AudioNeedsPicture = renderOutput is { Enabled: true } && new[] { RenderOutputProvider.AudioKey, RenderOutputProvider.ShouldRenderKey }
            .Any(field => NeedsPicture(renderOutput.AnchorsBindingState.GetValueOrDefault(field)));
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
    public string Id { get; set; } = "Pipeline:DynamicEffectGraph";
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
    EffectPipelineFrameResult IRenderEffectPipeline.Evaluate(IPicture source, IClip? clip, uint frame, int width, int height,
        int relativeWidth, int relativeHeight, ClipPositionTuple initialPosition, bool preserveAspect,
        bool processFromCanvas, CancellationToken cancellationToken, Action<IEffect, object?>? afterNode, bool keepHwAccelPicture)
    {
        var result = Evaluate(source, clip, frame, width, height, relativeWidth, relativeHeight, initialPosition,
            preserveAspect, processFromCanvas, cancellationToken, afterNode, keepHwAccelPicture);
        return new(result.Picture, result.Position, result.PreserveAspect);
    }

    private sealed record FrameKey(DynamicEffectGraph Graph, Guid Clip, uint Frame, int Width, int Height,
        int RelativeWidth, int RelativeHeight, int SourceWidth, int SourceHeight, Type SourceType, object SourceIdentity,
        ClipPositionTuple Position, bool PreserveAspect, bool ProcessFromCanvas, bool TransformInput);
    internal sealed record ContentFrameResult(object Content, ClipPositionTuple Position, bool PreserveAspect);
    private sealed record CachedFrame(ContentFrameResult Frame, IReadOnlyDictionary<Guid, object> Exports);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public FrameResult Evaluate(IPicture source, IClip? clip, uint frame, int width, int height, int relativeWidth,
        int relativeHeight, ClipPositionTuple initialPosition, bool preserveAspect = true, bool processFromCanvas = false,
        CancellationToken cancellationToken = default, Action<IEffect, object?>? afterNode = null, bool keepHwAccelPicture = false)
    {
        var result = EvaluateContent(source, clip, frame, width, height, relativeWidth, relativeHeight,
            initialPosition, preserveAspect, processFromCanvas, cancellationToken, afterNode);
        var picture = (IPicture)result.Content;
        if (!keepHwAccelPicture && picture is IHwAccelPicture gpu)
        {
            try { picture = gpu.Download(); }
            finally { gpu.Dispose(); }
        }
        return new(picture, result.Position, result.PreserveAspect);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public object EvaluateRenderValue(IClip clip, uint frame, string field, IAudioSamples<float>? audio = null,
        CancellationToken token = default)
    {
        int width = RelativeWidth > 0 ? RelativeWidth : Math.Max(1, clip.TargetWidth);
        int height = RelativeHeight > 0 ? RelativeHeight : Math.Max(1, clip.TargetHeight);
        return EvaluateContent(audio is null ? clip.PositionTuple : audio, clip, frame, width, height, width, height,
            clip.PositionTuple, cancellationToken: token, afterNode: ValueProviderFrameContext.NodeObserver, requestedOutput: field).Content;
    }

    public bool ShouldRender(IClip clip, uint frame) => (bool)EvaluateRenderValue(clip, frame, RenderOutputProvider.ShouldRenderKey,
        token: ValueProviderFrameContext.CancellationToken);

    internal ContentFrameResult EvaluateContent(object source, IClip? clip, uint frame, int width, int height, int relativeWidth,
        int relativeHeight, ClipPositionTuple initialPosition, bool preserveAspect = true, bool processFromCanvas = false,
        CancellationToken cancellationToken = default, Action<IEffect, object?>? afterNode = null, float? clipProgress = null,
        float? vectorProgress = null, IPicture? underPicture = null, IPicture.PicturePixelMode? mixturePixelMode = null, string? requestedOutput = null)
    {
        using var gpuScope = HwAccelPictureScope.Current is null ? new HwAccelPictureScope() : null;
        using var gpuEntry = gpuScope?.Enter();
        cancellationToken.ThrowIfCancellationRequested();
        var endpoints = clip is not null && ClipValueFrameContext.Current?.FrameIndex == frame && clip.ContainsFrame(frame)
            && nodes.Values.Any(n => n.Provider is IClipValueEndpointProvider)
            ? ClipValueFrameContext.Current : null;
        var key = endpoints is null || stage == EffectStage.Composition || requestedOutput is not null ? null : new FrameKey(this, clip!.Id, frame, width, height, relativeWidth,
            relativeHeight, (source as IPicture)?.Width ?? width, (source as IPicture)?.Height ?? height,
            source.GetType(), source, initialPosition, preserveAspect, processFromCanvas, endpoints.IsTransformInput);
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
        var results = new Dictionary<Guid, DynamicEffectValue>();
        var visiting = new HashSet<Guid>();
        var activation = new Dictionary<(Guid, string), bool?>();
        var checkingActivation = new HashSet<(Guid, string, bool)>();
        var conditions = new Dictionary<Guid, bool>();
        var mainInputs = new Dictionary<Guid, DynamicEffectValue>();
        var pictures = new HashSet<IPicture>(ReferenceEqualityComparer.Instance);
        var remaining = nodes.Keys.ToDictionary(id => id, _ => 0);
        void AddConsumer(string? binding)
        {
            if (EffectProviderOutputExtensions.TryParseOutputSourceId(binding, out var id, out _) && remaining.ContainsKey(id)) remaining[id]++;
        }
        foreach (var node in nodes.Values)
            foreach (var binding in node.Bindings.Where(p => !EffectProviderAnchorExtensions.IsOutputBindingKey(p.Key))) AddConsumer(binding.Value);
        AddConsumer(finalSource);
        if (stage == EffectStage.Render && renderOutput is { Enabled: true })
            foreach (var binding in renderOutput.AnchorsBindingState.Values) AddConsumer(binding);
        foreach (var node in nodes.Values.Where(n => n.Provider is IClipValueEndpointProvider { IsExport: true })) remaining[node.Provider.Id]++;
        void RetireInputs(DynamicEffectNode node)
        {
            foreach (var binding in node.Bindings.Where(p => !EffectProviderAnchorExtensions.IsOutputBindingKey(p.Key)))
            {
                if (!EffectProviderOutputExtensions.TryParseOutputSourceId(binding.Value, out var id, out _)
                    || !remaining.ContainsKey(id) || --remaining[id] != 0 || !results.TryGetValue(id, out var upstream)) continue;
                foreach (var picture in (upstream.Outputs?.Values ?? new[] { upstream.Value }).OfType<IHwAccelPicture>()
                    .Distinct<IHwAccelPicture>(ReferenceEqualityComparer.Instance))
                    if (pictures.Remove(picture)) picture.Dispose();
            }
        }
        object? output = null;
        bool returned = false;
        var previousBuffer = IRenderContext.CurrentFrameBuffer;
        float progress = clipProgress ?? (clip is not null && clip.GetEffectiveDuration() > 0
            ? Math.Clamp((float)((long)frame - clip.StartFrame) / clip.GetEffectiveDuration(), 0, 1)
            : Convert.ToSingle(ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInProgressProviderId) ?? 0f));
        using var clipScope = ValueProviderFrameContext.PushClip(clip ?? ValueProviderFrameContext.CurrentClip);
        using var scope = ValueProviderFrameContext.PushFrame(frame, progress, reuseComputed: true);
        var frameContext = new DynamicEffectFrame(this, source, clip, frame, width, height, relativeWidth, relativeHeight,
            initialPosition, preserveAspect, processFromCanvas, cancellationToken, afterNode, progress, clipProgress,
            vectorProgress, underPicture, mixturePixelMode) { Pictures = pictures };
        DynamicEffectValue Inactive(Guid id)
        {
            if (results.TryGetValue(id, out var cached)) return cached;
            Logger.LogDiagnostic($"Skipped inactive provider {id}, clip {clip?.Id}, frame {frame}.");
            return results[id] = frameContext.CreateInactive(nodes[id].Provider);
        }
        DynamicEffectValue MainValue(DynamicEffectNode node, bool useDefault)
        {
            if (mainInputs.TryGetValue(node.Provider.Id, out var cached)) return cached;
            DynamicEffectValue input;
            try { input = Resolve(node.Bindings.GetValueOrDefault(EffectProviderAnchorExtensions.InputKey)); }
            catch (Exception ex) when (useDefault && ex is not OperationCanceledException)
            {
                Logger.Log(ex, $"Resolve upstream of provider {node.Provider.Id}; using default value, clip {clip?.Id}, frame {frame}", this);
                input = new(null, initialPosition, preserveAspect);
            }
            return mainInputs[node.Provider.Id] = input;
        }
        bool Condition(DynamicEffectNode node, string key)
        {
            if (conditions.TryGetValue(node.Provider.Id, out bool cached)) return cached;
            var field = node.Fields[key];
            var input = Resolve(node.Bindings.GetValueOrDefault(key));
            var condition = frameContext.ReadCondition(node.Provider, key, field, input);
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
                bool hasEffects = node.Enabled && node.Effects.Any(e => frameContext.EffectProgress(e).HasValue);
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
                    var value = MainValue(node, true).Value;
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
        DynamicEffectValue Resolve(string? id)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (frameContext.TryResolveExternal(id, out var external)) return external;
            if (!EffectProviderOutputExtensions.TryParseOutputSourceId(id, out var guid, out var outputId))
                return new(null, initialPosition, preserveAspect);
            if (!nodes.TryGetValue(guid, out var node)) return new(null, initialPosition, preserveAspect);
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
        DynamicEffectValue EvaluateNode(Guid id)
        {
            if (results.TryGetValue(id, out var cached)) return cached;
            if (!visiting.Add(id)) throw new InvalidOperationException($"Effect cycle at {id}.");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (nodes[id].Provider is ConditionalLoopNodeEffectProvider or EffectVariableProvider)
                    throw new InvalidOperationException("Variables and conditional loops require dynamic calculation.");
                var result = new DynamicEffectNodeEvaluator(frameContext, id, new DelegateInputResolver(Resolve)).Evaluate();
                results[id] = result;
                RetireInputs(nodes[id]);
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                EffectProviderFailure.Attach(ex, id);
                Logger.Log(ex, $"Compute effect provider {id} of clip {clip?.Id}, frame {frame}", this);
                throw;
            }
            finally { visiting.Remove(id); }
        }
        DynamicEffectValue Terminal(string field, object fallback)
        {
            if (renderOutput is not { Enabled: true }) return new(fallback, initialPosition, preserveAspect);
            var binding = renderOutput.AnchorsBindingState.GetValueOrDefault(field);
            var result = string.IsNullOrWhiteSpace(binding) || binding == IEffectProvider.NoConnectionGUID.ToString()
                ? new DynamicEffectValue(renderOutput.Fields.GetValueOrDefault(field)?.GetGetter()(), initialPosition, preserveAspect) : Resolve(binding);
            return result.Value is null || result.IsInactive || result.Value is string { Length: 0 }
                ? new(fallback, initialPosition, preserveAspect) : result;
        }
        try
        {
            var exported = new Dictionary<Guid, object>();
            foreach (var (id, node) in nodes)
                if (requestedOutput != RenderOutputProvider.ShouldRenderKey && node.Provider is IClipValueEndpointProvider { IsExport: true } && node.Enabled
                    && node.Bindings.GetValueOrDefault(EffectProviderAnchorExtensions.InputKey) is { } binding
                    && binding != IEffectProvider.NoConnectionGUID.ToString())
                {
                    var value = Resolve(id.ToString()).Value;
                    if (ReferenceEquals(value, EffectBranchState.Inactive)) continue;
                    if (value is null) throw new ClipValueBindingException($"Endpoint {id} on clip {clip?.Id}, frame {frame}, produced no value.");
                    exported[id] = value;
                }
            DynamicEffectValue result;
            if (stage == EffectStage.Render)
            {
                bool shouldRender = !EffectParamConvert.TryConvertToBool(Terminal(RenderOutputProvider.ShouldRenderKey, true).Value, out bool visible) || visible;
                if (requestedOutput == RenderOutputProvider.ShouldRenderKey)
                    result = new(shouldRender, initialPosition, preserveAspect);
                else if (requestedOutput == RenderOutputProvider.AudioKey)
                {
                    var audio = Resolve(ValueProviderFrameContext.ClipAudioProviderId).Value!;
                    result = shouldRender ? Terminal(RenderOutputProvider.AudioKey, audio) : new(Silence((IAudioSamples<float>)audio), initialPosition, preserveAspect);
                }
                else
                {
                    result = requestedOutput == RenderOutputProvider.PositionKey
                        ? Terminal(RenderOutputProvider.PositionKey, initialPosition)
                        : shouldRender ? finalSource is not null ? Resolve(finalSource) : new(frameContext.ReadPicture(), initialPosition, preserveAspect)
                        : new(frameContext.Track(Timeline.CreateTransparentFrame(width, height, IPicture.PicturePixelMode.Float16Picture)), initialPosition, preserveAspect);
                    var position = Terminal(RenderOutputProvider.PositionKey, result.Position);
                    if (position.Value is not ClipPositionTuple pos) throw new InvalidOperationException("Render position output must be a ClipPositionTuple.");
                    result = result with { Position = initialPosition.Apply(pos), PreserveAspect = result.PreserveAspect && position.PreserveAspect };
                }
            }
            else result = finalSource is not null ? Resolve(finalSource) : new(source, initialPosition, preserveAspect);
            cancellationToken.ThrowIfCancellationRequested();
            var outputType = requestedOutput switch
            {
                RenderOutputProvider.AudioKey => EffectArgumentFieldType.AudioSample,
                RenderOutputProvider.PositionKey => EffectArgumentFieldType.SizeAndPosition,
                RenderOutputProvider.ShouldRenderKey => EffectArgumentFieldType.Boolean,
                _ => stage.GetContentType(),
            };
            if (!EffectFieldTypes.Accepts(outputType, result.Value) || result.Value is null)
                throw new InvalidOperationException($"Dynamic {stage} output on clip {clip?.Id} has invalid content.");
            output = result.Value;
            if (endpoints is not null)
            {
                var snapshots = exported.ToDictionary(p => p.Key, p => endpoints.Snapshot(p.Value));
                foreach (var (id, value) in snapshots) endpoints.Publish(clip!, frame, id, value);
                if (key is not null)
                    endpoints.SetComputed(key, new CachedFrame(new ContentFrameResult(endpoints.Snapshot(output), result.Position, result.PreserveAspect), snapshots));
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

    private static FloatAudioSamples Silence(IAudioSamples<float> source) => new()
    {
        Channels = Enumerable.Range(0, source.channelCount).Select(_ => new float[source.SampleCount]).ToArray(),
        SampleCount = source.SampleCount, SamplePerSecond = source.SamplePerSecond,
    };

    public static IPicture CopyPicture(IPicture source)
    {
        if (source is IHwAccelPicture gpu) return gpu.Retain();
        if (source is ISharedPicture shared) return shared.Snapshot();
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
