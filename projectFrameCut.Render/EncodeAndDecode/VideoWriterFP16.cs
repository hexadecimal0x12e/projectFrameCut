using projectFrameCut.Drawing.Processing.Converting;
using FFmpeg.AutoGen;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace projectFrameCut.Render.EncodeAndDecode
{
    public sealed unsafe class VideoWriterFP16 : IVideoWriter
    {
        private int _width;
        public int Width
        {
            get => _width;
            set
            {
                if (_inited) throw new InvalidOperationException("Cannot modify property after initialization");
                _width = value;
            }
        }

        private int _height;
        public int Height
        {
            get => _height;
            set
            {
                if (_inited) throw new InvalidOperationException("Cannot modify property after initialization");
                _height = value;
            }
        }

        private string _outputPath;
        public string OutputPath
        {
            get => _outputPath;
            set
            {
                if (_inited) throw new InvalidOperationException("Cannot modify property after initialization");
                _outputPath = value;
            }
        }

        private int _framePerSecond;
        public int FramePerSecond
        {
            get => _framePerSecond;
            set
            {
                if (_inited) throw new InvalidOperationException("Cannot modify property after initialization");
                _framePerSecond = value;
            }
        }

        private string _codecName;
        public string CodecName
        {
            get => _codecName;
            set
            {
                if (_inited) throw new InvalidOperationException("Cannot modify property after initialization");
                _codecName = value;
            }
        }

        private string _pixelFormatString;
        public string PixelFormat
        {
            get => _pixelFormatString;
            set
            {
                if (_inited) throw new InvalidOperationException("Cannot modify property after initialization");
                _pixelFormatString = value;
            }
        }



        private Dictionary<string, string>? _metadata;
        public Dictionary<string, string>? Metadata
        {
            get => _metadata;
            set
            {
                if (_isHeaderWritten) throw new InvalidOperationException("Cannot modify metadata after header has been written");
                _metadata = value;
            }
        }

        public long BitRate
        {
            get => _bitRate;
            set
            {
                if (_inited) throw new InvalidOperationException("Cannot modify property after initialization");
                _bitRate = value;
            }
        }

        private AVPixelFormat _pixelFormat;
        private AVFormatContext* _fmtCtx;
        private AVStream* _videoStream;
        private AVCodecContext* _codecCtx;
        private AVFrame* _frameDst;
        private AVFrame* _frameSrc;
        private SwsContext* _sws;
        private int _frameIndex;
        private bool _isHeaderWritten;
        private bool _isDisposed;
        private long _bitRate = 4_000_000;
        private bool _inited;

        public bool IsOpened => _fmtCtx != null;

        public uint Index { get; set; } = 0;
        //public string OutputOutputPath { get => OutputPath; }
        public IPicture.PicturePixelMode PixelMode => IPicture.PicturePixelMode.Float16Picture;
        public int Fps => FramePerSecond;

        public uint DurationWritten => Index;

        public IPicture.PicturePixelMode? TargetPPB => IPicture.PicturePixelMode.Float16Picture;

        public bool PreferToSpeed { get; set; }

        public static bool DetectCodec(string codec)
        {
            if (FFmpegHelper.CodecUtils.GetCodecsByType(AVMediaType.AVMEDIA_TYPE_VIDEO, true).Find(c => c.Name.Equals(codec, StringComparison.OrdinalIgnoreCase)) != null)
            {
                return true;
            }
            return false;
        }

        bool IVideoWriter.SupportCodec(string codecName) => DetectCodec(codecName);

        public void Initialize()
        {
            if (OutputPath is null || _inited == true) return;
            if (Width <= 0 || Height <= 0 || FramePerSecond <= 0) throw new ArgumentOutOfRangeException("You set an invalid width, height or fps.");
            if (Path.GetDirectoryName(OutputPath) is not string p || !Directory.Exists(p)) throw new DirectoryNotFoundException($"The target directory '{Path.GetDirectoryName(OutputPath)}' does not exist or it's invalid.");
            if (File.Exists(OutputPath)) throw new InvalidOperationException($"Video file {OutputPath} already exists.");
            if (!Enum.TryParse(PixelFormat, out _pixelFormat) || _pixelFormat == AVPixelFormat.AV_PIX_FMT_NONE)
            {
                throw new ArgumentException($"The pixel format '{PixelFormat}' is not found. Please check the pixel format name.");
            }

            AVFormatContext* oc = null;
            int ret = ffmpeg.avformat_alloc_output_context2(&oc, null, null, OutputPath);
            if (ret < 0 || oc == null)
            {
                try
                {
                    using (var fs = System.IO.File.OpenWrite(OutputPath))
                    {
                        fs.WriteByte(1);
                        FFmpegHelper.Throw(ret, "Init video stream (avformat_alloc_output_context2)");
                    }
                }
                catch (DirectoryNotFoundException)
                {
                    throw new DirectoryNotFoundException($"The target directory '{Path.GetDirectoryName(OutputPath)}' not exist. (FFmpeg error:{FFmpegHelper.GetErrorString(ret) ?? "unknown"}, code:{ret})");
                }
                catch (PathTooLongException ex)
                {
                    throw new FileLoadException($"projectFrameCut can't write the video file '{OutputPath}' because of path is too long. Try modify the temp directory in the settings. (FFmpeg error:{FFmpegHelper.GetErrorString(ret) ?? "unknown"}, code:{ret})", ex);
                }
                catch (IOException io)
                {
                    throw new IOException($"projectFrameCut can't write the target video file '{OutputPath}' because I/O operation fail: {io.Message} \r\n(FFmpeg error:{FFmpegHelper.GetErrorString(ret) ?? "unknown"}, code:{ret})", io);
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new FileLoadException($"projectFrameCut can't write the target video file '{OutputPath}' because of no enough privileges. Try modify the privileges of output dir. (FFmpeg error:{FFmpegHelper.GetErrorString(ret) ?? "unknown"}, code:{ret})", ex);
                }

                catch (Exception ex)
                {
                    throw new InvalidOperationException($"projectFrameCut failed to write the file because of '{ex.Message}'. (FFmpeg error:{FFmpegHelper.GetErrorString(ret) ?? "unknown"}, code:{ret})", ex);
                }
            }
            _fmtCtx = oc;

            AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(CodecName);
            if (codec == null) throw new EntryPointNotFoundException($"Could not found the encoder '{CodecName}'. Try install codec extension-pack, or reinstall projectFrameCut.");

            if (codec->id == AVCodecID.AV_CODEC_ID_MJPEG
                && _pixelFormat == AVPixelFormat.AV_PIX_FMT_YUV420P)
            {
                _pixelFormat = AVPixelFormat.AV_PIX_FMT_YUVJ420P;
            }

            _videoStream = ffmpeg.avformat_new_stream(_fmtCtx, codec);
            if (_videoStream == null) throw new InvalidOperationException("Failed to create a stream to write video.");

            _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (_codecCtx == null) throw new InvalidOperationException("Failed to allocate a context for video.");

            _codecCtx->codec_id = codec->id;
            _codecCtx->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            _codecCtx->width = Width;
            _codecCtx->height = Height;
            _codecCtx->pix_fmt = _pixelFormat;
            _codecCtx->time_base = new AVRational { num = 1, den = FramePerSecond };
            _videoStream->time_base = _codecCtx->time_base;
            _codecCtx->framerate = new AVRational { num = FramePerSecond, den = 1 };
            _codecCtx->gop_size = 12;
            _codecCtx->max_b_frames = 2;
            _codecCtx->bit_rate = _bitRate;

            if ((_fmtCtx->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                _codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

            AVDictionary* opts = null;
            string encoderName = Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? CodecName ?? string.Empty;
            if (encoderName.Equals("libx264", StringComparison.OrdinalIgnoreCase))
            {
                ffmpeg.av_dict_set(&opts, "preset", PreferToSpeed ? "veryfast" : "medium", 0);
            }
            else if (PreferToSpeed && encoderName.Equals("libx265", StringComparison.OrdinalIgnoreCase))
            {
                ffmpeg.av_dict_set(&opts, "preset", "veryfast", 0);
            }

            FFmpegHelper.Throw(ffmpeg.avcodec_open2(_codecCtx, codec, &opts), "Open target codec stream");
            ffmpeg.av_dict_free(&opts);

            FFmpegHelper.Throw(ffmpeg.avcodec_parameters_from_context(_videoStream->codecpar, _codecCtx),
                "avcodec_parameters_from_context");

            if ((_fmtCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                FFmpegHelper.Throw(ffmpeg.avio_open(&_fmtCtx->pb, OutputPath, ffmpeg.AVIO_FLAG_WRITE), "Open target audio/video stream");
            }

            _frameDst = FFmpegFP16FrameCodec.AllocateFrame(Width, Height, _pixelFormat);

            var srcPixFmt = FFmpegFP16FrameCodec.EncoderFormat;

            _frameSrc = FFmpegFP16FrameCodec.AllocateFrame(Width, Height, srcPixFmt);

            _sws = ffmpeg.sws_getContext(
                Width, Height, srcPixFmt,
                Width, Height, _pixelFormat,
                4, null, null, null);
            // SWS_BICUBIC == 4

            if (_sws == null) throw new InvalidOperationException("Couldn't get the SWS context.");
            FFmpegFP16FrameCodec.ConfigureEncoderColorspace(_sws, _codecCtx);
            Log($"[VideoBuilder] Successfully initialized encoder for {OutputPath}");

            _inited = true;

        }

        public void Append(IPicture<Half> picture)
        {
            if (picture is IHDRPicture<Half> hdr)
            {
                using var sdr = hdr.DegradeToSDR();
                Append((IPicture<Half>)sdr);
                return;
            }
            ArgumentNullException.ThrowIfNull(picture);
            if (picture.Width != _width || picture.Height != _height)
                throw new ArgumentException("The result size is different from original size. Please check the source.");
            if (_isDisposed) throw new ObjectDisposedException(nameof(VideoBuilder));

            EnsureHeader();

            FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(_frameSrc), "make frame writable");
            FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(_frameDst), "make frame writable");

            FFmpegFP16FrameCodec.Write(_frameSrc, picture);

            FFmpegHelper.ScaleFrame(_sws, _frameSrc, _frameDst);

            _frameDst->pts = _frameIndex++;

            EncodeFrame(_frameDst);

            Index++;
        }

        public void Append(IPicture picture)
        {
            if (picture is not IPicture<Half> fp) throw new NotSupportedException("The video writer requires FP16 pictures.");
            Append(fp);
        }

        private void EnsureHeader()
        {
            if (_isHeaderWritten) return;

            if (_fmtCtx == null)
            {
                if (string.IsNullOrWhiteSpace(OutputPath))
                    throw new InvalidOperationException(
                        "Cannot write video header: OutputPath was not set. " +
                        "The video writer was created without an output path (for codec probing) " +
                        "but Append was called as if it were ready to write. " +
                        "Set OutputPath and call Initialize() before writing frames.");
                throw new InvalidOperationException(
                    $"Cannot write video header: the video writer was not properly initialized " +
                    $"(OutputPath='{OutputPath}', but the format context is null). " +
                    "Ensure Initialize() completed successfully before calling Append.");
            }

            if (_metadata != null && _metadata.Count > 0)
            {
                foreach (var kv in _metadata)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    var value = kv.Value ?? string.Empty;
                    // Write to container-level metadata
                    ffmpeg.av_dict_set(&_fmtCtx->metadata, kv.Key, value, 0);
                    // Also write to stream-level metadata — some readers only look there
                    if (_videoStream != null)
                        ffmpeg.av_dict_set(&_videoStream->metadata, kv.Key, value, 0);
                }
            }

            FFmpegHelper.Throw(ffmpeg.avformat_write_header(_fmtCtx, null), "avformat_write_header");
            _isHeaderWritten = true;
        }

        private void EncodeFrame(AVFrame* frame)
        {
            // 当编码器内部队列满时，avcodec_send_frame 会返回 AVERROR(EAGAIN)。
            // 此时需要先取出已编码的包，然后重试发送同一帧，而不是直接报错。
            while (true)
            {
                int sendRet = ffmpeg.avcodec_send_frame(_codecCtx, frame);
                if (sendRet >= 0)
                    break;

                if (sendRet == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    DrainEncodedPackets();
                    continue;
                }

                FFmpegHelper.Throw(sendRet, "avcodec_send_frame");
            }

            DrainEncodedPackets();
        }

        private void DrainEncodedPackets()
        {
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

                ffmpeg.av_packet_rescale_ts(pkt, _codecCtx->time_base, _videoStream->time_base);
                pkt->stream_index = _videoStream->index;

                FFmpegHelper.Throw(ffmpeg.av_interleaved_write_frame(_fmtCtx, pkt), "av_interleaved_write_frame");
                ffmpeg.av_packet_free(&pkt);
            }
        }

        public void Finish()
        {
            if (_isDisposed || Index <= 0) return;

            FFmpegHelper.Throw(ffmpeg.avcodec_send_frame(_codecCtx, null), "avcodec_send_frame(flush)");
            DrainEncodedPackets();

            if (_isHeaderWritten)
            {
                FFmpegHelper.Throw(ffmpeg.av_write_trailer(_fmtCtx), "av_write_trailer");
            }

            Log($"[VideoBuilder] Successfully finished video writer for {OutputPath}, total {Index} frame written.");

        }

        private void ReleaseUnmanaged()
        {
            if (_frameSrc != null)
            {
                fixed (AVFrame** p = &_frameSrc)
                {
                    ffmpeg.av_frame_free(p);
                }
                _frameSrc = null;
            }
            if (_frameDst != null)
            {
                fixed (AVFrame** p = &_frameDst)
                {
                    ffmpeg.av_frame_free(p);
                }
                _frameDst = null;
            }
            if (_codecCtx != null)
            {
                fixed (AVCodecContext** p = &_codecCtx)
                {
                    ffmpeg.avcodec_free_context(p);
                }
                _codecCtx = null;
            }
            if (_sws != null)
            {
                ffmpeg.sws_freeContext(_sws);
                _sws = null;
            }
            if (_fmtCtx != null)
            {
                if (_fmtCtx->pb != null)
                {
                    ffmpeg.avio_closep(&_fmtCtx->pb);
                }
                fixed (AVFormatContext** p = &_fmtCtx)
                {
                    ffmpeg.avformat_free_context(*p);
                }
                _fmtCtx = null;
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

            }
            ReleaseUnmanaged();
            _isDisposed = true;
            GC.SuppressFinalize(this);
        }

        ~VideoWriterFP16()
        {
            // Never call into FFmpeg encoding APIs from the finalizer thread.
            // This type must be disposed deterministically; finalizer is best-effort cleanup only.
            try
            {
                ReleaseUnmanaged();
            }
            catch
            {
                // Suppress all exceptions on finalizer thread.
            }
        }
    }

}
