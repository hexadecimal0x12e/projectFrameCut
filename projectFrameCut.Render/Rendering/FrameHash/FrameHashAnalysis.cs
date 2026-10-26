using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Sources;
using projectFrameCut.Shared;
using static projectFrameCut.Render.Rendering.FrameHashHelper;

namespace projectFrameCut.Render.Rendering;

internal sealed class FrameHashAnalysis(IReadOnlyList<IClip> clips, CancellationToken token)
{
    private static readonly HashSet<string> layoutProperties = new(StringComparer.Ordinal)
    {
        nameof(IClip.TargetX), nameof(IClip.TargetY), nameof(IClip.TargetWidth), nameof(IClip.TargetHeight),
        nameof(IClip.Rotation), nameof(IClip.LayerIndex), nameof(IClip.SubLayerIndex), nameof(IClip.PositionTuple),
    };
    private readonly Dictionary<object, object> states = new();
    private readonly HashSet<object> visiting = new();

    private object Read(object key, Func<object> describe)
    {
        token.ThrowIfCancellationRequested();
        if (states.TryGetValue(key, out var state)) return state;
        if (!visiting.Add(key)) throw new UnhashableInputException();
        try { return states[key] = describe(); }
        finally { visiting.Remove(key); }
    }

    internal object DescribeClip(IClip clip, bool layout = false, bool mixture = true, bool transforms = true)
    {
        layout |= clip is MaskClip;
        return Read((clip.Id, layout, mixture, transforms), () =>
        {
            ValidateClip(clip);
            var providers = clip.EffectProvidersInstances ?? [];
            return new
            {
                State = DescribeClipState(clip, layout),
                Module = clip.GetType().Module.ModuleVersionId,
                Source = DescribeSource(clip),
                Arguments = clip.ArgumentFields.Where(p => layout || !layoutProperties.Contains(p.Key))
                    .ToDictionary(p => p.Key, p => p.Value.ReadValue(clip)),
                Providers = providers.Where(p => p.Enabled && (layout && mixture || p.Stage != Stage.Mixture)
                        && (p is ClipArgumentProvider or IClipValueEndpointProvider { IsExport: true }
                            || p.IsFinalOutputSource()
                            || !DynamicEffectBindings.IsGraphProvider(p) && !p.Target.HasFlag(EffectTarget.ValueProvider)))
                    .OrderBy(p => p.Stage).ThenBy(p => p.Id).Select(p => DescribeProvider(clip, p, layout)).ToArray(),
                Effects = Effects(clip).Where(e => e.Enabled && !DynamicEffectBindings.IsGraphEffect(e)
                        && (layout || !IsLayoutEffect(e))
                        && (layout || e.TypeOfEffect != EffectType.MixtureProvider)
                        && !providers.Any(p => p.Id.ToString() == e.BindedEffectProvidingSystemID))
                    .OrderBy(e => e.Index).Select(e => DescribeDeterministicEffect(e)).ToArray(),
                SavedEffects = (clip.Effects ?? []).Where(e => e.Enabled
                        && !providers.Any(p => p.Id.ToString() == e.BindedEffectGroupID)
                        && (layout || !Effects(clip).Any(instance => instance.TypeName == e.TypeName && IsLayoutEffect(instance))))
                    .OrderBy(e => e.Index).Select(e => new
                    {
                        e.TypeName, e.FromPlugin, e.ImplementType, e.RelativeWidth, e.RelativeHeight, e.Parameters,
                    }).ToArray(),
                Replacement = clip.AlternativeSource is { } source ? DescribeBoundEffect(clip, source, layout) : null,
                Speed = clip.SpeedVarianceProviderInstance is { } speed ? DescribeBoundEffect(clip, speed, layout) : null,
                Mixture = layout && mixture && clip.MixtureInstance is { } mixer && !DynamicEffectBindings.IsGraphEffect(mixer)
                    ? DescribeBoundEffect(clip, mixer, true) : null,
                Backdrop = clip is MaskClip ? DescribeBackdrop(clip) : null,
                Transforms = transforms ? DescribeTransforms(clip) : null,
                EffectHelper.ForcePreferToType, EffectHelper.DefaultImplementsType,
                PreferredPlugins = RenderAPIBase.Plugins.IPluginBase.EffectImplementations.PreferredPlugins
                    .OrderBy(p => p.Key.TypeName, StringComparer.Ordinal).ThenBy(p => p.Key.ImplementType)
                    .Select(p => new { p.Key, p.Value }).ToArray(),
                Rasterizer = IVectorContentClip.GlobalDefaultRasterizer.GetType().AssemblyQualifiedName,
                AntiAlias = IVectorContentClip.GlobalDefaultAntiAliasMode,
                ClassicOverlayMixture.EnableApproximatePath,
            };
        });
    }

