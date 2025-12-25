using projectFrameCut.Render.RenderAPIBase.Sources;
using System;
using System.Collections.Generic;

namespace projectFrameCut.Render.VideoMakeEngine
{
    public static class AudioMixer
    {
        /// <summary>
        /// Mixes multiple audio buffers into one.
        /// </summary>
        /// <param name="buffers">The list of audio buffers to mix.</param>
        /// <returns>A new <see cref="AudioBuffer"/> containing the mixed audio.</returns>
        public static AudioBuffer Mix(IEnumerable<AudioBuffer> buffers)
        {
            AudioBuffer? result = null;

            foreach (var buffer in buffers)
            {
                if (result == null)
                {
                    // Initialize result buffer with the first buffer's properties
                    result = new AudioBuffer(buffer.Channels, buffer.SampleCount, buffer.SampleRate);
                }

                if (buffer.Channels != result.Channels || buffer.SampleCount != result.SampleCount || buffer.SampleRate != result.SampleRate)
                {
                    // In a real implementation, we should resample/remix here.
                    // For now, we assume they match.
                    continue;
                }

                for (int c = 0; c < result.Channels; c++)
                {
                    for (int s = 0; s < result.SampleCount; s++)
                    {
                        result.Samples[c][s] += buffer.Samples[c][s];
                        
                        // Simple clipping (should use a better limiter in production)
                        if (result.Samples[c][s] > 1.0f) result.Samples[c][s] = 1.0f;
                        if (result.Samples[c][s] < -1.0f) result.Samples[c][s] = -1.0f;
                    }
                }
            }

            return result ?? new AudioBuffer(2, 0, 44100);
        }
    }
}
