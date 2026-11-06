using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.RenderAPIBase.Context;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using projectFrameCut.Render.Rendering;
using static projectFrameCut.Render.RenderAPIBase.EffectAndMixture.IFullDynamicValueProviderEffect;

namespace projectFrameCut.Render.Effect;

internal sealed record FullDynamicValue(FullDynamicInputState State, object? Value, ClipPositionTuple Position,
    bool PreserveAspect, Exception? Error = null);

internal sealed class FullDynamicEffectExecution : IDisposable
{
    private sealed class Scope
    {
        public Dictionary<(Guid, string), FullDynamicValue> Outputs { get; } = [];
        public Dictionary<(Guid, string), FullDynamicValue> Overrides { get; } = [];
        public Dictionary<Guid, Invocation> Invocations { get; } = [];
        public HashSet<(Guid, string)> Visiting { get; } = [];
    }

    private sealed class Invocation
    {
        public Guid Id { get; } = Guid.NewGuid();
        public List<Action> Releases { get; } = [];
        public Dictionary<string, FullDynamicValue> Inputs { get; } = [];
        public Dictionary<object, object?> Computed { get; } = [];
        public HashSet<string> Requested { get; } = [];
        public ClipPositionTuple Position;
        public bool PreserveAspect;
    }

    private readonly FullDynamicEffectPipeline pipeline;
    private readonly EffectExecutionContext frame;
    private readonly object source;
    private readonly IPicture? underPicture;
    private readonly ClipPositionTuple initialPosition;
    private readonly bool preserveAspect;
    private readonly bool processFromCanvas;
    private readonly IPicture.PicturePixelMode? pixelMode;
    private readonly Action<IEffect, object?>? observer;
    private readonly Action<Guid> countStep;
    private readonly Scope root = new();
    private readonly Dictionary<Guid, object?> variables = [];
    private readonly List<Scope> scopes = [];
    private IPicture? sourcePicture;
    private IAudioSamples<float>? audio;
    public HashSet<IPicture> Pictures { get; } = new(ReferenceEqualityComparer.Instance);

    public FullDynamicEffectExecution(FullDynamicEffectPipeline pipeline, EffectExecutionContext frame, object source,
        ClipPositionTuple initialPosition, bool preserveAspect, bool processFromCanvas,
        IPicture? underPicture, IPicture.PicturePixelMode? pixelMode, Action<IEffect, object?>? observer)
    {
        this.pipeline = pipeline;
        this.frame = frame;
        this.source = source;
        this.initialPosition = initialPosition;
        this.preserveAspect = preserveAspect;
        this.processFromCanvas = processFromCanvas;
        this.underPicture = underPicture;
        this.pixelMode = pixelMode;
        this.observer = observer;
        countStep = DynamicEffectCalculation.CreateCounter(frame.Clip, frame.FrameIndex, pipeline.Stage, DynamicEffectCalculation.MaxSteps);
        scopes.Add(root);
    }

