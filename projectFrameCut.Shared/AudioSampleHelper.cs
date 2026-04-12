using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Shared
{
    public static class AudioSampleHelper
    {
        public static IAudioSamples<T> ReSample<T>(this IAudioSamples<T> sample, int targetSampleRate)
        {
            if (sample is null)
            {
                throw new ArgumentNullException(nameof(sample));
            }

            if (targetSampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(targetSampleRate));
            }

            if (sample.SamplePerSecond <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sample), "SamplePerSecond must be greater than 0.");
            }

            if (sample.SamplePerSecond == targetSampleRate)
            {
                return sample;
            }

            if (typeof(T) != typeof(float))
            {
                throw new NotSupportedException($"Resampling {typeof(T).Name} samples is not supported.");
            }

            double ratio = (double)targetSampleRate / sample.SamplePerSecond;
            int newSampleCount = (int)(sample.SampleCount * ratio);
            float[][] channels = new float[sample.channelCount][];

            for (int channelIndex = 0; channelIndex < sample.channelCount; channelIndex++)
            {
                float[] sourceChannel = (float[])(object)sample.GetSamples(channelIndex);
                channels[channelIndex] = ReSampleChannel(sourceChannel, sample.SampleCount, newSampleCount, ratio);
            }

            if (sample.channelCount == 2)
            {
                return (IAudioSamples<T>)(object)new FloatStereoAudioSamples
                {
                    Left = channels[0],
                    Right = channels[1],
                    SampleCount = newSampleCount,
                    SamplePerSecond = targetSampleRate
                };
            }

            return (IAudioSamples<T>)(object)new FloatAudioSamples
            {
                Channels = channels,
                SampleCount = newSampleCount,
                SamplePerSecond = targetSampleRate
            };
        }

        private static float[] ReSampleChannel(float[] sourceChannel, int sourceSampleCount, int targetSampleCount, double ratio)
        {
            float[] result = new float[targetSampleCount];
            int boundedSampleCount = Math.Min(sourceSampleCount, sourceChannel.Length);

            if (boundedSampleCount == 0)
            {
                return result;
            }

            for (int i = 0; i < targetSampleCount; i++)
            {
                double sourceIndex = i / ratio;
                int sourceIndexInt = (int)sourceIndex;
                double fraction = sourceIndex - sourceIndexInt;

                if (sourceIndexInt + 1 < boundedSampleCount)
                {
                    result[i] = (float)(
                        sourceChannel[sourceIndexInt] * (1 - fraction) +
                        sourceChannel[sourceIndexInt + 1] * fraction);
                }
                else if (sourceIndexInt < boundedSampleCount)
                {
                    result[i] = sourceChannel[sourceIndexInt];
                }
            }

            return result;
        }
    }
}
