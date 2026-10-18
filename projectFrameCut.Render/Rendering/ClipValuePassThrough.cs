using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RPCProtocol;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.Rendering;

public static class ClipValuePassThrough
{
    public static bool HasEndpoints(IClip clip) => clip.EffectProvidersInstances?.Any(p => p is IClipValueEndpointProvider) == true
        || clip.EffectProviders?.Any(p => p.TypeName is "ClipValueInput" or "ClipValueOutput") == true;

    public static bool HasInputs(IEnumerable<IClip> clips) => clips.Any(c =>
        c.EffectProvidersInstances?.Any(p => p is IClipValueEndpointProvider { IsExport: false, Enabled: true }) == true
        || c.EffectProviders?.Any(p => p.TypeName == "ClipValueInput" && p.Enabled) == true);

    public static IDisposable? Open(IReadOnlyList<IClip> clips, uint frame, int width, int height,
        int projectWidth, int projectHeight, IPicture.PicturePixelMode pixelMode, CancellationToken token, Action<IClip>? evaluate = null)
    {
        if (ClipValueFrameContext.Current is not null || !clips.Any(HasEndpoints)) return null;
        foreach (var clip in clips.Where(c => c.ContainsFrame(frame) && HasEndpoints(c)))
            if (clip.EffectProvidersInstances?.Any(p => p is IClipValueEndpointProvider) != true)
            {
                try
                {
                    clip.ReInit(pixelMode);
                    if (clip.EffectProvidersInstances?.Any(p => p is IClipValueEndpointProvider) != true)
                        EffectHelper.ResolveClipEffects(clip);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    ClipsAndTracks.ClipInitializationFailure.Mark(clip, "ResolveBinding", ex);
                    projectFrameCut.Shared.Logger.Log(ex, $"Initialize endpoint clip {clip.Id}", nameof(ClipValuePassThrough));
                }
            }
        return new ClipValueFrameContext(clips, frame, source =>
        {
            token.ThrowIfCancellationRequested();
            if (evaluate is not null) { evaluate(source); return; }
            if (ClipsAndTracks.ClipInitializationFailure.IsMarked(source))
                throw new ClipValueBindingException($"Source clip {source.Id} failed to initialize at {frame}.");
            using var picture = ClipPreviewRenderer.Render(source, clips, width, height, projectWidth, projectHeight,
                frame, token, pixelMode, initializeClips: false);
            if (picture is null || ClipsAndTracks.ClipInitializationFailure.IsMarked(source))
                throw new ClipValueBindingException($"Source clip {source.Id} failed to produce an endpoint frame at {frame}.");
        }, DynamicEffectGraph.CopyPicture, source => !ClipsAndTracks.ClipInitializationFailure.IsMarked(source)
            && ClipArgumentBinding.ShouldRender(ClipArgumentBinding.InitializeFrame(source, frame)),
            (clip, endpoint) => FrameHashContext.Current?.ReadEndpoint(clip, endpoint),
            (clip, endpoint, value) => FrameHashContext.Current?.PublishEndpoint(clip, endpoint, value));
    }

    public static IEnumerable<Guid> GetSourceClipIds(IClip clip)
    {
        var serialized = clip.EffectProviders?.Where(p => p.TypeName == "ClipValueInput")
            .Select(p => Guid.TryParse(p.MetaData?.GetValueOrDefault(nameof(IClipValueEndpointProvider.SourceClipId))?.ToString(), out var id) ? id : Guid.Empty)
            ?? [];
        return (clip.EffectProvidersInstances?.OfType<IClipValueEndpointProvider>().Where(p => !p.IsExport)
            .Select(p => p.SourceClipId) ?? []).Concat(serialized).Distinct();
    }

    public static IReadOnlyList<EffectBindingHelper.BindingDiagnostic> Validate(IClip consumer, IReadOnlyList<IClip> clips)
    {
        var result = new List<EffectBindingHelper.BindingDiagnostic>();
        foreach (var input in consumer.EffectProvidersInstances?.OfType<IClipValueEndpointProvider>().Where(p => !p.IsExport) ?? [])
        {
            string? error = null;
            var source = clips.FirstOrDefault(c => c.Id == input.SourceClipId);
            var output = source?.EffectProvidersInstances?.OfType<IClipValueEndpointProvider>().FirstOrDefault(p => p.Id == input.SourceEndpointId && p.IsExport);
            if (source is null) error = "The source clip does not exist.";
            else if (source.Id != consumer.Id && ClipLayerOrder.BottomToTop.Compare(source, consumer) >= 0) error = "The source must precede the consumer in clip render order.";
            else if (output is null) error = "The source endpoint does not exist.";
            else if (!output.Enabled) error = "The source endpoint is disabled.";
            else if (!output.CanPassTo(input.GetPipeline(), source.Id == consumer.Id)) error = "The endpoint pipeline has changed or does not precede the input stage.";
            else if (output.ValueType.BaseType() != input.ValueType.BaseType() && !(output.ValueType.IsPicture() && input.ValueType.IsPicture())) error = "The endpoint type has changed.";
            if (error is not null) result.Add(new(input.Id, "InvalidClipEndpoint", error));
        }
        return result;
    }
}
