using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Sources;
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

            try
            {
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
                        MixContextIntoChunk(context, chunkStart, chunkLength, samplerate, channels, mixed, globalBindableCache);
                    }

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
            finally
            {
                DisposeContexts(contexts);
            }
        }



        private (List<AudioClipContext> contexts, int totalSamples) BuildAudioContexts(
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

            Dictionary<string, ISoundTrack> trackMap = new();
            if (SoundTracks != null)
            {
                foreach (var track in SoundTracks)
                {
                    trackMap[track.Id] = track;
                }
            }

            List<AudioClipContext> contexts = new();
            int totalSamples = requestedDurationSamples ?? 0;

            foreach (var clip in Clips)
            {
                if (clip.ClipType != ClipMode.AudioClip && clip.ClipType != ClipMode.VideoClip)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(clip.FilePath))
                {
                    continue;
                }

                IAudioSource? source;
                try
                {
                    source = PluginManager.CreateAudioSource(clip.FilePath);
                }
                catch
                {
                    source = null;
                }

                if (source is null)
                {
                    continue;
                }

                float ratio = ResolveSpeedRatio(clip);
                int clipStartSample = FrameToSample(clip.StartFrame, videoFramerate, outputSampleRate);
                int durationFrames = (int)Math.Max(0, Math.Round(clip.Duration * ratio));
                int clipDurationSamples = FrameToSample(durationFrames, videoFramerate, outputSampleRate);
                if (clipDurationSamples <= 0)
                {
                    source.Dispose();
                    continue;
                }

                int sourceStartSample = FrameToSample(clip.RelativeStartFrame, videoFramerate, Math.Max(1, source.SamplePerSecond));

                ISoundTrack? bindedTrack = null;
                float volume = 1.0f;
                string? bindedSoundTrackId = clip.BindedSoundTrack;
                if (!string.IsNullOrWhiteSpace(bindedSoundTrackId)
                    && trackMap.TryGetValue(bindedSoundTrackId!, out ISoundTrack? track)
                    && track is not null)
                {
                    bindedTrack = track;
                    volume = track.Volume;
                }

                IEffect[] effects = (clip.EffectsInstances ?? Array.Empty<IEffect>())
                    .Where(e => e.Enabled)
                    .OrderBy(e => e.Index)
                    .ToArray();

                int clipEndSample = clipStartSample + clipDurationSamples;
                int overlapStartSample = Math.Max(clipStartSample, renderStartSample);
                int overlapEndSample = Math.Min(clipEndSample, renderEndSample);
                if (overlapEndSample <= overlapStartSample)
                {
                    source.Dispose();
                    continue;
                }

                int localSampleOffset = overlapStartSample - clipStartSample;
                contexts.Add(new AudioClipContext
                {
                    Clip = clip,
                    SoundTrack = bindedTrack,
                    Source = source,
                    TimelineStartSample = overlapStartSample - renderStartSample,
                    TimelineEndSample = overlapEndSample - renderStartSample,
                    SourceStartSample = sourceStartSample,
                    LocalSampleOffset = localSampleOffset,
                    Volume = volume,
                    Ratio = ratio,
                    Effects = effects
                });

                totalSamples = Math.Max(totalSamples, overlapEndSample - renderStartSample);
            }

            if (SoundTracks != null)
            {
                foreach (var track in SoundTracks)
                {
                    if (track.NeedFilePath && string.IsNullOrWhiteSpace(track.FilePath))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(track.FilePath))
                    {
                        continue;
                    }

                    IAudioSource? source;
                    try
                    {
                        source = PluginManager.CreateAudioSource(track.FilePath);
                    }
                    catch
                    {
                        source = null;
                    }

                    if (source is null)
                    {
                        continue;
                    }

                    float ratio = track.Ratio <= 0f ? 1f : track.Ratio;
                    int trackStartSample = FrameToSample(track.StartFrame, videoFramerate, outputSampleRate);
                    int durationFrames = (int)Math.Max(0, Math.Round(track.Duration * ratio));
                    int trackDurationSamples = FrameToSample(durationFrames, videoFramerate, outputSampleRate);
                    if (trackDurationSamples <= 0)
                    {
                        source.Dispose();
                        continue;
                    }

                    int sourceStartSample = FrameToSample(track.RelativeStartFrame, videoFramerate, Math.Max(1, source.SamplePerSecond));
                    IEffect[] effects = (track.EffectsInstances ?? Array.Empty<IEffect>())
                        .Where(e => e.Enabled)
                        .OrderBy(e => e.Index)
                        .ToArray();

                    int trackEndSample = trackStartSample + trackDurationSamples;
                    int overlapStartSample = Math.Max(trackStartSample, renderStartSample);
                    int overlapEndSample = Math.Min(trackEndSample, renderEndSample);
                    if (overlapEndSample <= overlapStartSample)
                    {
                        source.Dispose();
                        continue;
                    }

                    int localSampleOffset = overlapStartSample - trackStartSample;
                    contexts.Add(new AudioClipContext
                    {
                        Clip = null,
                        SoundTrack = track,
                        Source = source,
                        TimelineStartSample = overlapStartSample - renderStartSample,
                        TimelineEndSample = overlapEndSample - renderStartSample,
                        SourceStartSample = sourceStartSample,
                        LocalSampleOffset = localSampleOffset,
                        Volume = track.Volume,
                        Ratio = ratio,
                        Effects = effects
                    });

                    totalSamples = Math.Max(totalSamples, overlapEndSample - renderStartSample);
                }
            }

            return (contexts, totalSamples);
        }


        private static void MixContextIntoChunk(
            AudioClipContext context,
            int chunkStart,
            int chunkLength,
            int outputSampleRate,
            int outputChannels,
            float[][] mixed,
            ConcurrentDictionary<string, object> globalBindableCache)
        {
            int chunkEnd = chunkStart + chunkLength;
            int overlapStart = Math.Max(chunkStart, context.TimelineStartSample);
            int overlapEnd = Math.Min(chunkEnd, context.TimelineEndSample);
            if (overlapEnd <= overlapStart)
            {
                return;
            }

            int clipLocalOffset = overlapStart - context.TimelineStartSample;
            int localChunkOffset = overlapStart - chunkStart;
            int overlapLength = overlapEnd - overlapStart;
            int clipLocalOffsetToSource = context.LocalSampleOffset + clipLocalOffset;

            FloatAudioSamples clipWindow = ReadClipWindowToFloat(context, clipLocalOffsetToSource, overlapLength, outputSampleRate, outputChannels);
            clipWindow = ApplyAudioEffects(context, clipWindow, (uint)clipLocalOffsetToSource, globalBindableCache);

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

        private static FloatAudioSamples ReadClipWindowToFloat(
            AudioClipContext context,
            int clipOutputOffset,
            int outputCount,
            int outputSampleRate,
            int outputChannels)
        {
            int sourceRate = Math.Max(1, context.Source.SamplePerSecond);
            float ratio = context.Ratio <= 0f ? 1f : context.Ratio;

            double sourceStep = sourceRate / (double)(outputSampleRate * ratio);
            double sourceOffset = clipOutputOffset * sourceStep;
            int sourceIntStart = (int)Math.Floor(sourceOffset);
            double sourceStartFrac = sourceOffset - sourceIntStart;

            int sourceReadStart = Math.Max(0, context.SourceStartSample + sourceIntStart);
            int sourceReadCount = Math.Max(2, (int)Math.Ceiling(sourceStartFrac + (outputCount - 1) * sourceStep) + 2);

            IAudioSamples raw;
            try
            {
                raw = context.Source.GetSample((uint)sourceReadStart, sourceReadCount);
            }
            catch
            {
                return new FloatAudioSamples
                {
                    Channels = CreateZeroChannels(outputChannels, outputCount),
                    SampleCount = outputCount,
                    SamplePerSecond = outputSampleRate
                };
            }

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
                    int idx = (int)pos;
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
            AudioClipContext context,
            FloatAudioSamples input,
            uint clipLocalSampleIndex,
            ConcurrentDictionary<string, object> globalBindableCache)
        {
            if (context.Effects.Length == 0)
            {
                return input;
            }

            IAudioSamples current = input;
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
                    if (!RangeOverlaps((int)clipLocalSampleIndex, input.SampleCount, continuous.StartPoint, continuous.EndPoint))
                    {
                        continue;
                    }

                    current = continuous.Process(current, clipLocalSampleIndex);
                    continue;
                }

                if (effect is IBindableArgumentAudioEffectValueProvider vp)
                {
                    string key = string.IsNullOrWhiteSpace(vp.Id) ? Guid.NewGuid().ToString() : vp.Id;
                    object value = vp.GenerateValue(current);
                    if (vp.GenerateOnce)
                    {
                        globalBindableCache[key] = value;
                    }
                    else
                    {
                        localBindableCache[key] = value;
                    }
                    continue;
                }

                if (effect is IBindableArgumentAudioEffectOneInputResultGenerator one)
                {
                    if (string.IsNullOrWhiteSpace(one.BindedArgumentProviderID))
                    {
                        continue;
                    }

                    if (!TryGetCachedValue(one.BindedArgumentProviderID, localBindableCache, globalBindableCache, out object sourceValue))
                    {
                        continue;
                    }

                    if (one.IsContinuous && !RangeOverlaps((int)clipLocalSampleIndex, input.SampleCount, one.StartPoint, one.EndPoint))
                    {
                        continue;
                    }

                    current = one.GenerateResult(sourceValue, clipLocalSampleIndex);
                    localBindableCache[string.IsNullOrWhiteSpace(one.Id) ? Guid.NewGuid().ToString() : one.Id] = current;
                    continue;
                }

                if (effect is IBindableArgumentAudioEffectManyInputResultGenerator many)
                {
                    if (!RangeOverlaps((int)clipLocalSampleIndex, input.SampleCount, many.StartPoint, many.EndPoint))
                    {
                        continue;
                    }

                    object[] values = many.BindedArgumentProviderIDs
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Where(id => TryGetCachedValue(id, localBindableCache, globalBindableCache, out _))
                        .Select(id => GetCachedValue(id, localBindableCache, globalBindableCache))
                        .ToArray();

                    if (values.Length == 0)
                    {
                        continue;
                    }

                    current = many.GenerateResult(values, clipLocalSampleIndex);
                    localBindableCache[string.IsNullOrWhiteSpace(many.Id) ? Guid.NewGuid().ToString() : many.Id] = current;
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

        private static float ResolveSpeedRatio(IClip clip)
        {
            if (clip.Duration == 0)
            {
                return 1f;
            }

            ISpeedVarianceProvider? provider = clip.SpeedVarianceProviderInstance;
            if (provider is null)
            {
                return 1f;
            }

            if (provider is ClassicSpeedVarianceProvider classic)
            {
                float ratio = classic.Ratio;
                if (ratio > 0f && !float.IsNaN(ratio) && !float.IsInfinity(ratio))
                {
                    return ratio;
                }
            }

            try
            {
                uint effectiveDuration = provider.GetEffectiveLength(clip.Duration);
                if (effectiveDuration > 0)
                {
                    float ratio = effectiveDuration / (float)clip.Duration;
                    if (ratio > 0f && !float.IsNaN(ratio) && !float.IsInfinity(ratio))
                    {
                        return ratio;
                    }
                }
            }
            catch
            {
            }

            return 1f;
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

        private static void DisposeContexts(IEnumerable<AudioClipContext> contexts)
        {
            foreach (var context in contexts)
            {
                try
                {
                    context.Source.Dispose();
                }
                catch
                {
                }
            }
        }

        private sealed class AudioClipContext
        {
            public IClip? Clip { get; init; }
            public ISoundTrack? SoundTrack { get; init; }
            public IAudioSource Source { get; init; } = null!;
            public int TimelineStartSample { get; init; }
            public int TimelineEndSample { get; init; }
            public int SourceStartSample { get; init; }
            public int LocalSampleOffset { get; init; }
            public float Volume { get; init; }
            public float Ratio { get; init; }
            public IEffect[] Effects { get; init; } = Array.Empty<IEffect>();
        }

    }
}
