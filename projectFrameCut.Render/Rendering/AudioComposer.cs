using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.VideoMakeEngine;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.Rendering
{
    public static class AudioComposer
    {
        /// <summary>
        /// Compose audio from multiple clips into a single audio buffer.
        /// </summary>
        /// <param name="clips">The clips to compose audio from.</param>
        /// <param name="soundTracks">Optional sound tracks for volume control.</param>
        /// <param name="videoFramerate">The video framerate for synchronization.</param>
        /// <param name="samplerate">The output sample rate.</param>
        /// <param name="channels">The number of output channels.</param>
        /// <returns>A composed <see cref="AudioBuffer"/> containing all mixed audio.</returns>
        public static AudioBuffer Compose(
            IClip[] clips,
            ISoundTrack[]? soundTracks = null,
            int videoFramerate = 30,
            int samplerate = 48000,
            int channels = 2)
        {
            Dictionary<string, IAudioSource> decoders = new();
            Dictionary<string, float> volumeMap = new();
            uint totalDurationFrames = 0;

            // Build volume map from sound tracks
            if (soundTracks != null)
            {
                foreach (var track in soundTracks)
                {
                    volumeMap[track.Id] = track.Volume;
                }
            }

            // Create audio sources and calculate total duration
            foreach (var clip in clips)
            {
                if (clip.ClipType == Shared.ClipMode.AudioClip)
                {
                    totalDurationFrames = Math.Max(totalDurationFrames, clip.StartFrame + clip.Duration);
                    if (!decoders.ContainsKey(clip.Id) && clip.FilePath != null)
                    {
                        decoders.Add(clip.Id, PluginManager.CreateAudioSource(clip.FilePath));
                    }
                }
                else if (clip.ClipType == Shared.ClipMode.VideoClip)
                {
                    IAudioSource? src = null;
                    try
                    {
                        if (clip.FilePath != null)
                        {
                            src = PluginManager.CreateAudioSource(clip.FilePath);
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                    if (src is not null)
                    {
                        totalDurationFrames = Math.Max(totalDurationFrames, clip.StartFrame + clip.Duration);
                        if (!decoders.ContainsKey(clip.Id))
                        {
                            decoders.Add(clip.Id, src);
                        }
                    }
                }
            }

            double totalDurationSeconds = (double)totalDurationFrames / videoFramerate;
            int totalSamples = (int)(totalDurationSeconds * samplerate);

            AudioBuffer outputBuffer = new AudioBuffer(channels, totalSamples, samplerate);

            foreach (var clip in clips)
            {
                if (!decoders.TryGetValue(clip.Id, out var audioSource))
                {
                    continue;
                }

                // Get volume for this clip (default to 1.0 if no sound track binding)
                float volume = 1.0f;
                if (!string.IsNullOrEmpty(clip.BindedSoundTrack) && volumeMap.TryGetValue(clip.BindedSoundTrack, out var trackVolume))
                {
                    volume = trackVolume;
                }

                // Calculate the sample position where this clip starts in the output
                int clipStartSample = (int)((double)clip.StartFrame / videoFramerate * samplerate);

                // Get audio samples from the source
                AudioBuffer clipAudio;
                try
                {
                    clipAudio = audioSource.GetAudioSamples(clip.RelativeStartFrame, clip.Duration, videoFramerate);
                }
                catch
                {
                    continue;
                }

                if (clipAudio == null || clipAudio.SampleCount == 0)
                {
                    continue;
                }

                AudioBuffer resampledAudio = clipAudio.SampleRate != samplerate
                    ? Resample(clipAudio, samplerate)
                    : clipAudio;

                if (clip.SecondPerFrameRatio != 1.0f)
                {
                    resampledAudio = ApplySpeedRatio(resampledAudio, clip.SecondPerFrameRatio);
                }

                MixIntoBuffer(outputBuffer, resampledAudio, clipStartSample, volume, channels);

                if (resampledAudio != clipAudio)
                {
                    resampledAudio.Dispose();
                }
            }

            // Dispose all audio sources
            foreach (var decoder in decoders.Values)
            {
                decoder.Dispose();
            }

            return outputBuffer;
        }

        /// <summary>
        /// Mix a source buffer into a destination buffer at a specific position with volume control.
        /// </summary>
        private static void MixIntoBuffer(AudioBuffer dest, AudioBuffer src, int destStartSample, float volume, int destChannels)
        {
            int srcChannels = src.Channels;
            
            // Safety checks
            if (srcChannels == 0 || src.SampleCount == 0 || dest.Channels == 0 || dest.SampleCount == 0)
                return;
            
            if (destStartSample < 0)
                destStartSample = 0;
            
            if (destStartSample >= dest.SampleCount)
                return;

            int samplesToMix = Math.Min(src.SampleCount, dest.SampleCount - destStartSample);
            
            // Use actual channel count, not the requested one
            int actualDestChannels = Math.Min(destChannels, dest.Channels);

            if (samplesToMix <= 0) return;

            for (int s = 0; s < samplesToMix; s++)
            {
                int destIndex = destStartSample + s;
                if (destIndex >= dest.SampleCount) break;

                for (int c = 0; c < actualDestChannels; c++)
                {
                    // Handle channel mapping (mono to stereo, etc.)
                    int srcChannel = srcChannels == 1 ? 0 : Math.Min(c, srcChannels - 1);
                    
                    // Additional bounds check
                    if (srcChannel >= src.Samples.Length || s >= src.Samples[srcChannel].Length)
                        continue;
                    if (c >= dest.Samples.Length || destIndex >= dest.Samples[c].Length)
                        continue;
                    
                    float sample = src.Samples[srcChannel][s] * volume;

                    dest.Samples[c][destIndex] += sample;

                    // Soft clipping to prevent distortion
                    if (dest.Samples[c][destIndex] > 1.0f)
                        dest.Samples[c][destIndex] = 1.0f;
                    else if (dest.Samples[c][destIndex] < -1.0f)
                        dest.Samples[c][destIndex] = -1.0f;
                }
            }
        }

        /// <summary>
        /// Resample an audio buffer to a new sample rate using linear interpolation.
        /// </summary>
        private static AudioBuffer Resample(AudioBuffer source, int targetSampleRate)
        {
            if (source.SampleRate == targetSampleRate)
            {
                return source;
            }

            double ratio = (double)targetSampleRate / source.SampleRate;
            int newSampleCount = (int)(source.SampleCount * ratio);

            AudioBuffer result = new AudioBuffer(source.Channels, newSampleCount, targetSampleRate);

            for (int c = 0; c < source.Channels; c++)
            {
                for (int i = 0; i < newSampleCount; i++)
                {
                    double srcIndex = i / ratio;
                    int srcIndexInt = (int)srcIndex;
                    double frac = srcIndex - srcIndexInt;

                    if (srcIndexInt + 1 < source.SampleCount)
                    {
                        // Linear interpolation
                        result.Samples[c][i] = (float)(
                            source.Samples[c][srcIndexInt] * (1 - frac) +
                            source.Samples[c][srcIndexInt + 1] * frac);
                    }
                    else if (srcIndexInt < source.SampleCount)
                    {
                        result.Samples[c][i] = source.Samples[c][srcIndexInt];
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Apply speed ratio to audio (time stretching without pitch correction).
        /// </summary>
        private static AudioBuffer ApplySpeedRatio(AudioBuffer source, float speedRatio)
        {
            if (Math.Abs(speedRatio - 1.0f) < 0.001f)
            {
                return source;
            }

            int newSampleCount = (int)(source.SampleCount / speedRatio);
            AudioBuffer result = new AudioBuffer(source.Channels, newSampleCount, source.SampleRate);

            for (int c = 0; c < source.Channels; c++)
            {
                for (int i = 0; i < newSampleCount; i++)
                {
                    double srcIndex = i * speedRatio;
                    int srcIndexInt = (int)srcIndex;
                    double frac = srcIndex - srcIndexInt;

                    if (srcIndexInt + 1 < source.SampleCount)
                    {
                        // Linear interpolation
                        result.Samples[c][i] = (float)(
                            source.Samples[c][srcIndexInt] * (1 - frac) +
                            source.Samples[c][srcIndexInt + 1] * frac);
                    }
                    else if (srcIndexInt < source.SampleCount)
                    {
                        result.Samples[c][i] = source.Samples[c][srcIndexInt];
                    }
                }
            }

            return result;
        }

    }
}
