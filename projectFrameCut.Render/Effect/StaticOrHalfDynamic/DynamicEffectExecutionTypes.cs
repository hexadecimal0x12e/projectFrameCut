using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

/// <summary>
/// A single provider in a built effect graph. The node keeps the immutable pieces captured when the
/// graph was rebuilt: the provider, its materialized fields, its bindings and its built effects.
/// </summary>
internal sealed record DynamicEffectNode(IEffectProvider Provider, Dictionary<string, IEffectArgumentField> Fields,
    Dictionary<string, string> Bindings, IEffect[] Effects, bool Enabled)
{
    public string? BindingOf(string fieldId) => Bindings.GetValueOrDefault(fieldId);

    public bool IsBound(string fieldId) => BindingOf(fieldId) is { } binding
        && binding != IEffectProvider.NoConnectionGUID.ToString();
}

/// <summary>
/// A value produced by one node in an effect graph together with the canvas placement it carries.
/// </summary>
internal sealed record DynamicEffectValue(object? Value, ClipPositionTuple Position, bool PreserveAspect,
    IReadOnlyDictionary<string, object?>? Outputs = null)
{
    public bool IsInactive => ReferenceEquals(Value, EffectBranchState.Inactive);

    public bool HasValue => Value is not null && !IsInactive;

    /// <summary>Selects the value exposed by a binding: a named port for multiple-output nodes, otherwise the main value.</summary>
    public object? ValueOf(string? outputId) => Outputs is null ? Value : Outputs.GetValueOrDefault(outputId);

    public DynamicEffectValue WithValue(object? value) => this with { Value = value };
}

/// <summary>Resolves a binding source id to its current value.</summary>
internal interface IDynamicEffectInputResolver
{
    DynamicEffectValue Resolve(string? sourceId);
}

/// <summary>Wraps a local resolution function so the node evaluator can use it as a resolver.</summary>
internal sealed class DelegateInputResolver(Func<string?, DynamicEffectValue> resolve) : IDynamicEffectInputResolver
{
    public DynamicEffectValue Resolve(string? sourceId) => resolve(sourceId);
}

/// <summary>
/// All per-frame ambient state shared by effect graph evaluation. Both the legacy pull based
/// evaluation and the dynamic calculation engine evaluate nodes through this context so that
/// picture lifetime, progress and fallback handling stay identical.
/// </summary>
internal sealed class DynamicEffectFrame
{
    private readonly DynamicEffectGraph graph;
    private IPicture? sourcePicture;
    private IAudioSamples<float>? audioFrame;

    public DynamicEffectFrame(DynamicEffectGraph graph, object source, IClip? clip, uint frame, int width, int height,
        int relativeWidth, int relativeHeight, ClipPositionTuple initialPosition, bool preserveAspect,
        bool processFromCanvas, CancellationToken cancellationToken, Action<IEffect, object?>? afterNode,
        float progress, float? clipProgress, float? vectorProgress, IPicture? underPicture,
        IPicture.PicturePixelMode? mixturePixelMode)
    {
        this.graph = graph;
        Source = source;
        Clip = clip;
        Frame = frame;
        Width = width;
        Height = height;
        RelativeWidth = relativeWidth;
        RelativeHeight = relativeHeight;
        InitialPosition = initialPosition;
        PreserveAspect = preserveAspect;
        ProcessFromCanvas = processFromCanvas;
        CancellationToken = cancellationToken;
        AfterNode = afterNode;
        Progress = progress;
        ClipProgress = clipProgress;
        VectorProgress = vectorProgress;
        UnderPicture = underPicture;
        MixturePixelMode = mixturePixelMode;
    }

