using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

internal sealed record FullDynamicEffectNode(IEffectProvider Provider, EffectProviderTypeState State,
    IEffect[] Effects, IFullDynamicValueProviderEffect[] Computations);

internal static class FullDynamicEffectPipelineBuilder
{
    public static Dictionary<string, IEffect> Build(IReadOnlyDictionary<Guid, IEffectProvider> providers,
        Dictionary<string, IEffect>? previous, IClip? clip, object? inputDataFormat)
    {
        var result = new Dictionary<string, IEffect>();
        var owned = new List<IDisposable>();
        try
        {
            Rendering.TransformProcessing.ValidateProviders(providers);
            foreach (var provider in providers.Values) provider.ValidateOutputFields();
            var types = new EffectFieldTypeResolution(providers, inputDataFormat: inputDataFormat);
            var errors = types.Diagnostics.Where(d => d.Code != "BindingCycle").ToArray();
            if (errors.Length > 0) throw new InvalidOperationException(string.Join(" ", errors.Select(e => e.Message)));
            var pipelines = new Dictionary<EffectStage, FullDynamicEffectPipeline>();
            var nodes = new Dictionary<Guid, FullDynamicEffectNode>();
            int index = 0;
            foreach (var provider in providers.Values.OrderBy(p => p.Id))
            {
                if (provider is RenderOutputProvider or ClipArgumentProvider) continue;
                var state = EffectProviderTypeState.Capture(provider);
                bool scheduled = provider.Target.HasFlag(EffectTarget.ValueProvider)
                    || provider.Stage is EffectStage.Render or EffectStage.VectorContent or EffectStage.Composition
                        && provider.TypeOfEffect != EffectType.Transform;
                var parameters = state.Parameters.ToDictionary(p => p.Key, p => p.Value!);
                foreach (var pair in state.Metadata.Where(p => p.Key == IEffectProvider.IsContinuousEffectParameterKey)) parameters[pair.Key] = pair.Value;
                foreach (var field in state.Bindings.Where(p => state.Fields.ContainsKey(p.Key)
                    && p.Value != IEffectProvider.NoConnectionGUID.ToString()))
                    parameters[field.Key] = scheduled
                        ? (Func<object>)(() => ValueProviderFrameContext.Get($"full://{state.Id}/{field.Key}")!)
                        : (Func<object>)(() => FullDynamicEffectStagePlan.Read(pipelines, provider.Stage, field.Value)!);
                IEffect[] effects;
                if (provider is IClipValueEndpointProvider || provider is Compose.MixtureSourceProvider) effects = [];
                else if (scheduled && provider.SupportsImplementTypes.Contains(EffectImplementType.FullDynamicValueProvider))
                    effects = provider.Build(EffectImplementType.FullDynamicValueProvider, parameters);
                else
                {
                    var implementation = ReadImplementation(provider);
                    effects = provider.Build(implementation, parameters, types.GetBuildDataFormat(provider));
                }
                foreach (var effect in effects)
                {
                    if (effect is IDisposable disposable) owned.Add(disposable);
                    effect.Index = index++;
                    effect.Enabled = provider.Enabled;
                    effect.BindedEffectProvidingSystemID = provider.Id.ToString();
                }
                if (scheduled)
                {
                    var computations = effects.Select(e => e as IFullDynamicValueProviderEffect ?? new FullDynamicValueEffect(e)).ToArray();
                    nodes.Add(provider.Id, new(provider, state, effects, computations));
                }
                else
                    for (int i = 0; i < effects.Length; i++) result[$"{provider.Id}_{i}"] = effects[i];
            }
            ValidateConnections(providers, nodes);
            foreach (var stage in nodes.Values.Select(n => n.Provider.Stage)
                .Concat(providers.Values.OfType<RenderOutputProvider>().Select(p => p.Stage)).Distinct())
            {
                if (providers.Values.Count(p => p.Stage == stage && p.IsFinalOutputSource()) > 1)
                    throw new InvalidOperationException($"Multiple final outputs in {stage}.");
                var pipeline = new FullDynamicEffectPipeline(providers, nodes.Where(p => p.Value.Provider.Stage == stage).ToDictionary(), types, stage);
                owned.Add(pipeline);
                pipelines[stage] = pipeline;
                if (!DynamicEffectCalculation.Supports(stage)) continue;
                IEffect host = stage switch
                {
                    EffectStage.VectorContent => new FullDynamicVectorPipeline(pipeline),
                    EffectStage.Composition => new FullDynamicMixturePipeline(pipeline),
                    _ => pipeline,
                };
                result[host.TypeName] = host;
            }
            if (previous is not null)
                foreach (var pair in previous.Where(p => string.IsNullOrWhiteSpace(p.Value.BindedEffectProvidingSystemID))) result.TryAdd(pair.Key, pair.Value);
            if (clip is not null)
            {
                var plan = new FullDynamicEffectStagePlan(clip, pipelines);
                clip.EffectStagePlan?.Dispose();
                clip.EffectStagePlan = plan;
            }
            foreach (var old in previous?.Values.OfType<IDisposable>().Distinct() ?? [])
                if (!result.Values.Any(e => ReferenceEquals(e, old))) old.Dispose();
            return result;
        }
        catch
        {
            var pipelines = owned.OfType<FullDynamicEffectPipeline>().ToArray();
            foreach (var disposable in owned.Distinct())
                if (disposable is FullDynamicEffectPipeline || !pipelines.Any(p => p.NodeEffects.Any(e => ReferenceEquals(e, disposable)))) disposable.Dispose();
            throw;
        }
    }

    private static EffectImplementType ReadImplementation(IEffectProvider provider) =>
        Enum.TryParse<EffectImplementType>(provider.MetaData.GetValueOrDefault(EffectProviderBase.ImplementTypeParameterKey)?.ToString(), out var type)
            && type != EffectImplementType.NotSpecified ? type
            : EffectHelper.GetPicturePreference(provider.TypeName) ?? provider.DefaultImplementType;

    private static void ValidateConnections(IReadOnlyDictionary<Guid, IEffectProvider> providers, Dictionary<Guid, FullDynamicEffectNode> nodes)
    {
        var feedback = nodes.ToDictionary(p => p.Key,
            p => p.Value.Computations.OfType<IFullDynamicSchedulingEffect>().Select(e => e.Feedback).FirstOrDefault(f => f is not null));
        var complete = new HashSet<Guid>();
        var visiting = new HashSet<Guid>();
        void Visit(Guid id)
        {
            if (complete.Contains(id)) return;
            if (!visiting.Add(id)) throw new InvalidOperationException($"Unscoped full dynamic cycle at {id}.");
            var node = nodes[id];
            foreach (var (field, binding) in node.State.Bindings.Where(p => node.State.Fields.ContainsKey(p.Key)))
            {
                if (binding == IEffectProvider.NoConnectionGUID.ToString() || binding == IEffectProvider.InputAnchorGUID.ToString()
                    || ValueProviderFrameContext.IsBuiltInSource(binding)) continue;
                if (!EffectProviderOutputExtensions.TryParseOutputSourceId(binding, out var upstream, out var port)
                    || !providers.TryGetValue(upstream, out var provider) || !provider.TryGetOutputField(port, out var output))
                    throw new InvalidOperationException($"Invalid source {binding} on {id}/{field}.");
                if (!nodes.ContainsKey(upstream)) continue;
                if (feedback[id]?.FeedbackInput == field || feedback[upstream]?.BodyOutput == output.Id) continue;
                Visit(upstream);
            }
            visiting.Remove(id);
            complete.Add(id);
        }
        foreach (var id in nodes.Keys) Visit(id);
    }
}
