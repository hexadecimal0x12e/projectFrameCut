using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using static projectFrameCut.Render.RenderAPIBase.EffectAndMixture.IFullDynamicValueProviderEffect;

namespace projectFrameCut.Render.PluginIsolation;

internal sealed class RemoteFullDynamicEffect(IPluginIsolationSession session, IsolationEffectDescriptor descriptor)
    : RemoteEffectBase(session, descriptor), IFullDynamicValueProviderEffect, IFullDynamicSchedulingEffect
{
    public bool HasSideEffects => descriptor.HasSideEffects;
    public FullDynamicFeedback? Feedback => descriptor.FeedbackPorts is [var start, var feedback, var condition, var body, var exit]
        ? new(start, feedback, condition, body, exit) : null;

    private object? Invoke(FullDynamicEffectContext c, string port, bool inspect)
    {
        c.GetOrCompute((this, "lease"), () =>
        {
            c.Services.OnCompleted(() => Session.InvokeAsync<IsolationEffectComputeRequest, IsolationEffectComputeResponse>(
                RenderOperation.IsolationFullDynamicEffect, new()
                {
                    Frame = new() { ObjectId = ObjectId },
                    FullDynamic = new() { InvocationId = c.InvocationId.ToString(), Release = true },
                }).AsTask().GetAwaiter().GetResult());
            return true;
        });
        var f = c.Frame;
        return ComputeCore(new()
        {
            Parameters = c.InputTypes.Keys.Where(k => c.PeekInput(k).State == FullDynamicInputState.Available)
                .ToDictionary(k => k, k => c.PeekInput(k).Value),
            InputTypes = c.InputTypes, Clip = f.Clip, FrameIndex = f.FrameIndex, AudioSampleIndex = f.AudioSampleIndex,
            Progress = f.Progress, ClipProgress = f.ClipProgress, TargetWidth = f.TargetWidth, TargetHeight = f.TargetHeight,
            RelativeWidth = f.RelativeWidth, RelativeHeight = f.RelativeHeight, CancellationToken = f.CancellationToken,
        }, c, port, inspect);
    }

    public IReadOnlyDictionary<string, FullDynamicValueProviderEffectOutputResult> EvaluateState(FullDynamicEffectContext context)
    {
        var response = (IsolationFullDynamicResponse)Invoke(context, "", true)!;
        return response.OutputStates.ToDictionary(p => p.Key, p => new FullDynamicValueProviderEffectOutputResult(
            (FullDynamicValueProviderEffectOutputAnchorState)p.Value, response.Messages.GetValueOrDefault(p.Key) ?? ""));
    }
    public IReadOnlyDictionary<string, EffectArgumentFieldType> EvaluateOutputPortType(FullDynamicEffectContext context) =>
        ((IsolationFullDynamicResponse)Invoke(context, "", true)!).OutputTypes.ToDictionary(p => p.Key, p => (EffectArgumentFieldType)p.Value);
    public object? ComputeValue(FullDynamicEffectContext context, string portToCompute) => Invoke(context, portToCompute, false);
}