    public DynamicEffectGraph Graph => graph;
    public IReadOnlyDictionary<Guid, DynamicEffectNode> Nodes => graph.Nodes;
    public EffectFieldTypeResolution Types => graph.Types;
    public EffectStage Stage => graph.Stage;
    public object Source { get; }
    public IClip? Clip { get; }
    public uint Frame { get; }
    public int Width { get; }
    public int Height { get; }
    public int RelativeWidth { get; }
    public int RelativeHeight { get; }
    public ClipPositionTuple InitialPosition { get; }
    public bool PreserveAspect { get; }
    public bool ProcessFromCanvas { get; }
    public CancellationToken CancellationToken { get; }
    public Action<IEffect, object?>? AfterNode { get; }
    public float Progress { get; }
    public float? ClipProgress { get; }
    public float? VectorProgress { get; }
    public IPicture? UnderPicture { get; }
    public IPicture.PicturePixelMode? MixturePixelMode { get; }
    public HashSet<IPicture> Pictures { get; init; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<Guid, object?> Variables { get; init; } = new();
    public Action<Guid>? CountStep { get; init; }
    public IPicture? SourcePicture => sourcePicture;

    public DynamicEffectValue Empty() => new(null, InitialPosition, PreserveAspect);

    public DynamicEffectValue CreateInactive(IEffectProvider provider) => new(EffectBranchState.Inactive,
        InitialPosition, PreserveAspect, provider is IMultipleOutputEffectProvider
            ? provider.GetOutputFields().Keys.ToDictionary(key => key, _ => (object?)EffectBranchState.Inactive) : null);

    public IPicture Track(IPicture picture)
    {
        if (!ReferenceEquals(picture, Source) && !ReferenceEquals(picture, UnderPicture)) Pictures.Add(picture);
        return picture;
    }

    public object? Copy(object? value) => value is IPicture picture ? Track(DynamicEffectGraph.CopyPicture(picture))
        : value is VectorPicture vector ? VectorPictureEffectProcessing.Copy(vector) : value;

    public void ReleaseDeviceInput(object? value, object? kept, IReadOnlyDictionary<string, object?>? outputs = null)
    {
        if (value is IHwAccelPicture picture && !ReferenceEquals(value, kept)
            && !(outputs?.Values.Any(v => ReferenceEquals(v, value)) ?? false) && Pictures.Remove(picture)) picture.Dispose();
    }

    public int AudioOffset() => Source is IAudioSamples
        ? ValueProviderFrameContext.AudioSampleIndex
        : (int)Math.Max(0, Math.Round(((double)Frame - (Clip?.StartFrame ?? 0))
            * (EffectProcessing.CurrentAudioSource?.SamplePerSecond ?? 48000)
            / (EffectProcessing.CurrentAudioSource?.FrameRate ?? 30)));

    /// <summary>Returns the effective progress of an effect for this frame, or null when the effect is out of range.</summary>
    public float? EffectProgress(IEffect effect)
    {
        if (effect is IVectorPictureEffect) return VectorProgress ?? Progress;
        if (effect is IAudioContinuousEffect audio && (audio.StartPoint != 0 || audio.EndPoint != 0))
        {
            int offset = AudioOffset();
            int length = (Source as IAudioSamples)?.SampleCount ?? 1;
            if (offset + (long)length <= audio.StartPoint || offset > audio.EndPoint) return null;
        }
        if (effect is not IContinuousEffect continuous || !continuous.IsScoped && Clip is null) return Progress;
        long start = continuous.IsScoped ? continuous.StartPoint : Clip?.StartFrame ?? 0;
        long end = continuous.IsScoped ? continuous.EndPoint : start + (Clip?.GetEffectiveDuration() ?? 1);
        return end > start && Frame >= start && Frame < end
            ? Math.Clamp((float)(Frame - start) / (end - start), 0, 1) : null;
    }

    public object ReadPicture()
    {
        if (Source is IPicture or VectorPicture) return Source;
        if (Clip is null) throw new InvalidOperationException("A clip is required to read the source picture.");
        return sourcePicture ??= Track(Clip.GetFrameRelativeToStartPointOfSource(Clip.GetRelativeFrameIndex(Frame) ?? 0,
            Width, Height, IPicture.PicturePixelMode.Float16Picture));
    }

    public DynamicEffectValue InputValue() => new(ReadPicture(), InitialPosition, PreserveAspect);

    public IAudioSamples<float> ReadClipAudio()
    {
        if (audioFrame is not null) return audioFrame;
        return audioFrame = EffectProcessing.CurrentAudioSource?.ReadClipFrame(Clip!, Frame, CancellationToken)
            ?? new FloatAudioSamples
            {
                Channels = [Array.Empty<float>(), Array.Empty<float>()], SampleCount = 0, SamplePerSecond = 48000,
            };
    }

    public object? Fallback(IEffectArgumentField field)
    {
        object? value = field switch
        {
            StaticEffectArgumentField f => f.Value,
            DynamicEffectParamField f => f.StaticFallbackValue,
            EffectArgumentFieldDescriptor => EffectFieldTypes.DefaultValue(field),
            _ => field.GetGetter()(),
        };
        return field.FieldType.IsContent() && !EffectFieldTypes.Accepts(field.FieldType, value) ? null : value;
    }

    public bool ReadCondition(IEffectProvider provider, string key, IEffectArgumentField field, DynamicEffectValue input)
    {
        var value = EffectFieldTypes.ConvertInput(field.FieldType, input.Value ?? Fallback(field));
        Types.ValidateValue(provider, key, true, field, value, Frame);
        return value is bool condition
            ? condition : throw new InvalidOperationException($"Provider {provider.Id} requires a bool condition, frame {Frame}.");
    }

    /// <summary>
    /// Resolves a binding that does not point at a scheduled node: the source picture, the built-in
    /// frame sources, clip position/audio and content sources owned by another stage.
    /// </summary>
    public bool TryResolveExternal(string? sourceId, out DynamicEffectValue value)
    {
        value = default!;
        if (sourceId == IEffectProvider.InputAnchorGUID.ToString())
        {
            value = InputValue();
            return true;
        }
        if (sourceId == ValueProviderFrameContext.ClipPositionProviderId)
        {
            value = new(InitialPosition, InitialPosition, PreserveAspect);
            return true;
        }
        if (sourceId == ValueProviderFrameContext.ClipAudioProviderId)
        {
            value = new(ReadClipAudio(), InitialPosition, PreserveAspect);
            return true;
        }
        if (sourceId is not null && ValueProviderFrameContext.IsBuiltInSource(sourceId))
        {
            value = new(ValueProviderFrameContext.Get(sourceId), InitialPosition, PreserveAspect);
            return true;
        }
        if (!EffectProviderOutputExtensions.TryParseOutputSourceId(sourceId, out var providerId, out _))
        {
            value = Empty();
            return true;
        }
        if (Nodes.ContainsKey(providerId)) return false;
        value = new(Stage == EffectStage.VectorContent && graph.ContentSources.Contains(providerId) ? Source : null,
            InitialPosition, PreserveAspect);
        return true;
    }
}
