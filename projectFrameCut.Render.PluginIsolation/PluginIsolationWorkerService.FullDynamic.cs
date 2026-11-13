using System.Collections.Concurrent;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.Context;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.PluginIsolation;

internal sealed partial class PluginIsolationWorkerService
{
    private sealed class FullDynamicInvocation : IDisposable
    {
        public Dictionary<object, object?> Computed { get; } = [];
        public HashSet<IPicture> Pictures { get; } = new(ReferenceEqualityComparer.Instance);
        public void Dispose()
        {
            foreach (var picture in Pictures) picture.Dispose();
            Pictures.Clear();
            Computed.Clear();
        }
        public object? Capture(object? value)
        {
            if (value is IPicture picture)
            {
                var copy = EffectPictureProcessing.Copy(picture);
                Pictures.Add(copy);
                return copy;
            }
            if (value is IReadOnlyDictionary<string, object?> outputs) return outputs.ToDictionary(p => p.Key, p => Capture(p.Value));
            return value;
        }
    }
    private readonly ConcurrentDictionary<(long, string), FullDynamicInvocation> fullDynamicInvocations = new();

    private async ValueTask<RenderResponseEnvelope> ComputeFullDynamicEffectAsync(RenderRequestEnvelope envelope, CancellationToken token)
    {
        var request = Read<IsolationEffectComputeRequest>(envelope);
        var dynamicRequest = request.FullDynamic ?? throw new InvalidDataException("Missing full dynamic request.");
        var key = (request.Frame.ObjectId, dynamicRequest.InvocationId);
        if (dynamicRequest.Release)
        {
            if (fullDynamicInvocations.TryRemove(key, out var released))
                lock (released) released.Dispose();
            return Success(envelope, new IsolationEffectComputeResponse());
        }
        var effect = Require<IEffect>(request.Frame.ObjectId) as IFullDynamicValueProviderEffect
            ?? throw new InvalidOperationException("The isolated effect does not implement full dynamic evaluation.");
        var invocation = fullDynamicInvocations.GetOrAdd(key, _ => new());
        using var gpuScope = new HwAccelPictureScope();
        using var gpuEntry = gpuScope.Enter();
        var pictures = new HashSet<IPicture>(ReferenceEqualityComparer.Instance);
        var leases = new List<IsolationPayloadLease>();
        bool transferred = false;
        var previousBuffer = IRenderContext.CurrentFrameBuffer;
        try
        {
            var values = request.Frame.DynamicValues.ToDictionary(p => p.Key, p => IsolationValueConverter.ToObject(p.Value));
            foreach (var pair in request.Frame.DynamicPictures)
            {
                var picture = await PicturePayloadCodec.ReadAsync(pair.Value, _payloads, token).ConfigureAwait(false);
                pictures.Add(picture);
                values[pair.Key] = picture;
            }
            foreach (var pair in request.Frame.DynamicAudio)
                values[pair.Key] = await AudioPayloadCodec.ReadAsync(pair.Value, _payloads, token).ConfigureAwait(false);
            using var clip = request.Frame.Clip is null ? null : new SnapshotClip(request.Frame.Clip);
            var response = new IsolationEffectComputeResponse { FullDynamic = new() };
            var services = new FullDynamicServices(dynamicRequest, values, response.FullDynamic, invocation);
            var context = new FullDynamicEffectContext
            {
                InvocationId = Guid.Parse(dynamicRequest.InvocationId), Services = services,
                InputTypes = request.InputTypes.ToDictionary(p => p.Key, p => (EffectArgumentFieldType)p.Value),
                OutputTypes = dynamicRequest.OutputTypes.ToDictionary(p => p.Key, p => (EffectArgumentFieldType)p.Value),
                OutputContracts = dynamicRequest.OutputContracts.ToDictionary(p => p.Key, p => IsolationFieldTypeCodec.ReadContract(p.Value)),
                Metadata = dynamicRequest.Metadata.ToDictionary(p => p.Key, p => IsolationValueConverter.ToObject(p.Value)!),
                Frame = new()
                {
                    Clip = clip, FrameIndex = request.Frame.TargetFrame, Progress = request.Frame.Progress,
                    ClipProgress = request.Frame.ClipProgress, AudioSampleIndex = request.AudioSampleIndex,
                    TargetWidth = request.Frame.TargetWidth, TargetHeight = request.Frame.TargetHeight,
                    RelativeWidth = request.RelativeWidth, RelativeHeight = request.RelativeHeight, CancellationToken = token,
                },
            };
            object? result = null;
            lock (invocation)
            {
                ApplyEffectState(effect, request.Frame.State);
                using var frameScope = ValueProviderFrameContext.PushFrame(request.Frame.TargetFrame, request.Frame.ClipProgress);
                using var valueScope = ValueProviderFrameContext.PushValues(values);
                if (dynamicRequest.Inspect)
                {
                    var types = effect.EvaluateOutputPortType(context);
                    response.FullDynamic.OutputTypes = types.ToDictionary(p => p.Key, p => (int)p.Value);
                    foreach (var pair in effect.EvaluateState(context))
                    {
                        response.FullDynamic.OutputStates[pair.Key] = (int)pair.Value.State;
                        response.FullDynamic.Messages[pair.Key] = pair.Value.Message;
                    }
                }
                else result = effect.ComputeValue(context, dynamicRequest.Port);
            }
            if (dynamicRequest.Inspect) return Success(envelope, response);
            if (result is IPicture output)
            {
                if (!invocation.Pictures.Contains(output)) pictures.Add(output);
                var lease = await PicturePayloadCodec.WriteAsync(output, _payloads, _payloadKind, token).ConfigureAwait(false);
                leases.Add(lease);
                response.Picture = lease.Reference;
            }
            else if (result is projectFrameCut.Shared.IAudioSamples audio)
                response.Audio = await AudioPayloadCodec.WriteAsync(audio, _payloads, _payloadKind, leases, token).ConfigureAwait(false);
            else response.Value = IsolationValueConverter.FromObject(result);
            var resultEnvelope = Success(envelope, response);
            transferred = true;
            return resultEnvelope;
        }
        finally
        {
            IRenderContext.CurrentFrameBuffer = previousBuffer;
            if (!transferred)
                foreach (var lease in leases) await lease.DisposeAsync().ConfigureAwait(false);
            foreach (var picture in pictures) picture.Dispose();
        }
    }

