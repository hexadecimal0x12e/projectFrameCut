using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.RenderAPIBase.Context;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

/// <summary>
/// Computes one node of an effect graph from already resolved inputs.
/// </summary>
/// <remarks>
/// The evaluator never schedules or traverses the graph by itself: every input lookup goes through
/// <see cref="IDynamicEffectInputResolver"/>. The legacy pull based evaluation passes a resolver that
/// recursively evaluates upstream nodes, while the dynamic calculation engine passes the inputs that
/// were delivered to the node by the branch scheduler. Keeping the compute path shared means both
/// modes apply exactly the same conversion, validation and picture lifetime rules.
/// </remarks>
internal sealed class DynamicEffectNodeEvaluator(DynamicEffectFrame frame, Guid id, IDynamicEffectInputResolver resolver)
{
    private readonly DynamicEffectNode node = frame.Nodes[id];
    private readonly Dictionary<string, DynamicEffectValue> mainInputs = new();
    private readonly Dictionary<string, bool> conditions = new();

    public DynamicEffectValue Evaluate()
    {
        var provider = node.Provider;
        var initialPosition = frame.InitialPosition;
        var preserveAspect = frame.PreserveAspect;
        var width = frame.Width;
        var height = frame.Height;
        var relativeWidth = frame.RelativeWidth;
        var relativeHeight = frame.RelativeHeight;
        var cancellationToken = frame.CancellationToken;
        try
        {
            if (provider is MixtureSourceProvider mixtureSource)
            {
                if (!node.Enabled) throw new InvalidOperationException($"Mixture source {id} is disabled.");
                return mixtureSource.IsUnder
                    ? new(frame.UnderPicture ?? throw new InvalidOperationException("The underlying picture is unavailable."),
                        new(0, 0, width, height, false), false)
                    : new(frame.Source, initialPosition, preserveAspect);
            }
            if (provider is IClipValueEndpointProvider { IsExport: false } endpointInput)
            {
                if (!node.Enabled || frame.Clip is null || ClipValueFrameContext.Current is null)
                    throw new ClipValueBindingException($"Endpoint input {id} on clip {frame.Clip?.Id}, frame {frame.Frame}, is unavailable.");
                object? imported = frame.Copy(ClipValueFrameContext.Current.Read(frame.Clip, frame.Frame, endpointInput));
                var field = frame.Types.GetOutputField(provider);
                frame.Types.ValidateValue(provider, field.Id, false, field, imported, frame.Frame);
                ValueProviderFrameContext.Set(id.ToString(), imported);
                ValueProviderFrameContext.Set(EffectProviderOutputExtensions.CreateOutputSourceId(id, field.Id), imported);
                foreach (var effect in node.Effects) frame.AfterNode?.Invoke(effect, imported);
                return new(imported, initialPosition, preserveAspect);
            }
            bool multiple = provider is IMultipleOutputEffectProvider;
            if (multiple && (!node.Enabled || node.Effects.Length == 0 || !frame.EffectProgress(node.Effects[^1]).HasValue))
                return new(null, initialPosition, preserveAspect, new Dictionary<string, object?>());
            bool useDefault = provider is IsUpstreamAvailableOrDefaultEffectProvider;
            var activeEffects = node.Enabled ? node.Effects.Select(e => (Effect: e, Progress: frame.EffectProgress(e)))
                .Where(e => e.Progress.HasValue).ToArray() : [];
            var input = MainInput(useDefault && activeEffects.Length > 0);
            if (provider.TypeOfEffect == EffectType.MixtureProvider && activeEffects.Length == 0)
                return resolver.Resolve(node.Bindings.GetValueOrDefault(MixtureProviderBase.UpperInputKey));
            if (useDefault && activeEffects.Length > 0 && (input.Value is null || input.IsInactive))
                input = resolver.Resolve(node.Bindings.GetValueOrDefault("DefaultValue"));
            if (input.IsInactive) return frame.CreateInactive(provider);
            if (provider is IClipValueEndpointProvider { IsExport: true } && input.Value is null)
                throw new ClipValueBindingException($"Endpoint {id} on clip {frame.Clip?.Id}, frame {frame.Frame}, has no source value.");
            var position = input.Value is ClipPositionTuple inputPosition ? initialPosition.Apply(inputPosition)
                : input.Value is IPicture ? input.Position : initialPosition;
            bool aspect = input.Value is IPicture ? input.PreserveAspect : preserveAspect;
            node.Fields.TryGetValue(EffectProviderAnchorExtensions.InputKey, out var main);
            object? value = input.Value ?? (main is null || useDefault ? null : frame.Fallback(main));
            IReadOnlyDictionary<string, object?>? outputs = null;
            if (activeEffects.Length == 0)
            {
                if (main is null || (main.FieldType.HasFlag(EffectArgumentFieldType.Any)
                    ? !EffectFieldTypes.Accepts(frame.Types.GetOutputType(provider), value, frame.Types.GetOutputContract(provider))
                    : !EffectFieldTypes.AreCompatible(frame.Types.GetInputType(provider, EffectProviderAnchorExtensions.InputKey),
                        frame.Types.GetOutputType(provider), frame.Types.GetInputContract(provider, EffectProviderAnchorExtensions.InputKey),
                        frame.Types.GetOutputContract(provider)))) value = null;
                if (value is IHwAccelPicture) value = frame.Copy(value);
            }
            else
            {
                if (main is not null)
                {
                    value = EffectFieldTypes.ConvertInput(main.FieldType, value, false);
                    if (value is IHwAccelPicture hardwareInput) frame.Track(hardwareInput);
                }
                if (main is not null && value is null && main.FieldType.IsContent())
                    throw new InvalidOperationException($"Invalid primary input on provider {id}.");
                if (main is not null)
                    frame.Types.ValidateValue(provider, EffectProviderAnchorExtensions.InputKey, true, main, value, frame.Frame);
                string? selectedKey = provider is IfConditionNodeEffectProvider
                    ? (ReadCondition(IfConditionNodeEffectProvider.ConditionKey) ? "TrueValue" : "FalseValue") : null;
                var selectedInput = selectedKey is null ? null : resolver.Resolve(node.Bindings.GetValueOrDefault(selectedKey));
                if (selectedInput?.IsInactive is true) return frame.CreateInactive(provider);
                if (provider is IfConditionNodeEffectProvider && selectedInput is { Value: null })
                {
                    // 选中的分支没有接入任何值：整条条件支路视为未激活，而不是把 null 传给下游的强制画面输入。
                    Logger.LogDiagnostic($"If-condition provider {id} selected an empty branch, clip {frame.Clip?.Id}, frame {frame.Frame}.");
                    return frame.CreateInactive(provider);
                }
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
                    if (provider is ConditionComputeNodeEffectProvider && key == ConditionComputeNodeEffectProvider.ConditionKey
                        || provider is IfConditionNodeEffectProvider && key == IfConditionNodeEffectProvider.ConditionKey)
                        parameter = ReadCondition(key);
                    else
                    {
                        var resolved = key == selectedKey ? selectedInput!.Value : resolver.Resolve(binding).Value;
                        if (ReferenceEquals(resolved, EffectBranchState.Inactive)) return frame.CreateInactive(provider);
                        parameter = key == selectedKey || provider is EqualityComparisonNodeEffectProvider && (key is "A" or "B")
                            ? resolved : resolved ?? frame.Fallback(field);
                    }
                    // Preserve operand types for numeric comparison and overload selection.
                    if (!(provider is NumericComparisonNodeEffectProvider && (key is "A" or "B")
                        || provider is AdvancedMathEffectProvider && (key is "A" or "B" or "C")))
                        parameter = EffectFieldTypes.ConvertInput(field.FieldType, parameter, false);
                    if (parameter is IHwAccelPicture hardwareParameter) frame.Track(hardwareParameter);
                    frame.Types.ValidateValue(provider, key, true, field, parameter, frame.Frame);
                    parameters[key] = activeEffects[0].Effect is IHwAccelPictureEffect && parameter is IPicture and not IHwAccelPicture
                        ? parameter : frame.Copy(parameter);
                    if (!string.IsNullOrEmpty(binding)) values[binding] = parameters[key];
                }
                if (activeEffects[0].Effect is not IHwAccelPictureEffect || value is not IPicture || value is IHwAccelPicture)
                    value = frame.Copy(value);
                var inputTypes = node.Fields.ToDictionary(p => p.Key, p => p.Value.FieldType);
                foreach (var field in node.Fields)
                    if (node.Bindings.TryGetValue(field.Key, out var binding)) inputTypes[binding] = field.Value.FieldType;
                using var diagnostic = RenderFrameDiagnostics.Current?.Measure($"Provider {id} ({provider.GetType().Name})",
                    $"Provider ({provider.GetType().Name})");
                using var nodeScope = ValueProviderFrameContext.PushValues(values);
                foreach (var (effect, nodeProgress) in activeEffects)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var effectTiming = RenderFrameDiagnostics.Current?.Measure(
                        $"Node effect {effect.Name} ({effect.Id}, {effect.GetType().Name})", $"Node effect ({effect.GetType().Name})");
                    if (frame.ProcessFromCanvas && effect.CanProcessFromCanvas && value is IPicture picture
                            && position.TargetWidth > 0 && position.TargetHeight > 0
                            && (picture.Width != position.TargetWidth || picture.Height != position.TargetHeight))
                    {
                        value = frame.Track(EffectPictureProcessing.Resize(picture, position.TargetWidth, position.TargetHeight, aspect));
                        if (!parameters.Values.Any(p => ReferenceEquals(p, picture))) frame.ReleaseDeviceInput(picture, value);
                    }
                    var effectInput = value;
                    IRenderContext.CurrentFrameBuffer = value as IPicture;
                    var context = new EffectExecutionContext
                    {
                        Input = value, Parameters = parameters, Clip = frame.Clip, FrameIndex = frame.Frame,
                        AudioSampleIndex = frame.AudioOffset(), Progress = nodeProgress!.Value, ClipProgress = frame.Progress,
                        TargetWidth = width, TargetHeight = height, RelativeWidth = relativeWidth, RelativeHeight = relativeHeight,
                        CancellationToken = cancellationToken, InputTypes = inputTypes,
                    };
                    object? computed;
                    object? Compute()
                    {
                        if (effect is not IMixture mixer) return EffectPictureProcessing.Compute(effect, context, values);
                        if (parameters.GetValueOrDefault(MixtureProviderBase.UpperInputKey) is not IPicture upper
                            || parameters.GetValueOrDefault(MixtureProviderBase.LowerInputKey) is not IPicture lower)
                            throw new InvalidOperationException($"Mixture {id} requires upper and lower pictures.");
                        var upperPosition = resolver.Resolve(node.Bindings.GetValueOrDefault(MixtureProviderBase.UpperInputKey)).Position;
                        var lowerPosition = resolver.Resolve(node.Bindings.GetValueOrDefault(MixtureProviderBase.LowerInputKey)).Position;
                        upper = frame.Track(EffectRuntimeDefaults.ApplyOpacity(upper, upperPosition.Opacity));
                        lower = frame.Track(EffectRuntimeDefaults.ApplyOpacity(lower, lowerPosition.Opacity));
                        var pixelMode = frame.MixturePixelMode ?? lower.BitPerPixel;
                        if (lowerPosition.TargetX != 0 || lowerPosition.TargetY != 0 || lower.Width != width || lower.Height != height)
                            lower = frame.Track(EffectPictureProcessing.Mix(ClassicOverlayMixture.Default,
                                frame.Track(PictureFP16.GenerateSolidColor(width, height, Half.Zero, Half.Zero, Half.Zero, Half.Zero)),
                                lower, pixelMode, lowerPosition.TargetX, lowerPosition.TargetY, width, height));
                        position = new(0, 0, width, height, false);
                        return EffectPictureProcessing.Mix(mixer, lower, upper, pixelMode,
                            upperPosition.TargetX, upperPosition.TargetY, width, height);
                    }
                    computed = effect is not IMixture && FrameHashContext.Current is { } hashes
                        ? hashes.ComputeEffect(effect, context, Compute, provider.IsDeterministic, provider, Array.IndexOf(node.Effects, effect))
                        : Compute();
                    if (effect is IMultipleOutputEffect)
                    {
                        outputs = computed as IReadOnlyDictionary<string, object?>
                            ?? throw new InvalidOperationException($"Provider {id} returned no output map.");
                        foreach (var pictureOutput in outputs.Values.OfType<IPicture>()) frame.Track(pictureOutput);
                    }
                    if (effect is IClipPositionProvider or IContinuousClipPositionProvider)
                    {
                        position = position.Apply(computed is ClipPositionTuple pos ? pos
                            : throw new InvalidOperationException($"Position effect {effect.Id} did not return a position."));
                        value = position;
                        if (effect is IClipPositionProvider p) aspect &= p.PreserveAspectRatio;
                        if (effect is IContinuousClipPositionProvider cp) aspect &= cp.PreserveAspectRatio;
                    }
                    else value = computed;
                    if (value is IPicture rendered) frame.Track(rendered);
                    if (!parameters.Values.Any(p => ReferenceEquals(p, effectInput)))
                        frame.ReleaseDeviceInput(effectInput, value, outputs);
                }
                foreach (var parameter in parameters.Values) frame.ReleaseDeviceInput(parameter, value, outputs);
            }
            if (multiple) DynamicEffectBindings.ValidateOutputValues(provider, outputs!, frame.Types, frame.Frame);
            else
            {
                var field = frame.Types.GetOutputField(provider);
                frame.Types.ValidateValue(provider, field.Id, false, field, value, frame.Frame);
            }
            if (activeEffects.Length > 0 && frame.AfterNode is not null)
            {
                object? observed = multiple ? outputs : value;
                if (outputs is not null && outputs.Values.Any(v => ReferenceEquals(v, EffectBranchState.Inactive)))
                    observed = outputs.ToDictionary(p => p.Key,
                        p => ReferenceEquals(p.Value, EffectBranchState.Inactive) ? null : p.Value);
                frame.AfterNode(activeEffects[^1].Effect, observed);
            }
            var result = new DynamicEffectValue(multiple ? null : value, position, aspect, outputs);
            if (outputs is not null)
                foreach (var (key, outputValue) in outputs)
                    ValueProviderFrameContext.Set(EffectProviderOutputExtensions.CreateOutputSourceId(id, key),
                        ReferenceEquals(outputValue, EffectBranchState.Inactive) ? null : outputValue);
            else
            {
                ValueProviderFrameContext.Set(id.ToString(), value);
                var field = frame.Types.GetOutputField(provider);
                if (!string.IsNullOrWhiteSpace(field.Id))
                    ValueProviderFrameContext.Set(EffectProviderOutputExtensions.CreateOutputSourceId(id, field.Id), value);
            }
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EffectProviderFailure.Attach(ex, id);
            Logger.Log(ex, $"Compute effect provider {id} of clip {frame.Clip?.Id}, frame {frame.Frame}", this);
            throw;
        }
    }

    private DynamicEffectValue MainInput(bool useDefault)
    {
        if (mainInputs.TryGetValue(EffectProviderAnchorExtensions.InputKey, out var cached)) return cached;
        DynamicEffectValue input;
        try { input = resolver.Resolve(node.Bindings.GetValueOrDefault(EffectProviderAnchorExtensions.InputKey)); }
        catch (Exception ex) when (useDefault && ex is not OperationCanceledException)
        {
            Logger.Log(ex, $"Resolve upstream of provider {id}; using default value, clip {frame.Clip?.Id}, frame {frame.Frame}", this);
            input = new(null, frame.InitialPosition, frame.PreserveAspect);
        }
        return mainInputs[EffectProviderAnchorExtensions.InputKey] = input;
    }

    private bool ReadCondition(string key)
    {
        if (conditions.TryGetValue(key, out bool cached)) return cached;
        var field = node.Fields[key];
        var input = resolver.Resolve(node.Bindings.GetValueOrDefault(key));
        var condition = frame.ReadCondition(node.Provider, key, field, input);
        Logger.LogDiagnostic($"Condition provider {id} selected {condition}, clip {frame.Clip?.Id}, frame {frame.Frame}.");
        return conditions[key] = condition;
    }
}
