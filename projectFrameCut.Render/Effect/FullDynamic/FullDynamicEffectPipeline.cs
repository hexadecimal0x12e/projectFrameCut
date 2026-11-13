using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Context;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

public sealed record EffectPipelineFrameResult(IPicture Picture, ClipPositionTuple Position, bool PreserveAspect);

public interface IRenderEffectPipeline : INormalEffect
{
    EffectStage Stage { get; }
    IEnumerable<IEffect> NodeEffects { get; }
    bool AudioNeedsPicture { get; }
    bool ShouldRender(IClip clip, uint frame);
    object EvaluateRenderValue(IClip clip, uint frame, string field, IAudioSamples<float>? audio = null, CancellationToken token = default);
    EffectPipelineFrameResult Evaluate(IPicture source, IClip? clip, uint frame, int width, int height,
        int relativeWidth, int relativeHeight, ClipPositionTuple initialPosition, bool preserveAspect = true,
        bool processFromCanvas = false, CancellationToken cancellationToken = default,
        Action<IEffect, object?>? afterNode = null, bool keepHwAccelPicture = false);
}

public sealed class FullDynamicEffectPipeline : IRenderEffectPipeline, IDisposable
{
    internal IReadOnlyDictionary<Guid, FullDynamicEffectNode> Nodes { get; }
    internal EffectFieldTypeResolution Types { get; }
    internal HashSet<Guid> ContentSources { get; }
    private readonly string? finalSource;
    private readonly RenderOutputProvider? output;
    private readonly Lock sync = new();
    private bool initialized;
    private bool disposed;
    private int active;
    public EffectStage Stage { get; }
    public bool AudioNeedsPicture { get; }
    public IEnumerable<IEffect> NodeEffects => Nodes.Values.SelectMany(n => n.Effects);
    public bool IsDeterministic => Nodes.Values.All(n => !n.State.Enabled || n.Provider.IsDeterministic && n.Effects.All(e => e.IsDeterministic));
    public string TypeName => "__FullDynamicEffectPipeline__";
    public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
    public string Id { get; set; } = "Pipeline:FullDynamicEffectPipeline";
    public string Name { get; set; } = "Full dynamic effect pipeline";
    public bool Enabled { get; set; } = true;
    public int Index { get; set; }
    public int RelativeWidth { get; set; }
    public int RelativeHeight { get; set; }
    public bool IsReorderable => false;
    public string? BindedEffectProvidingSystemID { get; set; } = "__FullDynamicEffectPipeline__";
    public EffectImplementType ImplementType => EffectImplementType.FullDynamicValueProvider;
    public Dictionary<string, object> Parameters { get; } = [];

    internal FullDynamicEffectPipeline(IReadOnlyDictionary<Guid, IEffectProvider> providers,
        IReadOnlyDictionary<Guid, FullDynamicEffectNode> nodes, EffectFieldTypeResolution types, EffectStage stage)
    {
        Nodes = nodes;
        Types = types;
        Stage = stage;
        finalSource = providers.Values.FirstOrDefault(p => p.Stage == stage && p.IsFinalOutputSource())?.GetFinalOutputSourceId();
        output = providers.Values.OfType<RenderOutputProvider>().SingleOrDefault();
        ContentSources = providers.Values.Where(p => p.Stage is EffectStage.Text or EffectStage.VectorContent && !nodes.ContainsKey(p.Id)).Select(p => p.Id).ToHashSet();
        Index = NodeEffects.Select(e => e.Index).DefaultIfEmpty(0).Min();
        var seen = new HashSet<Guid>();
        bool NeedsPicture(string? source)
        {
            if (source == IEffectProvider.InputAnchorGUID.ToString()) return true;
            return EffectProviderOutputExtensions.TryParseOutputSourceId(source, out var id, out _) && seen.Add(id)
                && nodes.TryGetValue(id, out var node) && node.State.Bindings.Where(p => !EffectProviderAnchorExtensions.IsOutputBindingKey(p.Key)).Any(p => NeedsPicture(p.Value));
        }
        AudioNeedsPicture = output is { Enabled: true } && new[] { RenderOutputProvider.AudioKey, RenderOutputProvider.ShouldRenderKey }
            .Any(k => NeedsPicture(output.AnchorsBindingState.GetValueOrDefault(k)));
    }

