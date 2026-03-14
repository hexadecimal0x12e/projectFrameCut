using FFmpeg.AutoGen;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace projectFrameCut.Render.EncodeAndDecode
{
    public sealed unsafe class AudioWriter : AudioWriterBase<float>
    {
        private readonly string _outputPath;
        private readonly string _codecName;
        private readonly int _sampleRate;
        private readonly int _channels;

        private AVFormatContext* _fmtCtx = null;
        private AVStream* _audioStream = null;
        private AVCodecContext* _codecCtx = null;
        private AVFrame* _frame = null;
        private SwrContext* _swr = null;
        private int _swrInputSampleRate = 0;
        private int _frameIndex = 0;
        private bool _isHeaderWritten = false;
        private bool _isDisposed = false;
        private readonly float[] _singleSampleBuffer;
        private readonly bool[] _singleSampleChannelWritten;

        public AudioWriter(string outputPath, int sampleRate = 44100, int channels = 2, string? codecName = null)
        {
            _outputPath = outputPath;
            _sampleRate = sampleRate;
            _channels = channels;
            _singleSampleBuffer = new float[channels];
            _singleSampleChannelWritten = new bool[channels];
            
            // Auto-detect codec based on file extension if not specified
            if (string.IsNullOrEmpty(codecName))
            {
                string ext = Path.GetExtension(outputPath).ToLowerInvariant();
                _codecName = ext switch
                {
                    ".wav" => "pcm_s16le",
                    ".mp3" => "libmp3lame",
                    ".aac" or ".m4a" => "aac",
                    ".ogg" => "libvorbis",
                    ".flac" => "flac",
                    _ => "pcm_s16le"  // Default to PCM
                };
            }
            else
            {
                _codecName = codecName;
            }

            // Keep base metadata in sync for base helper methods such as Append(T[]).
            OutputPath = _outputPath;
            CodecName = _codecName;
            AudioFormat = Path.GetExtension(_outputPath).TrimStart('.').ToLowerInvariant();
            SamplePerSecond = _sampleRate;
            ChannelCount = _channels;
            BitPerSample = sizeof(float) * 8;
            
        }

        public override bool SupportCodec(string codecName)
        {
            if (string.IsNullOrWhiteSpace(codecName))
            {
                return false;
            }

            return ffmpeg.avcodec_find_encoder_by_name(codecName) != null;
        }

        public override void Write(float data, int channel)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(AudioWriter));
            if (channel < 0 || channel >= _channels)
            {
                throw new ArgumentOutOfRangeException(nameof(channel));
            }

            if (_singleSampleChannelWritten[channel])
            {
                throw new InvalidOperationException($"Channel {channel} has already been written for current sample.");
            }

            _singleSampleBuffer[channel] = data;
            _singleSampleChannelWritten[channel] = true;

            for (int c = 0; c < _channels; c++)
            {
                if (!_singleSampleChannelWritten[c])
                {
                    return;
                }
            }

            float[][] channels = new float[_channels][];
            for (int c = 0; c < _channels; c++)
            {
                channels[c] = new[] { _singleSampleBuffer[c] };
                _singleSampleChannelWritten[c] = false;
            }

            Append(new FloatAudioSamples
            {
                Channels = channels,
                SampleCount = 1,
                SamplePerSecond = _sampleRate
            });
        }

        public override void Initialize()
        {
            AVFormatContext* oc = null;
            FFmpegHelper.Throw(ffmpeg.avformat_alloc_output_context2(&oc, null, null, _outputPath), "avformat_alloc_output_context2");
            _fmtCtx = oc;

            AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(_codecName);
            if (codec == null) throw new EntryPointNotFoundException($"Could not find encoder '{_codecName}'.");

            _audioStream = ffmpeg.avformat_new_stream(_fmtCtx, codec);
            _codecCtx = ffmpeg.avcodec_alloc_context3(codec);

            _codecCtx->codec_id = codec->id;
            _codecCtx->codec_type = AVMediaType.AVMEDIA_TYPE_AUDIO;
            _codecCtx->sample_rate = _sampleRate;
            
            // Use appropriate sample format for the codec
            // PCM codecs need S16/S32, while others typically use FLTP
            AVSampleFormat sampleFmt = _codecName switch
            {
                "pcm_s16le" or "pcm_s16be" => AVSampleFormat.AV_SAMPLE_FMT_S16,
                "pcm_s32le" or "pcm_s32be" => AVSampleFormat.AV_SAMPLE_FMT_S32,
                "flac" => AVSampleFormat.AV_SAMPLE_FMT_S16,
                _ => AVSampleFormat.AV_SAMPLE_FMT_FLTP
            };
            _codecCtx->sample_fmt = sampleFmt;
            _codecCtx->bit_rate = 192000;
            
            AVChannelLayout chLayout;
            ffmpeg.av_channel_layout_default(&chLayout, _channels);
            ffmpeg.av_channel_layout_copy(&_codecCtx->ch_layout, &chLayout);

            _audioStream->time_base = new AVRational { num = 1, den = _sampleRate };

            if ((_fmtCtx->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                _codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

            FFmpegHelper.Throw(ffmpeg.avcodec_open2(_codecCtx, codec, null), "avcodec_open2");
            FFmpegHelper.Throw(ffmpeg.avcodec_parameters_from_context(_audioStream->codecpar, _codecCtx), "avcodec_parameters_from_context");

            if ((_fmtCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                FFmpegHelper.Throw(ffmpeg.avio_open(&_fmtCtx->pb, _outputPath, ffmpeg.AVIO_FLAG_WRITE), "avio_open");
            }

            _frame = ffmpeg.av_frame_alloc();
            _frame->sample_rate = _codecCtx->sample_rate;
            _frame->format = (int)_codecCtx->sample_fmt;
            ffmpeg.av_channel_layout_copy(&_frame->ch_layout, &_codecCtx->ch_layout);
            _frame->nb_samples = _codecCtx->frame_size > 0 ? _codecCtx->frame_size : 1024;
            FFmpegHelper.Throw(ffmpeg.av_frame_get_buffer(_frame, 0), "av_frame_get_buffer");

            // Initialize SwrContext for conversion from float planar samples to encoder format.
            RecreateSwr(_sampleRate);
        }

        private void RecreateSwr(int inputSampleRate)
        {
            if (_swr != null)
            {
                fixed (SwrContext** p = &_swr) ffmpeg.swr_free(p);
            }

            _swr = ffmpeg.swr_alloc();

            AVChannelLayout inLayout;
            ffmpeg.av_channel_layout_default(&inLayout, _channels);

            SwrContext* swr = _swr;
            AVChannelLayout* outLayout = &_codecCtx->ch_layout;
            AVChannelLayout* pInLayout = &inLayout;
            FFmpegHelper.Throw(
                ffmpeg.swr_alloc_set_opts2(
                    &swr,
                    outLayout,
                    _codecCtx->sample_fmt,
                    _codecCtx->sample_rate,
                    pInLayout,
                    AVSampleFormat.AV_SAMPLE_FMT_FLTP,
                    inputSampleRate,
                    0,
                    null),
                "swr_alloc_set_opts2");
            _swr = swr;
            FFmpegHelper.Throw(ffmpeg.swr_init(_swr), "swr_init");
            _swrInputSampleRate = inputSampleRate;
        }

        [Obsolete("Use Append(IAudioSamples<float>) instead.")]
        public void Append(AudioBuffer buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            Append(new FloatAudioSamples
            {
                Channels = buffer.Samples,
                SampleCount = buffer.SampleCount,
                SamplePerSecond = buffer.SampleRate
            });
        }

        public override void Append(IAudioSamples<float> samples)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(AudioWriter));
            if (samples == null) throw new ArgumentNullException(nameof(samples));

            if (samples.channelCount <= 0)
            {
                throw new ArgumentException("Input has no channels.", nameof(samples));
            }

            // Ensure the resampler input side matches current sample rate.
            int inputSampleRate = samples.SamplePerSecond > 0 ? samples.SamplePerSecond : _sampleRate;
            if (_swr == null || _swrInputSampleRate != inputSampleRate)
            {
                RecreateSwr(inputSampleRate);
            }

            if (!_isHeaderWritten)
            {
                FFmpegHelper.Throw(ffmpeg.avformat_write_header(_fmtCtx, null), "avformat_write_header");
                _isHeaderWritten = true;
            }

            float[][] sourceChannels = new float[_channels][];
            for (int c = 0; c < _channels; c++)
            {
                int srcChannelIndex = samples.channelCount == 1 ? 0 : Math.Min(c, samples.channelCount - 1);
                sourceChannels[c] = samples.GetSamples(srcChannelIndex)
                    ?? throw new InvalidOperationException($"Channel {srcChannelIndex} is null.");
            }

            int totalSamples = samples.SampleCount;
            if (totalSamples <= 0)
            {
                totalSamples = sourceChannels[0].Length;
                for (int c = 1; c < sourceChannels.Length; c++)
                {
                    totalSamples = Math.Min(totalSamples, sourceChannels[c].Length);
                }
            }

            if (totalSamples <= 0)
            {
                return;
            }

            int samplesProcessed = 0;
            byte** inData = (byte**)ffmpeg.av_malloc((ulong)(sizeof(byte*) * _channels));

            GCHandle[] handles = new GCHandle[_channels];
            float** pinnedChannelPointers = stackalloc float*[_channels];
            try
            {
                for (int c = 0; c < _channels; c++)
                {
                    handles[c] = GCHandle.Alloc(sourceChannels[c], GCHandleType.Pinned);
                    pinnedChannelPointers[c] = (float*)handles[c].AddrOfPinnedObject();
                }

                while (samplesProcessed < totalSamples)
                {
                    int nb_samples = Math.Min(totalSamples - samplesProcessed, _frame->nb_samples);

                    for (int c = 0; c < _channels; c++)
                    {
                        inData[c] = (byte*)(pinnedChannelPointers[c] + samplesProcessed);
                    }

                    FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(_frame), "av_frame_make_writable");
                    int converted = ffmpeg.swr_convert(_swr, (byte**)&_frame->data, _frame->nb_samples, inData, nb_samples);

                    _frame->pts = ffmpeg.av_rescale_q(_frameIndex, new AVRational { num = 1, den = _sampleRate }, _audioStream->time_base);
                    _frameIndex += converted;

                    EncodeFrame(_frame);
                    samplesProcessed += nb_samples;
                }
            }
            finally
            {
                for (int c = 0; c < _channels; c++)
                {
                    if (handles[c].IsAllocated)
                    {
                        handles[c].Free();
                    }
                }

                ffmpeg.av_free(inData);
            }
        }

        private void EncodeFrame(AVFrame* frame)
        {
            int sendRet = ffmpeg.avcodec_send_frame(_codecCtx, frame);
            // When flushing (frame == null), EOF is expected and not an error
            if (sendRet == ffmpeg.AVERROR_EOF)
            {
                return;
            }
            // EAGAIN means we need to receive packets first
            if (sendRet == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                // Drain packets first, then retry
            }
            else if (sendRet < 0)
            {
                FFmpegHelper.Throw(sendRet, "avcodec_send_frame");
            }
            
            while (true)
            {
                AVPacket* pkt = ffmpeg.av_packet_alloc();
                int ret = ffmpeg.avcodec_receive_packet(_codecCtx, pkt);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                {
                    ffmpeg.av_packet_free(&pkt);
                    return;
                }
                FFmpegHelper.Throw(ret, "avcodec_receive_packet");

                ffmpeg.av_packet_rescale_ts(pkt, _codecCtx->time_base, _audioStream->time_base);
                pkt->stream_index = _audioStream->index;

                FFmpegHelper.Throw(ffmpeg.av_interleaved_write_frame(_fmtCtx, pkt), "av_interleaved_write_frame");
                ffmpeg.av_packet_free(&pkt);
            }
        }

        private bool _isFinished = false;
        
        override public void Finish()
        {
            if (_isDisposed || _isFinished) return;
            _isFinished = true;
            
            // Only flush and write trailer if we actually wrote something
            if (_isHeaderWritten && _codecCtx != null)
            {
                // Flush encoder
                EncodeFrame(null);
                
                // Write trailer
                if (_fmtCtx != null)
                {
                    ffmpeg.av_write_trailer(_fmtCtx);
                }
            }
        }

        override public void Dispose()
        {
            if (_isDisposed) return;
            
            try
            {
                Finish();
            }
            catch
            {
                // Ignore errors during finish in dispose
            }
            
            if (_frame != null) { fixed (AVFrame** p = &_frame) ffmpeg.av_frame_free(p); }
            if (_codecCtx != null) { fixed (AVCodecContext** p = &_codecCtx) ffmpeg.avcodec_free_context(p); }
            if (_fmtCtx != null)
            {
                if (_fmtCtx->pb != null) ffmpeg.avio_closep(&_fmtCtx->pb);
                fixed (AVFormatContext** p = &_fmtCtx) ffmpeg.avformat_free_context(*p);
            }
            if (_swr != null) { fixed (SwrContext** p = &_swr) ffmpeg.swr_free(p); }
            _isDisposed = true;
        }
    }
}
