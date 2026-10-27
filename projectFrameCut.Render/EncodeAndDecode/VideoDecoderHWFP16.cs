using FFmpeg.AutoGen;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using static projectFrameCut.Render.EncodeAndDecode.FFmpegHelper;

namespace projectFrameCut.Render.EncodeAndDecode
{
    public sealed unsafe class DecoderContextHWFP16 : IVideoSource<Half>, IHDRVideoSource
    {
        private readonly string _path;
        private readonly FFmpegStreamIOContext? _streamIO;
        private AVFormatContext* _fmt = null;
        private AVCodecContext* _codec = null;
        private AVBufferRef* _hwDeviceCtx = null;
        private long _totalFrames;
        private SwsContext* _sws = null;
        private AVPacket* _pkt = null;
        private AVFrame* _frm = null;
        private AVFrame* _swFrame = null;
        private AVFrame* _rgb = null;
        private bool _eof = false;

        private int _videoStreamIndex = -1;
        private int _width = -1;
        private int _height = -1;
        private double _fps = 0.0;
        private int _currentFrameNumber = 0;
        private uint? _lastDecodedFrame;
        private bool flushSent = false;
        private bool _packetPending = false;
        private bool _needsTimestampCalibration = false;
        private bool _hasTransferredFrame = false;
        private AVHWDeviceType _hwDeviceType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
        private AVPixelFormat _hwPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        private AVCodecContext_get_format? _getHardwareFormat;

        public bool Disposed { get; private set; }
        public bool Initialized { get; private set; } = false;

        public long TotalFrames => _totalFrames;

        public double Fps => _fps;

        public int Width => _width;

        public int Height => _height;

        public uint Index { get; set; } = 0;
        public string[] PreferredExtension => [".mp4", ".mov"];
        public string TypeName => "DecoderContextHWFP16";
        public bool IsDeterministic => true;
        public bool HasHdrContent => _codec != null && FFmpegFP16FrameCodec.IsHdr(_codec->color_trc);

        public int? ResultBitPerPixel => IPicture.PicturePixelMode.Float16Picture;

        public bool EnableLock { get; set; } = true;
        public bool StrictMode { get; set; }


        private Lock locker = new();

        public DecoderContextHWFP16()
        {
            _path = null!;
        }

        public DecoderContextHWFP16(string path)
        {
            _path = path;
            Initialize();
        }

        public DecoderContextHWFP16(Stream source, long length, bool leaveOpen = false)
        {
            _path = "<stream>";
            _streamIO = new FFmpegStreamIOContext(source, length, leaveOpen);
            Initialize();
        }

        public IVideoSource CreateNew(string newSource) => new DecoderContextHWFP16(newSource);
        public IVideoSource FromStream(Stream source, long length, bool leaveOpen = false) => new DecoderContextHWFP16(source, length, leaveOpen);

        private int CheckIO(int result) => _streamIO?.Check(result) ?? result;

