using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

internal sealed class FullDynamicEffectStagePlan(IClip owner, IReadOnlyDictionary<EffectStage, FullDynamicEffectPipeline> pipelines) : IEffectStagePlan
{
    public IClip Owner => owner;

    public static object? Read(IReadOnlyDictionary<EffectStage, FullDynamicEffectPipeline> pipelines, EffectStage stage, string source)
    {
        var clip = ValueProviderFrameContext.CurrentClip ?? throw new InvalidOperationException("A clip is required for a dynamic stage parameter.");
        uint frame = (uint)Convert.ToSingle(ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInFrameProviderId) ?? 0f);
        if (!pipelines.TryGetValue(stage, out var pipeline)) throw new InvalidOperationException($"No full dynamic value pipeline for {stage}.");
        return pipeline.EvaluateContent(clip.PositionTuple, clip, frame, Math.Max(1, clip.TargetWidth), Math.Max(1, clip.TargetHeight),
            Math.Max(1, clip.TargetWidth), Math.Max(1, clip.TargetHeight), clip.PositionTuple,
            cancellationToken: ValueProviderFrameContext.CancellationToken, requestedBinding: source).Content;
    }

    public void Complete(IClip clip, EffectStage stage, uint frame)
    {
        if (!pipelines.TryGetValue(stage, out var pipeline) || DynamicEffectCalculation.Supports(stage)) return;
        var endpoints = ClipValueFrameContext.Current;
        var key = (this, clip.Id, stage, frame);
        if (endpoints?.TryGetComputed<object>(key, out _) == true) return;
        using var clipScope = ValueProviderFrameContext.PushClip(clip);
        using var frameScope = ValueProviderFrameContext.PushFrame(frame,
            Math.Clamp(((float)frame - clip.StartFrame) / Math.Max(1u, clip.GetEffectiveDuration()), 0, 1), reuseComputed: true);
        var endpoint = pipeline.Nodes.Values.FirstOrDefault(n => n.State.Enabled && n.Provider is IClipValueEndpointProvider { IsExport: true });
        if (endpoint is not null) Read(pipelines, stage, endpoint.State.Id.ToString());
        endpoints?.SetComputed(key, this);
    }

    public void Dispose()
    {
        foreach (var pipeline in pipelines.Values.Where(p => !DynamicEffectCalculation.Supports(p.Stage))) pipeline.Dispose();
    }
}