    private static object DescribeClipState(IClip clip, bool layout)
    {
        var state = ReadProperties(clip, nameof(IClip.Effects), nameof(IClip.EffectProviders));
        if (!layout)
            foreach (string key in layoutProperties) state.Remove(key);
        return state;
    }

    private static void ValidateClip(IClip clip)
    {
        if (ClipInitializationFailure.IsMarked(clip)
            || clip.EffectProviders is { Length: > 0 } && clip.EffectProvidersInstances is not { Length: > 0 }
            || clip.Effects is { Length: > 0 } && clip.EffectsInstances is not { Length: > 0 })
            throw new UnhashableInputException();
    }

    private static IEnumerable<IEffect> Effects(IClip clip) => (clip.EffectsInstances ?? []).Where(e => e.Enabled).SelectMany(e => e switch
    {
        DynamicEffectGraph graph => graph.NodeEffects,
        NativeDynamicEffectGraph graph => graph.NodeEffects,
        DynamicMixtureGraph graph => graph.NodeEffects,
        _ => new[] { e },
    });

    private static bool IsLayoutEffect(IEffect effect) => effect is IClipPositionProvider or IContinuousClipPositionProvider;

    private static object DescribeDeterministicEffect(IEffect effect, bool bound = false)
    {
        if (!effect.IsDeterministic) throw new UnhashableInputException();
        return DescribeEffect(effect, bound);
    }

    private object DescribeBoundEffect(IClip clip, IEffect effect, bool layout)
        => (clip.EffectProvidersInstances ?? []).FirstOrDefault(p => p.Id.ToString() == effect.BindedEffectProvidingSystemID) is { } provider
            ? DescribeProvider(clip, provider, layout) : DescribeDeterministicEffect(effect);

    internal object? DescribeSpeed(IClip clip) => clip.SpeedVarianceProviderInstance is { } speed
        ? DescribeBoundEffect(clip, speed, true) : null;

    internal object DescribeProvider(IClip clip, IEffectProvider provider, bool layout)
        => Read((clip.Id, provider.Id, layout), () =>
        {
            ValidateClip(clip);
            if (!provider.Enabled || !layout && provider.TypeOfEffect is EffectType.ClipPositionProvider or EffectType.ContinuousClipPositionProvider)
                return DescribeBinding(clip, provider.GetMainInputSource(), layout);
            if (!provider.IsDeterministic) throw new UnhashableInputException();
            if (provider is MixtureSourceProvider mixture)
                return mixture.IsUnder ? DescribeBackdrop(clip) : DescribeClip(clip, layout, mixture: false);
            object? endpoint = null;
            if (provider is IClipValueEndpointProvider { IsExport: false } input)
            {
                var source = clips.FirstOrDefault(c => c.Id == input.SourceClipId) ?? throw new UnhashableInputException();
                var output = source.EffectProvidersInstances?.OfType<IClipValueEndpointProvider>()
                    .FirstOrDefault(p => p.Id == input.SourceEndpointId && p.IsExport && p.Enabled)
                    ?? throw new UnhashableInputException();
                if (source.Id != clip.Id && (!output.AllowCrossClip || ClipLayerOrder.BottomToTop.Compare(source, clip) >= 0)
                    || !output.CanPassTo(input.Stage, source.Id == clip.Id, clip.EffectProvidersInstances)
                    || output.ValueType.BaseType() != input.ValueType.BaseType() && !(output.ValueType.IsPicture() && input.ValueType.IsPicture()))
                    throw new UnhashableInputException();
                endpoint = new { Source = DescribeProvider(source, output, layout), source.StartFrame, source.Duration };
            }
            var inputs = DynamicEffectBindings.InputFields(provider);
            var effects = Effects(clip).Where(e => e.Enabled && e.BindedEffectProvidingSystemID == provider.Id.ToString()
                && (layout || !IsLayoutEffect(e))).OrderBy(e => e.Index).ToArray();
            if (effects.Any(e => e.Parameters.Any(p => IsMutableInput(p.Value) && !inputs.ContainsKey(p.Key))))
                throw new UnhashableInputException();
            var fields = inputs
                .Where(p => layout || provider is not ClipArgumentProvider || !layoutProperties.Contains(p.Key))
                .ToDictionary(p => p.Key, p => DescribeInput(clip, provider, p.Key, p.Value, layout));
            return new
            {
                provider.Id, provider.TypeName, provider.FromPlugin, provider.Stage, provider.TypeOfEffect,
                Module = provider.GetType().Module.ModuleVersionId,
                provider.MetaData,
                Saved = DescribeSavedProvider(clip, provider, layout),
                Source = DescribeStageInput(clip, provider.Stage, layout),
                Fields = fields,
                Outputs = provider.GetOutputFields(),
                Output = provider.GetFinalOutputFieldId(),
                Effects = effects.Select(e => DescribeDeterministicEffect(e, true)).ToArray(),
                Endpoint = endpoint,
                clip.StartFrame, clip.RelativeStartFrame, clip.Duration, clip.FrameTime,
            };
        });