        public void Initialize()
        {
            if ((_path is null && _streamIO is null) || Initialized) return;

            try
            {
                _fmt = ffmpeg.avformat_alloc_context();
                if (_fmt == null) throw new InvalidOperationException("Failed to alloc a context for the Renderer.");

                fixed (AVFormatContext** fmtPtr = &_fmt)
                {
                    var averr = _streamIO is null
                        ? ffmpeg.avformat_open_input(fmtPtr, _path, null, null)
                        : _streamIO.Open(fmtPtr);
                    if (averr != 0)
                    {
                        if (_streamIO is null) DetectWhyCannotOpenVideo(_path, averr);
                        throw new InvalidDataException($"Failed to open video stream (code {averr}, {FFmpegHelper.GetErrorString(averr) ?? "unknown"}).");
                    }
                }

                if (CheckIO(ffmpeg.avformat_find_stream_info(_fmt, null)) != 0)
                    throw new InvalidDataException($"File '{_path}' seems don't like a multimedia file. Try install the encoder extension. If you continuously encountering this issue, try install ffmpeg toolkit on your computer, then run this command and observe whether there is any error message:\r\nffprobe {Path.GetFullPath(_path)}");

                for (int i = 0; i < _fmt->nb_streams; i++)
                {
                    if (_fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        _videoStreamIndex = i;
                        break;
                    }
                }

                if (_videoStreamIndex < 0)
                    throw new InvalidDataException($"File '{_path}' seems don't like a video file. Try install the encoder extension. If you continuously encountering this issue, try encode your video again to another format.");

                _pkt = ffmpeg.av_packet_alloc();
                _frm = ffmpeg.av_frame_alloc();
                _swFrame = ffmpeg.av_frame_alloc();

                if (_pkt == null || _frm == null || _swFrame == null)
                    throw new OutOfMemoryException($"Failed to allocate enough memory space.");

                InitializeDecoder(_fmt->streams[_videoStreamIndex]->codecpar);

                _width = _codec->width;
                _height = _codec->height;

                AVRational fr = _codec->framerate;
                if (fr.num == 0 || fr.den == 0)
                    fr = _fmt->streams[_videoStreamIndex]->avg_frame_rate;
                if (fr.num == 0 || fr.den == 0)
                    fr = _fmt->streams[_videoStreamIndex]->r_frame_rate;

                _fps = fr.den != 0 ? ffmpeg.av_q2d(fr) : 0.0;

                long nbFrames = (long)_fmt->streams[_videoStreamIndex]->nb_frames;
                if (nbFrames <= 0)
                {
                    long duration = _fmt->streams[_videoStreamIndex]->duration;
                    AVRational tb = _fmt->streams[_videoStreamIndex]->time_base;
                    if (duration > 0 && tb.num > 0 && tb.den > 0 && _fps > 0)
                    {
                        double seconds = duration * ffmpeg.av_q2d(tb);
                        nbFrames = (long)Math.Round(seconds * _fps);
                        if (nbFrames < 0) nbFrames = -1;
                    }
                    else
                    {
                        nbFrames = -1;
                    }
                }
                _totalFrames = nbFrames > 0 ? nbFrames : -1;

                _rgb = FFmpegFP16FrameCodec.AllocateFrame(_width, _height, FFmpegFP16FrameCodec.DecoderFormat);

                Initialized = true;
                Log($"[{TypeName}] Initialized FP16 decoder for {_path}, decoder={Marshal.PtrToStringAnsi((nint)_codec->codec->name)}, hardware={_hwDeviceType}.");
            }
            catch (Exception)
            {
                Dispose();
                throw;
            }
        }

        private void EnsureDecoderReady(uint targetFrame)
        {
            if (Disposed)
                throw new ObjectDisposedException(nameof(DecoderContextHWFP16), $"Decoder for '{_path}' is already disposed when trying to read frame {targetFrame}.");
            if (!Initialized)
                throw new InvalidOperationException($"Decoder for '{_path}' is not initialized when trying to read frame {targetFrame}.");
            if (_videoStreamIndex < 0 || _width <= 0 || _height <= 0)
                throw new InvalidDataException($"Decoder metadata is invalid for '{_path}' when trying to read frame {targetFrame}.");

            if (IsPointerAddressesNotValid(_fmt) ||
                IsPointerAddressesNotValid(_codec) ||
                IsPointerAddressesNotValid(_pkt) ||
                IsPointerAddressesNotValid(_frm) ||
                IsPointerAddressesNotValid(_swFrame) ||
                IsPointerAddressesNotValid(_rgb))
            {
                throw new InvalidDataException($"Decoder native state is invalid for '{_path}' when trying to read frame {targetFrame}.");
            }

            if (_rgb->data[0] == null || _rgb->linesize[0] <= 0)
                throw new InvalidDataException($"Decoder RGB buffer is invalid for '{_path}' when trying to read frame {targetFrame}.");
        }