    public FullDynamicValue Resolve(string? binding) => Resolve(binding, root);
    private readonly Dictionary<Guid, HashSet<Guid>> feedbackDependents = [];
    private HashSet<Guid> FeedbackDependents(FullDynamicEffectNode loop, FullDynamicFeedback feedback)
    {
        if (feedbackDependents.TryGetValue(loop.State.Id, out var cached)) return cached;
        var pending = new Queue<(Guid, string?)>();
        var dependent = new HashSet<Guid>();
        pending.Enqueue((loop.State.Id, feedback.BodyOutput));
        while (pending.TryDequeue(out var source))
            foreach (var node in pipeline.Nodes.Values)
                if (node.State.Id != loop.State.Id && node.State.Bindings.Any(p => node.State.Fields.ContainsKey(p.Key)
                    && EffectProviderOutputExtensions.TryParseOutputSourceId(p.Value, out var id, out var port)
                    && id == source.Item1 && (source.Item2 is null || source.Item2 == port)) && dependent.Add(node.State.Id))
                    pending.Enqueue((node.State.Id, null));
        return feedbackDependents[loop.State.Id] = dependent;
    }
    public void RunSideEffects() => RunSideEffects(root, null);
    private void RunSideEffects(Scope scope, HashSet<Guid>? included)
    {
        var excluded = pipeline.Nodes.Values.Select(n => (Node: n, Feedback: n.Computations.OfType<IFullDynamicSchedulingEffect>()
            .Select(e => e.Feedback).FirstOrDefault(f => f is not null))).Where(p => p.Feedback is not null && (included is null || included.Contains(p.Node.State.Id)))
            .SelectMany(p => FeedbackDependents(p.Node, p.Feedback!)).ToHashSet();
        var pending = new Queue<FullDynamicEffectNode>(pipeline.Nodes.Values.Where(n => n.State.Enabled
            && !excluded.Contains(n.State.Id) && (included is null || included.Contains(n.State.Id))
            && n.Computations.OfType<IFullDynamicSchedulingEffect>().Any(e => e.HasSideEffects)
            && (!n.State.Fields.ContainsKey(EffectProviderAnchorExtensions.InputKey)
                || n.State.Bindings.GetValueOrDefault(EffectProviderAnchorExtensions.InputKey) is { } binding
                    && binding != IEffectProvider.NoConnectionGUID.ToString())));
        while (pending.TryDequeue(out var node))
            Require(Resolve(EffectProviderOutputExtensions.CreateOutputSourceId(node.State.Id, node.State.Outputs.Keys.First()), scope));
    }

    public object ReadPicture() => source is IPicture or VectorPicture ? source : sourcePicture ??= Track(
        frame.Clip?.GetFrameRelativeToStartPointOfSource(frame.Clip.GetRelativeFrameIndex(frame.FrameIndex) ?? 0,
            frame.TargetWidth, frame.TargetHeight, IPicture.PicturePixelMode.Float16Picture)
        ?? throw new InvalidOperationException("A clip is required to read the source picture."));

    public IAudioSamples<float> ReadAudio() => audio ??= source as IAudioSamples<float>
        ?? EffectProcessing.CurrentAudioSource?.ReadClipFrame(frame.Clip!, frame.FrameIndex, frame.CancellationToken)
        ?? new FloatAudioSamples { Channels = [[], []], SampleCount = 0, SamplePerSecond = 48000 };

    public IPicture Track(IPicture picture)
    {
        if (!ReferenceEquals(picture, source) && !ReferenceEquals(picture, underPicture)) Pictures.Add(picture);
        return picture;
    }

    private object? Copy(object? value) => value is IPicture picture ? Track(EffectPictureProcessing.Copy(picture))
        : value is VectorPicture vector ? VectorPictureEffectProcessing.Copy(vector) : value;
    private FullDynamicValue Value(object? value) => new(FullDynamicInputState.Available, value, initialPosition, preserveAspect);

    private FullDynamicValue Resolve(string? binding, Scope scope)
    {
        frame.CancellationToken.ThrowIfCancellationRequested();
        if (binding == IEffectProvider.InputAnchorGUID.ToString()) return Value(ReadPicture());
        if (binding == ValueProviderFrameContext.ClipPositionProviderId) return Value(initialPosition);
        if (binding == ValueProviderFrameContext.ClipAudioProviderId) return Value(ReadAudio());
        if (binding is not null && ValueProviderFrameContext.IsBuiltInSource(binding)) return Value(ValueProviderFrameContext.Get(binding));
        if (!EffectProviderOutputExtensions.TryParseOutputSourceId(binding, out var id, out var port)) return Value(null);
        if (!pipeline.Nodes.TryGetValue(id, out var node))
            return pipeline.ContentSources.Contains(id) ? Value(source)
                : new(FullDynamicInputState.Failed, null, initialPosition, preserveAspect,
                    new InvalidOperationException($"Source {binding} is unavailable in {pipeline.Stage}."));
        port ??= node.State.Outputs.Count == 1 ? node.State.Outputs.Keys.Single()
            : throw new InvalidOperationException($"Source {binding} requires an output port.");
        var key = (id, port);
        if (scope.Overrides.TryGetValue(key, out var routed)) return routed;
        if (scope.Outputs.TryGetValue(key, out var cached)) return cached;
        if (!scope.Visiting.Add(key)) throw new InvalidOperationException($"Unscoped effect cycle at {id}/{port}, frame {frame.FrameIndex}.");
        try
        {
            countStep(id);
            FullDynamicValue result;
            if (node.Computations.OfType<IFullDynamicSchedulingEffect>().Select(e => e.Feedback).FirstOrDefault(f => f is not null) is { } feedback)
                result = ExecuteFeedback(node, port, feedback, scope);
            else result = Compute(node, port, scope);
            scope.Outputs[key] = result;
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DynamicCalculationLimitException)
        {
            EffectProviderFailure.Attach(ex, id, port);
            var failed = new FullDynamicValue(FullDynamicInputState.Failed, null, initialPosition, preserveAspect, ex);
            scope.Outputs[key] = failed;
            return failed;
        }
        finally { scope.Visiting.Remove(key); }
    }

