using FFmpeg.AutoGen;
using projectFrameCut.Drawing.Processing.Converting;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using static projectFrameCut.Render.EncodeAndDecode.FFmpegHelper;

namespace projectFrameCut.Render.EncodeAndDecode
{
    /// <summary>
    /// Converts a decoded frame timestamp back to the source's nominal frame index.
    /// This is used only to calibrate the decoder after a seek; sequential decoding
    /// continues to use an ordinal counter so variable-frame-rate inputs remain stable.
    /// </summary>
    internal static unsafe class VideoDecoderTimestamp
    {
        public static bool ShouldSeek(uint targetFrame, int nextFrame, double fps)
        {
            return targetFrame < nextFrame
                || (double.IsFinite(fps) && fps > 0 && (long)targetFrame - nextFrame > Math.Max(1, Math.Ceiling(fps)));
        }

        public static bool TryGetFrameNumber(AVFrame* frame, AVStream* stream, double fps, out int frameNumber)
        {
            frameNumber = 0;
            if (frame == null || stream == null || fps <= 0 || double.IsNaN(fps) || double.IsInfinity(fps))
                return false;

            long timestamp = frame->best_effort_timestamp;
            if (timestamp == ffmpeg.AV_NOPTS_VALUE)
                timestamp = frame->pts;
            if (timestamp == ffmpeg.AV_NOPTS_VALUE)
                return false;

            long startTimestamp = stream->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : stream->start_time;
            double seconds = (timestamp - startTimestamp) * ffmpeg.av_q2d(stream->time_base);
            double value = seconds * fps;
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > int.MaxValue)
                return false;

