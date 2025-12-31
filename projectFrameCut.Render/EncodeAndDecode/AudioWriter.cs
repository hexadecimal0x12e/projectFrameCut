using FFmpeg.AutoGen;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace projectFrameCut.Render.EncodeAndDecode
{
    public sealed unsafe class AudioWriter : IDisposable
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
        private int _frameIndex = 0;
        private bool _isHeaderWritten = false;
        private bool _isDisposed = false;

        public AudioWriter(string outputPath, int sampleRate = 44100, int channels = 2, string? codecName = null)
        {
            _outputPath = outputPath;
            _sampleRate = sampleRate;
            _channels = channels;
            
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
            
            Init();
        }

        private void Init()
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

            // Initialize SwrContext for conversion from AudioBuffer (Float Planar) to encoder format
            _swr = ffmpeg.swr_alloc();
            AVChannelLayout inLayout;
            ffmpeg.av_channel_layout_default(&inLayout, _channels);
            
            SwrContext* swr = _swr;
            AVChannelLayout* outLayout = &_codecCtx->ch_layout;
            AVChannelLayout* pInLayout = &inLayout;
            ffmpeg.swr_alloc_set_opts2(&swr, outLayout, _codecCtx->sample_fmt, _codecCtx->sample_rate,
                                       pInLayout, AVSampleFormat.AV_SAMPLE_FMT_FLTP, _sampleRate, 0, null);
            _swr = swr;
            ffmpeg.swr_init(_swr);
        }

        public void Append(AudioBuffer buffer)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(AudioWriter));
            if (!_isHeaderWritten)
            {
                FFmpegHelper.Throw(ffmpeg.avformat_write_header(_fmtCtx, null), "avformat_write_header");
                _isHeaderWritten = true;
            }

            int samplesProcessed = 0;
            byte** inData = (byte**)ffmpeg.av_malloc((ulong)(sizeof(byte*) * _channels));

            while (samplesProcessed < buffer.SampleCount)
            {
                int nb_samples = Math.Min(buffer.SampleCount - samplesProcessed, _frame->nb_samples);
                
                float[] s0 = buffer.Samples[0];
                float[] s1 = buffer.Samples[1];

                fixed (float* p0 = s0)
                fixed (float* p1 = s1)
                {
                    inData[0] = (byte*)(p0 + samplesProcessed);
                    inData[1] = (byte*)(p1 + samplesProcessed);

                    FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(_frame), "av_frame_make_writable");
                    int converted = ffmpeg.swr_convert(_swr, (byte**)&_frame->data, _frame->nb_samples, inData, nb_samples);
                    
                    _frame->pts = ffmpeg.av_rescale_q(_frameIndex, new AVRational { num = 1, den = _sampleRate }, _audioStream->time_base);
                    _frameIndex += converted;

                    EncodeFrame(_frame);
                    samplesProcessed += nb_samples;
                }
            }
            ffmpeg.av_free(inData);
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
        
        public void Finish()
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

        public void Dispose()
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