    public IEffect WithParameters(Dictionary<string, object> parameters) => throw new NotSupportedException("Rebuild the effect pipeline.");
    public void Initialize()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (initialized) return;
            foreach (var effect in NodeEffects) effect.Initialize();
            initialized = true;
        }
    }

    public IPicture Render(IPicture source, int targetWidth, int targetHeight) => throw new InvalidOperationException("Cannot render this pipeline. Call Evaluate() instead.");

    public EffectPipelineFrameResult Evaluate(IPicture source, IClip? clip, uint frame, int width, int height,
        int relativeWidth, int relativeHeight, ClipPositionTuple initialPosition, bool preserveAspect = true,
        bool processFromCanvas = false, CancellationToken cancellationToken = default, Action<IEffect, object?>? afterNode = null,
        bool keepHwAccelPicture = false)
    {
        var result = EvaluateContent(source, clip, frame, width, height, relativeWidth, relativeHeight, initialPosition,
            preserveAspect, processFromCanvas, cancellationToken, afterNode);
        var picture = (IPicture)result.Content;
        if (!keepHwAccelPicture && picture is IHwAccelPicture gpu)
        {
            try { picture = gpu.Download(); }
            finally { gpu.Dispose(); }
        }
        return new(picture, result.Position, result.PreserveAspect);
    }

    public bool ShouldRender(IClip clip, uint frame) => (bool)EvaluateRenderValue(clip, frame, RenderOutputProvider.ShouldRenderKey, token: ValueProviderFrameContext.CancellationToken);
    public object EvaluateRenderValue(IClip clip, uint frame, string field, IAudioSamples<float>? audio = null, CancellationToken token = default)
    {
        int width = RelativeWidth > 0 ? RelativeWidth : Math.Max(1, clip.TargetWidth);
        int height = RelativeHeight > 0 ? RelativeHeight : Math.Max(1, clip.TargetHeight);
        return EvaluateContent(audio is null ? clip.PositionTuple : audio, clip, frame, width, height, width, height, clip.PositionTuple,
            cancellationToken: token, afterNode: ValueProviderFrameContext.NodeObserver, requestedOutput: field).Content;
    }

    internal sealed record ContentResult(object Content, ClipPositionTuple Position, bool PreserveAspect);
    internal ContentResult EvaluateContent(object source, IClip? clip, uint frame, int width, int height, int relativeWidth,
        int relativeHeight, ClipPositionTuple initialPosition, bool preserveAspect = true, bool processFromCanvas = false,
        CancellationToken cancellationToken = default, Action<IEffect, object?>? afterNode = null,
        float? clipProgress = null, float? vectorProgress = null, IPicture? underPicture = null,
        IPicture.PicturePixelMode? mixturePixelMode = null, string? requestedOutput = null, string? requestedBinding = null)
    {
        using var gpuScope = HwAccelPictureScope.Current is null ? new HwAccelPictureScope() : null;
        using var gpuEntry = gpuScope?.Enter();
        lock (sync) { Initialize(); active++; }
        var previousBuffer = IRenderContext.CurrentFrameBuffer;
        float progress = clipProgress ?? (clip is not null ? Math.Clamp(((float)frame - clip.StartFrame) / Math.Max(1u, clip.GetEffectiveDuration()), 0, 1)
            : Convert.ToSingle(ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInProgressProviderId) ?? 0f));
        using var clipScope = ValueProviderFrameContext.PushClip(clip ?? ValueProviderFrameContext.CurrentClip);
        using var frameScope = ValueProviderFrameContext.PushFrame(frame, progress, reuseComputed: true);
        var execution = new FullDynamicEffectExecution(this, new EffectExecutionContext
        {
            Clip = clip, FrameIndex = frame, Progress = vectorProgress ?? progress, ClipProgress = progress,
            AudioSampleIndex = source is IAudioSamples ? ValueProviderFrameContext.AudioSampleIndex
                : (int)Math.Max(0, Math.Round(((double)frame - (clip?.StartFrame ?? 0)) * (EffectProcessing.CurrentAudioSource?.SamplePerSecond ?? 48000)
                    / (EffectProcessing.CurrentAudioSource?.FrameRate ?? 30))),
            TargetWidth = width, TargetHeight = height, RelativeWidth = relativeWidth, RelativeHeight = relativeHeight,
            CancellationToken = cancellationToken,
        }, source, initialPosition, preserveAspect, processFromCanvas, underPicture, mixturePixelMode, afterNode);
        object? returned = null;
        try
        {
            FullDynamicValue Terminal(string field, object fallback)
            {
                var binding = output?.AnchorsBindingState.GetValueOrDefault(field);
                if (output is not { Enabled: true }) return new(FullDynamicInputState.Available, fallback, initialPosition, preserveAspect);
                var value = string.IsNullOrWhiteSpace(binding) || binding == IEffectProvider.NoConnectionGUID.ToString()
                    ? new FullDynamicValue(FullDynamicInputState.Available, output.Fields.GetValueOrDefault(field)?.GetGetter()(), initialPosition, preserveAspect)
                    : execution.Resolve(binding);
                FullDynamicEffectExecution.Require(value);
                return value.State == FullDynamicInputState.Inactive || value.Value is null or ""
                    ? value with { Value = fallback } : value;
            }
            execution.RunSideEffects();
            FullDynamicValue result;
            if (requestedBinding is not null) result = execution.Resolve(requestedBinding);
            else if (Stage == EffectStage.Render)
            {
                bool visible = Terminal(RenderOutputProvider.ShouldRenderKey, true).Value is not false;
                if (requestedOutput == RenderOutputProvider.ShouldRenderKey) result = new(FullDynamicInputState.Available, visible, initialPosition, preserveAspect);
                else if (requestedOutput == RenderOutputProvider.AudioKey)
                {
                    var audio = execution.ReadAudio();
                    result = visible ? Terminal(RenderOutputProvider.AudioKey, audio)
                        : new(FullDynamicInputState.Available, new FloatAudioSamples
                        {
                            Channels = Enumerable.Range(0, audio.channelCount).Select(_ => new float[audio.SampleCount]).ToArray(),
                            SampleCount = audio.SampleCount, SamplePerSecond = audio.SamplePerSecond,
                        }, initialPosition, preserveAspect);
                }
                else
                {
                    result = requestedOutput == RenderOutputProvider.PositionKey ? Terminal(RenderOutputProvider.PositionKey, initialPosition)
                        : !visible ? new(FullDynamicInputState.Available, execution.Track(Timeline.CreateTransparentFrame(width, height, IPicture.PicturePixelMode.Float16Picture)), initialPosition, preserveAspect)
                        : finalSource is null ? new(FullDynamicInputState.Available, execution.ReadPicture(), initialPosition, preserveAspect) : execution.Resolve(finalSource);
                    var position = Terminal(RenderOutputProvider.PositionKey, result.Position);
                    if (position.Value is not ClipPositionTuple pos) throw new InvalidOperationException("Render position must be a ClipPositionTuple.");
                    result = result with { Position = initialPosition.Apply(pos), PreserveAspect = result.PreserveAspect && position.PreserveAspect };
                }
            }
            else result = finalSource is null ? new(FullDynamicInputState.Available, source, initialPosition, preserveAspect) : execution.Resolve(finalSource);
            var value = FullDynamicEffectExecution.Require(result);
            if (result.State != FullDynamicInputState.Available || requestedBinding is null && value is null) throw new InvalidOperationException($"Full dynamic {Stage} output is unavailable, clip {clip?.Id}, frame {frame}.");
            var outputType = requestedOutput switch
            {
                RenderOutputProvider.ShouldRenderKey => EffectArgumentFieldType.Boolean,
                RenderOutputProvider.PositionKey => EffectArgumentFieldType.SizeAndPosition,
                RenderOutputProvider.AudioKey => EffectArgumentFieldType.AudioSample,
                _ => Stage.GetContentType(),
            };
            if (requestedBinding is null && !EffectFieldTypes.Accepts(outputType, value)) throw new InvalidOperationException($"Invalid {Stage} terminal type.");
            if (requestedOutput != RenderOutputProvider.ShouldRenderKey && clip is not null && ClipValueFrameContext.Current is { } endpoints)
                foreach (var node in Nodes.Values.Where(n => n.State.Enabled && n.Provider is IClipValueEndpointProvider { IsExport: true }))
                {
                    var exported = execution.Resolve(node.State.Id.ToString());
                    if (exported.State == FullDynamicInputState.Inactive) continue;
                    var exportValue = FullDynamicEffectExecution.Require(exported) ?? throw new ClipValueBindingException($"Endpoint {node.State.Id} produced no value.");
                    endpoints.Publish(clip, frame, node.State.Id, endpoints.Snapshot(exportValue));
                }
            cancellationToken.ThrowIfCancellationRequested();
            returned = value;
            return new(value!, result.Position, result.PreserveAspect);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Log(ex, $"Full dynamic {Stage}, clip {clip?.Id}, frame {frame}", this);
            throw;
        }
        finally
        {
            IRenderContext.CurrentFrameBuffer = previousBuffer;
            execution.Dispose();
            foreach (var picture in execution.Pictures)
                if (!ReferenceEquals(picture, returned))
                    try { picture.Dispose(); }
                    catch (Exception ex) { Logger.Log(ex, "Release full dynamic frame picture", this); }
            lock (sync) { if (--active == 0 && disposed) Release(); }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            if (active == 0) Release();
        }
    }
    private void Release()
    {
        foreach (var effect in NodeEffects.OfType<IDisposable>().Distinct())
            try { effect.Dispose(); }
            catch (Exception ex) { Logger.Log(ex, "Release full dynamic effect", this); }
    }
}
