using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace projectFrameCut.Shared
{
    #region base
    public interface IAudioSamples
    {
        public int bitPerSample { get; }
        public int channelCount { get; }
        public int SampleCount { get; set; }
        public int SamplePerSecond { get; set; }

        public object GetSamples(int channelID);
        public IAudioSamples ToBitPerSample(int bitPerSample);
    }

    public interface IAudioSamples<T> : IAudioSamples
    {
        public new T[] GetSamples(int channelID);
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

        object IAudioSamples.GetSamples(int channelID)
        {
            return GetSamples(channelID);
        }
    }
    #endregion
}
