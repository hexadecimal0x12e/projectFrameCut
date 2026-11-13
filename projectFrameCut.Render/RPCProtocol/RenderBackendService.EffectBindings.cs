using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;
using System.Globalization;
using System.Text.Json;

namespace projectFrameCut.Render.RPCProtocol;

public sealed partial class RenderBackendService
{
    private async ValueTask<EffectBindingPreviewResponse> RenderEffectBindingPreviewAsync(EffectBindingPreviewRequest request, CancellationToken token)
    {
        if (request.Width is <= 0 or > 16384 || request.Height is <= 0 or > 16384
            || !Enum.IsDefined((EffectStage)request.Stage))
            throw new ArgumentException("Invalid effect binding preview parameters.");
        var session = GetSession(request.SessionId);
        using var operation = session.BeginOperation(token);
        token = operation.Token;
        return await Task.Run(() =>
        {
            using var sources = ProjectExternalSourceRuntime.Use(session.ExternalSources);
            using var effects = new EffectRuntimeContext();
            using var effectScope = effects.Enter();
            using var gpu = new HwAccelPictureSession();
            using var gpuScope = new HwAccelPictureScope(gpu);
            using var gpuEntry = gpuScope.Enter();
            var draft = JsonSerializer.Deserialize<DraftStructureJSON>(request.TimelineJson, _jsonOptions)
                ?? throw new ArgumentException("Timeline JSON is invalid.");
            IClip[] clips = [];
            ISoundTrack[] tracks = [];
            try
            {
                clips = CreateClips(draft, session.Assets, "", session.ProjectRoot, token, null, new Dictionary<Guid, string>());
                tracks = CreateSoundTracks(draft, clips, session.Assets, session.ProjectRoot, token, null, new Dictionary<string, string>());
                var clip = clips.FirstOrDefault(c => c.Id == request.ClipId)
                    ?? throw new ArgumentException($"Clip '{request.ClipId}' was not found in the preview snapshot.");
                if (clip.ClipType == ClipMode.AudioClip) clip.ReInit(IPicture.PicturePixelMode.Float16Picture);
                var audio = new AudioSampleSource(clips, tracks, session.FrameRate) { TotalProjectFrames = ResolveDuration(draft) };
                using var audioScope = EffectProcessing.OpenAudioFrame(audio);
                return new EffectBindingPreviewCapture(request, clip, clips, audio, token).Run();
            }
            finally { DisposeResources(clips, tracks, null); }
        }, token).ConfigureAwait(false);
    }