    private FullDynamicValue ExecuteFeedback(FullDynamicEffectNode node, string port, FullDynamicFeedback feedback, Scope outer)
    {
        var input = ReadInput(node, feedback.StartInput, outer);
        if (!node.State.Enabled) return input with { State = FullDynamicInputState.Inactive, Value = null };
        while (input.State == FullDynamicInputState.Available)
        {
            frame.CancellationToken.ThrowIfCancellationRequested();
            countStep(node.State.Id);
            var iteration = new Scope();
            foreach (var pair in outer.Overrides) iteration.Overrides[pair.Key] = pair.Value;
            iteration.Overrides[(node.State.Id, feedback.BodyOutput)] = input;
            iteration.Overrides[(node.State.Id, feedback.ExitOutput)] = input with { State = FullDynamicInputState.Inactive, Value = null };
            scopes.Add(iteration);
            try
            {
                var invocation = GetInvocation(node, iteration);
                invocation.Inputs[EffectProviderAnchorExtensions.InputKey] = input;
                invocation.Inputs[feedback.StartInput] = input;
                var exit = Compute(node, feedback.ExitOutput, iteration);
                if (exit.State == FullDynamicInputState.Available)
                {
                    outer.Outputs[(node.State.Id, feedback.ExitOutput)] = exit;
                    outer.Outputs[(node.State.Id, feedback.BodyOutput)] = exit with { State = FullDynamicInputState.Inactive, Value = null };
                    return outer.Outputs[(node.State.Id, port)];
                }
                var body = Compute(node, feedback.BodyOutput, iteration);
                Require(body);
                iteration.Overrides[(node.State.Id, feedback.BodyOutput)] = body;
                RunSideEffects(iteration, FeedbackDependents(node, feedback));
                input = ReadInput(node, feedback.FeedbackInput, iteration);
                Require(input);
            }
            finally
            {
                scopes.Remove(iteration);
                foreach (var call in iteration.Invocations.Values) Release(call);
                ReleasePictures(input);
            }
        }
        return input;
    }

    private Invocation GetInvocation(FullDynamicEffectNode node, Scope scope)
    {
        if (!scope.Invocations.TryGetValue(node.State.Id, out var invocation))
            scope.Invocations[node.State.Id] = invocation = new() { Position = initialPosition, PreserveAspect = preserveAspect };
        return invocation;
    }

    private object? StaticInput(FullDynamicEffectNode node, string port)
    {
        var value = node.State.Parameters.GetValueOrDefault(port);
        var field = node.State.Fields.GetValueOrDefault(port);
        return value is string { Length: 0 } && field is not null && (field.FieldType.HasFlag(EffectArgumentFieldType.Any)
            || field.FieldType.IsContent()) ? null : value;
    }

