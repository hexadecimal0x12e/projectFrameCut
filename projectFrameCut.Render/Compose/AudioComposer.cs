using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.Effect;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;

namespace projectFrameCut.Render.Compose
{
    public class AudioComposer<T>
    {
        private const int DefaultChunkSampleCount = 40960;

        public AudioWriterBase<T>? Writer { get; init; }
        public required IClip[] Clips { get; init; }
        public ISoundTrack[]? SoundTracks { get; init; }
        public AudioSampleSource? SampleSource { get; init; }
        public uint StartFrame { get; set; } = 0;
        public uint Duration { get; set; } = uint.MaxValue;
        public event Action<double, TimeSpan>? OnProgressChanged;
        public bool LogState = false;



        /// <summary>
        /// Compose audio and write directly to <paramref name="writer"/>.
        /// </summary>
        public void Compose(
            int videoFramerate = 30,
            int samplerate = 48000,
            int channels = 2,
            int chunkSampleCount = DefaultChunkSampleCount,
            CancellationToken? cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(Clips);
            ArgumentNullException.ThrowIfNull(Writer);
            if (chunkSampleCount <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSampleCount));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

            int totalSamples;
            lock (AudioSampleSource.Sync) totalSamples = BuildAudioContexts(videoFramerate, samplerate).totalSamples;
            var source = SampleSource ?? new AudioSampleSource(Clips, SoundTracks ?? [], videoFramerate, samplerate, channels);
            if (source.FrameRate != videoFramerate || source.SamplePerSecond != samplerate || source.ChannelCount != channels)
                throw new ArgumentException("The shared audio source must match the composition frame rate, sample rate and channels.", nameof(SampleSource));

            Writer.SamplePerSecond = samplerate;
            Writer.ChannelCount = channels;
            Writer.Initialize();

            if (totalSamples <= 0)
            {
                return;
            }
            Stopwatch elapsed = Stopwatch.StartNew();
            Log($"[AudioComposer] Composing {totalSamples} samples.");
            for (int chunkStart = 0; chunkStart < totalSamples;)
            {
                if (cancellationToken?.IsCancellationRequested == true)
                {
                    Log($"[AudioComposer] Cancellation requested, stopping composition.");
                    return;
                }
                int chunkLength = Math.Min(chunkSampleCount, totalSamples - chunkStart);
                Writer.Append(source.ReadSamples(checked(FrameToSample(StartFrame, videoFramerate, samplerate) + chunkStart), chunkLength, cancellationToken ?? default));

                var prog = (double)(chunkStart + chunkLength) / totalSamples;
                TimeSpan etr = TimeSpan.Zero;
                if (prog > 0.005)
                {
                    double totalEst = elapsed.Elapsed.TotalSeconds / prog;
                    double remaining = totalEst - elapsed.Elapsed.TotalSeconds;
                    if (remaining > 0) etr = TimeSpan.FromSeconds(remaining);
                }

                OnProgressChanged?.Invoke(prog, etr);

                if (LogState && chunkStart % 200 == 0) Log($"[AudioComposer] Finished {(float)(chunkStart / (float)totalSamples):p2} ({chunkStart} of {totalSamples})");
                chunkStart += chunkLength;
            }
            Log($"[AudioComposer] Composed {totalSamples} samples in {elapsed.Elapsed.TotalSeconds:F2}s.");
        }

