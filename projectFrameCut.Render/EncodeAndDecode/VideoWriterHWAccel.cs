using FFmpeg.AutoGen;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace projectFrameCut.Render.EncodeAndDecode
{
    /// <summary>
    /// Hardware-accelerated video encoder.
    /// Platform-aware auto-detection using <see cref="OperatingSystem"/> APIs —
    /// on iOS/macOS: VideoToolbox; on Android: MediaCodec; on Windows: NVENC/AMF/QSV; on Linux: VAAPI.
    /// </summary>
    public sealed unsafe class VideoWriterHWAccel : IVideoWriter
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

        public long BitRate
        {
            get => _bitRate;
            set
            {
                if (_inited) throw new InvalidOperationException("Cannot modify property after initialization");
                _bitRate = value;
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

        // --- Native fields ---
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
        private int _colorDepth = 8;
        private long _bitRate = 8_000_000;
        private bool _inited;

        // --- HW acceleration fields ---
        private AVBufferRef* _hwDeviceCtx;
        private AVHWDeviceType _hwDeviceType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
        private string? _resolvedEncoderName;
        private List<string>? _hwEncoderCandidates;

        public bool IsOpened => _fmtCtx != null;

        public uint Index { get; set; } = 0;

        public IPicture.PicturePixelMode PixelMode => _colorDepth;
        public int Fps => FramePerSecond;

        public uint DurationWritten => Index;

        public IPicture.PicturePixelMode? TargetPPB => _colorDepth;

        // ────────────────────────── Codec detection ──────────────────────────

        public static bool DetectCodec(string codec)
        {
            if (FFmpegHelper.CodecUtils.GetCodecsByType(AVMediaType.AVMEDIA_TYPE_VIDEO, true).Find(c => c.Name.Equals(codec, StringComparison.OrdinalIgnoreCase)) != null)
                return true;
            return false;
        }

        bool IVideoWriter.SupportCodec(string codecName)
        {
            // Accept our own type name so PluginManager can match it.
            if (string.Equals(codecName, "hwaccel", StringComparison.OrdinalIgnoreCase))
                return true;

            return DetectCodec(codecName) || GetHardwareEncoderName(codecName) != null;
        }

        // ────────────────────────── Initialisation ──────────────────────────

        public void Initialize()
        {
            if (OutputPath is null || _inited) return;
            if (Width <= 0 || Height <= 0 || FramePerSecond <= 0)
                throw new ArgumentOutOfRangeException("You set an invalid width, height or fps.");
            if (Path.GetDirectoryName(OutputPath) is not string p || !Directory.Exists(p))
                throw new DirectoryNotFoundException($"The target directory '{Path.GetDirectoryName(OutputPath)}' does not exist or it's invalid.");
            if (File.Exists(OutputPath))
                throw new InvalidOperationException($"Video file {OutputPath} already exists.");
            if (!Enum.TryParse(PixelFormat, out _pixelFormat) || _pixelFormat == AVPixelFormat.AV_PIX_FMT_NONE)
                throw new ArgumentException($"The pixel format '{PixelFormat}' is not found. Please check the pixel format name.");

            // ── 1. Allocate format context ──
            AVFormatContext* oc = null;
            int ret = ffmpeg.avformat_alloc_output_context2(&oc, null, null, OutputPath);
            if (ret < 0 || oc == null)
                WrapFormatAllocError(ret);
            _fmtCtx = oc;

            // ── 2. Resolve encoder ──
            AVCodec* codec = ResolveEncoder(CodecName);
            if (codec == null)
                throw new EntryPointNotFoundException($"Could not find any hardware encoder for '{CodecName}'. Disable hardware acceleration and try again, or try install codec extension-pack, or reinstall projectFrameCut.");

            // ── 3. Create stream ──
            _videoStream = ffmpeg.avformat_new_stream(_fmtCtx, codec);
            if (_videoStream == null) throw new InvalidOperationException("Failed to create a stream to write video.");

            // ── 4. Codec context ──
            _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (_codecCtx == null) throw new InvalidOperationException("Failed to allocate a context for video.");

            _codecCtx->codec_id = codec->id;
            _codecCtx->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            _codecCtx->width = Width;
            _codecCtx->height = Height;
            _codecCtx->time_base = new AVRational { num = 1, den = FramePerSecond };
            _videoStream->time_base = _codecCtx->time_base;
            _codecCtx->framerate = new AVRational { num = FramePerSecond, den = 1 };
            _codecCtx->gop_size = Math.Max(FramePerSecond * 3, 30);
            _codecCtx->max_b_frames = 2;
            _codecCtx->bit_rate = _bitRate;
            _codecCtx->rc_max_rate = _bitRate * 2;
            _codecCtx->rc_buffer_size = _bitRate > int.MaxValue / 2 ? int.MaxValue / 2 : (int)(_bitRate * 2);

            // ── 5. Set up HW device ──
            SetupHardwareDevice(codec);

            // After setting hw_device_ctx, query the encoder's supported pixel formats
            // and pick the best one that is compatible with our input.
            _pixelFormat = SelectHardwarePixelFormat(codec) ?? _pixelFormat;

            _codecCtx->pix_fmt = _pixelFormat;

            if ((_fmtCtx->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                _codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

            // ── 6. HW encoder options — low-latency tuning ──
            AVDictionary* opts = null;
            ConfigureHardwareEncoderOptions(codec, &opts);

            ret = ffmpeg.avcodec_open2(_codecCtx, codec, &opts);
            if (ret < 0)
            {
                ffmpeg.av_dict_free(&opts);

                // ── Try remaining HW encoders in order ──
                if (_hwEncoderCandidates != null)
                {
                    int currentIndex = _resolvedEncoderName != null
                        ? _hwEncoderCandidates.IndexOf(_resolvedEncoderName)
                        : -1;

                    for (int i = currentIndex + 1; i < _hwEncoderCandidates.Count; i++)
                    {
                        string nextHwName = _hwEncoderCandidates[i];

                        // Tear down old HW state
                        TeardownHardware();

                        AVCodec* nextHwCodec = ffmpeg.avcodec_find_encoder_by_name(nextHwName);
                        if (nextHwCodec == null) continue;

                        _resolvedEncoderName = nextHwName;

                        // Recreate codec context for this encoder
                        fixed (AVCodecContext** pCodecCtx = &_codecCtx) { ffmpeg.avcodec_free_context(pCodecCtx); }
                        _codecCtx = ffmpeg.avcodec_alloc_context3(nextHwCodec);
                        if (_codecCtx == null) continue;

                        _codecCtx->codec_id = nextHwCodec->id;
                        _codecCtx->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
                        _codecCtx->width = Width;
                        _codecCtx->height = Height;
                        _codecCtx->time_base = new AVRational { num = 1, den = FramePerSecond };
                        _videoStream->time_base = _codecCtx->time_base;
                        _codecCtx->framerate = new AVRational { num = FramePerSecond, den = 1 };
                        _codecCtx->gop_size = Math.Max(FramePerSecond * 3, 30);
                        _codecCtx->max_b_frames = 2;
                        _codecCtx->bit_rate = _bitRate;
                        _codecCtx->rc_max_rate = _bitRate * 2;
                        _codecCtx->rc_buffer_size = _bitRate > int.MaxValue / 2 ? int.MaxValue / 2 : (int)(_bitRate * 2);

                        // Setup HW device for this encoder
                        SetupHardwareDevice(nextHwCodec);

                        // Select pixel format supported by this encoder
                        AVPixelFormat? hwPixFmt = SelectHardwarePixelFormat(nextHwCodec);
                        _pixelFormat = hwPixFmt ?? RestoreOriginalPixelFormat();
                        _codecCtx->pix_fmt = _pixelFormat;

                        if ((_fmtCtx->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                            _codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

                        // Configure HW encoder options
                        AVDictionary* hwOpts = null;
                        ConfigureHardwareEncoderOptions(nextHwCodec, &hwOpts);

                        ret = ffmpeg.avcodec_open2(_codecCtx, nextHwCodec, &hwOpts);
                        ffmpeg.av_dict_free(&hwOpts);

                        if (ret >= 0)
                        {
                            Log($"[VideoWriterHWAccel] Successfully opened hardware encoder '{nextHwName}' for codec '{CodecName}'.");
                            break;
                        }
                    }
                }

                // ── If all HW encoders failed, throw ──
                if (ret < 0)
                {
                    throw new InvalidOperationException($"All hardware encoders failed for '{CodecName}', try disable 'Use hardware accelerated encoding' option in Settings -> General. Last error: {FFmpegHelper.GetErrorString(ret) ?? $"error code {ret}"}(code: 0x{ret:x8}).")
                    {
                        HResult = ret
                    };
                }
            }
            else
            {
                ffmpeg.av_dict_free(&opts);
            }

            // Ensure _pixelFormat matches what the codec context actually uses.
            _pixelFormat = _codecCtx->pix_fmt;

            // ── 7. Copy codec parameters to stream ──
            FFmpegHelper.Throw(ffmpeg.avcodec_parameters_from_context(_videoStream->codecpar, _codecCtx),
                "avcodec_parameters_from_context");

            // ── 8. Open output file ──
            if ((_fmtCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                FFmpegHelper.Throw(ffmpeg.avio_open(&_fmtCtx->pb, OutputPath, ffmpeg.AVIO_FLAG_WRITE),
                    "Open target audio/video stream");
            }

            // ── 9. Allocate frames ──
            _frameDst = ffmpeg.av_frame_alloc();
            _frameDst->format = (int)_pixelFormat;
            _frameDst->width = Width;
            _frameDst->height = Height;
            FFmpegHelper.Throw(ffmpeg.av_frame_get_buffer(_frameDst, 32), "av_frame_get_buffer(dst)");

            // Source frame: always RGBA / RGBA64LE in system memory
            var srcPixFmt =
                (_pixelFormat == AVPixelFormat.AV_PIX_FMT_GBRP16LE ||
                 _pixelFormat == AVPixelFormat.AV_PIX_FMT_YUV420P16LE ||
                 _pixelFormat == AVPixelFormat.AV_PIX_FMT_RGBA64LE ||
                 _pixelFormat == AVPixelFormat.AV_PIX_FMT_BGRA64LE)
                ? AVPixelFormat.AV_PIX_FMT_RGBA64LE
                : AVPixelFormat.AV_PIX_FMT_RGBA;

            _colorDepth = (srcPixFmt == AVPixelFormat.AV_PIX_FMT_RGBA64LE) ? 16 : 8;

            _frameSrc = ffmpeg.av_frame_alloc();
            _frameSrc->format = (int)srcPixFmt;
            _frameSrc->width = Width;
            _frameSrc->height = Height;
            FFmpegHelper.Throw(ffmpeg.av_frame_get_buffer(_frameSrc, 32), "av_frame_get_buffer(src)");

            _sws = ffmpeg.sws_getContext(
                Width, Height, srcPixFmt,
                Width, Height, _pixelFormat,
                4, null, null, null);

            if (_sws == null) throw new InvalidOperationException("Couldn't get the SWS context.");

            _inited = true;
            Log($"[VideoWriterHWAccel] Initialized successfully for video '{OutputPath}' using hardware acceleration '{_resolvedEncoderName}'.");
        }

        // ────────────────────────── Append ──────────────────────────

        public void Append(IPicture<ushort> picture)
        {
            ArgumentNullException.ThrowIfNull(picture);
            if (picture.Width != _width || picture.Height != _height)
                throw new ArgumentException("The result size is different from original size. Please check the source.");
            if (_isDisposed) throw new ObjectDisposedException(nameof(VideoWriterHWAccel));

            EnsureHeader();

            FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(_frameSrc), "make frame writable");
            FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(_frameDst), "make frame writable");

            byte* srcData0 = _frameSrc->data[0];
            int srcLinesize = _frameSrc->linesize[0];

            int rLen = picture.r?.Length ?? 0;
            int gLen = picture.g?.Length ?? 0;
            int bLen = picture.b?.Length ?? 0;
            int aLen = picture.a?.Length ?? 0;
            bool hasAlpha = picture.HasAlphaChannel;

            fixed (ushort* pr = picture.r)
            fixed (ushort* pg = picture.g)
            fixed (ushort* pb = picture.b)
            fixed (float* pa = picture.a)
            {
                if (_colorDepth == 16)
                {
                    for (int y = 0; y < _height; y++)
                    {
                        ushort* row16 = (ushort*)(srcData0 + y * srcLinesize);
                        int baseIndex = y * _width;
                        for (int x = 0; x < _width; x++)
                        {
                            int k = baseIndex + x;
                            ushort r16 = (pr != null && k < rLen) ? pr[k] : (ushort)0;
                            ushort g16 = (pg != null && k < gLen) ? pg[k] : (ushort)0;
                            ushort b16 = (pb != null && k < bLen) ? pb[k] : (ushort)0;

                            ushort a16 = 65535;
                            if (hasAlpha && pa != null && k < aLen)
                            {
                                float af = pa[k];
                                if (float.IsNaN(af) || float.IsInfinity(af)) af = 1f;
                                if (af < 0f) af = 0f;
                                if (af > 1f) af = 1f;
                                a16 = (ushort)(af * 65535f + 0.5f);
                            }

                            int off = x * 4;
                            row16[off + 0] = r16;
                            row16[off + 1] = g16;
                            row16[off + 2] = b16;
                            row16[off + 3] = a16;
                        }
                    }
                }
                else
                {
                    for (int y = 0; y < _height; y++)
                    {
                        byte* row = srcData0 + y * srcLinesize;
                        int baseIndex = y * _width;
                        for (int x = 0; x < _width; x++)
                        {
                            int k = baseIndex + x;
                            ushort r16 = pr != null && k < rLen ? pr[k] : (ushort)0;
                            ushort g16 = pg != null && k < gLen ? pg[k] : (ushort)0;
                            ushort b16 = pb != null && k < bLen ? pb[k] : (ushort)0;
                            byte r8 = (byte)(r16 >> 8);
                            byte g8 = (byte)(g16 >> 8);
                            byte b8 = (byte)(b16 >> 8);
                            byte a8 = 255;
                            if (hasAlpha && pa != null && k < aLen)
                            {
                                float af = pa[k];
                                if (float.IsNaN(af) || float.IsInfinity(af)) af = 1f;
                                if (af < 0f) af = 0f;
                                if (af > 1f) af = 1f;
                                a8 = (byte)(af * 255f + 0.5f);
                            }
                            int off = x * 4;
                            row[off + 0] = r8;
                            row[off + 1] = g8;
                            row[off + 2] = b8;
                            row[off + 3] = a8;
                        }
                    }
                }
            }

            ffmpeg.sws_scale(
                _sws,
                _frameSrc->data,
                _frameSrc->linesize,
                0,
                Height,
                _frameDst->data,
                _frameDst->linesize);

            _frameDst->pts = _frameIndex++;

            EncodeFrame(_frameDst);

            Index++;
        }

        public void Append(IPicture<byte> picture)
        {
            if (picture == null) throw new ArgumentNullException(nameof(picture));
            if (picture.Width != Width || picture.Height != Height)
                throw new ArgumentException("The result size is different from original size. Please check the source.");
            if (_isDisposed) throw new ObjectDisposedException(nameof(VideoWriterHWAccel));

            EnsureHeader();

            FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(_frameSrc), "make frame writable");
            FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(_frameDst), "make frame writable");

            byte* srcData0 = _frameSrc->data[0];
            int srcLinesize = _frameSrc->linesize[0];

            int rLen = picture.r?.Length ?? 0;
            int gLen = picture.g?.Length ?? 0;
            int bLen = picture.b?.Length ?? 0;
            int aLen = picture.a?.Length ?? 0;
            bool hasAlpha = picture.HasAlphaChannel;

            fixed (byte* pr = picture.r)
            fixed (byte* pg = picture.g)
            fixed (byte* pb = picture.b)
            fixed (float* pa = picture.a)
            {
                if (_colorDepth == 16)
                {
                    for (int y = 0; y < _height; y++)
                    {
                        ushort* row16 = (ushort*)(srcData0 + y * srcLinesize);
                        int baseIndex = y * _width;
                        for (int x = 0; x < _width; x++)
                        {
                            int k = baseIndex + x;
                            byte r8 = (pr != null && k < rLen) ? pr[k] : (byte)0;
                            byte g8 = (pg != null && k < gLen) ? pg[k] : (byte)0;
                            byte b8 = (pb != null && k < bLen) ? pb[k] : (byte)0;

                            ushort r16 = (ushort)(r8 * 257);
                            ushort g16 = (ushort)(g8 * 257);
                            ushort b16 = (ushort)(b8 * 257);

                            ushort a16 = 65535;
                            if (hasAlpha && pa != null && k < aLen)
                            {
                                float af = pa[k];
                                if (float.IsNaN(af) || float.IsInfinity(af)) af = 1f;
                                if (af < 0f) af = 0f;
                                if (af > 1f) af = 1f;
                                a16 = (ushort)(af * 65535f + 0.5f);
                            }

                            int off = x * 4;
                            row16[off + 0] = r16;
                            row16[off + 1] = g16;
                            row16[off + 2] = b16;
                            row16[off + 3] = a16;
                        }
                    }
                }
                else
                {
                    for (int y = 0; y < _height; y++)
                    {
                        byte* row = srcData0 + y * srcLinesize;
                        int baseIndex = y * _width;
                        for (int x = 0; x < _width; x++)
                        {
                            int k = baseIndex + x;
                            byte r8 = pr != null && k < rLen ? pr[k] : (byte)0;
                            byte g8 = pg != null && k < gLen ? pg[k] : (byte)0;
                            byte b8 = pb != null && k < bLen ? pb[k] : (byte)0;
                            byte a8 = 255;
                            if (hasAlpha && pa != null && k < aLen)
                            {
                                float af = pa[k];
                                if (float.IsNaN(af) || float.IsInfinity(af)) af = 1f;
                                if (af < 0f) af = 0f;
                                if (af > 1f) af = 1f;
                                a8 = (byte)(af * 255f + 0.5f);
                            }
                            int off = x * 4;
                            row[off + 0] = r8;
                            row[off + 1] = g8;
                            row[off + 2] = b8;
                            row[off + 3] = a8;
                        }
                    }
                }
            }

            ffmpeg.sws_scale(
                _sws,
                _frameSrc->data,
                _frameSrc->linesize,
                0,
                Height,
                _frameDst->data,
                _frameDst->linesize);

            _frameDst->pts = _frameIndex++;

            EncodeFrame(_frameDst);

            Index++;
        }

        public void Append(Picture16bpp pic) => Append((IPicture<ushort>)pic);
        public void Append(Picture8bpp pic) => Append((IPicture<byte>)pic);

        public void Append(IPicture source)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source.BitPerPixel == IPicture.PicturePixelMode.UShortPicture) Append((IPicture<ushort>)source);
            else if (source.BitPerPixel == IPicture.PicturePixelMode.BytePicture) Append((IPicture<byte>)source);
            else throw new NotSupportedException("Unsupported pixel mode.");
        }

        // ────────────────────────── Header / Encode / Finish / Dispose ──────────────────────────

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
                    ffmpeg.av_dict_set(&_fmtCtx->metadata, kv.Key, value, 0);
                    if (_videoStream != null)
                        ffmpeg.av_dict_set(&_videoStream->metadata, kv.Key, value, 0);
                }
            }

            FFmpegHelper.Throw(ffmpeg.avformat_write_header(_fmtCtx, null), "avformat_write_header");
            _isHeaderWritten = true;
        }

        private void EncodeFrame(AVFrame* frame)
        {
            FFmpegHelper.Throw(ffmpeg.avcodec_send_frame(_codecCtx, frame), "avcodec_send_frame");

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
            while (true)
            {
                AVPacket* pkt = ffmpeg.av_packet_alloc();
                int ret = ffmpeg.avcodec_receive_packet(_codecCtx, pkt);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                {
                    ffmpeg.av_packet_free(&pkt);
                    break;
                }
                FFmpegHelper.Throw(ret, "avcodec_receive_packet(flush)");
                ffmpeg.av_packet_rescale_ts(pkt, _codecCtx->time_base, _videoStream->time_base);
                pkt->stream_index = _videoStream->index;
                FFmpegHelper.Throw(ffmpeg.av_interleaved_write_frame(_fmtCtx, pkt), "write_frame(flush)");
                ffmpeg.av_packet_free(&pkt);
            }

            if (_isHeaderWritten)
            {
                FFmpegHelper.Throw(ffmpeg.av_write_trailer(_fmtCtx), "av_write_trailer");
            }
        }

        private void ReleaseUnmanaged()
        {
            if (_frameSrc != null)
            {
                fixed (AVFrame** p = &_frameSrc) { ffmpeg.av_frame_free(p); }
                _frameSrc = null;
            }
            if (_frameDst != null)
            {
                fixed (AVFrame** p = &_frameDst) { ffmpeg.av_frame_free(p); }
                _frameDst = null;
            }
            if (_codecCtx != null)
            {
                // hw_device_ctx is attached to codecctx; clearing our ref before freeing
                if (_codecCtx->hw_device_ctx != null)
                {
                    ffmpeg.av_buffer_unref(&_codecCtx->hw_device_ctx);
                }
                fixed (AVCodecContext** p = &_codecCtx) { ffmpeg.avcodec_free_context(p); }
                _codecCtx = null;
            }
            if (_sws != null)
            {
                ffmpeg.sws_freeContext(_sws);
                _sws = null;
            }
            if (_hwDeviceCtx != null)
            {
                fixed (AVBufferRef** p = &_hwDeviceCtx) { ffmpeg.av_buffer_unref(p); }
                _hwDeviceCtx = null;
            }
            if (_fmtCtx != null)
            {
                if (_fmtCtx->pb != null) { ffmpeg.avio_closep(&_fmtCtx->pb); }
                fixed (AVFormatContext** p = &_fmtCtx) { ffmpeg.avformat_free_context(*p); }
                _fmtCtx = null;
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            try { Finish(); } catch { }
            ReleaseUnmanaged();
            _isDisposed = true;
            GC.SuppressFinalize(this);
        }

        ~VideoWriterHWAccel()
        {
            try { ReleaseUnmanaged(); } catch { }
        }

        // ────────────────────────── HW acceleration internals ──────────────────────────

        /// <summary>
        /// Detect the best available hardware device type.
        /// Uses <see cref="OperatingSystem"/> to short-circuit platform-native
        /// device types, avoiding full enumeration where the platform is known.
        /// </summary>
        private static AVHWDeviceType GetBestHWDeviceType()
        {
            // ── Apple platforms: only VideoToolbox makes sense ──
            if (OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS() || OperatingSystem.IsTvOS() || OperatingSystem.IsWatchOS())
            {
                var check = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
                while ((check = ffmpeg.av_hwdevice_iterate_types(check)) != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
                {
                    if (check == AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX)
                        return AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX;
                }
                return AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
            }

            // ── Android: only MediaCodec is relevant ──
            if (OperatingSystem.IsAndroid())
            {
                var check = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
                while ((check = ffmpeg.av_hwdevice_iterate_types(check)) != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
                {
                    if (check == AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC)
                        return AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC;
                }
                return AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
            }

            // ── Windows: D3D11VA > DXVA2 > CUDA > QSV > Vulkan ──
            if (OperatingSystem.IsWindows())
            {
                var winTypes = new List<AVHWDeviceType>();
                var iter = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
                while ((iter = ffmpeg.av_hwdevice_iterate_types(iter)) != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
                    winTypes.Add(iter);

                if (winTypes.Contains(AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA)) return AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA;
                if (winTypes.Contains(AVHWDeviceType.AV_HWDEVICE_TYPE_QSV)) return AVHWDeviceType.AV_HWDEVICE_TYPE_QSV;
                if (winTypes.Contains(AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2)) return AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2;
                if (winTypes.Contains(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA)) return AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA;
                if (winTypes.Contains(AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN)) return AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN;
                return winTypes.Count > 0 ? winTypes[0] : AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
            }

            // ── Linux: VAAPI > CUDA > QSV > Vulkan > DRM ──
            if (OperatingSystem.IsLinux())
            {
                var linuxTypes = new List<AVHWDeviceType>();
                var iter = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
                while ((iter = ffmpeg.av_hwdevice_iterate_types(iter)) != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
                    linuxTypes.Add(iter);

                if (linuxTypes.Contains(AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA)) return AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA;
                if (linuxTypes.Contains(AVHWDeviceType.AV_HWDEVICE_TYPE_QSV)) return AVHWDeviceType.AV_HWDEVICE_TYPE_QSV;
                if (linuxTypes.Contains(AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI)) return AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI;
                if (linuxTypes.Contains(AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN)) return AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN;
                if (linuxTypes.Contains(AVHWDeviceType.AV_HWDEVICE_TYPE_DRM)) return AVHWDeviceType.AV_HWDEVICE_TYPE_DRM;
                return linuxTypes.Count > 0 ? linuxTypes[0] : AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
            }

            // ── Unknown platform: full enumeration fallback ──
            var fallback = new List<AVHWDeviceType>();
            var fallbackIter = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
            while ((fallbackIter = ffmpeg.av_hwdevice_iterate_types(fallbackIter)) != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
                fallback.Add(fallbackIter);

            if (fallback.Contains(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA)) return AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA;
            if (fallback.Contains(AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2)) return AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2;
            if (fallback.Contains(AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA)) return AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA;
            if (fallback.Contains(AVHWDeviceType.AV_HWDEVICE_TYPE_QSV)) return AVHWDeviceType.AV_HWDEVICE_TYPE_QSV;
            if (fallback.Contains(AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI)) return AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI;
            if (fallback.Contains(AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX)) return AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX;
            if (fallback.Contains(AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC)) return AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC;

            return fallback.Count > 0 ? fallback[0] : AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
        }

        /// <summary>
        /// Get all available hardware encoder names for the given codec, ordered by platform-aware preference.
        /// Uses <see cref="OperatingSystem"/> to place the platform-native encoder first,
        /// avoiding unnecessary probing of irrelevant back-ends.
        /// </summary>
        private static List<string> GetAllHardwareEncoderNames(string codecName)
        {
            string baseName = codecName.Trim().ToLowerInvariant();

            // Normalise common codec names
            string codecRoot = baseName switch
            {
                "h264" or "avc" or "libx264" => "h264",
                "hevc" or "h265" or "libx265" => "hevc",
                "av1" or "libaom-av1" => "av1",
                "mpeg4" => "mpeg4",
                "vp9" or "libvpx-vp9" => "vp9",
                _ => baseName
            };

            // Build platform-aware suffix order so the native encoder is tried first.
            var suffixes = new List<string>(7);

            if (OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS() || OperatingSystem.IsTvOS() || OperatingSystem.IsWatchOS())
            {
                // iOS-family: only VideoToolbox is relevant
                suffixes.Add("_videotoolbox");
            }
            else if (OperatingSystem.IsAndroid())
            {
                // Android: MediaCodec first, anything else unlikely
                suffixes.Add("_mediacodec");
                suffixes.Add("_videotoolbox");
            }
            else if (OperatingSystem.IsWindows())
            {
                // Windows: NVENC > AMF > QSV > VAAPI > VideoToolbox > MediaCodec > MediaFoundation
                suffixes.Add("_nvenc");
                suffixes.Add("_amf");
                suffixes.Add("_qsv");
                suffixes.Add("_vaapi");
                suffixes.Add("_videotoolbox");
                suffixes.Add("_mediacodec");
                suffixes.Add("_mf");
            }
            else
            {
                // Linux / unknown: VAAPI > NVENC > QSV > AMF > VideoToolbox > MediaCodec
                suffixes.Add("_vaapi");
                suffixes.Add("_nvenc");
                suffixes.Add("_qsv");
                suffixes.Add("_amf");
                suffixes.Add("_videotoolbox");
                suffixes.Add("_mediacodec");
                suffixes.Add("_mf");
            }

            var results = new List<string>(suffixes.Count);

            foreach (string suffix in suffixes)
            {
                string candidate = codecRoot + suffix;
                AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(candidate);
                if (codec != null)
                    results.Add(candidate);
            }

            // Try directly probing if it's already a HW encoder name
            if (codecRoot.Contains('_'))
            {
                AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(codecRoot);
                if (codec != null && !results.Contains(codecRoot))
                    results.Add(codecRoot);
            }

            return results;
        }

        /// <summary>
        /// Get the best available hardware encoder name for the given codec, or null.
        /// </summary>
        private static string? GetHardwareEncoderName(string codecName)
        {
            var all = GetAllHardwareEncoderNames(codecName);
            return all.Count > 0 ? all[0] : null;
        }

        /// <summary>
        /// Resolve which hardware encoder to use.
        /// </summary>
        private AVCodec* ResolveEncoder(string codecName)
        {
            if (string.IsNullOrWhiteSpace(codecName))
                return null;

            // Try hardware encoder
            if (_resolvedEncoderName == null)
            {
                _hwEncoderCandidates = GetAllHardwareEncoderNames(codecName);
                if (_hwEncoderCandidates.Count > 0)
                {
                    string hwName = _hwEncoderCandidates[0];
                    AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(hwName);
                    if (codec != null)
                    {
                        _resolvedEncoderName = hwName;
                        return codec;
                    }
                }
            }

            // No hardware encoder found
            return null;
        }

        /// <summary>
        /// Set up the hardware device context on the codec context.
        /// </summary>
        private void SetupHardwareDevice(AVCodec* codec)
        {
            // Try encoder-specific device type first, then fall back to best available
            _hwDeviceType = GetDeviceTypeForEncoder(_resolvedEncoderName ?? string.Empty);
            if (_hwDeviceType == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
                _hwDeviceType = GetBestHWDeviceType();
            if (_hwDeviceType == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
                return;

            AVBufferRef* hwDeviceCtx = null;
            int ret = ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, _hwDeviceType, null, null, 0);

            // Some encoders (notably MediaCodec) can operate without an explicit
            // hw_device_ctx — the encoder manages its own device session internally.
            bool requiresDeviceCtx = _hwDeviceType != AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC;

            if (ret < 0 || hwDeviceCtx == null)
            {
                if (requiresDeviceCtx)
                    return; // avcodec_open2 will fail and the HW fallback chain will try the next encoder

                // MediaCodec may still work without an explicit device context
            }

            if (hwDeviceCtx != null)
                _hwDeviceCtx = hwDeviceCtx;
            // NOTE: We intentionally do NOT set _codecCtx->hw_device_ctx here.
            // Most HW encoders (NVENC, AMF, QSV on Windows) accept system-memory
            // frames (e.g. NV12 allocated via av_frame_get_buffer) and handle GPU
            // upload internally. Setting hw_device_ctx would make them expect
            // GPU-side hardware frames (e.g. AV_PIX_FMT_CUDA), which we don't
            // create. Our Append path converts RGBA → NV12 in system memory via
            // sws_scale and sends system-memory frames to the encoder.
        }

        /// <summary>
        /// Select the best pixel format supported by the HW encoder.
        /// Falls back to the original pixel format if no suitable format is found.
        /// </summary>
        private static AVPixelFormat? SelectHardwarePixelFormat(AVCodec* codec)
        {
            if (codec == null) return null;

            // Use the new FFmpeg 6.x+ API to query supported pixel formats.
            AVPixelFormat* configs;
            int nbConfigs;
            int ret = ffmpeg.avcodec_get_supported_config(
                null, codec,
                AVCodecConfig.AV_CODEC_CONFIG_PIX_FORMAT,
                0,
                (void**)&configs, &nbConfigs);

            if (ret < 0 || configs == null || nbConfigs <= 0)
                return null;

            // Filter out hardware-only pixel formats (CUDA, D3D11, VAAPI, etc.)
            // which cannot be allocated via av_frame_get_buffer or used with sws_scale.
            var supported = new List<AVPixelFormat>();
            for (int i = 0; i < nbConfigs; i++)
            {
                var desc = ffmpeg.av_pix_fmt_desc_get(configs[i]);
                if (desc != null && (desc->flags & ffmpeg.AV_PIX_FMT_FLAG_HWACCEL) != 0)
                    continue;
                supported.Add(configs[i]);
            }

            if (supported.Count == 0) return null;

            // Preferred order: NV12 > YUV420P > P010 (10-bit) > other YUV
            AVPixelFormat[] preferred =
            [
                AVPixelFormat.AV_PIX_FMT_NV12,
                AVPixelFormat.AV_PIX_FMT_YUV420P,
                AVPixelFormat.AV_PIX_FMT_P010LE,
                AVPixelFormat.AV_PIX_FMT_YUV420P10LE,
                AVPixelFormat.AV_PIX_FMT_YUV422P,
                AVPixelFormat.AV_PIX_FMT_YUV444P,
            ];

            foreach (var fmt in preferred)
            {
                if (supported.Contains(fmt))
                    return fmt;
            }

            // Return null (use original pixel format) if no suitable format found.
            return null;
        }

        /// <summary>
        /// Configure encoder options specific to the hardware encoder.
        /// </summary>
        private static void ConfigureHardwareEncoderOptions(AVCodec* codec, AVDictionary** opts)
        {
            if (codec == null || codec->name == null) return;

            string name = Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? string.Empty;

            if (name.Contains("nvenc"))
            {
                ffmpeg.av_dict_set(opts, "preset", "p5", 0);       // p5 = 质量/速度最佳平衡 (p1最快-p7最慢)
                ffmpeg.av_dict_set(opts, "tune", "hq", 0);          // hq = 高质量 (非低延迟模式)
                ffmpeg.av_dict_set(opts, "rc", "vbr_hq", 0);        // 高质量 VBR 速率控制
                ffmpeg.av_dict_set(opts, "rc_lookahead", "32", 0);  // 32帧预分析,平滑码率分配
                ffmpeg.av_dict_set(opts, "b_ref_mode", "middle", 0);// 中档B帧参考模式
            }
            else if (name.Contains("amf"))
            {
                ffmpeg.av_dict_set(opts, "quality", "quality", 0);     // 质量模式 (非 speed)
                ffmpeg.av_dict_set(opts, "usage", "transcoding", 0);   // 转码模式 (平衡延迟和质量)
                ffmpeg.av_dict_set(opts, "preanalysis", "1", 0);       // 启用预分析
            }
            else if (name.Contains("qsv"))
            {
                ffmpeg.av_dict_set(opts, "preset", "medium", 0);    // 平衡预设 (veryfast>fast>medium>slow)
                ffmpeg.av_dict_set(opts, "async_depth", "6", 0);    // 适度异步深度
            }
            else if (name.Contains("vaapi"))
            {
                ffmpeg.av_dict_set(opts, "compression_level", "4", 0); // 平衡 (1最快-7最慢)
            }
            else if (name.Contains("videotoolbox"))
            {
                ffmpeg.av_dict_set(opts, "realtime", "1", 0);
            }
            else if (name.Contains("mediacodec"))
            {
                ffmpeg.av_dict_set(opts, "bitrate_mode", "cq", 0);  // constant quality
                ffmpeg.av_dict_set(opts, "quality", "8", 0);        // high quality (1–10)
                ffmpeg.av_dict_set(opts, "priority", "realtime", 0);
            }
        }

        /// <summary>
        /// Map an encoder name to its preferred hardware device type.
        /// </summary>
        private static AVHWDeviceType GetDeviceTypeForEncoder(string encoderName)
        {
            if (encoderName.Contains("amf")) return AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA;
            if (encoderName.Contains("nvenc")) return AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA;
            if (encoderName.Contains("qsv")) return AVHWDeviceType.AV_HWDEVICE_TYPE_QSV;
            if (encoderName.Contains("vaapi")) return AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI;
            if (encoderName.Contains("videotoolbox")) return AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX;
            if (encoderName.Contains("mediacodec")) return AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC;
            if (encoderName.Contains("mf")) return AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA;
            return AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
        }

        /// <summary>
        /// Tear down HW device state before trying a different encoder.
        /// </summary>
        private void TeardownHardware()
        {
            if (_codecCtx != null && _codecCtx->hw_device_ctx != null)
            {
                ffmpeg.av_buffer_unref(&_codecCtx->hw_device_ctx);
            }
            if (_hwDeviceCtx != null)
            {
                fixed (AVBufferRef** p = &_hwDeviceCtx) { ffmpeg.av_buffer_unref(p); }
                _hwDeviceCtx = null;
            }
            _hwDeviceType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
        }

        /// <summary>
        /// Restore the original user-requested pixel format after a HW encoder fallback.
        /// </summary>
        private AVPixelFormat RestoreOriginalPixelFormat()
        {
            if (!string.IsNullOrEmpty(PixelFormat) &&
                Enum.TryParse(PixelFormat, out AVPixelFormat original) &&
                original != AVPixelFormat.AV_PIX_FMT_NONE)
            {
                return original;
            }
            return AVPixelFormat.AV_PIX_FMT_YUV420P;
        }

        // ────────────────────────── Helpers ──────────────────────────

        private void WrapFormatAllocError(int ret)
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
    }
}
