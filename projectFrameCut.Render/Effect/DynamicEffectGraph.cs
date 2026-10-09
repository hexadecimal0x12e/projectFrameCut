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
    private readonly object sync = new();
    private bool initialized;
    private bool disposed;
    private int activeFrames;

    public DynamicEffectGraph(IReadOnlyDictionary<Guid, IEffectProvider> providers, IReadOnlyDictionary<Guid, IEffect[]> effects)
        : this(providers, effects, null) { }

    internal DynamicEffectGraph(IReadOnlyDictionary<Guid, IEffectProvider> providers, IReadOnlyDictionary<Guid, IEffect[]> effects, InlineValueGraph? inlineValues)
    {
        this.inlineValues = inlineValues;
        nodes = providers.Values.Where(DynamicEffectBindings.IsGraphProvider).ToDictionary(p => p.Id,
            p => new Node(p, DynamicEffectBindings.InputFields(p), new(p.AnchorsBindingState), effects.GetValueOrDefault(p.Id) ?? [], p.Enabled));
        finalSource = providers.Values.FirstOrDefault(p => DynamicEffectBindings.IsGraphProvider(p) && p.IsFinalOutputSource())?.GetFinalOutputSourceId();
        Logger.Log($"Created dynamic effect graph with {nodes.Count} nodes; output {finalSource ?? "source"}.");
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

    public FrameResult Evaluate(IPicture source, IClip? clip, uint frame, int width, int height, int relativeWidth,
        int relativeHeight, ClipPositionTuple initialPosition, bool preserveAspect = true, bool processFromCanvas = false,
        CancellationToken cancellationToken = default, Action<IEffect, object?>? afterNode = null)
    {
        lock (sync)
        {
            Initialize();
            activeFrames++;
        }
        var results = new Dictionary<Guid, Result>();
        var visiting = new HashSet<Guid>();
        var pictures = new HashSet<IPicture>(ReferenceEqualityComparer.Instance);
        IPicture? output = null;
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
            if (!node.Provider.TryGetOutputField(outputId, out var field))
                throw new InvalidOperationException($"Provider {guid} requires a valid output port in '{id}'.");
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
                var input = Resolve(node.Bindings.GetValueOrDefault(EffectProviderAnchorExtensions.InputKey));
                var position = input.Value is IPicture ? input.Position : initialPosition;
                bool aspect = input.Value is IPicture ? input.PreserveAspect : preserveAspect;
                node.Fields.TryGetValue(EffectProviderAnchorExtensions.InputKey, out var main);
                object? value = input.Value ?? (main is null ? null : Fallback(main));
                IReadOnlyDictionary<string, object?>? outputs = null;
                var activeEffects = node.Enabled ? node.Effects.Select(e => (Effect: e, Progress: Progress(e)))
                    .Where(e => e.Progress.HasValue).ToArray() : [];
                if (activeEffects.Length == 0)
                {
                    if (main is null || !EffectFieldTypes.AreCompatible(main.FieldType, node.Provider.OutField.FieldType)) value = null;
                }
                else
                {
                    if (main is not null) value = EffectFieldTypes.ConvertInput(main.FieldType, value);
                    if (main is not null && (value is null && (main.FieldType.IsPicture() || main.FieldType.HasFlag(EffectArgumentFieldType.Mandatory))
                        || !EffectFieldTypes.Accepts(main.FieldType, value)))
                        throw new InvalidOperationException($"Invalid primary input on provider {id}.");
                    value = Copy(value);
                    var parameters = new Dictionary<string, object?>();
                    var values = new Dictionary<string, object?>();
                    foreach (var (key, field) in node.Fields.Where(p => p.Key != EffectProviderAnchorExtensions.InputKey))
                    {
                        var binding = node.Bindings.GetValueOrDefault(key);
                        object? parameter = EffectFieldTypes.ConvertInput(field.FieldType, Resolve(binding).Value ?? Fallback(field));
                        if (parameter is null && field.FieldType.HasFlag(EffectArgumentFieldType.Mandatory)
                            || !EffectFieldTypes.Accepts(field.FieldType, parameter))
                            throw new InvalidOperationException($"Invalid input '{key}' on provider {id}.");
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
                        afterNode?.Invoke(effect, value);
                    }
                }
                if (multiple) DynamicEffectBindings.ValidateOutputValues(node.Provider, outputs!);
                else if (!EffectFieldTypes.Accepts(node.Provider.OutField.FieldType, value))
                    throw new InvalidOperationException($"Provider {id} returned an incompatible output.");
                var result = new Result(multiple ? null : value, position, aspect, outputs);
                results[id] = result;
                if (outputs is not null)
                    foreach (var (key, outputValue) in outputs)
                        ValueProviderFrameContext.Set(EffectProviderOutputExtensions.CreateOutputSourceId(id, key), outputValue);
                else
                {
                    ValueProviderFrameContext.Set(id.ToString(), value);
                    if (!string.IsNullOrWhiteSpace(node.Provider.OutField.Id))
                        ValueProviderFrameContext.Set(EffectProviderOutputExtensions.CreateOutputSourceId(id, node.Provider.OutField.Id), value);
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
            var result = finalSource is not null ? Resolve(finalSource) : new Result(source, initialPosition, preserveAspect);
            cancellationToken.ThrowIfCancellationRequested();
            output = result.Value as IPicture ?? throw new InvalidOperationException($"Dynamic effect output on clip {clip?.Id} is not a picture.");
            return new(output, result.Position, result.PreserveAspect);
        }
        finally
        {
            IRenderContext.CurrentFrameBuffer = previousBuffer;
            try
            {
                foreach (var picture in pictures)
                {
                    if (ReferenceEquals(picture, output)) continue;
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
