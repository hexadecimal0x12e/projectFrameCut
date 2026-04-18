using FFmpeg.AutoGen;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace projectFrameCut.Render.EncodeAndDecode
{
    public sealed unsafe class Float32bitAudioDecoder : IAudioSource<float>
    {
        private readonly string _path;
        private AVFormatContext* _fmt = null;
        private AVCodecContext* _codec = null;
        private SwrContext* _swr = null;
        private AVPacket* _pkt = null;
        private AVFrame* _frm = null;
        private int _audioStreamIndex = -1;
        private bool _isDisposed = false;

        public bool Disposed => _isDisposed;

        public uint Duration { get; private set; }

        public string[] PreferredExtension => new[] { ".mp3", ".wav", ".m4a", ".flac", ".aac" };

        public int ChannelCount { get; private set; }

        public int SamplePerSecond { get; private set; }

        public Float32bitAudioDecoder(string path)
        {
            _path = path;
            Initialize();
        }

        public IAudioSource<float> CreateNew(string newSource) => new Float32bitAudioDecoder(newSource);


        public void Initialize()
        {
            if (string.IsNullOrEmpty(_path)) return;

            // Check if file exists first
            if (!File.Exists(_path))
            {
                throw new FileNotFoundException($"Audio file not found: {_path}");
            }

            _fmt = ffmpeg.avformat_alloc_context();
            fixed (AVFormatContext** fmtPtr = &_fmt)
            {
                int err = ffmpeg.avformat_open_input(fmtPtr, _path, null, null);
                if (err != 0)
                {
                    var errMsg = FFmpegHelper.GetErrorString(err);
                    throw new InvalidOperationException($"Could not open audio file '{_path}': {errMsg} (error code: {err})");
                }
            }

            if (ffmpeg.avformat_find_stream_info(_fmt, null) < 0)
            {
                throw new InvalidDataException($"Could not find stream information in '{_path}'.");
            }

            _audioStreamIndex = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (_audioStreamIndex < 0)
            {
                throw new InvalidDataException($"No audio stream found in '{_path}'.");
            }

            AVStream* stream = _fmt->streams[_audioStreamIndex];
            AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            if (codec == null)
            {
                throw new NotSupportedException($"Audio codec (id: {stream->codecpar->codec_id}) is not supported. Make sure FFmpeg is compiled with the required decoder.");
            }

            _codec = ffmpeg.avcodec_alloc_context3(codec);
            ffmpeg.avcodec_parameters_to_context(_codec, stream->codecpar);
            if (ffmpeg.avcodec_open2(_codec, codec, null) < 0)
            {
                throw new InvalidOperationException("Could not open audio codec.");
            }

            _pkt = ffmpeg.av_packet_alloc();
            _frm = ffmpeg.av_frame_alloc();

            int streamChannels = stream->codecpar->ch_layout.nb_channels;
            ChannelCount = streamChannels > 0 ? streamChannels : (_codec->ch_layout.nb_channels > 0 ? _codec->ch_layout.nb_channels : 2);

            int streamSampleRate = stream->codecpar->sample_rate;
            SamplePerSecond = streamSampleRate > 0 ? streamSampleRate : (_codec->sample_rate > 0 ? _codec->sample_rate : 44100);

            // Calculate duration in frames (assuming 30fps for now, but should be dynamic)
            Duration = (uint)(stream->duration * ffmpeg.av_q2d(stream->time_base)); // Placeholder
        }

        

        public void Dispose()
        {
            if (_isDisposed) return;
            if (_frm != null) { fixed (AVFrame** p = &_frm) ffmpeg.av_frame_free(p); }
            if (_pkt != null) { fixed (AVPacket** p = &_pkt) ffmpeg.av_packet_free(p); }
            if (_codec != null) { fixed (AVCodecContext** p = &_codec) ffmpeg.avcodec_free_context(p); }
            if (_fmt != null) { fixed (AVFormatContext** p = &_fmt) ffmpeg.avformat_close_input(p); }
            if (_swr != null) { fixed (SwrContext** p = &_swr) ffmpeg.swr_free(p); }
            _isDisposed = true;
        }

        public IAudioSamples<float> GetSample(uint startIndex, long count)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(Float32bitAudioDecoder));
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0)
            {
                int channelCount = ChannelCount > 0 ? ChannelCount : 2;
                if (channelCount == 2)
                {
                    return new FloatStereoAudioSamples
                    {
                        Left = Array.Empty<float>(),
                        Right = Array.Empty<float>(),
                        SampleCount = 0,
                        SamplePerSecond = SamplePerSecond
                    };
                }

                float[][] emptyChannels = new float[channelCount][];
                for (int ch = 0; ch < channelCount; ch++)
                {
                    emptyChannels[ch] = Array.Empty<float>();
                }

                return new FloatAudioSamples
                {
                    Channels = emptyChannels,
                    SampleCount = 0,
                    SamplePerSecond = SamplePerSecond
                };
            }
            if (count > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(count), "count is too large.");

            int targetSampleCount = (int)count;
            int targetSampleRate = SamplePerSecond > 0 ? SamplePerSecond : 44100;
            int targetChannels = ChannelCount > 0 ? ChannelCount : 2;

            AVStream* stream = _fmt->streams[_audioStreamIndex];
            double startTime = (double)startIndex / targetSampleRate;
            long timestamp = (long)(startTime / ffmpeg.av_q2d(stream->time_base));
            int seekRet = ffmpeg.av_seek_frame(_fmt, _audioStreamIndex, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (seekRet < 0)
            {
                LogDiagnostic($"[Float32bitAudioDecoder] Audio seek warning for sample index {startIndex}: code {seekRet}. Continuing with seek-from-start.");
            }
            ffmpeg.avcodec_flush_buffers(_codec);

            if (_swr == null)
            {
                AVChannelLayout outLayout;
                ffmpeg.av_channel_layout_default(&outLayout, targetChannels);

                SwrContext* swr = null;
                ffmpeg.swr_alloc_set_opts2(
                    &swr,
                    &outLayout,
                    AVSampleFormat.AV_SAMPLE_FMT_FLTP,
                    targetSampleRate,
                    &_codec->ch_layout,
                    _codec->sample_fmt,
                    _codec->sample_rate,
                    0,
                    null);
                _swr = swr;
                ffmpeg.swr_init(_swr);
            }

            float[][] channels = new float[targetChannels][];
            for (int ch = 0; ch < targetChannels; ch++)
            {
                channels[ch] = new float[targetSampleCount];
            }
            int samplesCollected = 0;

            byte** outDataPtr = (byte**)ffmpeg.av_malloc((ulong)(sizeof(byte*) * targetChannels));
            GCHandle[] pinnedChannels = new GCHandle[targetChannels];
            for (int ch = 0; ch < targetChannels; ch++)
            {
                pinnedChannels[ch] = GCHandle.Alloc(channels[ch], GCHandleType.Pinned);
            }
            try
            {
                while (samplesCollected < targetSampleCount)
                {
                    if (ffmpeg.av_read_frame(_fmt, _pkt) < 0) break;

                    if (_pkt->stream_index == _audioStreamIndex)
                    {
                        if (ffmpeg.avcodec_send_packet(_codec, _pkt) >= 0)
                        {
                            while (ffmpeg.avcodec_receive_frame(_codec, _frm) >= 0)
                            {
                                int outSamples = ffmpeg.swr_get_out_samples(_swr, _frm->nb_samples);

                                int remainingSpace = targetSampleCount - samplesCollected;
                                if (remainingSpace <= 0) break;
                                int samplesToConvert = Math.Min(outSamples, remainingSpace);

                                for (int ch = 0; ch < targetChannels; ch++)
                                {
                                    byte* basePtr = (byte*)pinnedChannels[ch].AddrOfPinnedObject();
                                    outDataPtr[ch] = basePtr + ((nint)samplesCollected * sizeof(float));
                                }

                                int converted = ffmpeg.swr_convert(_swr, outDataPtr, samplesToConvert, (byte**)&_frm->data, _frm->nb_samples);
                                if (converted > 0)
                                {
                                    samplesCollected += converted;
                                }

                                if (samplesCollected >= targetSampleCount) break;
                            }
                        }
                    }

                    ffmpeg.av_packet_unref(_pkt);
                }
            }
            finally
            {
                for (int ch = 0; ch < targetChannels; ch++)
                {
                    if (pinnedChannels[ch].IsAllocated)
                    {
                        pinnedChannels[ch].Free();
                    }
                }

                ffmpeg.av_free(outDataPtr);
            }

            if (samplesCollected != targetSampleCount)
            {
                for (int ch = 0; ch < targetChannels; ch++)
                {
                    Array.Resize(ref channels[ch], samplesCollected);
                }
            }

            if (targetChannels == 2)
            {
                return new FloatStereoAudioSamples
                {
                    Left = channels[0],
                    Right = channels[1],
                    SampleCount = samplesCollected,
                    SamplePerSecond = targetSampleRate
                };
            }

            return new FloatAudioSamples
            {
                Channels = channels,
                SampleCount = samplesCollected,
                SamplePerSecond = targetSampleRate
            };
        }

        public float GetSingleSample(uint index)
        {
            var samples = GetSample(index, 1);
            var left = samples.GetSamples(0);
            if (left.Length == 0)
            {
                throw new IndexOutOfRangeException($"No sample available at index {index}.");
            }

            return left[0];
        }
    }
}
