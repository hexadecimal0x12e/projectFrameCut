using FFmpeg.AutoGen;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace projectFrameCut.Render.EncodeAndDecode
{
    public sealed unsafe class AudioDecoder : IAudioSource
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

        public AudioDecoder(string path)
        {
            _path = path;
            Initialize();
        }

        public IAudioSource CreateNew(string newSource) => new AudioDecoder(newSource);


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

            // Calculate duration in frames (assuming 30fps for now, but should be dynamic)
            Duration = (uint)(stream->duration * ffmpeg.av_q2d(stream->time_base)); // Placeholder
        }

        public AudioBuffer GetAudioSamples(uint startFrame, uint frameCount, int videoFramerate)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(AudioDecoder));

            // Calculate target sample count
            int targetSampleRate = 44100; // Standard sample rate
            int targetChannels = 2;
            int targetSampleCount = (int)((double)frameCount / videoFramerate * targetSampleRate);
            
            AudioBuffer buffer = new AudioBuffer(targetChannels, targetSampleCount, targetSampleRate);

            // Seek to start position
            double startTime = (double)startFrame / videoFramerate;
            long timestamp = (long)(startTime / ffmpeg.av_q2d(_fmt->streams[_audioStreamIndex]->time_base));
            ffmpeg.av_seek_frame(_fmt, _audioStreamIndex, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD);

            // Initialize SwrContext for resampling if needed
            if (_swr == null)
            {
                AVChannelLayout outLayout;
                ffmpeg.av_channel_layout_default(&outLayout, targetChannels);
                
                SwrContext* swr = null;
                ffmpeg.swr_alloc_set_opts2(&swr, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLTP, targetSampleRate,
                                           &_codec->ch_layout, _codec->sample_fmt, _codec->sample_rate, 0, null);
                _swr = swr;
                ffmpeg.swr_init(_swr);
            }

            int samplesCollected = 0;
            byte** outDataPtr = (byte**)ffmpeg.av_malloc((ulong)(sizeof(byte*) * targetChannels));

            while (samplesCollected < targetSampleCount)
            {
                if (ffmpeg.av_read_frame(_fmt, _pkt) < 0) break;

                if (_pkt->stream_index == _audioStreamIndex)
                {
                    if (ffmpeg.avcodec_send_packet(_codec, _pkt) >= 0)
                    {
                        while (ffmpeg.avcodec_receive_frame(_codec, _frm) >= 0)
                        {
                            // Convert and copy samples to buffer
                            int outSamples = ffmpeg.swr_get_out_samples(_swr, _frm->nb_samples);
                            
                            fixed (float* p0 = buffer.Samples[0], p1 = buffer.Samples[1])
                            {
                                outDataPtr[0] = (byte*)(p0 + samplesCollected);
                                outDataPtr[1] = (byte*)(p1 + samplesCollected);

                                int converted = ffmpeg.swr_convert(_swr, outDataPtr, outSamples, (byte**)&_frm->data, _frm->nb_samples);
                                if (converted > 0)
                                {
                                    samplesCollected += converted;
                                }
                            }
                            if (samplesCollected >= targetSampleCount) break;
                        }
                    }
                }
                ffmpeg.av_packet_unref(_pkt);
            }

            ffmpeg.av_free(outDataPtr);

            return buffer;
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
    }
}