        private void InitializeDecoder(AVCodecParameters* parameters)
        {
            AVCodec* fallback = ffmpeg.avcodec_find_decoder(parameters->codec_id);
            if (fallback == null) throw new NotSupportedException("No suitable decoder found.");

            string? failure = null;
            if (TryInitializeHardware(fallback, parameters, ref failure)) return;

            void* opaque = null;
            AVCodec* codec;
            while ((codec = ffmpeg.av_codec_iterate(&opaque)) != null)
            {
                if (codec == fallback || codec->id != parameters->codec_id || ffmpeg.av_codec_is_decoder(codec) == 0) continue;
                if (TryInitializeHardware(codec, parameters, ref failure)) return;
            }

            _codec = ffmpeg.avcodec_alloc_context3(fallback);
            if (_codec == null) throw new OutOfMemoryException("Failed to allocate the software decoder context.");
            FFmpegHelper.Throw(ffmpeg.avcodec_parameters_to_context(_codec, parameters), "copy software decoder parameters");
            FFmpegHelper.Throw(ffmpeg.avcodec_open2(_codec, fallback, null), "open software video decoder");
            Log($"[{TypeName}] No usable hardware decoder for '{_path}'; using {Marshal.PtrToStringAnsi((nint)fallback->name)}. {failure ?? "No matching hardware configuration."}", "warn");
        }