    private sealed class EffectBindingPreviewCapture(EffectBindingPreviewRequest request, IClip owner,
        IClip[] clips, AudioSampleSource audio, CancellationToken token)
    {
        private readonly EffectBindingPreviewResponse result = new();
        private readonly Dictionary<Guid, EffectBindingPreviewStep> steps = [];
        private readonly Dictionary<string, EffectBindingPreviewValue> values = [];
        private readonly Dictionary<Guid, IEffectProvider> providers = (owner.EffectProvidersInstances ?? []).ToDictionary(p => p.Id);
        private readonly uint frame = (uint)Math.Clamp((double)request.FrameIndex, owner.StartFrame,
            (double)owner.StartFrame + Math.Max(1u, owner.GetEffectiveDuration()) - 1);
        private int Width => request.Width;
        private int Height => request.Height;
        private int ProjectWidth => Math.Max(1, request.ProjectWidth);
        private int ProjectHeight => Math.Max(1, request.ProjectHeight);

        public EffectBindingPreviewResponse Run()
        {
            try
            {
                using var project = ValueProviderFrameContext.PushProject(audio.TotalProjectFrames ?? 0);
                using var observer = ValueProviderFrameContext.ObserveNodes(Capture);
                using var endpoints = ClipValuePassThrough.Open(clips, frame, Width, Height,
                    ProjectWidth, ProjectHeight, IPicture.PicturePixelMode.Float16Picture, token);
                var clip = ClipArgumentBinding.InitializeFrame(owner, frame);
                if (owner.ClipType != ClipMode.AudioClip && ClipArgumentBinding.ShouldRender(clip)) RenderPicture(clip);
                CaptureTerminals(clip);
                if ((EffectStage)request.Stage == EffectStage.Composition && clip.SpeedVarianceProviderInstance is CustomSpeedVarianceEffect speed)
                {
                    uint local = frame - clip.StartFrame;
                    speed.InspectFrame(local, clip.Duration, clip.GetAvailableSourceLength(), (finished, sourceFrame) =>
                    {
                        Capture(speed, new Dictionary<string, object?>
                        {
                            [CustomSpeedVarianceProvider.FinishedKey] = finished,
                            [CustomSpeedVarianceProvider.SourceFrameKey] = sourceFrame,
                        });
                        foreach (var source in request.BuiltInSources.Where(ValueProviderFrameContext.IsSpeedSource))
                            result.Sources[source] = Snapshot(ValueProviderFrameContext.Get(source), false);
                    }, token);
                }
                using var clipScope = ValueProviderFrameContext.PushClip(clip);
                using var frameScope = ValueProviderFrameContext.PushFrame(frame,
                    Math.Clamp(((float)frame - clip.StartFrame) / Math.Max(1u, clip.GetEffectiveDuration()), 0, 1), false, token);
                result.Sources["position"] = Snapshot(clip.PositionTuple, false);
                result.Sources["audio"] = Snapshot((EffectStage)request.Stage == EffectStage.Render
                    ? audio.ReadClipFrame(clip, frame, token) : audio.ReadFrame(frame, token), false);
                foreach (var source in request.BuiltInSources.Where(s => ValueProviderFrameContext.IsBuiltInSource(s) && !ValueProviderFrameContext.IsSpeedSource(s)))
                    result.Sources[source] = Snapshot(ValueProviderFrameContext.Get(source), false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log(ex, $"Preview effect bindings for clip {owner.Id}", this);
                ReportError(ex);
            }
            return result;
        }

        private void RenderPicture(IClip clip)
        {
            var ppb = IPicture.PicturePixelMode.Float16Picture;
            IPicture? under = null;
            try
            {
                if ((EffectStage)request.Stage == EffectStage.Composition)
                {
                    using var ignored = ValueProviderFrameContext.ObserveNodes((_, _) => { });
                    using var background = Timeline.FallBackImageGetter(Width, Height);
                    var lower = clips.Where(c => c.ClipType is not (ClipMode.AudioClip or ClipMode.MarkingClip)
                        && ClipLayerOrder.BottomToTop.Compare(c, owner) < 0).ToArray();
                    under = Timeline.MixtureLayers(Timeline.GetFramesInOneFrame(lower, frame, Width, Height, ppb,
                        ProjectWidth, ProjectHeight), frame, Width, Height, ppb.Value,
                        projectRelativeWidth: ProjectWidth, projectRelativeHeight: ProjectHeight,
                        disposeIntermediateFrames: true, cancellationToken: token, allClips: clips, basePicture: background);
                    result.Sources["under"] = Snapshot(under);
                }
                IPicture ReadSource(bool native = false)
                {
                    using var context = ValueProviderFrameContext.PushFrame(frame,
                        Math.Clamp(((float)frame - clip.StartFrame) / Math.Max(1u, clip.GetEffectiveDuration()), 0, 1), false, token);
                    var source = clip;
                    if (native && clip is IVectorContentClip)
                    {
                        source = clip.CopyForFrame();
                        source.EffectsInstances = clip.EffectsInstances?.Where(e => e is not IVectorPictureEffect).ToArray();
                    }
                    return VideoClipRotation.ReadFrame(source, clip.GetRelativeFrameIndex(frame) ?? 0,
                        clip.TargetWidth > 0 ? Math.Max(1, (int)Math.Round((double)clip.TargetWidth * Width / ProjectWidth)) : Width,
                        clip.TargetHeight > 0 ? Math.Max(1, (int)Math.Round((double)clip.TargetHeight * Height / ProjectHeight)) : Height, ppb);
                }
                using var source = ReadSource();
                result.Sources["input"] = Snapshot(source);
                if ((EffectStage)request.Stage == EffectStage.VectorContent)
                {
                    using var native = ReadSource(true);
                    result.Sources["input"] = Snapshot(native);
                }
                using var output = Timeline.MixtureLayers([new OneFrame(frame, clip, source, resolveEffects: false)],
                    frame, Width, Height, ppb.Value, AfterEffectCallback: (effect, picture) => Capture(effect, picture),
                    projectRelativeWidth: ProjectWidth, projectRelativeHeight: ProjectHeight,
                    disposeIntermediateFrames: true, cancellationToken: token,
                    afterNodeCallback: (effect, value) => { if (value is not IPicture) Capture(effect, value); },
                    allClips: clips, basePicture: under);
                result.Sources["output"] = Snapshot(output);
            }
            finally { under?.Dispose(); }
        }

        private void CaptureTerminals(IClip clip)
        {
            foreach (var terminal in request.Terminals)
            {
                token.ThrowIfCancellationRequested();
                if (!providers.TryGetValue(terminal.ProviderId, out var provider) || terminal.Fields.Count == 0) continue;
                try
                {
                    object? value = null;
                    if (provider is RenderOutputProvider && clip.EffectsInstances?.OfType<IRenderEffectPipeline>()
                        .FirstOrDefault(g => g.Stage == EffectStage.Render) is { } render)
                        value = render.EvaluateRenderValue(clip, frame, terminal.Fields[0],
                            clip.ClipType == ClipMode.AudioClip ? audio.ReadClipFrame(clip, frame, token) : null, token);
                    var step = new EffectBindingPreviewStep { NodeId = terminal.NodeId, ProviderId = terminal.ProviderId };
                    if (value is not null) step.Outputs[terminal.Fields[0]] = Snapshot(value, providerId: provider.Id, fieldId: terminal.Fields[0]);
                    else foreach (var field in terminal.Fields)
                        step.Outputs[field] = Snapshot(clip.ArgumentFields.GetValueOrDefault(field)?.ReadValue(clip), providerId: provider.Id, fieldId: field);
                    if (request.RecordSteps) CaptureInputs(step, provider);
                    result.Steps.Add(step);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log(ex, $"Preview effect terminal {terminal.NodeId}", this);
                    ReportError(ex, provider.Id, terminal.Fields[0]);
                }
            }
        }

        private void Capture(IEffect effect, object? value)
        {
            token.ThrowIfCancellationRequested();
            if (!Guid.TryParse(effect.BindedEffectProvidingSystemID, out var id)
                || !providers.TryGetValue(id, out var provider) || provider is RenderOutputProvider or ClipArgumentProvider) return;
            try
            {
                if (!steps.TryGetValue(id, out var step))
                {
                    step = new() { ProviderId = id };
                    if (request.RecordSteps) CaptureInputs(step, provider);
                    steps.Add(id, step);
                    result.Steps.Add(step);
                }
                if (value is IReadOnlyDictionary<string, object?> outputs)
                    foreach (var (field, output) in outputs) AddOutput(step, field, output);
                else AddOutput(step, provider.GetOutputFields().Keys.FirstOrDefault() ?? "", value);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log(ex, $"Capture effect preview for provider {id}", this);
                ReportError(ex, id);
            }
        }

        private void AddOutput(EffectBindingPreviewStep step, string field, object? value)
        {
            var snapshot = Snapshot(value, providerId: step.ProviderId, fieldId: field);
            step.Outputs[field] = snapshot;
            if (!string.IsNullOrWhiteSpace(field))
                values[EffectProviderOutputExtensions.CreateOutputSourceId(step.ProviderId, field)] = snapshot;
            values[step.ProviderId.ToString()] = snapshot;
        }

        private void CaptureInputs(EffectBindingPreviewStep step, IEffectProvider provider)
        {
            foreach (var (key, field) in DynamicEffectBindings.InputFields(provider))
            {
                string? source = key == EffectProviderAnchorExtensions.InputKey ? provider.GetMainInputSource()
                    : provider.TryGetFieldBinding(key, out var binding) ? binding : null;
                if (!string.IsNullOrWhiteSpace(source) && source != IEffectProvider.NoConnectionGUID.ToString())
                {
                    var value = source switch
                    {
                        ValueProviderFrameContext.ClipPositionProviderId => (ValueProviderFrameContext.CurrentClip ?? owner).PositionTuple,
                        ValueProviderFrameContext.ClipAudioProviderId => audio.ReadClipFrame(ValueProviderFrameContext.CurrentClip ?? owner, frame, token),
                        _ => ValueProviderFrameContext.Get(source),
                    };
                    var snapshot = value is not null ? Snapshot(value, false)
                        : values.GetValueOrDefault(source)
                        ?? (source == IEffectProvider.InputAnchorGUID.ToString() ? result.Sources.GetValueOrDefault("input") : null)
                        ?? Snapshot(null, false);
                    step.Inputs[key] = new() { Text = snapshot.Text, Inactive = snapshot.Inactive, IsVector = snapshot.IsVector };
                }
                else step.Inputs[key] = Snapshot(field switch
                {
                    StaticEffectArgumentField s => s.Value,
                    DynamicEffectParamField d => d.StaticFallbackValue,
                    _ => field.GetGetter()?.Invoke(),
                }, false);
            }
        }

        private void ReportError(Exception ex, Guid? providerId = null, string? fieldId = null)
        {
            var origin = EffectProviderFailure.Read(ex);
            providerId = origin.ProviderId ?? providerId;
            fieldId = origin.FieldId ?? fieldId;
            var message = EffectProviderFailure.Describe(ex);
            result.Error ??= message;
            if (!result.Diagnostics.Any(d => d.ProviderId == (providerId ?? Guid.Empty) && d.FieldId == fieldId && d.Message == message))
                result.Diagnostics.Add(new() { ProviderId = providerId ?? Guid.Empty, FieldId = fieldId, Message = message });
        }

        private EffectBindingPreviewValue Snapshot(object? value, bool image = true, Guid? providerId = null, string? fieldId = null)
        {
            token.ThrowIfCancellationRequested();
            var snapshot = new EffectBindingPreviewValue
            {
                Inactive = value is null,
                IsVector = value is VectorPicture,
                Text = value switch
                {
                    null => "-",
                    IPicture p => $"{p.Width}×{p.Height}",
                    VectorPicture => "Vector",
                    IAudioSamples a => $"{a.SampleCount} / {a.SamplePerSecond} Hz / {a.channelCount} ch",
                    float f => f.ToString("0.####", CultureInfo.InvariantCulture),
                    double d => d.ToString("0.####", CultureInfo.InvariantCulture),
                    bool b => b ? "true" : "false",
                    _ => value.ToString() ?? "-",
                },
            };
            if (!image) return snapshot;
            try
            {
                using var raster = value is VectorPicture vector
                    ? IVectorContentClip.GlobalDefaultRasterizer.Convert(vector, Width, Height, true, cancellationToken: token) : null;
                if ((raster ?? value as IPicture) is not { } picture) return snapshot;
                double scale = Math.Min(1, 512d / Math.Max(picture.Width, picture.Height));
                using var resized = scale < 1 ? picture.Resize(Math.Max(1, (int)(picture.Width * scale)), Math.Max(1, (int)(picture.Height * scale)), false) : null;
                using var downloaded = (resized ?? picture) is IHwAccelPicture gpu ? gpu.Download() : null;
                snapshot.Png = PreviewFrameData.ToPng(downloaded ?? resized ?? picture);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log(ex, "Snapshot effect binding preview", this);
                ReportError(ex, providerId, fieldId);
            }
            return snapshot;
        }
    }
}