            frameNumber = Math.Max(0, (int)Math.Round(value, MidpointRounding.AwayFromZero));
            return true;
        }
    }

    public sealed unsafe class DecoderContextFP16 : IVideoSource<Half>
    {
        private readonly string _path;
        private readonly bool _streaming;
        private readonly FFmpegStreamIOContext? _streamIO;
        private AVFormatContext* _fmt = null;
        private AVCodecContext* _codec = null;
        private long _totalFrames;
        private SwsContext* _sws = null;
        private AVPacket* _pkt = null;
        private AVFrame* _frm = null;
        private AVFrame* _rgb = null;

        private int _videoStreamIndex = -1;
        private int _width = -1;
        private int _height = -1;
        private double _fps = -1.0;
        private int _currentFrameNumber = 0;
        private uint? _lastDecodedFrame;
        private bool _eof = false;
        private bool flushSent = false;
        private bool _packetPending = false;
        private bool _needsTimestampCalibration = false;

        public bool Disposed { get; private set; }
        public bool Initialized { get; private set; } = false;

        public long TotalFrames => _totalFrames;

        public double Fps => _fps;

        public int Width => _width;

        public int Height => _height;

        public uint Index { get; set; } = 0;

        public string[] PreferredExtension => [".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".wmv", ".flv", ".ts", ".m2ts", ".mpeg", ".mpg"];
        public int? ResultBitPerPixel => IPicture.PicturePixelMode.Float16Picture;
        public bool IsDeterministic => true;

        public bool EnableLock { get; set; } = true;
        public bool StrictMode { get; set; }

        public string TypeName => "DecoderContextFP16";

        private Lock locker = new();

        public DecoderContextFP16()
        {
            _path = null!;
        }

        public DecoderContextFP16(string path) : this(path, false) { }

        internal DecoderContextFP16(string path, bool streaming)
        {
            _path = path;
            _streaming = streaming;
            Initialize();
        }

        public DecoderContextFP16(Stream source, long length, bool leaveOpen = false)
        {
            _path = "<stream>";
            _streamIO = new FFmpegStreamIOContext(source, length, leaveOpen);
            Initialize();
        }

        public IVideoSource CreateNew(string newSource) => new DecoderContextFP16(newSource);
        public IVideoSource FromStream(Stream source, long length, bool leaveOpen = false) => new DecoderContextFP16(source, length, leaveOpen);

        private int CheckIO(int result) => _streamIO?.Check(result) ?? result;


        public void Initialize()
        {
            if ((_path is null && _streamIO is null) || Initialized) return; //VideoSourceCreator needs a instance to get PreferredExtension

            try
            {
                _fmt = ffmpeg.avformat_alloc_context();
                if (_fmt == null) throw new InvalidOperationException("Failed to alloc a context for the Renderer. Please try reboot your device, or reinstall projectFrameCut.");


                fixed (AVFormatContext** fmtPtr = &_fmt)
                {
                    int averr = _streamIO is null
                        ? ffmpeg.avformat_open_input(fmtPtr, _path, null, null)
                        : _streamIO.Open(fmtPtr);
                    if (averr != 0)
                    {
                        if (_streamIO is null) FFmpegHelper.DetectWhyCannotOpenVideo(_path, averr);
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

                AVCodecParameters* par = _fmt->streams[_videoStreamIndex]->codecpar;
                AVCodec* codec = ffmpeg.avcodec_find_decoder(par->codec_id);
                if (codec == null)
                    throw new NotSupportedException("No suitable decoder found. Try install the encoder extension or encode your video again to another format.");

                _codec = ffmpeg.avcodec_alloc_context3(codec);
                if (_codec == null) throw new InvalidOperationException("Failed to alloc a context for the Renderer. Please try reboot your device, or reinstall projectFrameCut.");

                ffmpeg.avcodec_parameters_to_context(_codec, par);
                if (ffmpeg.avcodec_open2(_codec, codec, null) < 0)
                    throw new NotSupportedException("Failed to open decoder. Please reinstall projectFrameCut.");

                _pkt = ffmpeg.av_packet_alloc();
                _frm = ffmpeg.av_frame_alloc();
                if (_pkt == null || _frm == null)
                    throw new OutOfMemoryException($"Failed to allocate enough memory space to process the video '{_path}'. Try closing other programs, restarting your device, reinstall projectFrameCut, increasing page file size (on Windows platforms)/swapping files (on Linux platforms), or adding more RAM on your device if possible.");


                _width = _codec->width;
                _height = _codec->height;

                AVRational fr = _codec->framerate;
                if (fr.num == 0 || fr.den == 0)
                    fr = _fmt->streams[_videoStreamIndex]->avg_frame_rate;
                if (fr.num == 0 || fr.den == 0)
                    fr = _fmt->streams[_videoStreamIndex]->r_frame_rate;

                _fps = fr.den != 0 ? ffmpeg.av_q2d(fr) : 0.0;

                if (_width <= 0 || _height <= 0)
                    throw new InvalidDataException($"Video file is invalid. Try install ffmpeg toolkit on your computer, then run this command and observe whether there is any error message:\r\nffprobe {Path.GetFullPath(_path)}");

                if (_fps <= 0 && !_streaming)
                    throw new InvalidDataException($"The file '{_path}' is more like a single frame media, like a photo, rather than a video. If you're sure this file is a video, try encoding it again to another format.");

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

                _currentFrameNumber = 0;
                Initialized = true;

                Log($"[VideoDecoder] Successfully initialized decoder for {_path}");
            }
            catch (Exception ex)
            {
                Dispose();
                Log(ex, "Init VideoDecoder", this);
                throw;
            }
        }


        private void EnsureDecoderReady(uint targetFrame)
        {
            if (Disposed)
                throw new ObjectDisposedException(nameof(DecoderContextFP16), $"Decoder for '{_path}' is already disposed when trying to read frame {targetFrame}.");
            if (!Initialized)
                throw new InvalidOperationException($"Decoder for '{_path}' is not initialized when trying to read frame {targetFrame}.");
            if (_videoStreamIndex < 0 || _width <= 0 || _height <= 0)
                throw new InvalidDataException($"Decoder metadata is invalid for '{_path}' when trying to read frame {targetFrame}.");
            if (IsPointerAddressesNotValid(_fmt) || IsPointerAddressesNotValid(_codec) || IsPointerAddressesNotValid(_pkt) ||
                IsPointerAddressesNotValid(_frm) || IsPointerAddressesNotValid(_rgb))
                throw new InvalidDataException($"Decoder native state is invalid for '{_path}' when trying to read frame {targetFrame}.");
            if (_rgb->data[0] == null || _rgb->linesize[0] <= 0)
                throw new InvalidDataException($"Decoder RGB buffer is invalid for '{_path}' when trying to read frame {targetFrame}.");
        }


        public IPicture<Half> GetFrame(uint targetFrame)
            => GetFrameCore(targetFrame, null);

        public IPicture<Half> GetFrame(uint targetFrame, int sourceX, int sourceY, int sourceWidth, int sourceHeight,
            int targetWidth, int targetHeight)
            => GetFrameCore(targetFrame,
                new VideoFrameRegion(sourceX, sourceY, sourceWidth, sourceHeight, targetWidth, targetHeight));

        public IPicture<Half> GetFrame(uint targetFrame, int targetWidth, int targetHeight)
            => GetFrame(targetFrame, 0, 0, _width, _height, targetWidth, targetHeight);

        private IPicture<Half> GetFrameCore(uint targetFrame, VideoFrameRegion? region)
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
                    throw new ObjectDisposedException(nameof(DecoderContextFP16), $"Decoder for '{_path}' was disposed while waiting for lock (frame {targetFrame}).");

                EnsureDecoderReady(targetFrame);

                bool frameFound = _lastDecodedFrame == targetFrame;
                if (!frameFound)
                {
                    _lastDecodedFrame = null;
                    if (targetFrame < _currentFrameNumber || !_streaming && VideoDecoderTimestamp.ShouldSeek(targetFrame, _currentFrameNumber, _fps))
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
                        int receiveRet = ffmpeg.avcodec_receive_frame(_codec, _frm);
                        if (receiveRet == 0)
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

                        if (receiveRet == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveRet == ffmpeg.AVERROR_EOF)
                            break;

                        throw new InvalidDataException($"Decoder failed to receive frame for '{_path}' (code {receiveRet}).");
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
                        continue;
                    }
                }

                _currentFrameNumber = decodedFrameNumber + 1;

                if (!frameFound)
                {
                    if (_totalFrames > 0 && targetFrame > 0 && Math.Abs((long)targetFrame - _totalFrames) < 5)
                    {
                        Log($"[VideoDecoder] Frame {targetFrame} not found(may due to rounding), try getting frame {targetFrame - 1} instead.");
                        return GetFrameCore(targetFrame - 1, region);
                    }

                    double fps = _fps > 0 ? _fps : 1.0;
                    double seconds = targetFrame / fps;
                    throw new OverflowException($"Frame #{targetFrame} (timespan {TimeSpan.FromSeconds(seconds)}) not exist in video '{_path}'.");
                }

                _lastDecodedFrame = targetFrame;
                Index++;
                FFmpegFP16FrameCodec.ApplyDecoderMetadata(_frm, _codec);
                if (_frm->width != _width || _frm->height != _height)
                    Log($"[VideoDecoder] Frame dimensions mismatch in '{_path}': expected {_width}x{_height}, got {_frm->width}x{_frm->height}.", "warning");

                if (region is VideoFrameRegion requestedRegion)
                {
                    return FFmpegFrameCropScaler.Scale(
                        _frm,
                        requestedRegion.SourceX, requestedRegion.SourceY,
                        requestedRegion.SourceWidth, requestedRegion.SourceHeight,
                        requestedRegion.TargetWidth, requestedRegion.TargetHeight,
                        FFmpegFP16FrameCodec.DecoderFormat,
                        frame => FFmpegFP16FrameCodec.Read(frame, FFmpegFP16FrameCodec.HasAlpha(_frm), _path, targetFrame,
                            typeof(DecoderContextFP16), FFmpegFP16FrameCodec.IsHdr(_frm->color_trc)
                                ? FFmpegFP16FrameCodec.ResolveMaximumBrightness(_frm) : null, _frm->color_trc));
                }

                _sws = ffmpeg.sws_getCachedContext(_sws, _frm->width, _frm->height, (AVPixelFormat)_frm->format,
                    _width, _height, (AVPixelFormat)_rgb->format, 4, null, null, null);
                if (_sws == null) throw new InvalidOperationException("Failed to create the floating-point video conversion context.");
                FFmpegFP16FrameCodec.ConfigureDecoderColorspace(_sws, _frm);
                int scaledRows = FFmpegHelper.ScaleFrame(_sws, _frm, _rgb);
                if (scaledRows != _height)
                    throw new InvalidDataException($"Floating-point frame conversion produced {scaledRows}/{_height} rows.");

                var picture = FFmpegFP16FrameCodec.Read(_rgb, FFmpegFP16FrameCodec.HasAlpha(_frm), _path, targetFrame,
                    typeof(DecoderContextFP16), FFmpegFP16FrameCodec.IsHdr(_frm->color_trc)
                        ? FFmpegFP16FrameCodec.ResolveMaximumBrightness(_frm) : null, _frm->color_trc);
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

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (Disposed) return;

            if (disposing)
            {
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
            }
            else
            {
                Disposed = true;
            }

            if (_rgb != null) { AVFrame* tmp = _rgb; _rgb = null; ffmpeg.av_frame_free(&tmp); }
            if (_frm != null) { AVFrame* tmp = _frm; _frm = null; ffmpeg.av_frame_free(&tmp); }
            if (_pkt != null) { AVPacket* tmp = _pkt; _pkt = null; ffmpeg.av_packet_free(&tmp); }
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            if (_codec != null) { AVCodecContext* tmp = _codec; _codec = null; ffmpeg.avcodec_free_context(&tmp); }
            if (_fmt != null) { AVFormatContext* tmp = _fmt; _fmt = null; ffmpeg.avformat_close_input(&tmp); }
            _streamIO?.Dispose();
        }

        ~DecoderContextFP16()
        {
            Dispose(disposing: false);
        }
    }

    public sealed unsafe class HDRDecoderContextFP16 : IVideoSource<Half>, IHDRVideoSource
    {
        private readonly string _path;
        private AVPixelFormat OutputPixelFormat => FFmpegFP16FrameCodec.DecoderFormat;
        private readonly FFmpegStreamIOContext? _streamIO;
        private AVFormatContext* _fmt = null;
        private AVCodecContext* _codec = null;
        private long _totalFrames;
        private SwsContext* _sws = null;
        private AVPacket* _pkt = null;
        private AVFrame* _frm = null;
        private AVFrame* _rgb = null;

        private int _videoStreamIndex = -1;
        private int _width = -1;
        private int _height = -1;
        private double _fps = -1.0;
        private int _currentFrameNumber = 0;
        private uint? _lastDecodedFrame;
        private bool _eof = false;
        private bool flushSent = false;
        private bool _packetPending = false;
        private bool _needsTimestampCalibration = false;

        public bool Disposed { get; private set; }
        public bool Initialized { get; private set; } = false;

        public long TotalFrames => _totalFrames;

        public double Fps => _fps;

        public int Width => _width;

        public int Height => _height;

        public uint Index { get; set; } = 0;

        public string[] PreferredExtension => [".mkv", ".mp4", ".mov"];
        public int? ResultBitPerPixel => IPicture.PicturePixelMode.Float16Picture;
        public string TypeName => "HDRDecoderContextFP16";
        public bool IsDeterministic => true;

        public bool EnableLock { get; set; } = true;
        public bool StrictMode { get; set; }


        private Lock locker = new();

        public HDRDecoderContextFP16()
        {
            _path = null!;
        }

        public HDRDecoderContextFP16(string path)
        {
            _path = path;
            Initialize();
        }

        public HDRDecoderContextFP16(Stream source, long length, bool leaveOpen = false)
        {
            _path = "<stream>";
            _streamIO = new FFmpegStreamIOContext(source, length, leaveOpen);
            Initialize();
        }

        public IVideoSource CreateNew(string newSource) => new HDRDecoderContextFP16(newSource);
        public IVideoSource FromStream(Stream source, long length, bool leaveOpen = false) => new HDRDecoderContextFP16(source, length, leaveOpen);

        private int CheckIO(int result) => _streamIO?.Check(result) ?? result;


        public void Initialize()
        {
            if ((_path is null && _streamIO is null) || Initialized) return; //VideoSourceCreator needs a instance to get PreferredExtension

            try
            {
                _fmt = ffmpeg.avformat_alloc_context();
                if (_fmt == null) throw new InvalidOperationException("Failed to alloc a context for the Renderer. Please try reboot your device, or reinstall projectFrameCut.");


                fixed (AVFormatContext** fmtPtr = &_fmt)
                {
                    int averr = _streamIO is null
                        ? ffmpeg.avformat_open_input(fmtPtr, _path, null, null)
                        : _streamIO.Open(fmtPtr);
                    if (averr != 0)
                    {
                        if (_streamIO is null) FFmpegHelper.DetectWhyCannotOpenVideo(_path, averr);
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

                AVCodecParameters* par = _fmt->streams[_videoStreamIndex]->codecpar;
                AVCodec* codec = ffmpeg.avcodec_find_decoder(par->codec_id);
                if (codec == null)
                    throw new NotSupportedException("No suitable decoder found. Try install the encoder extension or encode your video again to another format.");

                _codec = ffmpeg.avcodec_alloc_context3(codec);
                if (_codec == null) throw new InvalidOperationException("Failed to alloc a context for the Renderer. Please try reboot your device, or reinstall projectFrameCut.");

                ffmpeg.avcodec_parameters_to_context(_codec, par);
                if (ffmpeg.avcodec_open2(_codec, codec, null) < 0)
                    throw new NotSupportedException("Failed to open decoder. Please reinstall projectFrameCut.");

                _pkt = ffmpeg.av_packet_alloc();
                _frm = ffmpeg.av_frame_alloc();
                if (_pkt == null || _frm == null)
                    throw new OutOfMemoryException($"Failed to allocate enough memory space to process the video '{_path}'. Try closing other programs, restarting your device, reinstall projectFrameCut, increasing page file size (on Windows platforms)/swapping files (on Linux platforms), or adding more RAM on your device if possible.");


                _width = _codec->width;
                _height = _codec->height;

                AVRational fr = _codec->framerate;
                if (fr.num == 0 || fr.den == 0)
                    fr = _fmt->streams[_videoStreamIndex]->avg_frame_rate;
                if (fr.num == 0 || fr.den == 0)
                    fr = _fmt->streams[_videoStreamIndex]->r_frame_rate;

                _fps = fr.den != 0 ? ffmpeg.av_q2d(fr) : 0.0;

                if (_width <= 0 || _height <= 0)
                    throw new InvalidDataException($"Video file is invalid. Try install ffmpeg toolkit on your computer, then run this command and observe whether there is any error message:\r\nffprobe {Path.GetFullPath(_path)}");

                if (_fps <= 0)
                    throw new InvalidDataException($"The file '{_path}' is more like a single frame media, like a photo, rather than a video. If you're sure this file is a video, try encoding it again to another format.");

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

                _rgb = FFmpegFP16FrameCodec.AllocateFrame(_width, _height, OutputPixelFormat);

                _currentFrameNumber = 0;
                Initialized = true;

                Log($"[VideoDecoder] Successfully initialized HDR decoder for {_path}");
            }
            catch (Exception ex)
            {
                Dispose();
                Log(ex, "Init HDRDecoderContextFP16", this);
                throw;
            }
        }


        private void EnsureDecoderReady(uint targetFrame)
        {
            if (Disposed)
                throw new ObjectDisposedException(nameof(HDRDecoderContextFP16), $"Decoder for '{_path}' is already disposed when trying to read frame {targetFrame}.");
            if (!Initialized)
                throw new InvalidOperationException($"Decoder for '{_path}' is not initialized when trying to read frame {targetFrame}.");
            if (_videoStreamIndex < 0 || _width <= 0 || _height <= 0)
                throw new InvalidDataException($"Decoder metadata is invalid for '{_path}' when trying to read frame {targetFrame}.");
            if (IsPointerAddressesNotValid(_fmt) || IsPointerAddressesNotValid(_codec) || IsPointerAddressesNotValid(_pkt) ||
                IsPointerAddressesNotValid(_frm) || IsPointerAddressesNotValid(_rgb))
                throw new InvalidDataException($"Decoder native state is invalid for '{_path}' when trying to read frame {targetFrame}.");
            if (_rgb->data[0] == null || _rgb->linesize[0] <= 0)
                throw new InvalidDataException($"Decoder RGB buffer is invalid for '{_path}' when trying to read frame {targetFrame}.");
        }

        public IPicture<Half> GetFrame(uint targetFrame)
        {
            using var frame = GetHDRFrame(targetFrame, true);
            return frame.DegradeToSDR();
        }

        public IPicture<Half> GetFrame(uint targetFrame, int sourceX, int sourceY, int sourceWidth, int sourceHeight,
            int targetWidth, int targetHeight)
        {
            using var frame = GetHDRFrame(targetFrame, sourceX, sourceY, sourceWidth, sourceHeight, targetWidth, targetHeight, true);
            return frame.DegradeToSDR();
        }

        public IPicture<Half> GetFrame(uint targetFrame, int targetWidth, int targetHeight)
            => GetFrame(targetFrame, 0, 0, _width, _height, targetWidth, targetHeight);

        public HDRPictureFP16 GetHDRFrame(uint targetFrame, bool hasAlpha = false)
            => GetHDRFrameCore(targetFrame, hasAlpha, null);

        public HDRPictureFP16 GetHDRFrame(uint targetFrame, int sourceX, int sourceY, int sourceWidth, int sourceHeight,
            int targetWidth, int targetHeight, bool hasAlpha = false)
            => GetHDRFrameCore(targetFrame, hasAlpha,
                new VideoFrameRegion(sourceX, sourceY, sourceWidth, sourceHeight, targetWidth, targetHeight));

        public HDRPictureFP16 GetHDRFrame(uint targetFrame, int targetWidth, int targetHeight, bool hasAlpha = false)
            => GetHDRFrame(targetFrame, 0, 0, _width, _height, targetWidth, targetHeight, hasAlpha);

        private HDRPictureFP16 GetHDRFrameCore(uint targetFrame, bool hasAlpha, VideoFrameRegion? region)
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
                    throw new ObjectDisposedException(nameof(HDRDecoderContextFP16), $"Decoder for '{_path}' was disposed while waiting for lock (frame {targetFrame}).");

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
                        int receiveRet = ffmpeg.avcodec_receive_frame(_codec, _frm);
                        if (receiveRet == 0)
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

                        if (receiveRet == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveRet == ffmpeg.AVERROR_EOF)
                            break;

                        throw new InvalidDataException($"Decoder failed to receive frame for '{_path}' (code {receiveRet}).");
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
                        continue;
                    }
                }

                _currentFrameNumber = decodedFrameNumber + 1;

                if (!frameFound)
                {
                    if (_totalFrames > 0 && targetFrame > 0 && Math.Abs((long)targetFrame - _totalFrames) < 5)
                    {
                        Log($"[VideoDecoder] Frame {targetFrame} not found(may due to rounding), try getting frame {targetFrame - 1} instead.");
                        return GetHDRFrameCore(targetFrame - 1, hasAlpha, region);
                    }

                    double fps = _fps > 0 ? _fps : 1.0;
                    double seconds = targetFrame / fps;
                    throw new OverflowException($"Frame #{targetFrame} (timespan {TimeSpan.FromSeconds(seconds)}) not exist in video '{_path}'.");
                }

                _lastDecodedFrame = targetFrame;
                Index++;
                FFmpegFP16FrameCodec.ApplyDecoderMetadata(_frm, _codec);
                Half maximumBrightness = FFmpegFP16FrameCodec.ResolveMaximumBrightness(_frm);
                AVColorTransferCharacteristic transferCharacteristic = _frm->color_trc;

                if (region is VideoFrameRegion requestedRegion)
                {
                    return FFmpegFrameCropScaler.Scale(
                        _frm,
                        requestedRegion.SourceX, requestedRegion.SourceY,
                        requestedRegion.SourceWidth, requestedRegion.SourceHeight,
                        requestedRegion.TargetWidth, requestedRegion.TargetHeight,
                        OutputPixelFormat,
                        frame => (HDRPictureFP16)FFmpegFP16FrameCodec.Read(frame, hasAlpha, _path, targetFrame,
                            typeof(HDRDecoderContextFP16), maximumBrightness, transferCharacteristic));
                }

                _sws = ffmpeg.sws_getCachedContext(_sws, _frm->width, _frm->height, (AVPixelFormat)_frm->format,
                    _width, _height, (AVPixelFormat)_rgb->format, 4, null, null, null);
                if (_sws == null) throw new InvalidOperationException("Failed to create the floating-point video conversion context.");
                FFmpegFP16FrameCodec.ConfigureDecoderColorspace(_sws, _frm);
                int scaledRows = FFmpegHelper.ScaleFrame(_sws, _frm, _rgb);
                if (scaledRows != _height)
                    throw new InvalidDataException($"Floating-point frame conversion produced {scaledRows}/{_height} rows.");

                var picture = (HDRPictureFP16)FFmpegFP16FrameCodec.Read(_rgb, hasAlpha, _path, targetFrame, typeof(HDRDecoderContextFP16), maximumBrightness, transferCharacteristic);
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

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (Disposed) return;

            if (disposing)
            {
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
            }
            else
            {
                Disposed = true;
            }

            if (_rgb != null) { AVFrame* tmp = _rgb; _rgb = null; ffmpeg.av_frame_free(&tmp); }
            if (_frm != null) { AVFrame* tmp = _frm; _frm = null; ffmpeg.av_frame_free(&tmp); }
            if (_pkt != null) { AVPacket* tmp = _pkt; _pkt = null; ffmpeg.av_packet_free(&tmp); }
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            if (_codec != null) { AVCodecContext* tmp = _codec; _codec = null; ffmpeg.avcodec_free_context(&tmp); }
            if (_fmt != null) { AVFormatContext* tmp = _fmt; _fmt = null; ffmpeg.avformat_close_input(&tmp); }
            _streamIO?.Dispose();
        }

        /// <summary>
        /// Allocates a Half array of the specified size, filled entirely with 1.0f.
        /// Replaces Enumerable.Repeat(1f, size).ToArray() to avoid LINQ overhead.
        /// </summary>
        ~HDRDecoderContextFP16()
        {
            Dispose(disposing: false);
        }

        public static bool IsHdrVideo(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;

            AVFormatContext* fmt = null;
            try
            {
                fmt = ffmpeg.avformat_alloc_context();
                if (fmt == null) return false;

                if (ffmpeg.avformat_open_input(&fmt, path, null, null) != 0)
                    return false;

                if (ffmpeg.avformat_find_stream_info(fmt, null) < 0)
                    return false;

                for (int i = 0; i < fmt->nb_streams; i++)
                {
                    if (fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        AVCodecParameters* par = fmt->streams[i]->codecpar;
                        if (FFmpegFP16FrameCodec.IsHdr(par->color_trc))
                            return true;

                        if (par->color_primaries == AVColorPrimaries.AVCOL_PRI_BT2020
                            && par->color_space == AVColorSpace.AVCOL_SPC_BT2020_NCL)
                            return true;

                        break;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (fmt != null)
                {
                    AVFormatContext* tmp = fmt;
                    ffmpeg.avformat_close_input(&tmp);
                }
            }
        }
    }

}
