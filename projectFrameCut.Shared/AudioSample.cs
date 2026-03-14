using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace projectFrameCut.Shared
{
    #region base
    /// <summary>
    /// A collection of many audio samples.
    /// </summary>
    public interface IAudioSamples
    {
        /// <summary>
        /// How many bit contains in one sample.
        /// </summary>
        /// <remarks>
        /// Similar to <see cref="IPicture.bitPerPixel"/>, for example, if a IAudioSample use 32-bit float to storage samples, this field should be 32.
        /// </remarks>
        public int bitPerSample { get; }
        /// <summary>
        /// How many channel does this sample collection contains. 
        /// </summary>
        /// <remarks>
        /// For example, for stereo audio, this field should be 2.
        /// </remarks>
        public int channelCount { get; }
        /// <summary>
        /// How many samples are there in each channel.
        /// </summary>
        /// <remarks>
        /// Similar to SecondPerFrame in IClip. You can calculate the total duration (in seconds) of the audio samples by easily multiplying <see cref="SampleCount"/> with <see cref="SamplePerSecond"/>.
        /// </remarks>
        public int SampleCount { get; set; }
        /// <summary>
        /// How many samples are there per second.
        /// </summary>
        public int SamplePerSecond { get; set; }

        /// <summary>
        /// Read out the sample collection in specific channel.
        /// </summary>
        /// <param name="channelID"></param>
        /// <returns>An array with specific sample data(s).</returns>
        public object[] GetSamples(int channelID);
        /// <summary>
        /// Converts this sample collection to another bit per sample. 
        /// </summary>
        public IAudioSamples ToBitPerSample(int bitPerSample);
    }

    public interface IAudioSamples<T> : IAudioSamples
    {
        /// <summary>
        /// Read out the sample collection in specific channel.
        /// </summary>
        /// <param name="channelID"></param>
        /// <returns>An <see cref="Array{T}"/> of specific channel.</returns>
        public new T[] GetSamples(int channelID);

        object[] IAudioSamples.GetSamples(int channelID) => GetSamples(channelID).OfType<object>().ToArray();

    }
    #endregion

    #region float-generic
    public class FloatAudioSamples : IAudioSamples<float>
    {
        [NotNull]
        public float[][] Channels { get; set; } = Array.Empty<float[]>();

        public int bitPerSample => 32;
        public int channelCount => Channels.Length;

        public int SampleCount { get; set; }
        public int SamplePerSecond { get; set; }

        public float[] GetSamples(int channelID)
        {
            if (channelID < 0 || channelID >= Channels.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(channelID));
            }

            return Channels[channelID];
        }

        public IAudioSamples ToBitPerSample(int bitPerSample)
        {
            if (bitPerSample == 32) return this;
            throw new NotSupportedException($"Conversion to {bitPerSample} bits per sample is not supported.");
        }
    }
    #endregion

    #region float
    public class FloatStereoAudioSamples : IAudioSamples<float>
    {
        [NotNull]
        public float[] Left { get; set; }
        [NotNull]
        public float[] Right { get; set; }

        public int bitPerSample => 32;
        public int channelCount => 2;

        public int SampleCount { get; set; }
        public int SamplePerSecond { get; set; }


        public float[] GetSamples(int channelID)
        {
            return channelID switch
            {
                0 => Left,
                1 => Right,
                _ => throw new ArgumentOutOfRangeException(nameof(channelID), "channelID must be 0 or 1 for stereo audio.")
            };
        }

        public IAudioSamples ToBitPerSample(int bitPerSample)
        {
            if (bitPerSample == 32) return this;
            throw new NotSupportedException($"Conversion to {bitPerSample} bits per sample is not supported.");
        }

    }
    #endregion
}
