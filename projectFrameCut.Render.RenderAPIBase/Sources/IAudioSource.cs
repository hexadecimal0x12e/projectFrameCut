using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.RenderAPIBase.Sources
{
    public interface IAudioSource : IDisposable
    {
        /// <summary>
        /// Initialize the audio source. This method should prepare the audio source for frame extraction.
        /// </summary>
        /// <remarks>
        /// If the file path is null, please just return without doing anything. 
        /// This is because <see cref="IPluginBase.AudioSourceCreator"/> need an instance of this to get <see cref="PreferredExtension"/> to determine which plugin to use.
        /// </remarks>
        public abstract void Initialize();
        /// <summary>
        /// Try to initialize the audio source. Returns true if successful, false otherwise.
        /// </summary>
        public virtual bool TryInitialize()
        {
            try
            {
                Initialize();
                return true;

            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Create a new instance of the video source with a different source.
        /// </summary>
        public IAudioSource CreateNew(string newSource);

        /// <summary>
        /// The preferred file extensions for this audio source.
        /// </summary>
        public string[] PreferredExtension { get; }

        /// <summary>
        /// Get the total duration of this audio source.
        /// </summary>
        public uint Duration { get; }

        /// <summary>
        /// How many channels this source have.
        /// </summary>
        public int ChannelCount { get; }

        /// <summary>
        /// How many sample per second this source have.
        /// </summary>
        /// <remarks>
        /// Also knows as sample rate. 
        /// For example, if the source have 44100 samples per second, this field should be 44100.
        /// </remarks>
        public int SamplePerSecond { get; }

        /// <summary>
        /// Get whether the audio source has been disposed.
        /// </summary>
        public bool Disposed { get; }

        /// <summary>
        /// Get the sample from the source file, starting from <paramref name="startIndex"/> with <paramref name="count"/> samples.
        /// </summary>
        /// <param name="startIndex">The starting index of the sample.</param>
        /// <param name="count">The number of samples to retrieve.</param>
        /// <returns>An <see cref="IAudioSamples"/>, with <see cref="ChannelCount"/> channels containing the requested samples in <see cref="SamplePerSecond"/> rate of sample.</returns>
        public IAudioSamples GetSample(uint startIndex, long count);

        /// <summary>
        /// Get one sample from the source file, in <paramref name="index"/>.
        /// </summary>
        public object GetSingleSample(uint index);

    }

    public interface IAudioSource<T> : IAudioSource
    {
        /// <summary>
        /// Create a new instance of the video source with a different source.
        /// </summary>
        public new IAudioSource<T> CreateNew(string newSource);

        /// <summary>
        /// Get the sample from the source file, starting from <paramref name="startIndex"/> with <paramref name="count"/> samples.
        /// </summary>
        /// <param name="startIndex">The starting index of the sample.</param>
        /// <param name="count">The number of samples to retrieve.</param>
        /// <returns>An <see cref="IAudioSamples"/>, with <see cref="ChannelCount"/> channels containing the requested samples in <see cref="SamplePerSecond"/> rate of sample.</returns>
        public new IAudioSamples<T> GetSample(uint startIndex, long count);

        /// <summary>
        /// Get one sample from the source file, in <paramref name="index"/>.
        /// </summary>
        public new T GetSingleSample(uint index);

        object IAudioSource.GetSingleSample(uint index) => GetSingleSample(index) ?? throw new NullReferenceException("Source returns null.");
        IAudioSamples IAudioSource.GetSample(uint startIndex, long count) => GetSample(startIndex, count);
        IAudioSource IAudioSource.CreateNew(string newSource) => CreateNew(newSource);



    }

    public interface IAudioWriter : IDisposable
    {
        public int SamplePerSecond { get; set; }
        public int BitPerSample { get; set; }
        public int ChannelCount { get; set; }
        public string OutputPath { get; set; }
        public string CodecName { get; set; }
        public string AudioFormat { get; set; }

        /// <summary>
        /// Write a single sample to the specific channel of the result.
        /// </summary>
        /// <remarks>
        /// This is the final method called by writer. You <b>must</b> implement this method to write the sample to the result.
        /// The <see cref="Append(IAudioSamples)"/> and <see cref="Append(object[])"/> will call this method for each sample.
        /// </remarks>
        /// <param name="data"></param>
        /// <param name="channel"></param>
        public abstract void Write(object data, int channel);

        /// <summary>
        /// Get how many <b>individual</b> samples have been written to the result.
        /// </summary>
        /// <remarks>
        /// For example, if you write 500 <see cref="FloatStereoAudioSamples"/> which have <see cref="IAudioSamples.SamplePerSecond"/> of 10 with <see cref="Append(IAudioSamples)"/>, and called <see cref="Append(object[])"/> for 1000 times, this field should be 500 * 10 + 1000 = 6000.
        /// </remarks>
        public uint SamplesWritten { get; }
        /// <summary>
        /// Prepare the writer.
        /// </summary>
        public void Initialize();
        public virtual bool TryInitialize()
        {
            try
            {
                Initialize();
                return true;

            }
            catch
            {
                return false;
            }
        }
        /// <summary>
        /// Detect whether writer supports the specific codec in <paramref name="codecName"/>.
        /// </summary>
        /// <param name="codecName"></param>
        /// <returns></returns>
        public bool SupportCodec(string codecName);
        /// <summary>
        /// Flush all data, write closing data (if have), release all resources and close result stream. 
        /// After calling this method, the writer should be ready for disposal.
        /// </summary>
        public void Finish();

    }

    public abstract class AudioWriterBase<T> : IAudioWriter
    {
        public int SamplePerSecond { get; set; }
        public int BitPerSample { get; set; }
        public int ChannelCount { get; set; }
        public string OutputPath { get; set; }
        public string CodecName { get; set; }
        public string AudioFormat { get; set; }

        public uint SamplesWritten { get; private set; }

        /// <summary>
        /// Write a single sample to the specific channel of the result.
        /// </summary>
        /// <remarks>
        /// This is the final method called by writer. You <b>must</b> implement this method to write the sample to the result.
        /// The <see cref="Append(IAudioSamples)"/> and <see cref="Append(object[])"/> will call this method for each sample.
        /// </remarks>
        /// <param name="data"></param>
        /// <param name="channel"></param>
        public abstract void Write(T data, int channel);
        public abstract void Initialize();
        public abstract bool SupportCodec(string codecName);

        /// <summary>
        /// Write a sample collection to the result.
        /// </summary>
        /// <param name="samples"></param>
        public virtual void Append(IAudioSamples<T> samples)
        {
            if (samples.channelCount != ChannelCount || samples.SamplePerSecond != SamplePerSecond)
            {
                throw new ArgumentException("The sample collection's channel count or sample rate does not match the writer's settings.");
            }
            int lastLength = 0;
            for (int channel = 0; channel < ChannelCount; channel++)
            {
                var data = samples.GetSamples(channel);

                if (channel > 0 && data.Length != lastLength)
                {
                    throw new ArgumentException($"Channel {channel} in the sample collection must contain the same number of samples as any other channel (probably be {lastLength}).");
                }

                for (int i = 0; i < data.Length; i++)
                {
                    Write(data[i], channel);
                }
            }

            SamplesWritten += (uint)samples.SampleCount;

        }
        /// <summary>
        /// Write a single sample to the result.
        /// </summary>
        /// <param name="singleSample"></param>
        public void Append(T[] singleSample)
        {
            if (singleSample.Length != ChannelCount)
            {
                throw new ArgumentException($"The single sample must contain exactly {ChannelCount} channels.");
            }
            for (int channel = 0; channel < ChannelCount; channel++)
            {
                Write(singleSample[channel], channel);
            }
            SamplesWritten++;
        }

        public abstract void Dispose();

        public abstract void Finish();


        void IAudioWriter.Write(object data, int channel) => Write((T)data, channel);
        public void Append(IAudioSamples samples) => Append((IAudioSamples<T>)samples);
    }
}
