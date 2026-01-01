using System;

namespace projectFrameCut.Render.RenderAPIBase.Sources
{
    /// <summary>
    /// Represents a buffer of audio samples.
    /// </summary>
    public class AudioBuffer : IDisposable
    {
        /// <summary>
        /// The audio samples. The first dimension is the channel, the second is the sample index.
        /// Samples are typically in the range [-1.0, 1.0].
        /// </summary>
        public float[][] Samples { get; set; }

        /// <summary>
        /// The sample rate of the audio data.
        /// </summary>
        public int SampleRate { get; set; }

        /// <summary>
        /// The number of channels.
        /// </summary>
        public int Channels => Samples?.Length ?? 0;

        /// <summary>
        /// The number of samples per channel.
        /// </summary>
        public int SampleCount => Samples != null && Samples.Length > 0 ? Samples[0].Length : 0;

        public AudioBuffer(int channels, int sampleCount, int sampleRate)
        {
            Samples = new float[channels][];
            for (int i = 0; i < channels; i++)
            {
                Samples[i] = new float[sampleCount];
            }
            SampleRate = sampleRate;
        }

        public void Dispose()
        {
            Samples = null!;
        }
    }
}