        private bool TryInitializeHardware(AVCodec* codec, AVCodecParameters* parameters, ref string? failure)
        {
            const int hardwareDeviceContext = 1; // AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX
            List<AVHWDeviceType> preferred = [AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2,
                AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI, AVHWDeviceType.AV_HWDEVICE_TYPE_QSV,
                AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX, AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC];
            var available = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
            while ((available = ffmpeg.av_hwdevice_iterate_types(available)) != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
                if (!preferred.Contains(available)) preferred.Add(available);
            foreach (var type in preferred)
            {
                for (int i = 0; ; i++)
                {
                    AVCodecHWConfig* config = ffmpeg.avcodec_get_hw_config(codec, i);
                    if (config == null) break;
                    if (config->device_type != type || (config->methods & hardwareDeviceContext) == 0) continue;
                    AVBufferRef* device = null;
                    int result = ffmpeg.av_hwdevice_ctx_create(&device, type, null, null, 0);
                    if (result < 0 || device == null)
                    {
                        if (device != null) ffmpeg.av_buffer_unref(&device);
                        failure = $"{Marshal.PtrToStringAnsi((nint)codec->name)}/{type}: {FFmpegHelper.GetErrorString(result)}.";
                        break;
                    }
                    _hwDeviceCtx = device;
                    _codec = ffmpeg.avcodec_alloc_context3(codec);
                    if (_codec == null) throw new OutOfMemoryException("Failed to allocate the hardware decoder context.");
                    FFmpegHelper.Throw(ffmpeg.avcodec_parameters_to_context(_codec, parameters), "copy hardware decoder parameters");
                    _codec->hw_device_ctx = ffmpeg.av_buffer_ref(device);
                    if (_codec->hw_device_ctx == null) throw new OutOfMemoryException("Failed to retain the video decoder device.");
                    _hwDeviceType = type;
                    _hwPixelFormat = config->pix_fmt;
                    _codec->get_format = _getHardwareFormat ??= GetHardwareFormat;
                    result = ffmpeg.avcodec_open2(_codec, codec, null);
                    bool probed = result >= 0;
                    if (probed) result = ProbeHardwareDecoder();
                    if (result >= 0)
                    {
                        _lastDecodedFrame = 0;
                        _currentFrameNumber = 1;
                        return true;
                    }

                    failure = $"{Marshal.PtrToStringAnsi((nint)codec->name)}/{type}: {FFmpegHelper.GetErrorString(result)}.";
                    ReleaseDecoder();
                    if (probed)
                    {
                        var stream = _fmt->streams[_videoStreamIndex];
                        FFmpegHelper.Throw(CheckIO(ffmpeg.av_seek_frame(_fmt, _videoStreamIndex,
                            stream->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : stream->start_time, ffmpeg.AVSEEK_FLAG_BACKWARD)), "rewind input for decoder fallback");
                    }
                }
            }
            return false;
        }

        private AVPixelFormat GetHardwareFormat(AVCodecContext* context, AVPixelFormat* formats)
        {
            for (var p = formats; p != null && *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
                if (*p == _hwPixelFormat) return *p;
            return AVPixelFormat.AV_PIX_FMT_NONE;
        }

        private int ProbeHardwareDecoder()
        {
            // Device creation may succeed even when the stream's profile is unsupported.
            while (true)
            {
                ffmpeg.av_frame_unref(_frm);
                _hasTransferredFrame = false;
                int result = ffmpeg.avcodec_receive_frame(_codec, _frm);
                if (result == 0)
                {
                    if (_frm->format != (int)_hwPixelFormat) return ffmpeg.AVERROR_INVALIDDATA;
                    FFmpegFP16FrameCodec.ApplyDecoderMetadata(_frm, _codec);
                    ffmpeg.av_frame_unref(_swFrame);
                    result = ffmpeg.av_hwframe_transfer_data(_swFrame, _frm, 0);
                    if (result < 0) return result;
                    result = ffmpeg.av_frame_copy_props(_swFrame, _frm);
                    if (result >= 0) _hasTransferredFrame = true;
                    return result;
                }
                if (result != ffmpeg.AVERROR(ffmpeg.EAGAIN)) return result;

                while (!_eof && !_packetPending)
                {
                    result = CheckIO(ffmpeg.av_read_frame(_fmt, _pkt));
                    if (result == ffmpeg.AVERROR_EOF)
                    {
                        _eof = true;
                        ffmpeg.av_packet_unref(_pkt);
                    }
                    else if (result < 0) return result;
                    else if (_pkt->stream_index == _videoStreamIndex) _packetPending = true;
                    else ffmpeg.av_packet_unref(_pkt);
                }

                if (_packetPending)
                {
                    result = ffmpeg.avcodec_send_packet(_codec, _pkt);
                    if (result >= 0)
                    {
                        ffmpeg.av_packet_unref(_pkt);
                        _packetPending = false;
                    }
                }
                else if (!flushSent)
                {
                    result = ffmpeg.avcodec_send_packet(_codec, null);
                    if (result >= 0) flushSent = true;
                }
                else return ffmpeg.AVERROR_EOF;

                if (result < 0 && result != ffmpeg.AVERROR(ffmpeg.EAGAIN)) return result;
            }
        }

        private void ReleaseDecoder()
        {
            if (_pkt != null) ffmpeg.av_packet_unref(_pkt);
            if (_frm != null) ffmpeg.av_frame_unref(_frm);
            if (_swFrame != null) ffmpeg.av_frame_unref(_swFrame);
            if (_codec != null) { AVCodecContext* codec = _codec; _codec = null; ffmpeg.avcodec_free_context(&codec); }
            GC.KeepAlive(_getHardwareFormat);
            if (_hwDeviceCtx != null) { AVBufferRef* device = _hwDeviceCtx; _hwDeviceCtx = null; ffmpeg.av_buffer_unref(&device); }
            _hwDeviceType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
            _hwPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
            _lastDecodedFrame = null;
            _currentFrameNumber = 0;
            _packetPending = _eof = flushSent = _needsTimestampCalibration = _hasTransferredFrame = false;
        }

        [DebuggerNonUserCode()]
        public IPicture<Half> GetFrame(uint targetFrame)
            => GetFrameCore(targetFrame, null);

        public IPicture<Half> GetFrame(uint targetFrame, int sourceX, int sourceY, int sourceWidth, int sourceHeight,
            int targetWidth, int targetHeight)
            => GetFrameCore(targetFrame,
                new VideoFrameRegion(sourceX, sourceY, sourceWidth, sourceHeight, targetWidth, targetHeight));

        public IPicture<Half> GetFrame(uint targetFrame, int targetWidth, int targetHeight)
            => GetFrame(targetFrame, 0, 0, _width, _height, targetWidth, targetHeight);

        public HDRPictureFP16 GetHDRFrame(uint targetFrame, bool hasAlpha = false)
            => (HDRPictureFP16)GetFrameCore(targetFrame, null, true, hasAlpha);

        public HDRPictureFP16 GetHDRFrame(uint targetFrame, int sourceX, int sourceY, int sourceWidth, int sourceHeight,
            int targetWidth, int targetHeight, bool hasAlpha = false)
            => (HDRPictureFP16)GetFrameCore(targetFrame,
                new VideoFrameRegion(sourceX, sourceY, sourceWidth, sourceHeight, targetWidth, targetHeight), true, hasAlpha);

        private IPicture<Half> GetFrameCore(uint targetFrame, VideoFrameRegion? region, bool hdr = false, bool hasAlpha = true)
        {
            bool lockTaken = false;
            try
            {
                if (EnableLock)
                {
                    locker.Enter();
                    lockTaken = true;
                }

                if (Disposed)
                    throw new ObjectDisposedException(nameof(DecoderContextHWFP16), $"Decoder for '{_path}' was disposed while waiting for lock (frame {targetFrame}).");

                EnsureDecoderReady(targetFrame);

                bool frameFound = _lastDecodedFrame == targetFrame;
                if (!frameFound)
                {
                    _lastDecodedFrame = null;
                    if (VideoDecoderTimestamp.ShouldSeek(targetFrame, _currentFrameNumber, _fps))
                        SmartSeekTo(targetFrame);
                }

                int decodedFrameNumber = frameFound ? (int)targetFrame : _currentFrameNumber;
                while (!frameFound)
                {
                    if (!_eof && !_packetPending)
                    {
                        while (true)
                        {
                            int readRet = CheckIO(ffmpeg.av_read_frame(_fmt, _pkt));
                            if (readRet < 0)
                            {
                                _eof = true;
                                ffmpeg.av_packet_unref(_pkt);
                                break;
                            }

                            if (_pkt->stream_index == _videoStreamIndex)
                            {
                                _packetPending = true;
                                break;
                            }

                            ffmpeg.av_packet_unref(_pkt);
                        }
                    }

                    if (_packetPending)
                    {
                        int sendRet = ffmpeg.avcodec_send_packet(_codec, _pkt);
                        if (sendRet >= 0 || sendRet == ffmpeg.AVERROR_EOF)
                        {
                            ffmpeg.av_packet_unref(_pkt);
                            _packetPending = false;
                        }
                        else if (sendRet != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                        {
                            throw new InvalidDataException($"Decoder failed to send packet for '{_path}' (code {sendRet}).");
                        }
                    }
                    else if (_eof && !flushSent)
                    {
                        int flushRet = ffmpeg.avcodec_send_packet(_codec, null);
                        if (flushRet >= 0 || flushRet == ffmpeg.AVERROR_EOF)
                            flushSent = true;
                        else if (flushRet != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                            throw new InvalidDataException($"Decoder failed to flush packets for '{_path}' (code {flushRet}).");
                    }

                    bool restartFromBeginning = false;
                    while (true)
                    {
                        ffmpeg.av_frame_unref(_frm);
                        _hasTransferredFrame = false;
                        int ret = ffmpeg.avcodec_receive_frame(_codec, _frm);
                        if (ret == 0)
                        {
                            if (_needsTimestampCalibration)
                            {
                                if (!VideoDecoderTimestamp.TryGetFrameNumber(_frm, _fmt->streams[_videoStreamIndex], _fps, out decodedFrameNumber)
                                    || decodedFrameNumber > targetFrame)
                                {
                                    Log($"[{TypeName}] Seek timestamp calibration failed for '{_path}' at target {targetFrame}; restarting from the beginning.", "warning");
                                    SmartSeekTo(0);
                                    decodedFrameNumber = 0;
                                    restartFromBeginning = true;
                                    break;
                                }

                                _needsTimestampCalibration = false;
                            }

                            if (decodedFrameNumber == targetFrame)
                            {
                                frameFound = true;
                                break;
                            }

                            decodedFrameNumber++;
                            continue;
                        }

                        if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                            break;

                        throw new InvalidDataException($"Decoder failed to receive frame for '{_path}' (code {ret}).");
                    }

                    if (restartFromBeginning)
                        continue;

                    if (frameFound)
                        break;

                    if (_eof && flushSent)
                        break;

                    if (_totalFrames >= 0 && decodedFrameNumber > _totalFrames)
                        break;

                    if (decodedFrameNumber > targetFrame)
                    {
                        SmartSeekTo(0);
                        decodedFrameNumber = 0;
                    }
                }

                _currentFrameNumber = decodedFrameNumber + 1;

                if (!frameFound)
                {
                    if (_totalFrames > 0 && targetFrame > 0 && Math.Abs((long)targetFrame - _totalFrames) < 5)
                        return GetFrameCore(targetFrame - 1, region, hdr, hasAlpha);

                    double fps = _fps > 0 ? _fps : 1.0;
                    double seconds = targetFrame / fps;
                    throw new OverflowException($"Frame #{targetFrame} (timespan {TimeSpan.FromSeconds(seconds)}) not exist in video '{_path}'.");
                }

                _lastDecodedFrame = targetFrame;
                Index++;
                var picture = ConvertCurrentDecodedFrame(targetFrame, region, hdr, hasAlpha);
                return picture;
            }
            finally
            {
                if (lockTaken)
                    locker.Exit();
            }
        }

        private void SmartSeekTo(uint targetFrame)
        {
            if (_packetPending)
            {
                ffmpeg.av_packet_unref(_pkt);
                _packetPending = false;
            }

            if (_fps <= 0 || _fmt == null || _videoStreamIndex < 0)
            {
                CheckIO(ffmpeg.av_seek_frame(_fmt, _videoStreamIndex, 0, ffmpeg.AVSEEK_FLAG_BACKWARD));
                ffmpeg.avcodec_flush_buffers(_codec);
                _currentFrameNumber = 0;
                _eof = false;
                flushSent = false;
                _needsTimestampCalibration = false;
                return;
            }

            AVStream* stream = _fmt->streams[_videoStreamIndex];
            var timeBase = stream->time_base;
            double timeBaseSeconds = ffmpeg.av_q2d(timeBase);
            if (timeBaseSeconds <= 0)
            {
                CheckIO(ffmpeg.av_seek_frame(_fmt, _videoStreamIndex, 0, ffmpeg.AVSEEK_FLAG_BACKWARD));
                ffmpeg.avcodec_flush_buffers(_codec);
                _currentFrameNumber = 0;
                _eof = false;
                flushSent = false;
                _needsTimestampCalibration = false;
                return;
            }

            double targetTimeSeconds = targetFrame / _fps;
            double seekTimeSeconds = Math.Max(0, targetTimeSeconds - 0.5);
            long streamStart = stream->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : stream->start_time;
            long seekTimestamp = streamStart + (long)(seekTimeSeconds / timeBaseSeconds);

            int seekRet = CheckIO(ffmpeg.av_seek_frame(_fmt, _videoStreamIndex, seekTimestamp, ffmpeg.AVSEEK_FLAG_BACKWARD));
            bool fellBackToStart = false;
            if (seekRet < 0)
            {
                seekRet = CheckIO(ffmpeg.av_seek_frame(_fmt, _videoStreamIndex, streamStart, ffmpeg.AVSEEK_FLAG_BACKWARD));
                if (seekRet < 0)
                {
                    var msg = $"Failed to seek decoder for '{_path}' (code {seekRet}).";
                    if (StrictMode)
                        throw new InvalidOperationException(msg);
                    Log(msg, "warning");
                    throw new InvalidOperationException(msg);
                }
                Log($"[{TypeName}] Seek to frame {targetFrame} failed for '{_path}'; restarting from the beginning.", "warning");
                fellBackToStart = true;
            }

            ffmpeg.avcodec_flush_buffers(_codec);
            _currentFrameNumber = 0;
            _eof = false;
            flushSent = false;
            _needsTimestampCalibration = targetFrame > 0 && !fellBackToStart;
        }

        private PictureFP16 ConvertCurrentDecodedFrame(uint frameNumber, VideoFrameRegion? region, bool hdr, bool hasAlpha)
        {
            FFmpegFP16FrameCodec.ApplyDecoderMetadata(_frm, _codec);
            AVFrame* srcFrame = _frm;

            if (IsHWFormat((AVPixelFormat)_frm->format))
            {
                if (!_hasTransferredFrame)
                {
                    ffmpeg.av_frame_unref(_swFrame);
                    int transferRet = ffmpeg.av_hwframe_transfer_data(_swFrame, _frm, 0);
                    if (transferRet < 0)
                        throw new InvalidDataException($"Decoder failed to transfer hw frame for '{_path}' (code {transferRet}).");
                    FFmpegHelper.Throw(ffmpeg.av_frame_copy_props(_swFrame, _frm), "copy hardware frame metadata");
                    _hasTransferredFrame = true;
                }
                srcFrame = _swFrame;
            }

            if (srcFrame->width != _width || srcFrame->height != _height)
            {
                Log($"[DecoderContextHWFP16] Frame dimensions mismatch: expected {_width}x{_height}, got {srcFrame->width}x{srcFrame->height} for '{_path}' frame {frameNumber}.", "warning");
            }

            Half? maximumBrightness = hdr || FFmpegFP16FrameCodec.IsHdr(_frm->color_trc)
                ? FFmpegFP16FrameCodec.ResolveMaximumBrightness(_frm) : null;
            bool includeAlpha = hasAlpha && (hdr || FFmpegFP16FrameCodec.HasAlpha(srcFrame));
            if (region is VideoFrameRegion requestedRegion)
            {
                return FFmpegFrameCropScaler.Scale(
                    srcFrame,
                    requestedRegion.SourceX, requestedRegion.SourceY,
                    requestedRegion.SourceWidth, requestedRegion.SourceHeight,
                    requestedRegion.TargetWidth, requestedRegion.TargetHeight,
                    FFmpegFP16FrameCodec.DecoderFormat,
                    frame => FFmpegFP16FrameCodec.Read(frame, includeAlpha, _path, frameNumber,
                        typeof(DecoderContextHWFP16), maximumBrightness, _frm->color_trc));
            }

            _sws = ffmpeg.sws_getCachedContext(_sws, srcFrame->width, srcFrame->height, (AVPixelFormat)srcFrame->format,
                _width, _height, (AVPixelFormat)_rgb->format, 4, null, null, null);
            if (_sws == null) throw new InvalidOperationException($"Failed to create the floating-point conversion context for '{_path}'.");

            FFmpegFP16FrameCodec.ConfigureDecoderColorspace(_sws, srcFrame);
            int scaledRows = FFmpegHelper.ScaleFrame(_sws, srcFrame, _rgb);
            if (scaledRows != _height)
                throw new InvalidDataException($"Floating-point frame conversion produced {scaledRows}/{_height} rows.");

            return FFmpegFP16FrameCodec.Read(_rgb, includeAlpha, _path, frameNumber, typeof(DecoderContextHWFP16), maximumBrightness, _frm->color_trc);
        }

        private static bool IsHWFormat(AVPixelFormat format)
        {
            var descriptor = ffmpeg.av_pix_fmt_desc_get(format);
            return descriptor != null && (descriptor->flags & ffmpeg.AV_PIX_FMT_FLAG_HWACCEL) != 0;
        }

        public void Dispose()
        {
            if (Disposed) return;

            locker.Enter();
            try
            {
                if (Disposed) return;
                Disposed = true;
            }
            finally
            {
                locker.Exit();
            }

            if (_rgb != null) { AVFrame* tmp = _rgb; _rgb = null; ffmpeg.av_frame_free(&tmp); }
            if (_swFrame != null) { AVFrame* tmp = _swFrame; _swFrame = null; ffmpeg.av_frame_free(&tmp); }
            if (_frm != null) { AVFrame* tmp = _frm; _frm = null; ffmpeg.av_frame_free(&tmp); }
            if (_pkt != null) { AVPacket* tmp = _pkt; _pkt = null; ffmpeg.av_packet_free(&tmp); }
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            ReleaseDecoder();
            if (_fmt != null) { AVFormatContext* tmp = _fmt; _fmt = null; ffmpeg.avformat_close_input(&tmp); }
            _streamIO?.Dispose();
        }

        ~DecoderContextHWFP16()
        {
            Dispose();
        }
    }
}