    private FullDynamicValue ReadInput(FullDynamicEffectNode node, string port, Scope scope)
    {
        var invocation = GetInvocation(node, scope);
        if (invocation.Inputs.TryGetValue(port, out var cached)) return cached;
        var field = node.State.Fields.GetValueOrDefault(port);
        var binding = node.State.Bindings.GetValueOrDefault(port);
        var input = !string.IsNullOrWhiteSpace(binding) && binding != IEffectProvider.NoConnectionGUID.ToString()
            ? Resolve(binding, scope) : Value(StaticInput(node, port));
        if (input.State == FullDynamicInputState.Available && field is not null)
        {
            // Numeric overloads and equality consume the original runtime value. Conversion is an effect policy.
            pipeline.Types.ValidateValue(node.Provider, port, true, field, input.Value, frame.FrameIndex);
        }
        invocation.Inputs[port] = input;
        if (input.Value is IPicture or VectorPicture)
        {
            invocation.Position = input.Position;
            invocation.PreserveAspect = input.PreserveAspect;
        }
        return input;
    }

    private FullDynamicValue Compute(FullDynamicEffectNode node, string port, Scope scope)
    {
        if (!node.State.Enabled)
            return node.State.Outputs.Count == 1 && node.State.Fields.ContainsKey(EffectProviderAnchorExtensions.InputKey)
                ? ReadInput(node, EffectProviderAnchorExtensions.InputKey, scope)
                : Value(null) with { State = FullDynamicInputState.Inactive };
        if (node.Provider is Compose.MixtureSourceProvider mixtureSource)
            return mixtureSource.IsUnder
                ? new(FullDynamicInputState.Available, underPicture ?? throw new InvalidOperationException("Underlying picture unavailable."),
                    new(0, 0, frame.TargetWidth, frame.TargetHeight, false), false)
                : Value(source);
        if (node.Provider is IClipValueEndpointProvider { IsExport: false } endpoint)
            return Value(Copy(ClipValueFrameContext.Current?.Read(frame.Clip!, frame.FrameIndex, endpoint)
                ?? throw new ClipValueBindingException($"Endpoint {node.State.Id} is unavailable.")));
        if (node.Provider is IClipValueEndpointProvider { IsExport: true }) return ReadInput(node, EffectProviderAnchorExtensions.InputKey, scope);
        if (!node.State.Enabled || node.Effects.Length == 0)
            return node.State.Fields.ContainsKey(EffectProviderAnchorExtensions.InputKey)
                ? ReadInput(node, EffectProviderAnchorExtensions.InputKey, scope)
                : Value(null) with { State = FullDynamicInputState.Inactive };
        var invocation = GetInvocation(node, scope);
        var services = new Services(this, node, scope, invocation);
        object? result = null;
        for (int i = 0; i < node.Computations.Length; i++)
        {
            if (!IsActive(node.Effects[i]))
            {
                if (node.State.Outputs.Count > 1) return Value(null) with { State = FullDynamicInputState.Inactive };
                if (i == 0) result = Require(ReadInput(node, EffectProviderAnchorExtensions.InputKey, scope));
                continue;
            }
            var effect = node.Computations[i];
            var inputTypes = node.State.Fields.Keys.ToDictionary(k => k, k => pipeline.Types.GetInputType(node.Provider, k));
            if (invocation.Inputs.ContainsKey(EffectProviderAnchorExtensions.InputKey) && !inputTypes.ContainsKey(EffectProviderAnchorExtensions.InputKey))
                inputTypes[EffectProviderAnchorExtensions.InputKey] = pipeline.Types.GetOutputType(node.Provider, port);
            var context = new FullDynamicEffectContext
            {
                InvocationId = invocation.Id, Frame = FrameFor(node.Effects[i]), Services = services, Metadata = node.State.Metadata,
                InputTypes = inputTypes,
                OutputTypes = node.State.Outputs.Keys.ToDictionary(k => k, k => pipeline.Types.GetOutputType(node.Provider, k)),
                OutputContracts = node.State.Outputs.Keys.ToDictionary(k => k, k => pipeline.Types.GetOutputContract(node.Provider, k)),
            };
            if (!effect.EvaluateOutputPortType(context).TryGetValue(port, out var type) || type != context.OutputTypes[port])
                throw new InvalidOperationException($"Effect {node.State.Id}/{port} disagrees with the resolved output type.");
            while (true)
            {
                invocation.Requested.Clear();
                var states = effect.EvaluateState(context);
                if (!states.TryGetValue(port, out var state)) throw new InvalidOperationException($"Missing state for {node.State.Id}/{port}.");
                if (state.State == FullDynamicValueProviderEffectOutputAnchorState.NotComputed)
                {
                    var pending = invocation.Requested.Where(k => !invocation.Inputs.ContainsKey(k)).ToArray();
                    if (pending.Length == 0) throw new InvalidOperationException($"Effect {node.State.Id}/{port} cannot advance: {state.Message}");
                    foreach (var input in pending) ReadInput(node, input, scope);
                    continue;
                }
                if (state.State == FullDynamicValueProviderEffectOutputAnchorState.Inactive)
                    return new(FullDynamicInputState.Inactive, null, invocation.Position, invocation.PreserveAspect);
                if (state.State == FullDynamicValueProviderEffectOutputAnchorState.CannotToCompute)
                    throw new InvalidOperationException($"Effect {node.State.Id}/{port}: {state.Message}");
                result = effect.ComputeValue(context, port);
                break;
            }
            if (i + 1 < node.Computations.Length) invocation.Inputs[context.PrimaryInput] = Value(result);
        }
        if (ReferenceEquals(result, EffectBranchState.Inactive)) return Value(null) with { State = FullDynamicInputState.Inactive };
        if (result is IPicture picture) Track(picture);
        var output = node.State.Outputs[port];
        pipeline.Types.ValidateValue(node.Provider, port, false, output, result, frame.FrameIndex);
        observer?.Invoke(node.Effects[^1], node.State.Outputs.Count > 1 ? new Dictionary<string, object?> { [port] = result } : result);
        return new(FullDynamicInputState.Available, result, invocation.Position, invocation.PreserveAspect);
    }

