using FFmpeg.AutoGen;
using projectFrameCut.Render.RenderAPIBase.Sources;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace projectFrameCut.Render.EncodeAndDecode
{
    /// <summary>
    /// Muxes an AudioBuffer with a video file to create a combined output file.
    /// This class copies the video stream from the source file and encodes the AudioBuffer as audio.
    /// </summary>
    public sealed unsafe class VideoAudioMuxer : IDisposable
    {
        private readonly string _videoInputPath;
        private readonly string _outputPath;
        private readonly string _audioCodecName;

        private AVFormatContext* _inputFmtCtx = null;
        private AVFormatContext* _outputFmtCtx = null;
        private AVCodecContext* _audioEncoderCtx = null;
        private AVStream* _outputVideoStream = null;
        private AVStream* _outputAudioStream = null;
        private SwrContext* _swr = null;
        private AVFrame* _audioFrame = null;

        private int _inputVideoStreamIndex = -1;
        private int _inputAudioStreamIndex = -1;
        private int _outputVideoStreamIndex = -1;
        private int _outputAudioStreamIndex = -1;

        private bool _isInitialized = false;
        private bool _isDisposed = false;

        /// <summary>
        /// Creates a new VideoAudioMuxer instance.
        /// </summary>
        /// <param name="videoInputPath">Path to the input video file.</param>
        /// <param name="outputPath">Path to the output file.</param>
        /// <param name="audioCodecName">Audio codec name (e.g., "aac", "libmp3lame"). If null, auto-detects based on output extension.</param>
        public VideoAudioMuxer(string videoInputPath, string outputPath, string? audioCodecName = null)
        {
            if (string.IsNullOrEmpty(videoInputPath))
                throw new ArgumentNullException(nameof(videoInputPath));
            if (string.IsNullOrEmpty(outputPath))
                throw new ArgumentNullException(nameof(outputPath));
            if (!File.Exists(videoInputPath))
                throw new FileNotFoundException($"Video input file not found: {videoInputPath}");

            _videoInputPath = videoInputPath;
            _outputPath = outputPath;

            // Auto-detect codec based on file extension if not specified
            if (string.IsNullOrEmpty(audioCodecName))
            {
                string ext = Path.GetExtension(outputPath).ToLowerInvariant();
                _audioCodecName = ext switch
                {
                    ".wav" => "pcm_s16le",
                    ".mp4" or ".m4v" or ".mov" => "aac",
                    ".mkv" or ".webm" => "libvorbis",
                    ".avi" => "mp3",
                    ".wmv" => "wmav2",
                    _ => "aac"  // Default to AAC
                };
            }
            else
            {
                _audioCodecName = audioCodecName;
            }
        }

        /// <summary>
        /// Muxes the video file with the provided AudioBuffer and writes to the output file.
        /// </summary>
        /// <param name="audioBuffer">The audio buffer to mux with the video.</param>
        /// <param name="sampleRate">Target sample rate for encoding. If 0, uses audioBuffer's sample rate.</param>
        public void Mux(AudioBuffer audioBuffer, int sampleRate = 0)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(VideoAudioMuxer));
            if (audioBuffer == null)
                throw new ArgumentNullException(nameof(audioBuffer));

            if (sampleRate <= 0)
                sampleRate = audioBuffer.SampleRate;

            Initialize(sampleRate, audioBuffer.Channels);
            WriteOutput(audioBuffer);
        }

        private void Initialize(int sampleRate, int channels)
        {
            if (_isInitialized) return;

            // Open input file
            fixed (AVFormatContext** pInputFmtCtx = &_inputFmtCtx)
            {
                FFmpegHelper.Throw(
                    ffmpeg.avformat_open_input(pInputFmtCtx, _videoInputPath, null, null),
                    "avformat_open_input");
            }

            FFmpegHelper.Throw(
                ffmpeg.avformat_find_stream_info(_inputFmtCtx, null),
                "avformat_find_stream_info");

            // Find video stream in input
            for (int i = 0; i < (int)_inputFmtCtx->nb_streams; i++)
            {
                if (_inputFmtCtx->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    _inputVideoStreamIndex = i;
                    break;
                }
            }

            if (_inputVideoStreamIndex < 0)
                throw new InvalidDataException($"No video stream found in '{_videoInputPath}'.");

            // Allocate output format context
            fixed (AVFormatContext** pOutputFmtCtx = &_outputFmtCtx)
            {
                FFmpegHelper.Throw(
                    ffmpeg.avformat_alloc_output_context2(pOutputFmtCtx, null, null, _outputPath),
                    "avformat_alloc_output_context2");
            }

            // Create output video stream (copy from input)
            AVStream* inputVideoStream = _inputFmtCtx->streams[_inputVideoStreamIndex];
            _outputVideoStream = ffmpeg.avformat_new_stream(_outputFmtCtx, null);
            if (_outputVideoStream == null)
                throw new InvalidOperationException("Failed to create output video stream.");

            FFmpegHelper.Throw(
                ffmpeg.avcodec_parameters_copy(_outputVideoStream->codecpar, inputVideoStream->codecpar),
                "avcodec_parameters_copy (video)");

            _outputVideoStream->codecpar->codec_tag = 0;
            _outputVideoStream->time_base = inputVideoStream->time_base;
            _outputVideoStreamIndex = _outputVideoStream->index;

            // Create output audio stream
            AVCodec* audioCodec = ffmpeg.avcodec_find_encoder_by_name(_audioCodecName);
            if (audioCodec == null)
                throw new EntryPointNotFoundException($"Could not find audio encoder '{_audioCodecName}'.");

            _outputAudioStream = ffmpeg.avformat_new_stream(_outputFmtCtx, audioCodec);
            if (_outputAudioStream == null)
                throw new InvalidOperationException("Failed to create output audio stream.");

            _audioEncoderCtx = ffmpeg.avcodec_alloc_context3(audioCodec);
            if (_audioEncoderCtx == null)
                throw new InvalidOperationException("Failed to allocate audio encoder context.");

            // Configure audio encoder
            _audioEncoderCtx->codec_id = audioCodec->id;
            _audioEncoderCtx->codec_type = AVMediaType.AVMEDIA_TYPE_AUDIO;
            _audioEncoderCtx->sample_rate = sampleRate;
            _audioEncoderCtx->bit_rate = 192000;

            // Set sample format based on codec
            AVSampleFormat sampleFmt = _audioCodecName switch
            {
                "pcm_s16le" or "pcm_s16be" => AVSampleFormat.AV_SAMPLE_FMT_S16,
                "pcm_s32le" or "pcm_s32be" => AVSampleFormat.AV_SAMPLE_FMT_S32,
                "flac" => AVSampleFormat.AV_SAMPLE_FMT_S16,
                _ => AVSampleFormat.AV_SAMPLE_FMT_FLTP
            };
            _audioEncoderCtx->sample_fmt = sampleFmt;

            // Set channel layout
            AVChannelLayout chLayout;
            ffmpeg.av_channel_layout_default(&chLayout, channels);
            ffmpeg.av_channel_layout_copy(&_audioEncoderCtx->ch_layout, &chLayout);

            _outputAudioStream->time_base = new AVRational { num = 1, den = sampleRate };
            _audioEncoderCtx->time_base = new AVRational { num = 1, den = sampleRate };

            if ((ffmpeg.AVFMT_GLOBALHEADER) != 0)
                _audioEncoderCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

            FFmpegHelper.Throw(ffmpeg.avcodec_open2(_audioEncoderCtx, audioCodec, null), "avcodec_open2 (audio)");
            FFmpegHelper.Throw(
                ffmpeg.avcodec_parameters_from_context(_outputAudioStream->codecpar, _audioEncoderCtx),
                "avcodec_parameters_from_context (audio)");

            _outputAudioStreamIndex = _outputAudioStream->index;

            // Open output file
            if ((_outputFmtCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                FFmpegHelper.Throw(
                    ffmpeg.avio_open(&_outputFmtCtx->pb, _outputPath, ffmpeg.AVIO_FLAG_WRITE),
                    "avio_open");
            }

            // Allocate audio frame
            _audioFrame = ffmpeg.av_frame_alloc();
            _audioFrame->sample_rate = _audioEncoderCtx->sample_rate;
            _audioFrame->format = (int)_audioEncoderCtx->sample_fmt;
            ffmpeg.av_channel_layout_copy(&_audioFrame->ch_layout, &_audioEncoderCtx->ch_layout);
            _audioFrame->nb_samples = _audioEncoderCtx->frame_size > 0 ? _audioEncoderCtx->frame_size : 1024;
            FFmpegHelper.Throw(ffmpeg.av_frame_get_buffer(_audioFrame, 0), "av_frame_get_buffer (audio)");

            // Initialize SwrContext for conversion from AudioBuffer (Float Planar) to encoder format
            _swr = ffmpeg.swr_alloc();
            AVChannelLayout inLayout;
            ffmpeg.av_channel_layout_default(&inLayout, channels);

            SwrContext* swr = _swr;
            AVChannelLayout* outLayout = &_audioEncoderCtx->ch_layout;
            AVChannelLayout* pInLayout = &inLayout;
            ffmpeg.swr_alloc_set_opts2(&swr, outLayout, _audioEncoderCtx->sample_fmt, _audioEncoderCtx->sample_rate,
                                       pInLayout, AVSampleFormat.AV_SAMPLE_FMT_FLTP, sampleRate, 0, null);
            _swr = swr;
            ffmpeg.swr_init(_swr);

            _isInitialized = true;
        }

        private void WriteOutput(AudioBuffer audioBuffer)
        {
            // Write header
            FFmpegHelper.Throw(ffmpeg.avformat_write_header(_outputFmtCtx, null), "avformat_write_header");

            // Process video packets and audio simultaneously
            AVPacket* videoPkt = ffmpeg.av_packet_alloc();
            int audioFrameIndex = 0;
            int audioSamplesProcessed = 0;
            int channels = audioBuffer.Channels;

            // Calculate audio duration in video timebase for synchronization
            AVStream* inputVideoStream = _inputFmtCtx->streams[_inputVideoStreamIndex];
            double audioDuration = (double)audioBuffer.SampleCount / audioBuffer.SampleRate;
            long audioEndPts = (long)(audioDuration / ffmpeg.av_q2d(_outputAudioStream->time_base));

            byte** inData = (byte**)ffmpeg.av_malloc((ulong)(sizeof(byte*) * channels));

            try
            {
                // Read video packets and write them
                while (ffmpeg.av_read_frame(_inputFmtCtx, videoPkt) >= 0)
                {
                    if (videoPkt->stream_index == _inputVideoStreamIndex)
                    {
                        // Write pending audio frames up to current video pts
                        double videoPtsSeconds = videoPkt->pts * ffmpeg.av_q2d(inputVideoStream->time_base);
                        WriteAudioUpTo(audioBuffer, ref audioSamplesProcessed, ref audioFrameIndex, videoPtsSeconds, inData);

                        // Rescale video packet timestamps
                        ffmpeg.av_packet_rescale_ts(videoPkt, inputVideoStream->time_base, _outputVideoStream->time_base);
                        videoPkt->stream_index = _outputVideoStreamIndex;

                        FFmpegHelper.Throw(
                            ffmpeg.av_interleaved_write_frame(_outputFmtCtx, videoPkt),
                            "av_interleaved_write_frame (video)");
                    }
                    ffmpeg.av_packet_unref(videoPkt);
                }

                // Write remaining audio
                WriteAudioUpTo(audioBuffer, ref audioSamplesProcessed, ref audioFrameIndex, double.MaxValue, inData);

                // Flush audio encoder
                FlushAudioEncoder();
            }
            finally
            {
                ffmpeg.av_free(inData);
                ffmpeg.av_packet_free(&videoPkt);
            }

            // Write trailer
            FFmpegHelper.Throw(ffmpeg.av_write_trailer(_outputFmtCtx), "av_write_trailer");
        }

        private void WriteAudioUpTo(AudioBuffer audioBuffer, ref int samplesProcessed, ref int frameIndex, double targetSeconds, byte** inData)
        {
            int sampleRate = audioBuffer.SampleRate;
            int targetSamples = targetSeconds == double.MaxValue
                ? audioBuffer.SampleCount
                : Math.Min((int)(targetSeconds * sampleRate), audioBuffer.SampleCount);

            int channels = audioBuffer.Channels;

            while (samplesProcessed < targetSamples)
            {
                int nb_samples = Math.Min(targetSamples - samplesProcessed, _audioFrame->nb_samples);
                if (nb_samples <= 0) break;

                // Pin the sample arrays
                GCHandle[] handles = new GCHandle[channels];
                try
                {
                    for (int ch = 0; ch < channels; ch++)
                    {
                        handles[ch] = GCHandle.Alloc(audioBuffer.Samples[ch], GCHandleType.Pinned);
                        inData[ch] = (byte*)((float*)handles[ch].AddrOfPinnedObject() + samplesProcessed);
                    }

                    FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(_audioFrame), "av_frame_make_writable");
                    int converted = ffmpeg.swr_convert(_swr, (byte**)&_audioFrame->data, _audioFrame->nb_samples, inData, nb_samples);

                    if (converted > 0)
                    {
                        _audioFrame->pts = ffmpeg.av_rescale_q(frameIndex, new AVRational { num = 1, den = sampleRate }, _outputAudioStream->time_base);
                        frameIndex += converted;

                        EncodeAudioFrame(_audioFrame);
                        samplesProcessed += nb_samples;
                    }
                }
                finally
                {
                    for (int ch = 0; ch < channels; ch++)
                    {
                        if (handles[ch].IsAllocated)
                            handles[ch].Free();
                    }
                }
            }
        }

        private void EncodeAudioFrame(AVFrame* frame)
        {
            int sendRet = ffmpeg.avcodec_send_frame(_audioEncoderCtx, frame);
            if (sendRet == ffmpeg.AVERROR_EOF)
                return;
            if (sendRet < 0 && sendRet != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                FFmpegHelper.Throw(sendRet, "avcodec_send_frame (audio)");

            while (true)
            {
                AVPacket* pkt = ffmpeg.av_packet_alloc();
                int ret = ffmpeg.avcodec_receive_packet(_audioEncoderCtx, pkt);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                {
                    ffmpeg.av_packet_free(&pkt);
                    return;
                }
                FFmpegHelper.Throw(ret, "avcodec_receive_packet (audio)");

                ffmpeg.av_packet_rescale_ts(pkt, _audioEncoderCtx->time_base, _outputAudioStream->time_base);
                pkt->stream_index = _outputAudioStreamIndex;

                FFmpegHelper.Throw(
                    ffmpeg.av_interleaved_write_frame(_outputFmtCtx, pkt),
                    "av_interleaved_write_frame (audio)");
                ffmpeg.av_packet_free(&pkt);
            }
        }

        private void FlushAudioEncoder()
        {
            EncodeAudioFrame(null);
        }

        public void Dispose()
        {
            if (_isDisposed) return;

            if (_audioFrame != null)
            {
                fixed (AVFrame** p = &_audioFrame)
                    ffmpeg.av_frame_free(p);
            }

            if (_swr != null)
            {
                fixed (SwrContext** p = &_swr)
                    ffmpeg.swr_free(p);
            }

            if (_audioEncoderCtx != null)
            {
                fixed (AVCodecContext** p = &_audioEncoderCtx)
                    ffmpeg.avcodec_free_context(p);
            }

            if (_outputFmtCtx != null)
            {
                if ((_outputFmtCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && _outputFmtCtx->pb != null)
                    ffmpeg.avio_closep(&_outputFmtCtx->pb);
                fixed (AVFormatContext** p = &_outputFmtCtx)
                    ffmpeg.avformat_free_context(*p);
            }

            if (_inputFmtCtx != null)
            {
                fixed (AVFormatContext** p = &_inputFmtCtx)
                    ffmpeg.avformat_close_input(p);
            }

            _isDisposed = true;
        }

        /// <summary>
        /// Muxes a video file with an audio file to create a combined output file.
        /// This method copies both video and audio streams without re-encoding when possible.
        /// </summary>
        /// <param name="videoInputPath">Path to the input video file.</param>
        /// <param name="audioInputPath">Path to the input audio file.</param>
        /// <param name="outputPath">Path to the output file.</param>
        /// <param name="reencodeAudio">If true, re-encodes audio to match the output container format. If false, tries to copy the audio stream directly.</param>
        public static void MuxFromFiles(string videoInputPath, string audioInputPath, string outputPath, bool reencodeAudio = false)
        {
            if (string.IsNullOrEmpty(videoInputPath))
                throw new ArgumentNullException(nameof(videoInputPath));
            if (string.IsNullOrEmpty(audioInputPath))
                throw new ArgumentNullException(nameof(audioInputPath));
            if (string.IsNullOrEmpty(outputPath))
                throw new ArgumentNullException(nameof(outputPath));
            if (!File.Exists(videoInputPath))
                throw new FileNotFoundException($"Video input file not found: {videoInputPath}");
            if (!File.Exists(audioInputPath))
                throw new FileNotFoundException($"Audio input file not found: {audioInputPath}");

            AVFormatContext* videoFmtCtx = null;
            AVFormatContext* audioFmtCtx = null;
            AVFormatContext* outputFmtCtx = null;
            AVCodecContext* audioEncoderCtx = null;
            AVCodecContext* audioDecoderCtx = null;
            SwrContext* swr = null;
            AVFrame* audioFrame = null;

            try
            {
                // Open video input
                FFmpegHelper.Throw(
                    ffmpeg.avformat_open_input(&videoFmtCtx, videoInputPath, null, null),
                    "avformat_open_input (video)");
                FFmpegHelper.Throw(ffmpeg.avformat_find_stream_info(videoFmtCtx, null), "avformat_find_stream_info (video)");

                // Open audio input
                FFmpegHelper.Throw(
                    ffmpeg.avformat_open_input(&audioFmtCtx, audioInputPath, null, null),
                    "avformat_open_input (audio)");
                FFmpegHelper.Throw(ffmpeg.avformat_find_stream_info(audioFmtCtx, null), "avformat_find_stream_info (audio)");

                // Find video stream
                int videoStreamIndex = -1;
                for (int i = 0; i < (int)videoFmtCtx->nb_streams; i++)
                {
                    if (videoFmtCtx->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        videoStreamIndex = i;
                        break;
                    }
                }
                if (videoStreamIndex < 0)
                    throw new InvalidDataException($"No video stream found in '{videoInputPath}'.");

                // Find audio stream
                int audioStreamIndex = -1;
                for (int i = 0; i < (int)audioFmtCtx->nb_streams; i++)
                {
                    if (audioFmtCtx->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                    {
                        audioStreamIndex = i;
                        break;
                    }
                }
                if (audioStreamIndex < 0)
                    throw new InvalidDataException($"No audio stream found in '{audioInputPath}'.");

                AVStream* inputVideoStream = videoFmtCtx->streams[videoStreamIndex];
                AVStream* inputAudioStream = audioFmtCtx->streams[audioStreamIndex];

                // Allocate output context
                FFmpegHelper.Throw(
                    ffmpeg.avformat_alloc_output_context2(&outputFmtCtx, null, null, outputPath),
                    "avformat_alloc_output_context2");

                // Create output video stream (copy)
                AVStream* outputVideoStream = ffmpeg.avformat_new_stream(outputFmtCtx, null);
                if (outputVideoStream == null)
                    throw new InvalidOperationException("Failed to create output video stream.");
                FFmpegHelper.Throw(
                    ffmpeg.avcodec_parameters_copy(outputVideoStream->codecpar, inputVideoStream->codecpar),
                    "avcodec_parameters_copy (video)");
                outputVideoStream->codecpar->codec_tag = 0;
                outputVideoStream->time_base = inputVideoStream->time_base;
                int outputVideoStreamIndex = outputVideoStream->index;

                // Create output audio stream
                AVStream* outputAudioStream = ffmpeg.avformat_new_stream(outputFmtCtx, null);
                if (outputAudioStream == null)
                    throw new InvalidOperationException("Failed to create output audio stream.");

                int outputAudioStreamIndex = outputAudioStream->index;

                if (reencodeAudio)
                {
                    // Determine audio codec based on output extension
                    string ext = Path.GetExtension(outputPath).ToLowerInvariant();
                    string audioCodecName = ext switch
                    {
                        ".mp4" or ".m4v" or ".mov" => "aac",
                        ".mkv" or ".webm" => "libvorbis",
                        ".avi" => "libmp3lame",
                        ".wmv" => "wmav2",
                        _ => "aac"
                    };

                    // Setup audio decoder
                    AVCodec* audioDecoder = ffmpeg.avcodec_find_decoder(inputAudioStream->codecpar->codec_id);
                    if (audioDecoder == null)
                        throw new NotSupportedException($"Audio decoder not found for codec id: {inputAudioStream->codecpar->codec_id}");

                    audioDecoderCtx = ffmpeg.avcodec_alloc_context3(audioDecoder);
                    FFmpegHelper.Throw(
                        ffmpeg.avcodec_parameters_to_context(audioDecoderCtx, inputAudioStream->codecpar),
                        "avcodec_parameters_to_context (audio decoder)");
                    FFmpegHelper.Throw(ffmpeg.avcodec_open2(audioDecoderCtx, audioDecoder, null), "avcodec_open2 (audio decoder)");

                    // Setup audio encoder
                    AVCodec* audioEncoder = ffmpeg.avcodec_find_encoder_by_name(audioCodecName);
                    if (audioEncoder == null)
                        throw new EntryPointNotFoundException($"Could not find audio encoder '{audioCodecName}'.");

                    audioEncoderCtx = ffmpeg.avcodec_alloc_context3(audioEncoder);
                    audioEncoderCtx->codec_id = audioEncoder->id;
                    audioEncoderCtx->codec_type = AVMediaType.AVMEDIA_TYPE_AUDIO;
                    audioEncoderCtx->sample_rate = audioDecoderCtx->sample_rate;
                    audioEncoderCtx->bit_rate = 192000;
                    audioEncoderCtx->time_base = new AVRational { num = 1, den = audioEncoderCtx->sample_rate };

                    // Select best sample format supported by the encoder
#pragma warning disable 618
                    if (audioEncoder->sample_fmts != null)
                    {
                        // Default to first supported
                        audioEncoderCtx->sample_fmt = *audioEncoder->sample_fmts;
                        
                        // Try to find preferred formats (FLTP > S16 > FLT)
                        AVSampleFormat* p = audioEncoder->sample_fmts;
                        while (*p != AVSampleFormat.AV_SAMPLE_FMT_NONE)
                        {
                            if (*p == AVSampleFormat.AV_SAMPLE_FMT_FLTP) 
                            {
                                audioEncoderCtx->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
                                break;
                            }
                            if (*p == AVSampleFormat.AV_SAMPLE_FMT_S16 && audioEncoderCtx->sample_fmt != AVSampleFormat.AV_SAMPLE_FMT_FLTP)
                            {
                                audioEncoderCtx->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_S16;
                            }
                            p++;
                        }
                    }
                    else
#pragma warning restore 618
                    {
                        AVSampleFormat sampleFmt = audioCodecName switch
                        {
                            "pcm_s16le" or "pcm_s16be" => AVSampleFormat.AV_SAMPLE_FMT_S16,
                            "pcm_s32le" or "pcm_s32be" => AVSampleFormat.AV_SAMPLE_FMT_S32,
                            "flac" => AVSampleFormat.AV_SAMPLE_FMT_S16,
                            _ => AVSampleFormat.AV_SAMPLE_FMT_FLTP
                        };
                        audioEncoderCtx->sample_fmt = sampleFmt;
                    }

                    // Set channel layout - use default layout for channel count to ensure compatibility
                    int channels = audioDecoderCtx->ch_layout.nb_channels;
                    if (channels <= 0) channels = 2;
                    ffmpeg.av_channel_layout_default(&audioEncoderCtx->ch_layout, channels);
                    
                    outputAudioStream->time_base = audioEncoderCtx->time_base;

                    if ((outputFmtCtx->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                        audioEncoderCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

                    FFmpegHelper.Throw(ffmpeg.avcodec_open2(audioEncoderCtx, audioEncoder, null), "avcodec_open2 (audio encoder)");
                    FFmpegHelper.Throw(
                        ffmpeg.avcodec_parameters_from_context(outputAudioStream->codecpar, audioEncoderCtx),
                        "avcodec_parameters_from_context (audio)");

                    // Setup resampler
                    swr = ffmpeg.swr_alloc();
                    SwrContext* swrTemp = swr;
                    AVChannelLayout* outLayout = &audioEncoderCtx->ch_layout;
                    AVChannelLayout* inLayout = &audioDecoderCtx->ch_layout;
                    ffmpeg.swr_alloc_set_opts2(&swrTemp, outLayout, audioEncoderCtx->sample_fmt, audioEncoderCtx->sample_rate,
                                               inLayout, audioDecoderCtx->sample_fmt, audioDecoderCtx->sample_rate, 0, null);
                    swr = swrTemp;
                    ffmpeg.swr_init(swr);

                    // Allocate audio frame
                    audioFrame = ffmpeg.av_frame_alloc();
                    audioFrame->sample_rate = audioEncoderCtx->sample_rate;
                    audioFrame->format = (int)audioEncoderCtx->sample_fmt;
                    ffmpeg.av_channel_layout_copy(&audioFrame->ch_layout, &audioEncoderCtx->ch_layout);
                    audioFrame->nb_samples = audioEncoderCtx->frame_size > 0 ? audioEncoderCtx->frame_size : 1024;
                    FFmpegHelper.Throw(ffmpeg.av_frame_get_buffer(audioFrame, 0), "av_frame_get_buffer (audio)");
                }
                else
                {
                    // Copy audio stream directly
                    FFmpegHelper.Throw(
                        ffmpeg.avcodec_parameters_copy(outputAudioStream->codecpar, inputAudioStream->codecpar),
                        "avcodec_parameters_copy (audio)");
                    outputAudioStream->codecpar->codec_tag = 0;
                    outputAudioStream->time_base = inputAudioStream->time_base;
                }

                // Open output file
                if ((outputFmtCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                {
                    FFmpegHelper.Throw(
                        ffmpeg.avio_open(&outputFmtCtx->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE),
                        "avio_open");
                }

                // Write header
                FFmpegHelper.Throw(ffmpeg.avformat_write_header(outputFmtCtx, null), "avformat_write_header");

                // Read and write packets
                AVPacket* pkt = ffmpeg.av_packet_alloc();
                AVFrame* decodedFrame = reencodeAudio ? ffmpeg.av_frame_alloc() : null;
                long audioFrameIndex = 0;
                
                // FIFO for audio buffering
                AVAudioFifo* fifo = null;
                if (reencodeAudio)
                {
                    fifo = ffmpeg.av_audio_fifo_alloc(audioEncoderCtx->sample_fmt, audioEncoderCtx->ch_layout.nb_channels, 1);
                }

                try
                {
                    // Process video packets
                    while (ffmpeg.av_read_frame(videoFmtCtx, pkt) >= 0)
                    {
                        if (pkt->stream_index == videoStreamIndex)
                        {
                            ffmpeg.av_packet_rescale_ts(pkt, inputVideoStream->time_base, outputVideoStream->time_base);
                            pkt->stream_index = outputVideoStreamIndex;
                            FFmpegHelper.Throw(
                                ffmpeg.av_interleaved_write_frame(outputFmtCtx, pkt),
                                "av_interleaved_write_frame (video)");
                        }
                        ffmpeg.av_packet_unref(pkt);
                    }

                    // Process audio packets
                    while (ffmpeg.av_read_frame(audioFmtCtx, pkt) >= 0)
                    {
                        if (pkt->stream_index == audioStreamIndex)
                        {
                            if (reencodeAudio)
                            {
                                // Decode audio
                                if (ffmpeg.avcodec_send_packet(audioDecoderCtx, pkt) >= 0)
                                {
                                    while (ffmpeg.avcodec_receive_frame(audioDecoderCtx, decodedFrame) >= 0)
                                    {
                                        // Resample and write to FIFO
                                        // Calculate output samples
                                        int out_samples = (int)ffmpeg.av_rescale_rnd(
                                            ffmpeg.swr_get_delay(swr, audioDecoderCtx->sample_rate) + decodedFrame->nb_samples,
                                            audioEncoderCtx->sample_rate, audioDecoderCtx->sample_rate, AVRounding.AV_ROUND_UP);
                                        
                                        if (out_samples > 0)
                                        {
                                            byte** convertedData = null;
                                            ffmpeg.av_samples_alloc_array_and_samples(&convertedData, null, audioEncoderCtx->ch_layout.nb_channels, out_samples, audioEncoderCtx->sample_fmt, 0);
                                            
                                            int converted = ffmpeg.swr_convert(swr, convertedData, out_samples,
                                                                               (byte**)&decodedFrame->data, decodedFrame->nb_samples);
                                            
                                            if (converted > 0)
                                            {
                                                ffmpeg.av_audio_fifo_write(fifo, (void**)convertedData, converted);
                                            }
                                            
                                            if (convertedData != null)
                                            {
                                                ffmpeg.av_freep(&convertedData[0]);
                                                ffmpeg.av_freep(&convertedData);
                                            }
                                        }
                                        
                                        // Read from FIFO and encode
                                        while (ffmpeg.av_audio_fifo_size(fifo) >= audioFrame->nb_samples)
                                        {
                                            FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(audioFrame), "av_frame_make_writable");
                                            
                                            ffmpeg.av_audio_fifo_read(fifo, (void**)&audioFrame->data, audioFrame->nb_samples);
                                            
                                            audioFrame->pts = audioFrameIndex;
                                            audioFrameIndex += audioFrame->nb_samples;

                                            // Encode
                                            int sendRet = ffmpeg.avcodec_send_frame(audioEncoderCtx, audioFrame);
                                            if (sendRet >= 0 || sendRet == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                                            {
                                                AVPacket* outPkt = ffmpeg.av_packet_alloc();
                                                while (ffmpeg.avcodec_receive_packet(audioEncoderCtx, outPkt) >= 0)
                                                {
                                                    ffmpeg.av_packet_rescale_ts(outPkt, audioEncoderCtx->time_base, outputAudioStream->time_base);
                                                    outPkt->stream_index = outputAudioStreamIndex;
                                                    ffmpeg.av_interleaved_write_frame(outputFmtCtx, outPkt);
                                                }
                                                ffmpeg.av_packet_free(&outPkt);
                                            }
                                        }
                                        ffmpeg.av_frame_unref(decodedFrame);
                                    }
                                }
                            }
                            else
                            {
                                // Copy audio packet directly
                                ffmpeg.av_packet_rescale_ts(pkt, inputAudioStream->time_base, outputAudioStream->time_base);
                                pkt->stream_index = outputAudioStreamIndex;
                                FFmpegHelper.Throw(
                                    ffmpeg.av_interleaved_write_frame(outputFmtCtx, pkt),
                                    "av_interleaved_write_frame (audio)");
                            }
                        }
                        ffmpeg.av_packet_unref(pkt);
                    }

                    // Flush audio encoder if re-encoding
                    if (reencodeAudio)
                    {
                        // Flush remaining samples in FIFO
                        int remaining = ffmpeg.av_audio_fifo_size(fifo);
                        if (remaining > 0)
                        {
                            FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(audioFrame), "av_frame_make_writable");
                            ffmpeg.av_audio_fifo_read(fifo, (void**)&audioFrame->data, remaining);
                            audioFrame->nb_samples = remaining;
                            audioFrame->pts = audioFrameIndex;
                            audioFrameIndex += remaining;
                            
                            ffmpeg.avcodec_send_frame(audioEncoderCtx, audioFrame);
                            AVPacket* outPkt = ffmpeg.av_packet_alloc();
                            while (ffmpeg.avcodec_receive_packet(audioEncoderCtx, outPkt) >= 0)
                            {
                                ffmpeg.av_packet_rescale_ts(outPkt, audioEncoderCtx->time_base, outputAudioStream->time_base);
                                outPkt->stream_index = outputAudioStreamIndex;
                                ffmpeg.av_interleaved_write_frame(outputFmtCtx, outPkt);
                            }
                            ffmpeg.av_packet_free(&outPkt);
                        }
                        
                        // Flush encoder
                        ffmpeg.avcodec_send_frame(audioEncoderCtx, null);
                        AVPacket* outPktFlush = ffmpeg.av_packet_alloc();
                        while (ffmpeg.avcodec_receive_packet(audioEncoderCtx, outPktFlush) >= 0)
                        {
                            ffmpeg.av_packet_rescale_ts(outPktFlush, audioEncoderCtx->time_base, outputAudioStream->time_base);
                            outPktFlush->stream_index = outputAudioStreamIndex;
                            ffmpeg.av_interleaved_write_frame(outputFmtCtx, outPktFlush);
                        }
                        ffmpeg.av_packet_free(&outPktFlush);
                    }
                }
                finally
                {
                    if (fifo != null)
                    {
                        AVAudioFifo* tempFifo = fifo;
                        ffmpeg.av_audio_fifo_free(tempFifo);
                    }
                    ffmpeg.av_packet_free(&pkt);
                    if (decodedFrame != null)
                    {
                        AVFrame* tempFrame = decodedFrame;
                        ffmpeg.av_frame_free(&tempFrame);
                    }
                }

                // Write trailer
                FFmpegHelper.Throw(ffmpeg.av_write_trailer(outputFmtCtx), "av_write_trailer");
            }
            finally
            {
                // Cleanup
                if (audioFrame != null)
                {
                    AVFrame* tempFrame = audioFrame;
                    ffmpeg.av_frame_free(&tempFrame);
                }
                if (swr != null)
                {
                    SwrContext* tempSwr = swr;
                    ffmpeg.swr_free(&tempSwr);
                }
                if (audioEncoderCtx != null)
                {
                    AVCodecContext* tempCtx = audioEncoderCtx;
                    ffmpeg.avcodec_free_context(&tempCtx);
                }
                if (audioDecoderCtx != null)
                {
                    AVCodecContext* tempCtx = audioDecoderCtx;
                    ffmpeg.avcodec_free_context(&tempCtx);
                }
                if (outputFmtCtx != null)
                {
                    if ((outputFmtCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && outputFmtCtx->pb != null)
                        ffmpeg.avio_closep(&outputFmtCtx->pb);
                    AVFormatContext* tempFmt = outputFmtCtx;
                    ffmpeg.avformat_free_context(tempFmt);
                }
                if (audioFmtCtx != null)
                {
                    AVFormatContext* tempFmt = audioFmtCtx;
                    ffmpeg.avformat_close_input(&tempFmt);
                }
                if (videoFmtCtx != null)
                {
                    AVFormatContext* tempFmt = videoFmtCtx;
                    ffmpeg.avformat_close_input(&tempFmt);
                }
            }
        }
    }
}
