using projectFrameCut.Drawing.Base;
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
    private readonly object sync = new();
    private bool initialized;
    private bool disposed;
    private int activeFrames;

    public DynamicEffectGraph(IReadOnlyDictionary<Guid, IEffectProvider> providers, IReadOnlyDictionary<Guid, IEffect[]> effects)
        : this(providers, effects, null) { }

    internal DynamicEffectGraph(IReadOnlyDictionary<Guid, IEffectProvider> providers, IReadOnlyDictionary<Guid, IEffect[]> effects,
        InlineValueGraph? inlineValues, EffectFieldTypeResolution? resolvedTypes = null)
    {
        this.inlineValues = inlineValues;
        types = resolvedTypes ?? inlineValues?.Types ?? new(providers);
        foreach (var provider in providers.Values) provider.ValidateOutputFields();
        nodes = providers.Values.Where(DynamicEffectBindings.IsGraphProvider).ToDictionary(p => p.Id,
            p => new Node(p, DynamicEffectBindings.InputFields(p), new(p.AnchorsBindingState), effects.GetValueOrDefault(p.Id) ?? [], p.Enabled));
        finalSource = providers.Values.FirstOrDefault(p => DynamicEffectBindings.IsGraphProvider(p) && p.IsFinalOutputSource())?.GetFinalOutputSourceId();
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
    private sealed record CachedFrame(FrameResult Frame, IReadOnlyDictionary<Guid, object> Exports);

    public FrameResult Evaluate(IPicture source, IClip? clip, uint frame, int width, int height, int relativeWidth,
        int relativeHeight, ClipPositionTuple initialPosition, bool preserveAspect = true, bool processFromCanvas = false,
        CancellationToken cancellationToken = default, Action<IEffect, object?>? afterNode = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var endpoints = clip is not null && ClipValueFrameContext.Current?.FrameIndex == frame && clip.ContainsFrame(frame)
            && nodes.Values.Any(n => n.Provider is IClipValueEndpointProvider)
            ? ClipValueFrameContext.Current : null;
        var key = endpoints is null ? null : new FrameKey(this, clip!.Id, frame, width, height, relativeWidth,
            relativeHeight, source.Width, source.Height, source.GetType(), initialPosition, preserveAspect, processFromCanvas, endpoints.IsTransformInput);
        if (key is not null && afterNode is null && endpoints!.TryGetComputed<CachedFrame>(key, out var cachedFrame))
        {
            foreach (var (id, value) in cachedFrame!.Exports) endpoints.Publish(clip!, frame, id, value);
            return cachedFrame.Frame with { Picture = CopyPicture(cachedFrame.Frame.Picture) };
        }
        lock (sync)
        {
            Initialize();
            activeFrames++;
        }
        var results = new Dictionary<Guid, Result>();
        var visiting = new HashSet<Guid>();
        var pictures = new HashSet<IPicture>(ReferenceEqualityComparer.Instance);
        IPicture? output = null;
        bool returned = false;
        var previousBuffer = IRenderContext.CurrentFrameBuffer;
        float progress = clip is not null && clip.GetEffectiveDuration() > 0
            ? Math.Clamp((float)((long)frame - clip.StartFrame) / clip.GetEffectiveDuration(), 0, 1)
            : Convert.ToSingle(ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInProgressProviderId) ?? 0f);
        using var scope = ValueProviderFrameContext.PushFrame(frame, progress, reuseComputed: true);

        IPicture Track(IPicture p)
        {
            if (!ReferenceEquals(p, source)) pictures.Add(p);
            return p;
        }
        object? Copy(object? value) => value is IPicture p ? Track(CopyPicture(p)) : value;
        Result Resolve(string? id)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (id == IEffectProvider.InputAnchorGUID.ToString()) return new(source, initialPosition, preserveAspect);
            if (id is ValueProviderFrameContext.BuiltInFrameProviderId or ValueProviderFrameContext.BuiltInProgressProviderId)
                return new(ValueProviderFrameContext.Get(id), initialPosition, preserveAspect);
            if (!EffectProviderOutputExtensions.TryParseOutputSourceId(id, out var guid, out var outputId)
                || !nodes.TryGetValue(guid, out var node)) return new(null, initialPosition, preserveAspect);
            if (!types.TryGetOutputField(node.Provider, outputId, out var field))
                throw new InvalidOperationException($"Provider {guid} requires a valid output port in '{id}'.");
            if (types.GetOutputType(node.Provider, field.Id).HasFlag(EffectArgumentFieldType.Any))
                throw new InvalidOperationException($"Provider {guid} output '{field.Id}' has an unresolved type, frame {frame}.");
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
            return field.FieldType.IsPicture() && value is not IPicture ? null : value;
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
                float? Progress(IEffect effect)
                {
                    if (effect is not IContinuousEffect continuous || !continuous.IsScoped && clip is null) return progress;
                    long start = continuous.IsScoped ? continuous.StartPoint : clip?.StartFrame ?? 0;
                    long end = continuous.IsScoped ? continuous.EndPoint : start + (clip?.GetEffectiveDuration() ?? 1);
                    return end > start && frame >= start && frame < end ? Math.Clamp((float)(frame - start) / (end - start), 0, 1) : null;
                }
                bool multiple = node.Provider is IMultipleOutputEffectProvider;
                if (multiple && (!node.Enabled || node.Effects.Length == 0 || !Progress(node.Effects[^1]).HasValue))
                    return results[id] = new(null, initialPosition, preserveAspect, new Dictionary<string, object?>());
                bool useDefault = node.Provider is IsUpstreamAvailableOrDefaultEffectProvider;
                var activeEffects = node.Enabled ? node.Effects.Select(e => (Effect: e, Progress: Progress(e)))
                    .Where(e => e.Progress.HasValue).ToArray() : [];
                Result input;
                try
                {
                    input = Resolve(node.Bindings.GetValueOrDefault(EffectProviderAnchorExtensions.InputKey));
                }
                catch (Exception ex) when (useDefault && activeEffects.Length > 0 && ex is not OperationCanceledException)
                {
                    Logger.Log(ex, $"Resolve upstream of provider {id}; using default value, clip {clip?.Id}, frame {frame}", this);
                    input = new(null, initialPosition, preserveAspect);
                }
                if (useDefault && activeEffects.Length > 0 && input.Value is null)
                    input = Resolve(node.Bindings.GetValueOrDefault("DefaultValue"));
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
                    if (main is not null && value is null && main.FieldType.IsPicture())
                        throw new InvalidOperationException($"Invalid primary input on provider {id}.");
                    if (main is not null) types.ValidateValue(node.Provider, EffectProviderAnchorExtensions.InputKey, true, main, value, frame);
                    string? selectedKey = node.Provider is IfConditionNodeEffectProvider
                        ? (value is true ? "TrueValue" : "FalseValue") : null;
                    var selectedInput = selectedKey is null ? null : Resolve(node.Bindings.GetValueOrDefault(selectedKey));
                    if (selectedInput?.Value is IPicture)
                    {
                        position = selectedInput.Position;
                        aspect = selectedInput.PreserveAspect;
                    }
                    value = Copy(value);
                    var parameters = new Dictionary<string, object?>();
                    var values = new Dictionary<string, object?>();
                    foreach (var (key, field) in node.Fields.Where(p => p.Key != EffectProviderAnchorExtensions.InputKey))
                    {
                        if (useDefault && key == "DefaultValue") continue;
                        if (selectedKey is not null && (key is "TrueValue" or "FalseValue") && key != selectedKey) continue;
                        var binding = node.Bindings.GetValueOrDefault(key);
                        object? parameter;
                        if (key == selectedKey) parameter = selectedInput!.Value;
                        else if (node.Provider is EqualityComparisonNodeEffectProvider && (key is "A" or "B"))
                            parameter = Resolve(binding).Value;
                        else parameter = Resolve(binding).Value ?? Fallback(field);
                        // Numeric comparison retains integer/decimal precision instead of coercing operands to float.
                        if (node.Provider is not NumericComparisonNodeEffectProvider || (key is not "A" and not "B"))
                            parameter = EffectFieldTypes.ConvertInput(field.FieldType, parameter);
                        types.ValidateValue(node.Provider, key, true, field, parameter, frame);
                        parameters[key] = Copy(parameter);
                        if (!string.IsNullOrEmpty(binding)) values[binding] = parameters[key];
                    }
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
                if (activeEffects.Length > 0) afterNode?.Invoke(activeEffects[^1].Effect, multiple ? outputs : value);
                var result = new Result(multiple ? null : value, position, aspect, outputs);
                results[id] = result;
                if (outputs is not null)
                    foreach (var (key, outputValue) in outputs)
                        ValueProviderFrameContext.Set(EffectProviderOutputExtensions.CreateOutputSourceId(id, key), outputValue);
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
                    var value = EvaluateNode(id).Value;
                    if (value is null) throw new ClipValueBindingException($"Endpoint {id} on clip {clip?.Id}, frame {frame}, produced no value.");
                    exported[id] = value;
                }
            var result = finalSource is not null ? Resolve(finalSource) : new Result(source, initialPosition, preserveAspect);
            cancellationToken.ThrowIfCancellationRequested();
            output = result.Value as IPicture ?? throw new InvalidOperationException($"Dynamic effect output on clip {clip?.Id} is not a picture.");
            if (endpoints is not null)
            {
                var snapshots = exported.ToDictionary(p => p.Key, p => endpoints.Snapshot(p.Value));
                foreach (var (id, value) in snapshots) endpoints.Publish(clip!, frame, id, value);
                endpoints.SetComputed(key!, new CachedFrame(new FrameResult((IPicture)endpoints.Snapshot(output), result.Position, result.PreserveAspect), snapshots));
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