    private bool IsActive(IEffect effect)
    {
        if (effect is IVectorPictureEffect) return true;
        if (effect is IAudioContinuousEffect audioEffect && (audioEffect.StartPoint != 0 || audioEffect.EndPoint != 0))
        {
            int length = (source as IAudioSamples)?.SampleCount ?? 1;
            if (frame.AudioSampleIndex + (long)length <= audioEffect.StartPoint || frame.AudioSampleIndex > audioEffect.EndPoint) return false;
        }
        if (effect is not IContinuousEffect continuous || !continuous.IsScoped && frame.Clip is null) return true;
        long start = continuous.IsScoped ? continuous.StartPoint : frame.Clip?.StartFrame ?? 0;
        long end = continuous.IsScoped ? continuous.EndPoint : start + (frame.Clip?.GetEffectiveDuration() ?? 1);
        return end > start && frame.FrameIndex >= start && frame.FrameIndex < end;
    }

    private EffectExecutionContext FrameFor(IEffect effect)
    {
        float progress = frame.Progress;
        if (effect is IContinuousEffect continuous && frame.Clip is not null)
        {
            long start = continuous.IsScoped ? continuous.StartPoint : frame.Clip.StartFrame;
            long end = continuous.IsScoped ? continuous.EndPoint : start + frame.Clip.GetEffectiveDuration();
            progress = end > start ? Math.Clamp((float)((long)frame.FrameIndex - start) / (end - start), 0, 1) : 0;
        }
        return new()
        {
            Clip = frame.Clip, FrameIndex = frame.FrameIndex, AudioSampleIndex = frame.AudioSampleIndex,
            Progress = progress, ClipProgress = frame.ClipProgress, TargetWidth = frame.TargetWidth, TargetHeight = frame.TargetHeight,
            RelativeWidth = frame.RelativeWidth, RelativeHeight = frame.RelativeHeight, CancellationToken = frame.CancellationToken,
        };
    }