    private static object? DescribeSavedProvider(IClip clip, IEffectProvider provider, bool layout)
    {
        var saved = clip.EffectProviders?.FirstOrDefault(p => p.Id == provider.Id);
        return saved is null ? null : new
        {
            saved.TypeName, saved.FromPlugin, saved.Stage, saved.Enabled, saved.MetaData,
            Fields = saved.StaticFields?.Where(p => layout || provider is not ClipArgumentProvider || !layoutProperties.Contains(p.Key))
                .ToDictionary(p => p.Key, p => p.Value),
            Bindings = saved.AnchorsBindingState.Where(p => layout || provider is not ClipArgumentProvider || !layoutProperties.Contains(p.Key))
                .ToDictionary(p => p.Key, p => p.Value),
        };
    }

    private object? DescribeInput(IClip clip, IEffectProvider provider, string key, IEffectArgumentField field, bool layout)
    {
        string? binding = provider.AnchorsBindingState.GetValueOrDefault(key)
            ?? (field as DynamicEffectParamField)?.BoundProviderId;
        if (!string.IsNullOrWhiteSpace(binding) && binding != IEffectProvider.NoConnectionGUID.ToString())
            return new { field.FieldType, field.DefaultValue,
                Fallback = field is DynamicEffectParamField f ? DescribeFallback(f.StaticFallbackValue) : null,
                Source = binding == IEffectProvider.InputAnchorGUID.ToString()
                    ? DescribeStageInput(clip, provider.Stage, layout) : DescribeBinding(clip, binding, layout) };
        if (field is IEffect { BindedEffectProvidingSystemID: { } source } && field.IsDynamic)
            return DescribeBinding(clip, source, layout);
        object? value = field switch
        {
            StaticEffectArgumentField f => f.Value,
            DynamicEffectParamField f => f.StaticFallbackValue,
            IEffect e => DescribeDeterministicEffect(e),
            EffectArgumentFieldDescriptor => field.DefaultValue,
            _ when !field.IsDynamic && !field.IsDynamicAtRenderTime => field.GetGetter()(),
            _ => throw new UnhashableInputException(),
        };
        // Content must have a known producer; an object instance is not a persistent identity.
        if (IsMutableInput(value)) throw new UnhashableInputException();
        return new { field.FieldType, Value = value };
    }

    private static object? DescribeFallback(object? value) => IsMutableInput(value)
        ? throw new UnhashableInputException() : value;

    private object DescribeStageInput(IClip clip, Stage stage, bool layout) => stage == Stage.SpeedVariance
        ? new { clip.StartFrame, clip.Duration, clip.FrameTime }
        : Read(("stage", clip.Id, stage, layout), () => new
        {
            Source = stage == Stage.ClipArguments ? null : DescribeSource(clip),
            Providers = (clip.EffectProvidersInstances ?? []).Where(p => p.Enabled && (int)p.Stage < (int)stage
                    && p.Stage != Stage.ClipArguments && (p.IsFinalOutputSource()
                        || !DynamicEffectBindings.IsGraphProvider(p) && !p.Target.HasFlag(EffectTarget.ValueProvider)))
                .OrderBy(p => p.Stage).ThenBy(p => p.Id).Select(p => DescribeProvider(clip, p, layout)).ToArray(),
        });

