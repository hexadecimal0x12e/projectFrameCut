using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

internal sealed class ClipEffectStagePlan : IEffectStagePlan
{
    private readonly InlineValueGraph values;
    private readonly Dictionary<Stage, (Guid Id, string Source)[]> exports;
    private bool disposed;
    public IClip Owner { get; }

    public ClipEffectStagePlan(IClip owner, IReadOnlyDictionary<Guid, IEffectProvider> providers, InlineValueGraph values)
    {
        Owner = owner;
        this.values = values;
        exports = providers.Values.OfType<IClipValueEndpointProvider>()
            .Where(p => p.IsExport && p.Enabled && p.Stage is not (Stage.Picture or Stage.VectorPicture or Stage.Mixture))
            .GroupBy(p => p.Stage).ToDictionary(g => g.Key,
                g => g.Where(p => p.GetMainInputSource() != IEffectProvider.NoConnectionGUID.ToString())
                    .Select(p => (p.Id, p.GetMainInputSource())).ToArray());
        foreach (var (_, source) in exports.Values.SelectMany(e => e))
        {
            if (!EffectProviderOutputExtensions.TryParseOutputSourceId(source, out var id, out _)) continue;
            if (!DynamicEffectBindings.CanInlineValue(providers[id], providers, types: values.Types))
                throw new InvalidOperationException($"Stage endpoint source {source} must produce an ordinary value.");
            values.GetEffects(id);
        }
        values.AddOwner();
    }

    public void Complete(IClip clip, Stage stage, uint frame)
    {
        if (!exports.TryGetValue(stage, out var endpoints) || endpoints.Length == 0) return;
        var context = ClipValueFrameContext.Current
            ?? throw new ClipValueBindingException($"Stage {stage} on clip {clip.Id} requires an endpoint frame context.");
        var key = (this, clip.Id, stage, frame);
        if (context.TryGetComputed<object>(key, out _)) return;
        using var clipScope = ValueProviderFrameContext.PushClip(clip);
        using var scope = ValueProviderFrameContext.PushFrame(frame,
            ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInFrameProviderId) is float current && current == frame
                ? Convert.ToSingle(ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInProgressProviderId) ?? 0f)
                : Math.Clamp(((float)frame - clip.StartFrame) / Math.Max(1u, clip.GetEffectiveDuration()), 0, 1), reuseComputed: true);
        try
        {
            foreach (var (id, source) in endpoints)
            {
                var value = values.GetValue(source)
                    ?? throw new ClipValueBindingException($"Stage {stage} endpoint {id} produced no value at frame {frame}.");
                if (!ReferenceEquals(value, EffectBranchState.Inactive)) context.Publish(clip, frame, id, value);
            }
            context.SetComputed(key, this);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Log(ex, $"Publish {stage} endpoints for clip {clip.Id}, frame {frame}", this);
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        values.ReleaseWhenIdle(() =>
        {
            foreach (var effect in values.Effects.OfType<IDisposable>().Distinct<IDisposable>(ReferenceEqualityComparer.Instance))
                effect.Dispose();
        });
    }
}