    public static object? Require(FullDynamicValue value)
    {
        if (value.State == FullDynamicInputState.Failed) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(value.Error!).Throw();
        if (value.State == FullDynamicInputState.Waiting) throw new InvalidOperationException("Unresolved full dynamic input.");
        return value.Value;
    }

    private static void Release(Invocation invocation)
    {
        foreach (var release in invocation.Releases)
            try { release(); }
            catch (Exception ex) { Logger.Log(ex, "Release full dynamic invocation", typeof(FullDynamicEffectExecution)); }
        invocation.Releases.Clear();
    }

    private void ReleasePictures(FullDynamicValue? pending = null)
    {
        var retained = new HashSet<IPicture>(ReferenceEqualityComparer.Instance);
        void Keep(object? value)
        {
            if (value is IPicture picture) retained.Add(picture);
            else if (value is FullDynamicValue output) Keep(output.Value);
            else if (value is IReadOnlyDictionary<string, object?> outputs)
                foreach (var item in outputs.Values) Keep(item);
        }
        foreach (var scope in scopes)
        {
            foreach (var output in scope.Outputs.Values) Keep(output);
            foreach (var output in scope.Overrides.Values) Keep(output);
            foreach (var call in scope.Invocations.Values)
            {
                foreach (var input in call.Inputs.Values) Keep(input);
                foreach (var computed in call.Computed.Values) Keep(computed);
            }
        }
        Keep(pending);
        Keep(sourcePicture);
        foreach (var picture in Pictures.Where(p => !retained.Contains(p)).ToArray())
        {
            Pictures.Remove(picture);
            picture.Dispose();
        }
    }
    public void Dispose()
    {
        foreach (var scope in scopes)
            foreach (var call in scope.Invocations.Values) Release(call);
    }

    private sealed class Services(FullDynamicEffectExecution execution, FullDynamicEffectNode node, Scope scope, Invocation invocation) : IFullDynamicExecutionServices
    {
        public IReadOnlyDictionary<Guid, object?> Variables => execution.variables;
        public void OnCompleted(Action release) => invocation.Releases.Add(release);
        public FullDynamicInput InspectInput(string port, bool request = true)
        {
            if (invocation.Inputs.TryGetValue(port, out var value)) return new(value.State, value.Value, value.Error);
            if (!node.State.Bindings.TryGetValue(port, out var binding) || binding == IEffectProvider.NoConnectionGUID.ToString())
                return new(FullDynamicInputState.Available, execution.StaticInput(node, port));
            if (request) invocation.Requested.Add(port);
            return new(FullDynamicInputState.Waiting);
        }

        public object? ReadInput(string port) => Require(execution.ReadInput(node, port, scope));
        public object? GetOrCompute(object key, Func<object?> compute)
        {
            if (!invocation.Computed.TryGetValue(key, out var value)) invocation.Computed[key] = value = compute();
            return value;
        }
        public object? ReadVariable(Guid id, Func<object?> initialValue)
        {
            if (!execution.variables.TryGetValue(id, out var value)) execution.variables[id] = value = initialValue();
            return value;
        }
        public void WriteVariable(Guid id, object? value)
        {
            if (execution.variables.TryGetValue(id, out var old) && Equals(old, value)) return;
            execution.variables[id] = value;
            // Preserve completed side effects, invalidate pure results that may depend on variable reads.
            foreach (var current in execution.scopes)
                foreach (var pair in current.Invocations.ToArray())
                {
                    if (execution.pipeline.Nodes[pair.Key].Computations.OfType<IFullDynamicSchedulingEffect>().Any(e => e.HasSideEffects)) continue;
                    Release(pair.Value);
                    current.Invocations.Remove(pair.Key);
                    foreach (var key in current.Outputs.Keys.Where(k => k.Item1 == pair.Key).ToArray()) current.Outputs.Remove(key);
                }
        }