    private sealed class FullDynamicServices(IsolationFullDynamicRequest request, IReadOnlyDictionary<string, object?> inputs,
        IsolationFullDynamicResponse response, FullDynamicInvocation invocation) : IFullDynamicExecutionServices
    {
        private readonly Dictionary<Guid, object?> variables = request.Variables.ToDictionary(p => Guid.Parse(p.Key), p => IsolationValueConverter.ToObject(p.Value));
        public IReadOnlyDictionary<Guid, object?> Variables => variables;
        public FullDynamicInput InspectInput(string port, bool register = true)
        {
            var state = (FullDynamicInputState)request.InputStates.GetValueOrDefault(port, (int)FullDynamicInputState.Waiting);
            if (register && state == FullDynamicInputState.Waiting && !response.RequestedInputs.Contains(port)) response.RequestedInputs.Add(port);
            return new(state, inputs.GetValueOrDefault(port), request.InputErrors.TryGetValue(port, out var error) ? new InvalidOperationException(error) : null);
        }
        public object? ReadInput(string port)
        {
            var value = InspectInput(port);
            if (value.State != FullDynamicInputState.Available) throw value.Error ?? new InvalidOperationException($"Input {port} is {value.State}.");
            return value.Value;
        }
        public object? ComputeEffect(IEffect effect, EffectExecutionContext context) => EffectPictureProcessing.Compute(effect, context);
        public object? GetOrCompute(object key, Func<object?> compute)
        {
            if (!invocation.Computed.TryGetValue(key, out var value))
            {
                var computed = compute();
                invocation.Computed[key] = value = invocation.Capture(computed);
                if (computed is IPicture picture && !inputs.Values.Any(v => ReferenceEquals(v, picture))) picture.Dispose();
                if (computed is IReadOnlyDictionary<string, object?> outputs)
                    foreach (var output in outputs.Values.OfType<IPicture>().Distinct())
                        if (!inputs.Values.Any(v => ReferenceEquals(v, output))) output.Dispose();
            }
            return value;
        }
        public object? ReadVariable(Guid id, Func<object?> initialValue)
        {
            if (!variables.TryGetValue(id, out var value))
            {
                variables[id] = value = initialValue();
                if (!request.Inspect) response.VariableWrites[id.ToString()] = IsolationValueConverter.FromObject(value);
            }
            return value;
        }
        public void WriteVariable(Guid id, object? value)
        {
            if (request.Inspect) throw new InvalidOperationException("State inspection cannot write variables.");
            variables[id] = value;
            response.VariableWrites[id.ToString()] = IsolationValueConverter.FromObject(value);
        }
        public void OnCompleted(Action release) => throw new NotSupportedException("Nested remote invocations are not supported.");
    }
}
