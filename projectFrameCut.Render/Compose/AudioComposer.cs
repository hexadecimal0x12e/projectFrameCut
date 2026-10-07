using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace projectFrameCut.Render.Compose
{
    public class AudioComposer<T>
    {
        private const int DefaultChunkSampleCount = 40960;

        public required AudioWriterBase<T> Writer { get; init; }
        public required IClip[] Clips { get; init; }
        public ISoundTrack[]? SoundTracks { get; init; }
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

            var (contexts, totalSamples) = BuildAudioContexts(videoFramerate, samplerate);
            var transforms = BuildTransforms(contexts, videoFramerate, samplerate);

            Writer.SamplePerSecond = samplerate;
            Writer.ChannelCount = channels;
            Writer.Initialize();

            if (totalSamples <= 0)
            {
                return;
            }
            Stopwatch elapsed = Stopwatch.StartNew();
            ConcurrentDictionary<string, object> globalBindableCache = new();
            Log($"[AudioComposer] Total {totalSamples} samples need to compose.");
            for (int chunkStart = 0; chunkStart < totalSamples; chunkStart += chunkSampleCount)
            {
                if (cancellationToken?.IsCancellationRequested == true)
                {
                    Log($"[AudioComposer] Cancellation requested, stopping composition.");
                    return;
                }
                int chunkLength = Math.Min(chunkSampleCount, totalSamples - chunkStart);
                float[][] mixed = CreateZeroChannels(channels, chunkLength);

                foreach (var context in contexts)
                {
                    int start = Math.Max(chunkStart, context.TimelineStartSample);
                    int end = Math.Min(chunkStart + chunkLength, context.TimelineEndSample);
                    foreach (var transform in transforms.Where(t => t.Left == context || t.Right == context).OrderBy(t => t.StartSample))
                    {
                        if (transform.EndSample <= start || transform.StartSample >= end) continue;
                        MixContextIntoChunk(context, chunkStart, start, Math.Min(end, transform.StartSample), samplerate, channels, mixed, globalBindableCache);
                        start = Math.Min(end, transform.EndSample);
                    }
                    MixContextIntoChunk(context, chunkStart, start, end, samplerate, channels, mixed, globalBindableCache);
                }
                foreach (var transform in transforms)
                    MixTransformIntoChunk(transform, chunkStart, chunkLength, videoFramerate, samplerate, channels, mixed, globalBindableCache);

                Writer.Append(new FloatAudioSamples
                {
                    Channels = mixed,
                    SampleCount = chunkLength,
                    SamplePerSecond = samplerate
                });

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

            }
        }



        private (List<SoundTrackContext> contexts, int totalSamples) BuildAudioContexts(
            int videoFramerate,
            int outputSampleRate)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(videoFramerate);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputSampleRate);

            int renderStartSample = FrameToSample(StartFrame, videoFramerate, outputSampleRate);
            int? requestedDurationSamples = Duration == uint.MaxValue ? null : FrameToSample(Duration, videoFramerate, outputSampleRate);
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

                    // Soundtracks are initialized by the project loader. Reuse that
                    // validated source instead of opening a second decoder here. Apart
                    // from being wasteful, the old path silently converted any second
                    // open/read failure into an all-zero preview WAV.
                    int sourceSampleRate = track.SamplePerSecond;
                    if (sourceSampleRate <= 0)
                    {
                        throw new InvalidOperationException(
                            $"Soundtrack '{track.Name}' ({track.Id}) has no initialized audio source.");
                    }

                    float ratio = track.Ratio <= 0f ? 1f : track.Ratio;
                    int trackStartSample = FrameToSample(track.StartFrame, videoFramerate, outputSampleRate);
                    int durationFrames = (int)Math.Max(0, Math.Round(track.Duration * ratio));
                    int trackDurationSamples = FrameToSample(durationFrames, videoFramerate, outputSampleRate);
                    if (trackDurationSamples <= 0)
                    {
                        continue;
                    }

                    int sourceStartSample = FrameToSample(track.RelativeStartFrame, videoFramerate, sourceSampleRate);
                    IEffect[] effects = (track.EffectsInstances ?? Array.Empty<IEffect>())
                        .Where(e => e.Enabled)
                        .OrderBy(e => e.Index)
                        .ToArray();

                    int trackEndSample = FrameToSample((ulong)track.StartFrame + (uint)durationFrames, videoFramerate, outputSampleRate);
                    int overlapStartSample = Math.Max(trackStartSample, renderStartSample);
                    int overlapEndSample = Math.Min(trackEndSample, renderEndSample);
                    contexts.Add(new SoundTrackContext
                    {
                        SoundTrack = track,
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
                        Effects = effects
                    });

                    if (overlapEndSample > overlapStartSample)
                        totalSamples = Math.Max(totalSamples, overlapEndSample - renderStartSample);
                }
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
            float[][] mixed,
            ConcurrentDictionary<string, object> globalBindableCache)
        {
            int overlapStart = Math.Max(start, context.TimelineStartSample);
            int overlapEnd = Math.Min(end, context.TimelineEndSample);
            if (overlapEnd <= overlapStart)
            {
                return;
            }

            int clipLocalOffset = overlapStart - context.TimelineStartSample;
            int localChunkOffset = overlapStart - chunkStart;
            int overlapLength = overlapEnd - overlapStart;
            FloatAudioSamples clipWindow = ReadClipWindowToFloat(context, clipLocalOffset, overlapLength, outputSampleRate, outputChannels);
            clipWindow = ApplyAudioEffects(context, clipWindow, clipLocalOffset, globalBindableCache);

            for (int c = 0; c < outputChannels; c++)
            {
                float[] src = clipWindow.GetSamples(c);
                float[] dst = mixed[c];

                for (int i = 0; i < overlapLength; i++)
                {
                    float value = dst[localChunkOffset + i] + src[i] * context.Volume;
                    dst[localChunkOffset + i] = SoftClip(value);
                }
            }
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
            if (result.Count > 0) Log($"[AudioComposer] Resolved {result.Count} audio transforms across {contexts.Count} soundtracks.");
            return result;
        }

        private void MixTransformIntoChunk(AudioTransformContext context, int chunkStart, int chunkLength, int fps,
            int sampleRate, int channels, float[][] mixed, ConcurrentDictionary<string, object> cache)
        {
            int start = Math.Max(chunkStart, context.StartSample);
            int end = Math.Min(chunkStart + chunkLength, context.EndSample);
            while (start < end)
            {
                var active = context.Right is { } right && start >= right.TimelineStartSample ? right : context.Left;
                int length = context.BeforeEffects && context.Right is { } next && start < next.TimelineStartSample
                    ? Math.Min(end, next.TimelineStartSample) - start : end - start;
                try
                {
                    uint frame = StartFrame + (uint)Math.Max(0, Math.Floor(start * (double)fps / sampleRate));
                    using var frameContext = ValueProviderFrameContext.PushFrame(frame,
                        Math.Clamp((start - context.Left.TimelineStartSample) / (float)Math.Max(1, context.Left.TimelineEndSample - context.Left.TimelineStartSample), 0, 1));
                    var left = ReadInput(context.Left);
                    var rightInput = context.Right is { } r ? ReadInput(r) : null;
                    var output = ToFloatSamples(TransformProcessing.ProcessSamples(left, rightInput, context.Transform,
                        context.Right is null ? TransformInputMode.OneInput : TransformInputMode.TwoInput,
                        start - context.StartSample, context.EndSample - context.StartSample, context.Side));
                    if (context.BeforeEffects)
                        output = ApplyAudioEffects(active, output, start - active.TimelineStartSample, cache);
                    if (output.SampleCount != length || output.channelCount != channels || output.SamplePerSecond != sampleRate)
                        throw new InvalidOperationException("Audio transform changed the output sample count, rate or channels.");
                    for (int c = 0; c < channels; c++)
                        for (int i = 0; i < length; i++)
                            mixed[c][start - chunkStart + i] = SoftClip(mixed[c][start - chunkStart + i] + output.Channels[c][i]);
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
                    if (!context.BeforeEffects) input = ApplyAudioEffects(track, input, offset, cache);
                    for (int c = 0; c < channels; c++)
                        for (int i = 0; i < length; i++) input.Channels[c][i] *= track.Volume;
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
            int sourceRate = Math.Max(1, context.SourceSampleRate);
            float ratio = context.Ratio <= 0f ? 1f : context.Ratio;

            double sourceStep = sourceRate / (double)(outputSampleRate * ratio);
            double sourceOffset = clipOutputOffset * sourceStep;
            int sourceIntStart = (int)Math.Floor(sourceOffset);
            double sourceStartFrac = sourceOffset - sourceIntStart;

            int sourceReadStart = Math.Max(0, context.SourceStartSample + sourceIntStart);
            sourceStartFrac += context.SourceStartSample + sourceIntStart - sourceReadStart;
            int sourceReadCount = Math.Max(2, (int)Math.Ceiling(sourceStartFrac + (outputCount - 1) * sourceStep) + 2);

            IAudioSamples raw = context.SoundTrack.GetAudioSamplesRelatedToStartPointOfSource((uint)sourceReadStart, sourceReadCount);

            float[][] sourceChannels = ToFloatChannels(raw);
            int sourceChannelCount = Math.Max(1, raw.channelCount);
            float[][] mapped = CreateZeroChannels(outputChannels, outputCount);

            for (int c = 0; c < outputChannels; c++)
            {
                int srcChannel = sourceChannelCount == 1 ? 0 : Math.Min(c, sourceChannelCount - 1);
                float[] src = sourceChannels[srcChannel];

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

        private static FloatAudioSamples ApplyAudioEffects(
            SoundTrackContext context,
            FloatAudioSamples input,
            int clipLocalSampleIndex,
            ConcurrentDictionary<string, object> globalBindableCache)
        {
            if (context.Effects.Length == 0)
            {
                return input;
            }

            IAudioSamples current = input;
            using var frameContext = ValueProviderFrameContext.PushFrame(
                (uint)Math.Clamp(context.ClipInfo.Start + Math.Floor(clipLocalSampleIndex * (double)context.FrameRate / input.SamplePerSecond), 0, uint.MaxValue),
                Math.Clamp(clipLocalSampleIndex / (float)Math.Max(1, context.TimelineEndSample - context.TimelineStartSample), 0, 1));
            Dictionary<string, object> localBindableCache = new();

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

        private static int FrameToSample(double frame, int fps, int sampleRate)
        {
            if (fps <= 0 || sampleRate <= 0)
            {
                return 0;
            }

            return (int)Math.Max(0, Math.Round(frame / fps * sampleRate));
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

        private static float[][] ToFloatChannels(IAudioSamples samples)
        {
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

        private static bool TryGetCachedValue(
            string key,
            Dictionary<string, object> local,
            ConcurrentDictionary<string, object> global,
            out object value)
        {
            if (local.TryGetValue(key, out value!))
            {
                return true;
            }

            return global.TryGetValue(key, out value!);
        }

        private static object GetCachedValue(
            string key,
            Dictionary<string, object> local,
            ConcurrentDictionary<string, object> global)
        {
            if (local.TryGetValue(key, out var localValue))
            {
                return localValue;
            }

            if (global.TryGetValue(key, out var globalValue))
            {
                return globalValue;
            }

            throw new KeyNotFoundException($"Cached value with key '{key}' not found.");
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
            public IEffect[] Effects { get; init; } = Array.Empty<IEffect>();
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