        public Func<int, int, CancellationToken, FloatAudioSamples> CreateSampleReader(
            int videoFramerate, int samplerate, int channels, out int totalSamples)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
            lock (AudioSampleSource.Sync)
            {
                var (contexts, count) = BuildAudioContexts(videoFramerate, samplerate);
                var transforms = BuildTransforms(contexts, videoFramerate, samplerate);
                totalSamples = count;
                int cachedFrame = -1;
                FloatAudioSamples? cachedSamples = null;
                int origin = FrameToSample(StartFrame, videoFramerate, samplerate);
                return (start, length, token) =>
                {
                    ArgumentOutOfRangeException.ThrowIfNegative(start);
                    ArgumentOutOfRangeException.ThrowIfNegative(length);
                    _ = checked(start + length);
                    lock (AudioSampleSource.Sync)
                    {
                        token.ThrowIfCancellationRequested();
                        using var audioScope = ValueProviderFrameContext.PushAudioSource(null);
                        using var frameScope = ValueProviderFrameContext.PushFrame(0, 0, false, token);
                        FloatAudioSamples ReadWindow(int start, int length)
                        {
                            var mixed = CreateZeroChannels(channels, length);
                            foreach (var context in contexts)
                            {
                                token.ThrowIfCancellationRequested();
                                int first = Math.Max(start, context.TimelineStartSample);
                                int end = Math.Min(start + length, context.TimelineEndSample);
                                if (end <= first) continue;
                                foreach (var transform in context.Transforms)
                                {
                                    if (transform.StartSample >= end) break;
                                    if (transform.EndSample <= first) continue;
                                    MixContextIntoChunk(context, start, first, Math.Min(end, transform.StartSample), samplerate, channels, mixed);
                                    first = Math.Min(end, transform.EndSample);
                                }
                                MixContextIntoChunk(context, start, first, end, samplerate, channels, mixed);
                            }
                            foreach (var transform in transforms)
                            {
                                token.ThrowIfCancellationRequested();
                                if (transform.StartSample < start + length && transform.EndSample > start)
                                    MixTransformIntoChunk(transform, start, length, videoFramerate, samplerate, channels, mixed);
                            }
                            return new FloatAudioSamples { Channels = mixed, SampleCount = length, SamplePerSecond = samplerate };
                        }
                        var result = new FloatAudioSamples { Channels = CreateZeroChannels(channels, length), SampleCount = length, SamplePerSecond = samplerate };
                        for (int position = start; position < start + length;)
                        {
                            token.ThrowIfCancellationRequested();
                            int frame = SampleToFrame(checked(origin + position), videoFramerate, samplerate);
                            int first = FrameToSample(frame, videoFramerate, samplerate) - origin;
                            int end = FrameToSample(frame + 1, videoFramerate, samplerate) - origin;
                            if (cachedSamples is null || cachedFrame != frame)
                            {
                                cachedSamples = ReadWindow(first, end - first);
                                cachedFrame = frame;
                            }
                            int n = Math.Min(start + length, end) - position;
                            for (int c = 0; c < channels; c++)
                                cachedSamples.Channels[c].AsSpan(position - first, n).CopyTo(result.Channels[c].AsSpan(position - start, n));
                            position += n;
                        }
                        return result;
                    }
                };
            }
        }


        private (List<SoundTrackContext> contexts, int totalSamples) BuildAudioContexts(
            int videoFramerate,
            int outputSampleRate)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(videoFramerate);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputSampleRate);

            int renderStartSample = FrameToSample(StartFrame, videoFramerate, outputSampleRate);
            int? requestedDurationSamples = Duration == uint.MaxValue ? null
                : checked(FrameToSample((double)StartFrame + Duration, videoFramerate, outputSampleRate) - renderStartSample);
            int renderEndSample = requestedDurationSamples is null
                ? int.MaxValue
                : SafeAdd(renderStartSample, requestedDurationSamples.Value);

            List<SoundTrackContext> contexts = new();
            int totalSamples = requestedDurationSamples ?? 0;

            if (SoundTracks != null)
            {
                foreach (var track in SoundTracks)
                {
                    if (!SoundTrackMetadata.ReadBool(track.ExtraData, SoundTrackMetadata.EnabledKey, true))
                    {
                        continue;
                    }
                    if (track.NeedFilePath && string.IsNullOrWhiteSpace(track.FilePath))
                    {
                        continue;
                    }

                    var owner = Clips.FirstOrDefault(c => c.BindedSoundTrack == track.Id || c.Id.ToString() == track.Id)
                        ?? Clips.FirstOrDefault(c => c.Id == SoundTrackMetadata.ReadSourceClipId(track.ExtraData)
                            && !SoundTrackMetadata.ReadBool(c.ExtraData, SoundTrackMetadata.DetachedKey));
                    if (owner is not null && owner.EffectProviders is { Length: > 0 })
                    {
                        if (owner.EffectProvidersInstances is not { Length: > 0 }) EffectHelper.ResolveClipEffects(owner);
                        if (owner.EffectsInstances?.OfType<IRenderEffectPipeline>().Any(g => g.AudioNeedsPicture) == true
                            && owner is VideoClip { Decoder: null } or PhotoClip { source: null })
                            owner.ReInit(projectFrameCut.Drawing.Base.IPicture.PicturePixelMode.Float16Picture);
                    }
                    SpeedVarianceFrameMap? speedMap = owner?.SpeedVarianceProviderInstance is IDirectSpeedVarianceProvider speed
                        ? speed.GetFrameMap(owner.Duration, owner.GetAvailableSourceLength()) : null;
                    float ratio = track.Ratio <= 0f ? 1f : track.Ratio;
                    int trackStartSample = FrameToSample(track.StartFrame, videoFramerate, outputSampleRate);
                    int durationFrames = speedMap is not null ? checked((int)speedMap.Length) : (int)Math.Max(0, Math.Round(track.Duration * ratio));
                    int trackDurationSamples = FrameToSample(durationFrames, videoFramerate, outputSampleRate);
                    if (trackDurationSamples <= 0)
                    {
                        continue;
                    }

                    int trackEndSample = FrameToSample((ulong)track.StartFrame + (uint)durationFrames, videoFramerate, outputSampleRate);
                    int overlapStartSample = Math.Max(trackStartSample, renderStartSample);
                    int overlapEndSample = Math.Min(trackEndSample, renderEndSample);
                    if (overlapEndSample > overlapStartSample)
                        totalSamples = Math.Max(totalSamples, overlapEndSample - renderStartSample);
                    if (SoundTrackMetadata.IsSourceUnavailable(track.ExtraData)) continue;
                    int sourceSampleRate = track.SamplePerSecond;
                    if (sourceSampleRate <= 0)
                        throw new InvalidOperationException($"Soundtrack '{track.Name}' ({track.Id}) has no initialized audio source.");
                    int sourceStartSample = FrameToSample(track.RelativeStartFrame, videoFramerate, sourceSampleRate);
                    IEffect[] effects = (track.EffectsInstances ?? Array.Empty<IEffect>())
                        .Where(e => e.Enabled)
                        .OrderBy(e => e.Index)
                        .ToArray();
                    contexts.Add(new SoundTrackContext
                    {
                        SoundTrack = track,
                        Owner = owner ?? track.EffectStagePlan?.Owner,
                        ClipInfo = new TransformClipInfo(Guid.TryParse(track.Id, out var id) ? id :
                            SoundTrackMetadata.ReadSourceClipId(track.ExtraData) ?? Guid.Empty,
                            track.StartFrame, (uint)durationFrames, track.LayerIndex, track.SubLayerIndex, track.EffectProviders ?? []),
                        SourceSampleRate = sourceSampleRate,
                        FrameRate = videoFramerate,
                        TimelineStartSample = trackStartSample - renderStartSample,
                        TimelineEndSample = trackEndSample - renderStartSample,
                        SourceStartSample = sourceStartSample,
                        Volume = track.Volume,
                        Ratio = ratio,
                        SpeedMap = speedMap,
                        Effects = effects
                    });
                }
            }

            var stageClips = Clips.Concat(contexts.Select(c => c.Owner).OfType<IClip>()).DistinctBy(c => c.Id)
                .Where(ClipValuePassThrough.HasEndpoints).ToArray();
            foreach (var context in contexts)
            {
                context.StageClips = stageClips;
                context.AudioContexts = contexts;
                if (context.SoundTrack.EffectStagePlan is { } plan && plan.Owner.SpeedVarianceProviderInstance is not IDirectSpeedVarianceProvider)
                    plan.Owner.Duration = context.ClipInfo.Duration;
            }
            return (contexts, totalSamples);
        }


        private static void MixContextIntoChunk(
            SoundTrackContext context,
            int chunkStart,
            int start,
            int end,
            int outputSampleRate,
            int outputChannels,
            float[][] mixed)
        {
            int overlapStart = Math.Max(start, context.TimelineStartSample);
            int overlapEnd = Math.Min(end, context.TimelineEndSample);
            if (overlapEnd <= overlapStart)
            {
                return;
            }

            while (overlapStart < overlapEnd)
            {
                int absolute = FrameToSample(context.ClipInfo.Start, context.FrameRate, outputSampleRate) + overlapStart - context.TimelineStartSample;
                int frame = SampleToFrame(absolute, context.FrameRate, outputSampleRate);
                int boundary = overlapStart + FrameToSample(frame + 1, context.FrameRate, outputSampleRate) - absolute;
                int length = Math.Min(overlapEnd, boundary) - overlapStart;
                int offset = overlapStart - context.TimelineStartSample;
                int frameStart = FrameToSample(frame, context.FrameRate, outputSampleRate);
                int frameLength = FrameToSample(frame + 1, context.FrameRate, outputSampleRate) - frameStart;
                if (context.RenderedFrame != frame || context.RenderedSamples is null)
                {
                    int localStart = offset - (absolute - frameStart);
                    context.RenderedSamples = ApplyAudioEffects(context,
                        ReadClipWindowToFloat(context, localStart, frameLength, outputSampleRate, outputChannels), localStart);
                    context.RenderedFrame = frame;
                }
                for (int c = 0; c < outputChannels; c++)
                    MixSamples(context.RenderedSamples.GetSamples(c).AsSpan(absolute - frameStart, length),
                        mixed[c].AsSpan(overlapStart - chunkStart, length), context.Volume);
                overlapStart += length;
            }
        }

        internal Func<IClip, int, int, FloatAudioSamples> CreateClipSourceReader(int fps, int sampleRate, int channels)
        {
            var contexts = BuildAudioContexts(fps, sampleRate).contexts;
            var owners = contexts.Where(c => c.Owner is not null).GroupBy(c => c.Owner!.Id).ToDictionary(g => g.Key, g => g.First());
            return (clip, start, length) => owners.TryGetValue(clip.Id, out var context)
                ? ReadClipWindowToFloat(context, start - context.TimelineStartSample, length, sampleRate, channels)
                : new FloatAudioSamples { Channels = CreateZeroChannels(channels, length), SampleCount = length, SamplePerSecond = sampleRate };
        }

        private List<AudioTransformContext> BuildTransforms(List<SoundTrackContext> contexts, int fps, int sampleRate)
        {
            var index = new TransformProcessing.Index(contexts.Where(c => c.ClipInfo.Id != Guid.Empty).Select(c => c.ClipInfo).ToArray());
            var result = new List<AudioTransformContext>();
            int renderStart = FrameToSample(StartFrame, fps, sampleRate);
            foreach (var context in contexts.Where(c => c.ClipInfo.Id != Guid.Empty))
            {
                foreach (var side in Enum.GetValues<TransformSide>())
                {
                    var resolved = index.Resolve(context.ClipInfo.Id, side);
                    if (resolved is not { Duration: > 0 } || resolved.Owner.Id != context.ClipInfo.Id) continue;
                    var transform = context.Effects.OfType<ITransform>().SingleOrDefault(e =>
                        e.BindedEffectProvidingSystemID == resolved.Provider.Id.ToString())
                        ?? throw new InvalidOperationException($"Missing audio transform instance for provider {resolved.Provider.Id}.");
                    var mode = TransformProcessing.ReadEnum<TransformInputMode>(resolved.Provider.MetaData, TransformProcessing.ModeKey);
                    var required = TransformDefinition.Audio | (mode == TransformInputMode.OneInput ? TransformDefinition.SupportOneInput : TransformDefinition.SupportTwoInput);
                    if (!transform.Definition.HasFlag(required))
                        throw new NotSupportedException($"Transform {transform.TypeName} does not support {mode} audio.");
                    result.Add(new AudioTransformContext
                    {
                        Left = context,
                        Right = resolved.Right is { } right ? contexts.Single(c => c.ClipInfo.Id == right.Id) : null,
                        Transform = transform,
                        StartSample = FrameToSample(resolved.Start, fps, sampleRate) - renderStart,
                        EndSample = FrameToSample(resolved.Start + resolved.Duration, fps, sampleRate) - renderStart,
                        Side = TransformProcessing.ReadEnum<TransformSide>(resolved.Provider.MetaData, TransformProcessing.SideKey),
                        BeforeEffects = TransformProcessing.ReadEnum<TransformRenderOrder>(resolved.Provider.MetaData, TransformProcessing.OrderKey) == TransformRenderOrder.BeforeEffects
                    });
                }
            }
            foreach (var transform in result.OrderBy(t => t.StartSample))
            {
                transform.Left.Transforms.Add(transform);
                transform.Right?.Transforms.Add(transform);
            }
            if (result.Count > 0) Log($"[AudioComposer] Resolved {result.Count} audio transforms across {contexts.Count} soundtracks.");
            return result;
        }

        private void MixTransformIntoChunk(AudioTransformContext context, int chunkStart, int chunkLength, int fps,
            int sampleRate, int channels, float[][] mixed)
        {
            int start = Math.Max(chunkStart, context.StartSample);
            int end = Math.Min(chunkStart + chunkLength, context.EndSample);
            while (start < end)
            {
                var active = context.Right is { } right && start >= right.TimelineStartSample ? right : context.Left;
                int length = context.BeforeEffects && context.Right is { } next && start < next.TimelineStartSample
                    ? Math.Min(end, next.TimelineStartSample) - start : end - start;
                int absolute = FrameToSample(StartFrame, fps, sampleRate) + start;
                length = Math.Min(length, Math.Max(1, FrameToSample(SampleToFrame(absolute, fps, sampleRate) + 1, fps, sampleRate) - absolute));
                try
                {
                    uint frame = (uint)SampleToFrame(absolute, fps, sampleRate);
                    using var frameContext = ValueProviderFrameContext.PushFrame(frame,
                        Math.Clamp((start - context.Left.TimelineStartSample) / (float)Math.Max(1, context.Left.TimelineEndSample - context.Left.TimelineStartSample), 0, 1));
                    var left = ReadInput(context.Left);
                    var rightInput = context.Right is { } r ? ReadInput(r) : null;
                    var output = ToFloatSamples(TransformProcessing.ProcessSamples(left, rightInput, context.Transform,
                        context.Right is null ? TransformInputMode.OneInput : TransformInputMode.TwoInput,
                        start - context.StartSample, context.EndSample - context.StartSample, context.Side));
                    if (context.BeforeEffects)
                        output = ApplyAudioEffects(active, output, start - active.TimelineStartSample);
                    if (output.SampleCount != length || output.channelCount != channels || output.SamplePerSecond != sampleRate)
                        throw new InvalidOperationException("Audio transform changed the output sample count, rate or channels.");
                    for (int c = 0; c < channels; c++)
                        MixSamples(output.Channels[c].AsSpan(0, length), mixed[c].AsSpan(start - chunkStart, length), 1f);
                }
                catch (Exception ex)
                {
                    Log(ex, $"Audio transform {context.Transform.TypeName}/{context.Transform.BindedEffectProvidingSystemID}, samples {start}..{start + length}", this);
                    throw;
                }
                start += length;

                FloatAudioSamples ReadInput(SoundTrackContext track)
                {
                    int offset = start - track.TimelineStartSample;
                    var input = ReadClipWindowToFloat(track, offset, length, sampleRate, channels);
                    if (!context.BeforeEffects) input = ApplyAudioEffects(track, input, offset);
                    for (int c = 0; c < channels; c++)
                    {
                        if (track.Volume == 1f) continue;
                        var data = input.Channels[c];
                        var volume = new Vector<float>(track.Volume);
                        int i = 0;
                        for (; i <= length - Vector<float>.Count; i += Vector<float>.Count)
                            (new Vector<float>(data, i) * volume).CopyTo(data, i);
                        for (; i < length; i++) data[i] *= track.Volume;
                    }
                    return input;
                }
            }
        }

        private static FloatAudioSamples ReadClipWindowToFloat(
            SoundTrackContext context,
            int clipOutputOffset,
            int outputCount,
            int outputSampleRate,
            int outputChannels)
        {
            if (context.SpeedMap is not null)
                return ReadCustomSpeedWindow(context, clipOutputOffset, outputCount, outputSampleRate, outputChannels);
            int sourceRate = Math.Max(1, context.SourceSampleRate);
            float ratio = context.Ratio <= 0f ? 1f : context.Ratio;

            double sourceStep = sourceRate / (double)(outputSampleRate * ratio);
            double sourceOffset = clipOutputOffset * sourceStep;
            int sourceIntStart = (int)Math.Floor(sourceOffset);
            double sourceStartFrac = sourceOffset - sourceIntStart;

            int sourceReadStart = Math.Max(0, context.SourceStartSample + sourceIntStart);
            sourceStartFrac += context.SourceStartSample + sourceIntStart - sourceReadStart;
            int sourceReadCount = Math.Max(2, (int)Math.Ceiling(sourceStartFrac + (outputCount - 1) * sourceStep) + 2);

            float[][] mapped = CreateZeroChannels(outputChannels, outputCount);
            if (sourceStartFrac + (outputCount - 1) * sourceStep <= -1)
                return new FloatAudioSamples { Channels = mapped, SampleCount = outputCount, SamplePerSecond = outputSampleRate };

            IAudioSamples raw = context.SoundTrack.GetAudioSamplesRelatedToStartPointOfSource((uint)sourceReadStart, sourceReadCount);

            float[][] sourceChannels = ToFloatChannels(raw);
            int sourceChannelCount = Math.Max(1, raw.channelCount);

            for (int c = 0; c < outputChannels; c++)
            {
                int srcChannel = sourceChannelCount == 1 ? 0 : Math.Min(c, sourceChannelCount - 1);
                float[] src = sourceChannels[srcChannel];

                if (c > 0 && srcChannel == (sourceChannelCount == 1 ? 0 : Math.Min(c - 1, sourceChannelCount - 1)))
                {
                    mapped[c - 1].AsSpan().CopyTo(mapped[c]);
                    continue;
                }
                if (sourceStep == 1 && sourceStartFrac == Math.Truncate(sourceStartFrac))
                {
                    int offset = (int)sourceStartFrac;
                    int start = Math.Max(0, -offset);
                    int end = Math.Min(outputCount, src.Length - offset);
                    if (end > start) src.AsSpan(start + offset, end - start).CopyTo(mapped[c].AsSpan(start));
                    continue;
                }

                for (int i = 0; i < outputCount; i++)
                {
                    double pos = sourceStartFrac + i * sourceStep;
                    int idx = (int)Math.Floor(pos);
                    double frac = pos - idx;

                    float a = idx >= 0 && idx < src.Length ? src[idx] : 0f;
                    float b = (idx + 1) >= 0 && (idx + 1) < src.Length ? src[idx + 1] : a;
                    mapped[c][i] = (float)(a * (1.0 - frac) + b * frac);
                }
            }

            return new FloatAudioSamples
            {
                Channels = mapped,
                SampleCount = outputCount,
                SamplePerSecond = outputSampleRate
            };
        }

        private static FloatAudioSamples ReadCustomSpeedWindow(SoundTrackContext context, int offset, int count, int rate, int channels)
        {
            var map = context.SpeedMap!;
            var output = CreateZeroChannels(channels, count);
            int position = 0;
            while (position < count)
            {
                ValueProviderFrameContext.CancellationToken.ThrowIfCancellationRequested();
                int sample = offset + position;
                if (sample < 0) { position++; continue; }
                int frame = SampleToFrame(sample, context.FrameRate, rate);
                if ((uint)frame >= map.Length) break;
                int first = FrameToSample(frame, context.FrameRate, rate);
                int end = FrameToSample(frame + 1, context.FrameRate, rate);
                int n = Math.Min(count - position, end - sample);
                uint current = map.GetSourceFrame((uint)frame);
                double delta = map.GetSourceFrameStep((uint)frame);
                double step = delta * context.SourceSampleRate / context.FrameRate / Math.Max(1, end - first);
                double start = context.SourceStartSample + (double)current * context.SourceSampleRate / context.FrameRate + (sample - first) * step;
                if (delta < 0) start += context.SourceSampleRate / context.FrameRate - 1;
                if (Math.Abs(step) > 0) n = Math.Min(n, Math.Max(1, (int)Math.Min(int.MaxValue, Math.Floor(65534 / Math.Abs(step)) + 1)));
                double last = start + (n - 1) * step;
                int readStart = checked((int)Math.Max(0, Math.Floor(Math.Min(start, last))));
                int readCount = checked((int)Math.Max(2, Math.Ceiling(Math.Max(start, last)) - readStart + 2));
                var raw = context.SoundTrack.GetAudioSamplesRelatedToStartPointOfSource((uint)readStart, readCount);
                var input = ToFloatChannels(raw);
                for (int c = 0; c < channels; c++)
                {
                    var src = input[raw.channelCount == 1 ? 0 : Math.Min(c, raw.channelCount - 1)];
                    for (int i = 0; i < n; i++)
                    {
                        double p = start + i * step - readStart;
                        int index = (int)Math.Floor(p);
                        float x = index >= 0 && index < src.Length ? src[index] : 0;
                        float y = index + 1 >= 0 && index + 1 < src.Length ? src[index + 1] : x;
                        output[c][position + i] = (float)(x + (y - x) * (p - index));
                    }
                }
                position += n;
            }
            return new FloatAudioSamples { Channels = output, SampleCount = count, SamplePerSecond = rate };
        }

        private static FloatAudioSamples ApplyAudioEffects(
            SoundTrackContext context,
            FloatAudioSamples input,
            int clipLocalSampleIndex)
        {
            IAudioSamples current = input;
            using var clipScope = ValueProviderFrameContext.PushClip(context.Owner);
            using var frameContext = ValueProviderFrameContext.PushFrame(
                (uint)SampleToFrame(FrameToSample(context.ClipInfo.Start, context.FrameRate, input.SamplePerSecond)
                    + clipLocalSampleIndex, context.FrameRate, input.SamplePerSecond),
                Math.Clamp(clipLocalSampleIndex / (float)Math.Max(1, context.TimelineEndSample - context.TimelineStartSample), 0, 1));

            uint frame = (uint)Convert.ToSingle(ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInFrameProviderId) ?? 0f);
            using var endpoints = context.StageClips.Count == 0 ? null : new ClipValueFrameContext(context.StageClips, frame,
                source =>
                {
                    if (source.EffectsInstances?.OfType<IRenderEffectPipeline>().FirstOrDefault(g => g.Stage == EffectStage.Render) is { } render)
                    {
                        var audioContext = context.AudioContexts.FirstOrDefault(c => c.Owner?.Id == source.Id);
                        FloatAudioSamples? samples = null;
                        if (audioContext is not null)
                        {
                            int start = FrameToSample(frame, context.FrameRate, input.SamplePerSecond);
                            int length = FrameToSample((double)frame + 1, context.FrameRate, input.SamplePerSecond) - start;
                            samples = ReadClipWindowToFloat(audioContext,
                                start - FrameToSample(source.StartFrame, context.FrameRate, input.SamplePerSecond), length, input.SamplePerSecond, input.channelCount);
                        }
                        render.EvaluateRenderValue(source, frame, RenderOutputProvider.AudioKey, samples, ValueProviderFrameContext.CancellationToken);
                    }
                    else source.EffectStagePlan?.Complete(source, EffectStage.Render, frame);
                }, Effect.DynamicEffectGraph.CopyPicture);
            int previousSample = ValueProviderFrameContext.AudioIndex.Value;
            ValueProviderFrameContext.AudioIndex.Value = clipLocalSampleIndex;
            try
            {
                var owner = context.Owner;
                var graph = owner?.EffectsInstances?.OfType<IRenderEffectPipeline>().FirstOrDefault(g => g.Stage == EffectStage.Render)
                    ?? context.Effects.OfType<IRenderEffectPipeline>().FirstOrDefault(g => g.Stage == EffectStage.Render);
                if (graph is not null && owner is not null)
                {
                    var output = (IAudioSamples)graph.EvaluateRenderValue(owner, frame, RenderOutputProvider.AudioKey, input,
                        ValueProviderFrameContext.CancellationToken);
                    if (output.SampleCount != input.SampleCount || output.SamplePerSecond != input.SamplePerSecond || output.channelCount != input.channelCount)
                        throw new InvalidOperationException($"Render audio for clip {owner.Id} changed the sample layout.");
                    return ToFloatSamples(output);
                }
            }
            finally { ValueProviderFrameContext.AudioIndex.Value = previousSample; }
            foreach (var effect in context.Effects)
            {
                if (!effect.Enabled)
                {
                    continue;
                }

                if (effect is IAudioNormalEffect normal)
                {
                    current = normal.Process(current);
                    continue;
                }

                if (effect is IAudioContinuousEffect continuous)
                {
                    if (!RangeOverlaps(clipLocalSampleIndex, input.SampleCount, continuous.StartPoint, continuous.EndPoint))
                    {
                        continue;
                    }

                    current = continuous.Process(current, clipLocalSampleIndex);
                    continue;
                }
            }

            if (context.SoundTrack.EffectStagePlan is { } plan) plan.Complete(plan.Owner, EffectStage.Render, frame);
            return ToFloatSamples(current);
        }

        private static bool RangeOverlaps(int start, int length, int effectStart, int effectEnd)
        {
            if (effectStart == 0 && effectEnd == 0)
            {
                return true;
            }

            int end = start + Math.Max(0, length);
            return end > effectStart && start <= effectEnd;
        }

        private static int SampleToFrame(int sample, int fps, int sampleRate)
        {
            int frame = Math.Max(0, (int)Math.Floor(sample * (double)fps / sampleRate));
            while (FrameToSample(frame + 1, fps, sampleRate) <= sample) frame++;
            while (frame > 0 && FrameToSample(frame, fps, sampleRate) > sample) frame--;
            return frame;
        }

        private static int FrameToSample(double frame, int fps, int sampleRate)
        {
            if (fps <= 0 || sampleRate <= 0)
            {
                return 0;
            }

            return checked((int)Math.Max(0, Math.Round(frame / fps * sampleRate)));
        }

        private static int SafeAdd(int left, int right)
        {
            if (right <= 0)
            {
                return left;
            }

            long sum = (long)left + right;
            return sum >= int.MaxValue ? int.MaxValue : (int)sum;
        }

        private static float[][] CreateZeroChannels(int channels, int sampleCount)
        {
            float[][] result = new float[channels][];
            for (int i = 0; i < channels; i++)
            {
                result[i] = new float[sampleCount];
            }
            return result;
        }

        private static float SoftClip(float sample)
        {
            if (sample > 1.0f) return 1.0f;
            if (sample < -1.0f) return -1.0f;
            return sample;
        }

        private static void MixSamples(ReadOnlySpan<float> source, Span<float> destination, float volume)
        {
            int i = 0;
            var gain = new Vector<float>(volume);
            var min = new Vector<float>(-1f);
            for (; i <= source.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                var value = new Vector<float>(destination.Slice(i)) + new Vector<float>(source.Slice(i)) * gain;
                Vector.ConditionalSelect(Vector.Equals(value, value), Vector.Min(Vector.Max(value, min), Vector<float>.One), value)
                    .CopyTo(destination.Slice(i));
            }
            for (; i < source.Length; i++) destination[i] = SoftClip(destination[i] + source[i] * volume);
        }

        private static float[][] ToFloatChannels(IAudioSamples samples)
        {
            if (samples is FloatAudioSamples fa) return fa.Channels;
            int channelCount = Math.Max(1, samples.channelCount);
            float[][] result = new float[channelCount][];

            for (int c = 0; c < channelCount; c++)
            {
                if (samples is IAudioSamples<float> floatSamples)
                {
                    result[c] = floatSamples.GetSamples(c);
                    continue;
                }

                object[] source = samples.GetSamples(c);
                float[] converted = new float[source.Length];
                for (int i = 0; i < source.Length; i++)
                {
                    converted[i] = Convert.ToSingle(source[i]);
                }
                result[c] = converted;
            }

            return result;
        }

        private static FloatAudioSamples ToFloatSamples(IAudioSamples samples)
        {
            if (samples is FloatAudioSamples fa)
            {
                return fa;
            }

            if (samples is FloatStereoAudioSamples fs)
            {
                return new FloatAudioSamples
                {
                    Channels = new[] { fs.Left, fs.Right },
                    SampleCount = fs.SampleCount,
                    SamplePerSecond = fs.SamplePerSecond
                };
            }

            return new FloatAudioSamples
            {
                Channels = ToFloatChannels(samples),
                SampleCount = samples.SampleCount,
                SamplePerSecond = samples.SamplePerSecond
            };
        }

        private sealed class SoundTrackContext
        {
            public required ISoundTrack SoundTrack { get; init; }
            public TransformClipInfo ClipInfo { get; init; }
            public int SourceSampleRate { get; init; }
            public int FrameRate { get; init; }
            public int TimelineStartSample { get; init; }
            public int TimelineEndSample { get; init; }
            public int SourceStartSample { get; init; }
            public float Volume { get; init; }
            public float Ratio { get; init; }
            public SpeedVarianceFrameMap? SpeedMap { get; init; }
            public IEffect[] Effects { get; init; } = Array.Empty<IEffect>();
            public IClip? Owner { get; init; }
            public int RenderedFrame { get; set; } = -1;
            public FloatAudioSamples? RenderedSamples { get; set; }
            public IReadOnlyList<IClip> StageClips { get; set; } = [];
            public IReadOnlyList<SoundTrackContext> AudioContexts { get; set; } = [];
            public List<AudioTransformContext> Transforms { get; } = [];
        }

        private sealed class AudioTransformContext
        {
            public required SoundTrackContext Left { get; init; }
            public SoundTrackContext? Right { get; init; }
            public required ITransform Transform { get; init; }
            public int StartSample { get; init; }
            public int EndSample { get; init; }
            public TransformSide Side { get; init; }
            public bool BeforeEffects { get; init; }
        }

    }
}