        public object? ComputeEffect(IEffect effect, EffectExecutionContext context)
        {
            var parameters = context.Parameters.ToDictionary(p => p.Key, p => execution.Copy(p.Value));
            object? input = parameters.GetValueOrDefault(EffectProviderAnchorExtensions.InputKey);
            if (execution.processFromCanvas && effect.CanProcessFromCanvas && input is IPicture picture
                && invocation.Position.TargetWidth > 0 && invocation.Position.TargetHeight > 0
                && (picture.Width != invocation.Position.TargetWidth || picture.Height != invocation.Position.TargetHeight))
                input = execution.Track(EffectPictureProcessing.Resize(picture, invocation.Position.TargetWidth, invocation.Position.TargetHeight, invocation.PreserveAspect));
            var values = new Dictionary<string, object?>();
            foreach (var p in parameters)
            {
                values[$"full://{node.State.Id}/{p.Key}"] = p.Value;
                if (node.State.Bindings.TryGetValue(p.Key, out var binding)) values[binding] = p.Value;
            }
            using var valueScope = ValueProviderFrameContext.PushValues(values);
            IRenderContext.CurrentFrameBuffer = input as IPicture;
            object? computed;
            if (effect is IMixture mixture)
            {
                var upper = parameters.GetValueOrDefault(MixtureProviderBase.UpperInputKey) as IPicture ?? throw new InvalidOperationException("Upper picture unavailable.");
                var lower = parameters.GetValueOrDefault(MixtureProviderBase.LowerInputKey) as IPicture ?? throw new InvalidOperationException("Lower picture unavailable.");
                var up = execution.ReadInput(node, MixtureProviderBase.UpperInputKey, scope).Position;
                var lo = execution.ReadInput(node, MixtureProviderBase.LowerInputKey, scope).Position;
                upper = execution.Track(EffectRuntimeDefaults.ApplyOpacity(upper, up.Opacity));
                lower = execution.Track(EffectRuntimeDefaults.ApplyOpacity(lower, lo.Opacity));
                if (lo.TargetX != 0 || lo.TargetY != 0 || lower.Width != context.TargetWidth || lower.Height != context.TargetHeight)
                    lower = execution.Track(EffectPictureProcessing.Mix(ClassicOverlayMixture.Default,
                        execution.Track(PictureFP16.GenerateSolidColor(context.TargetWidth, context.TargetHeight, Half.Zero, Half.Zero, Half.Zero, Half.Zero)),
                        lower, execution.pixelMode ?? lower.BitPerPixel, lo.TargetX, lo.TargetY, context.TargetWidth, context.TargetHeight));
                computed = EffectPictureProcessing.Mix(mixture, lower, upper, execution.pixelMode ?? lower.BitPerPixel,
                    up.TargetX, up.TargetY, context.TargetWidth, context.TargetHeight);
                invocation.Position = new(0, 0, context.TargetWidth, context.TargetHeight, false);
            }
            else computed = EffectPictureProcessing.Compute(effect, new EffectExecutionContext
            {
                Input = input, Parameters = parameters, InputTypes = context.InputTypes, Clip = context.Clip,
                FrameIndex = context.FrameIndex, AudioSampleIndex = context.AudioSampleIndex,
                Progress = context.Progress, ClipProgress = context.ClipProgress, TargetWidth = context.TargetWidth,
                TargetHeight = context.TargetHeight, RelativeWidth = context.RelativeWidth, RelativeHeight = context.RelativeHeight,
                CancellationToken = context.CancellationToken,
            }, values);
            if (computed is IReadOnlyDictionary<string, object?> outputs)
                foreach (var output in outputs.Values.OfType<IPicture>()) execution.Track(output);
            if (computed is IPicture rendered) execution.Track(rendered);
            if (computed is ClipPositionTuple position && effect is IClipPositionProvider or IContinuousClipPositionProvider)
            {
                invocation.Position = invocation.Position.Apply(position);
                invocation.PreserveAspect &= effect is IClipPositionProvider p ? p.PreserveAspectRatio : ((IContinuousClipPositionProvider)effect).PreserveAspectRatio;
            }
            return computed;
        }
    }
}