    private object DescribeBinding(IClip clip, string? binding, bool layout)
    {
        if (binding == IEffectProvider.InputAnchorGUID.ToString()) return DescribeSource(clip);
        if (binding is ValueProviderFrameContext.BuiltInFrameProviderId or ValueProviderFrameContext.BuiltInProgressProviderId)
            return new { BuiltIn = binding, clip.StartFrame, clip.Duration, clip.FrameTime };
        if (binding == ValueProviderFrameContext.BuiltInAudioSampleProviderId)
            return EffectProcessing.CurrentAudioSource?.DescribeHashInputs(token) ?? throw new UnhashableInputException();
        if (!EffectProviderOutputExtensions.TryParseOutputSourceId(binding, out var id, out var outputId))
            throw new UnhashableInputException();
        var source = clip.EffectProvidersInstances?.FirstOrDefault(p => p.Id == id) ?? throw new UnhashableInputException();
        if (!source.TryGetOutputField(outputId, out _)) throw new UnhashableInputException();
        if (!layout && source.TypeOfEffect is EffectType.ClipPositionProvider or EffectType.ContinuousClipPositionProvider)
            return Read(("position", clip.Id, id), () => DescribeBinding(clip, source.GetMainInputSource(), false));
        return new { Output = outputId, Provider = DescribeProvider(clip, source, layout) };
    }

    private object DescribeSource(IClip clip) => Read(("source", clip.Id), () =>
    {
        object? source = clip switch
        {
            VideoClip video when video.Decoder is { IsDeterministic: true, AllowCachingResult: true, Disposed: false } decoder
                && !string.IsNullOrWhiteSpace(clip.FilePath)
                => new { decoder.TypeName, Module = decoder.GetType().Module.ModuleVersionId },
            VideoClip => throw new UnhashableInputException(),
            VirtualSourceVideoClip { VirtualSource: { IsDeterministic: true } virtualSource }
                => new { virtualSource.TypeName, Module = virtualSource.GetType().Module.ModuleVersionId, State = ReadProperties(virtualSource) },
            VirtualSourceVideoClip => throw new UnhashableInputException(),
            PhotoClip when string.IsNullOrWhiteSpace(clip.FilePath) => throw new UnhashableInputException(),
            MaskClip mask => DescribeMaskSource(mask),
            _ when clip.FromPlugin == InternalPluginBase.InternalPluginBaseID => null,
            _ => throw new UnhashableInputException(),
        };
        return new
        {
            clip.TypeName, Module = clip.GetType().Module.ModuleVersionId,
            State = DescribeClipState(clip, false),
            File = SourceFingerprint(clip.FilePath),
            Font = clip is TextClip text ? SourceFingerprint(text.FontPath) : null,
            Source = source,
            Arguments = (clip.EffectProvidersInstances ?? []).OfType<ClipArgumentProvider>().Where(p => p.Enabled)
                .Select(p => DescribeProvider(clip, p, clip is MaskClip)).ToArray(),
        };
    });

    private static object DescribeMaskSource(MaskClip clip)
    {
        var source = MaskSources.Create(clip.SourceType);
        if (!source.IsDeterministic) throw new UnhashableInputException();
        return new { source.TypeName, Module = source.GetType().Module.ModuleVersionId, clip.SourceArguments,
            Identity = SourceFingerprint(clip.SourceType) };
    }

    private object DescribeBackdrop(IClip clip) => Read(("backdrop", clip.Id), () => clips
        .Where(c => ClipLayerOrder.BottomToTop.Compare(c, clip) < 0)
        .OrderBy(c => c, ClipLayerOrder.BottomToTop).Select(c => DescribeClip(c, true)).ToArray());

    private object[] DescribeTransforms(IClip clip)
    {
        var infos = clips.Select(c => new TransformClipInfo(c.Id, c.StartFrame, c.Duration,
            c.LayerIndex, c.SubLayerIndex, c.EffectProviders ?? [])).ToArray();
        return Enum.GetValues<TransformSide>().Select(side => TransformProcessing.Find(infos, clip.Id, side))
            .Where(found => found is not null).Select(found => found!.Value).Where(found => found.Provider.Enabled)
            .Select(found => new
            {
                found.Provider,
                Inputs = new[] { found.Owner.Id, TransformProcessing.ReadNextClip(found.Provider.MetaData) }
                    .Where(id => id != Guid.Empty && id != clip.Id).Distinct()
                    .Select(id => DescribeClip(clips.FirstOrDefault(c => c.Id == id)
                        ?? throw new UnhashableInputException(), true, transforms: false)).ToArray(),
            }).ToArray();
    }

    internal static bool IsMutableInput(object? value) => value is projectFrameCut.Drawing.Base.IPicture
        or projectFrameCut.Drawing.Vector.VectorPicture or IAudioSamples or Delegate || IsRuntimeValue(value);
}
