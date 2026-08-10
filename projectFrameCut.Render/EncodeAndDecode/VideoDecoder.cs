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

    public sealed unsafe class DecoderContext16Bit : IVideoSource<ushort>
    {
        private readonly string _path;
        private AVFormatContext* _fmt = null;
        private AVCodecContext* _codec = null;
        private long _totalFrames;
        private SwsContext* _sws = null;
        private AVPacket* _pkt = null;
        private AVFrame* _frm = null;
        private AVFrame* _rgb = null;
        private byte* _rgbBuffer = null;

        private int _videoStreamIndex = -1;
        private int _width = -1;
        private int _height = -1;
        private double _fps = -1.0;
        private int _currentFrameNumber = 0;
        private bool _eof = false;
        private bool flushSent = false;

        private readonly VideoFrameDiskCache _diskCache;

        public bool Disposed { get; private set; }
        public bool Initialized { get; private set; } = false;

        public long TotalFrames => _totalFrames;

        public double Fps => _fps;

        public int Width => _width;

        public int Height => _height;

        public uint Index { get; set; } = 0;

        public string[] PreferredExtension => [".mkv"];
        public int? ResultBitPerPixel => 8;

        public bool EnableLock { get; set; } = true;
        public bool StrictMode { get; set; }

        public string TypeName => "DecoderContext16Bit";

        private Lock locker = new();

        public DecoderContext16Bit(string path)
        {
            _path = path;
            Initialize();
            if (!string.IsNullOrWhiteSpace(path) && IVideoSource.EnableDiskCache) _diskCache = new VideoFrameDiskCache(_path);
        }

        public IVideoSource CreateNew(string newSource) => new DecoderContext16Bit(newSource);


        public void Initialize()
        {
            if (_path is null || Initialized) return; //VideoSourceCreator needs a instance to get PreferredExtension

            try
            {
                _fmt = ffmpeg.avformat_alloc_context();
                if (_fmt == null) throw new InvalidOperationException("Failed to alloc a context for the Renderer. Please try reboot your device, or reinstall projectFrameCut.");


                fixed (AVFormatContext** fmtPtr = &_fmt)
                {
                    int averr = ffmpeg.avformat_open_input(fmtPtr, _path, null, null);
                    if (averr != 0)
                    {
                        FFmpegHelper.DetectWhyCannotOpenVideo(_path, averr);
                    }
                }

                if (ffmpeg.avformat_find_stream_info(_fmt, null) != 0)
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
                _rgb = ffmpeg.av_frame_alloc();
                if (_pkt == null || _frm == null || _rgb == null)
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

                _sws = ffmpeg.sws_getContext(
                        _width, _height, _codec->pix_fmt,
                        _width, _height, AVPixelFormat.AV_PIX_FMT_BGR48LE,
                        4 /* SWS_BICUBIC == 4*/, null, null, null);

                if (_sws == null)
                    throw new InvalidOperationException("Failed to alloc a context for the Renderer. Please try reboot your device, or reinstall projectFrameCut.");

                int bufferSize = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGR48LE, _width, _height, 32);
                if (bufferSize <= 0) throw new OutOfMemoryException($"Failed to allocate enough memory space to process the video '{_path}'. Try closing other programs, restarting your device, reinstall projectFrameCut, increasing page file size (on Windows platforms)/swapping files (on Linux platforms), or adding more RAM on your device if possible.");

                _rgbBuffer = (byte*)ffmpeg.av_malloc((ulong)bufferSize);
                if (_rgbBuffer == null)
                    throw new OutOfMemoryException($"Failed to allocate enough memory space to process the video '{_path}'. Try closing other programs, restarting your device, reinstall projectFrameCut, increasing page file size (on Windows platforms)/swapping files (on Linux platforms), or adding more RAM on your device if possible.");

                byte_ptrArray4 tmpData = default;
                int_array4 tmpLinesize = default;
                int fillRet = ffmpeg.av_image_fill_arrays(
                    ref tmpData, ref tmpLinesize,
                    _rgbBuffer, AVPixelFormat.AV_PIX_FMT_BGR48LE,
                    _width, _height, 32);
                if (fillRet < 0) throw new InvalidOperationException("av_image_fill_arrays failed.");

                for (uint i = 0; i < 4; i++)
                {
                    _rgb->data[i] = tmpData[i];
                    _rgb->linesize[i] = tmpLinesize[i];
                }
                _rgb->format = (int)AVPixelFormat.AV_PIX_FMT_BGR48LE;
                _rgb->width = _width;
                _rgb->height = _height;

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
                throw new ObjectDisposedException(nameof(DecoderContext16Bit), $"Decoder for '{_path}' is already disposed when trying to read frame {targetFrame}.");
            if (!Initialized)
                throw new InvalidOperationException($"Decoder for '{_path}' is not initialized when trying to read frame {targetFrame}.");
            if (_videoStreamIndex < 0 || _width <= 0 || _height <= 0)
                throw new InvalidDataException($"Decoder metadata is invalid for '{_path}' when trying to read frame {targetFrame}.");
            if (IsPointerAddressesNotValid(_fmt) || IsPointerAddressesNotValid(_codec) || IsPointerAddressesNotValid(_pkt) ||
                IsPointerAddressesNotValid(_frm) || IsPointerAddressesNotValid(_rgb) || IsPointerAddressesNotValid(_sws) || _rgbBuffer == null)
                throw new InvalidDataException($"Decoder native state is invalid for '{_path}' when trying to read frame {targetFrame}.");
            if (_rgb->data[0] == null || _rgb->linesize[0] <= 0)
                throw new InvalidDataException($"Decoder RGB buffer is invalid for '{_path}' when trying to read frame {targetFrame}.");
        }


        public IPicture<ushort> GetFrame(uint targetFrame, bool hasAlpha = false)
            => GetFrameCore(targetFrame, hasAlpha, null);

        public IPicture<ushort> GetFrame(uint targetFrame, int sourceX, int sourceY, int sourceWidth, int sourceHeight,
            int targetWidth, int targetHeight, bool hasAlpha = false)
            => GetFrameCore(targetFrame, hasAlpha,
                new VideoFrameRegion(sourceX, sourceY, sourceWidth, sourceHeight, targetWidth, targetHeight));

        public IPicture<ushort> GetFrame(uint targetFrame, int targetWidth, int targetHeight, bool hasAlpha = false)
            => GetFrame(targetFrame, 0, 0, _width, _height, targetWidth, targetHeight, hasAlpha);

        private IPicture<ushort> GetFrameCore(uint targetFrame, bool hasAlpha, VideoFrameRegion? region)
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
                    throw new ObjectDisposedException(nameof(DecoderContext16Bit), $"Decoder for '{_path}' was disposed while waiting for lock (frame {targetFrame}).");

                EnsureDecoderReady(targetFrame);

                // Try disk cache before decoding
                if (region is null && IVideoSource.EnableDiskCache && _diskCache.TryLoad16bpp(targetFrame, out var diskFrame))
                {
                    Index++;
                    return diskFrame;
                }

                if (targetFrame < _currentFrameNumber)
                {
                    SmartSeekTo(targetFrame);
                }

                bool frameFound = false;
                int decodedFrameNumber = _currentFrameNumber;
                while (true)
                {
                    if (!_eof)
                    {
                        int readRet = ffmpeg.av_read_frame(_fmt, _pkt);
                        if (readRet < 0)
                        {
                            _eof = true;
                            ffmpeg.av_packet_unref(_pkt);
                        }
                        else
                        {
                            try
                            {
                                if (_pkt->stream_index == _videoStreamIndex)
                                {
                                    int sendRet = ffmpeg.avcodec_send_packet(_codec, _pkt);
                                    if (sendRet < 0 && sendRet != ffmpeg.AVERROR(ffmpeg.EAGAIN) && sendRet != ffmpeg.AVERROR_EOF)
                                        throw new InvalidDataException($"Decoder failed to send packet for '{_path}' (code {sendRet}).");
                                }
                            }
                            finally
                            {
                                ffmpeg.av_packet_unref(_pkt);
                            }
                        }
                    }
                    else if (!flushSent)
                    {
                        int flushRet = ffmpeg.avcodec_send_packet(_codec, null);
                        if (flushRet < 0 && flushRet != ffmpeg.AVERROR_EOF)
                            throw new InvalidDataException($"Decoder failed to flush packets for '{_path}' (code {flushRet}).");
                        flushSent = true;
                    }

                    while (true)
                    {
                        ffmpeg.av_frame_unref(_frm);
                        int receiveRet = ffmpeg.avcodec_receive_frame(_codec, _frm);
                        if (receiveRet == 0)
                        {
                            if (decodedFrameNumber == targetFrame)
                            {
                                frameFound = true;
                                break;
                            }

                            CacheDecodedFrame((uint)decodedFrameNumber, hasAlpha, targetFrame);
                            decodedFrameNumber++;
                            continue;
                        }

                        if (receiveRet == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveRet == ffmpeg.AVERROR_EOF)
                            break;

                        throw new InvalidDataException($"Decoder failed to receive frame for '{_path}' (code {receiveRet}).");
                    }

                    if (frameFound)
                        break;

                    if (_eof && flushSent)
                        break;

                    if (_totalFrames >= 0 && decodedFrameNumber > _totalFrames)
                        break;

                    // Overshoot detection: if seek landed too far forward, re-seek earlier
                    if (!frameFound && _currentFrameNumber > 0 && decodedFrameNumber > targetFrame + 60)
                    {
                        SmartSeekTo(Math.Max(0, targetFrame - 120));
                        decodedFrameNumber = _currentFrameNumber;
                        continue;
                    }
                }

                _currentFrameNumber = decodedFrameNumber + 1;

                if (!frameFound)
                {
                    if (_totalFrames > 0 && targetFrame > 0 && Math.Abs((long)targetFrame - _totalFrames) < 5)
                    {
                        Log($"[VideoDecoder] Frame {targetFrame} not found(may due to rounding), try getting frame {targetFrame - 1} instead.");
                        return GetFrameCore(targetFrame - 1, hasAlpha, region);
                    }

                    double fps = _fps > 0 ? _fps : 1.0;
                    double seconds = targetFrame / fps;
                    throw new OverflowException($"Frame #{targetFrame} (timespan {TimeSpan.FromSeconds(seconds)}) not exist in video '{_path}'.");
                }

                Index++;
                if (_frm->width != _width || _frm->height != _height)
                    Log($"[VideoDecoder] Frame dimensions mismatch in '{_path}': expected {_width}x{_height}, got {_frm->width}x{_frm->height}.", "warning");

                if (region is VideoFrameRegion requestedRegion)
                {
                    return FFmpegFrameCropScaler.Scale(
                        _frm,
                        requestedRegion.SourceX, requestedRegion.SourceY,
                        requestedRegion.SourceWidth, requestedRegion.SourceHeight,
                        requestedRegion.TargetWidth, requestedRegion.TargetHeight,
                        AVPixelFormat.AV_PIX_FMT_BGR48LE,
                        (data, stride, width, height) => PixelsToPicture(
                            data, stride, width, height, hasAlpha, _path, targetFrame, height));
                }

                int scaledRows = ffmpeg.sws_scale(
                    _sws,
                    _frm->data,
                    _frm->linesize,
                    0,
                    _height,
                    _rgb->data,
                    _rgb->linesize
                );
                if (scaledRows <= 0)
                    throw new InvalidDataException($"Decoder failed to convert frame for '{_path}' (sws_scale returned {scaledRows}).");
                if (scaledRows < _height)
                    Log($"[VideoDecoder] sws_scale only processed {scaledRows}/{_height} rows for '{_path}' frame {targetFrame}.", "warning");

                var picture = PixelsToPicture(_rgb->data[0], _rgb->linesize[0], _width, _height, hasAlpha, _path, targetFrame, scaledRows);
                CacheFinalFrame(targetFrame, picture);
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
            if (_fps <= 0 || _fmt == null || _videoStreamIndex < 0)
            {
                ffmpeg.av_seek_frame(_fmt, _videoStreamIndex, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
                ffmpeg.avcodec_flush_buffers(_codec);
                _currentFrameNumber = 0;
                _eof = false;
                flushSent = false;
                return;
            }

            var timeBase = _fmt->streams[_videoStreamIndex]->time_base;
            double timeBaseSeconds = ffmpeg.av_q2d(timeBase);
            if (timeBaseSeconds <= 0)
            {
                ffmpeg.av_seek_frame(_fmt, _videoStreamIndex, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
                ffmpeg.avcodec_flush_buffers(_codec);
                _currentFrameNumber = 0;
                _eof = false;
                flushSent = false;
                return;
            }

            double targetTimeSeconds = targetFrame / _fps;
            double seekTimeSeconds = Math.Max(0, targetTimeSeconds - 0.5);
            long seekTimestamp = (long)(seekTimeSeconds / timeBaseSeconds);

            int seekRet = ffmpeg.av_seek_frame(_fmt, _videoStreamIndex, seekTimestamp, ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (seekRet < 0)
            {
                seekRet = ffmpeg.av_seek_frame(_fmt, _videoStreamIndex, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
                if (seekRet < 0)
                {
                    var msg = $"Failed to seek decoder for '{_path}' (code {seekRet}).";
                    if (StrictMode)
                        throw new InvalidOperationException(msg);
                    Log(msg, "warning");
                    throw new InvalidOperationException(msg);
                }
                _currentFrameNumber = 0;
            }
            else
            {
                _currentFrameNumber = Math.Max(0, (int)(seekTimeSeconds * _fps) - 60);
            }

            ffmpeg.avcodec_flush_buffers(_codec);
            _eof = false;
            flushSent = false;
        }

        private void CacheDecodedFrame(uint frameNumber, bool hasAlpha, uint targetFrame)
        {
            if (!IVideoSource.EnableDiskCache)
                return;

            ffmpeg.sws_scale(
                _sws,
                _frm->data,
                _frm->linesize,
                0,
                _height,
                _rgb->data,
                _rgb->linesize);

            var picture = PixelsToPicture(_rgb->data[0], _rgb->linesize[0], _width, _height, hasAlpha, _path, frameNumber, _height);
            CacheFinalFrame(frameNumber, picture);
        }

        private void CacheFinalFrame(uint frameNumber, Picture16bpp picture)
        {
            if (!IVideoSource.EnableDiskCache)
                return;

            _diskCache.Save16bppFrameAsync(frameNumber, picture);
        }


        [DebuggerNonUserCode()]
        private static Picture16bpp PixelsToPicture(byte* data, int stride, int width, int height, bool hasAlpha = false, string filePath = "", uint frameIdx = 0, int maxRows = int.MaxValue)
        {
            // Validate input parameters
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (width <= 0 || height <= 0)
                throw new ArgumentException($"Invalid dimensions: {width}x{height}");
            if (stride <= 0 || stride < width * 3)
                throw new ArgumentException($"Invalid stride {stride} for width {width} (expected at least {width * 3})");
            var size = width * height;
            var result = new Picture16bpp(width, height)
            {
                Tag = string.IsNullOrWhiteSpace(filePath)
                    ? (frameIdx == 0 ? null : $"frame #{frameIdx}")
                    : $"{filePath} frame #{frameIdx}",
                r = new ushort[size],
                g = new ushort[size],
                b = new ushort[size],
                HasAlphaChannel = hasAlpha,
                a = hasAlpha ? AllocateFilledAlphaArray(size) : null,
            };
            int validRows = Math.Min(height, maxRows);
            int idx, baseIndex, offset, x, y;
            byte* srcRow;
            for (y = 0; y < validRows; y++)
            {
                srcRow = data + y * stride;
                baseIndex = y * width;
                for (x = 0; x < width; x++)
                {
                    idx = baseIndex + x;
                    offset = x * 6;
                    if (offset + 5 >= stride) break;

                    result.b[idx] = (ushort)(srcRow[offset] | (srcRow[offset + 1] << 8));
                    result.g[idx] = (ushort)(srcRow[offset + 2] | (srcRow[offset + 3] << 8));
                    result.r[idx] = (ushort)(srcRow[offset + 4] | (srcRow[offset + 5] << 8));

                }
            }
            result.ProcessStack = new List<PictureProcessStack>
            {
                new PictureProcessStack
                {
                    OperationDisplayName = $"From video '{filePath}', frame #{frameIdx}",
                    Operator = typeof(DecoderContext16Bit),
                    ProcessingFuncStackTrace =
#if DEBUG
                        new StackTrace(true)
#else
                        null
#endif
                    ,
                }
            };
            return result;
        }

        private static float[] AllocateFilledAlphaArray(int size)
        {
            var arr = new float[size];
            Array.Fill(arr, 1f);
            return arr;
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

                _diskCache?.Dispose();
            }
            else
            {
                Disposed = true;
            }

            if (_rgbBuffer != null) { ffmpeg.av_free(_rgbBuffer); _rgbBuffer = null; }
            if (_rgb != null) { AVFrame* tmp = _rgb; _rgb = null; ffmpeg.av_frame_free(&tmp); }
            if (_frm != null) { AVFrame* tmp = _frm; _frm = null; ffmpeg.av_frame_free(&tmp); }
            if (_pkt != null) { AVPacket* tmp = _pkt; _pkt = null; ffmpeg.av_packet_free(&tmp); }
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            if (_codec != null) { AVCodecContext* tmp = _codec; _codec = null; ffmpeg.avcodec_free_context(&tmp); }
            if (_fmt != null) { AVFormatContext* tmp = _fmt; _fmt = null; ffmpeg.avformat_close_input(&tmp); }
        }

        ~DecoderContext16Bit()
        {
            Dispose(disposing: false);
        }
    }

    public sealed unsafe class HDRDecoderContext : IVideoSource<ushort>
    {
        private const float DefaultSdrMaximumBrightness = 100f;
        private const float DefaultHdrMaximumBrightness = 1000f;
        private const float PqReferencePeakNits = 10000f;
        private const float ChannelMaxValue = 65535f;

        private readonly string _path;
        private AVFormatContext* _fmt = null;
        private AVCodecContext* _codec = null;
        private long _totalFrames;
        private SwsContext* _sws = null;
        private AVPacket* _pkt = null;
        private AVFrame* _frm = null;
        private AVFrame* _rgb = null;
        private byte* _rgbBuffer = null;

        private int _videoStreamIndex = -1;
        private int _width = -1;
        private int _height = -1;
        private double _fps = -1.0;
        private int _currentFrameNumber = 0;
        private bool _eof = false;
        private bool flushSent = false;

        private readonly VideoFrameDiskCache _diskCache;

        public bool Disposed { get; private set; }
        public bool Initialized { get; private set; } = false;

        public long TotalFrames => _totalFrames;

        public double Fps => _fps;

        public int Width => _width;

        public int Height => _height;

        public uint Index { get; set; } = 0;

        public string[] PreferredExtension => [".mkv", ".mp4", ".mov"];
        public int? ResultBitPerPixel => 16;
        public string TypeName => "HDRDecoderContext";


        public bool EnableLock { get; set; } = true;
        public bool StrictMode { get; set; }
        
        
        private Lock locker = new();

        public HDRDecoderContext(string path)
        {
            _path = path;
            Initialize();
            if (!string.IsNullOrWhiteSpace(path) && IVideoSource.EnableDiskCache) _diskCache = new VideoFrameDiskCache(_path);
        }

        public IVideoSource CreateNew(string newSource) => new HDRDecoderContext(newSource);


        public void Initialize()
        {
            if (_path is null || Initialized) return; //VideoSourceCreator needs a instance to get PreferredExtension

            try
            {
                _fmt = ffmpeg.avformat_alloc_context();
                if (_fmt == null) throw new InvalidOperationException("Failed to alloc a context for the Renderer. Please try reboot your device, or reinstall projectFrameCut.");


                fixed (AVFormatContext** fmtPtr = &_fmt)
                {
                    int averr = ffmpeg.avformat_open_input(fmtPtr, _path, null, null);
                    if (averr != 0)
                    {
                        FFmpegHelper.DetectWhyCannotOpenVideo(_path, averr);
                    }
                }

                if (ffmpeg.avformat_find_stream_info(_fmt, null) != 0)
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
                _rgb = ffmpeg.av_frame_alloc();
                if (_pkt == null || _frm == null || _rgb == null)
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

                _sws = ffmpeg.sws_getContext(
                        _width, _height, _codec->pix_fmt,
                        _width, _height, AVPixelFormat.AV_PIX_FMT_BGR48LE,
                        4 /* SWS_BICUBIC == 4*/, null, null, null);

                if (_sws == null)
                    throw new InvalidOperationException("Failed to alloc a context for the Renderer. Please try reboot your device, or reinstall projectFrameCut.");

                int bufferSize = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGR48LE, _width, _height, 32);
                if (bufferSize <= 0) throw new OutOfMemoryException($"Failed to allocate enough memory space to process the video '{_path}'. Try closing other programs, restarting your device, reinstall projectFrameCut, increasing page file size (on Windows platforms)/swapping files (on Linux platforms), or adding more RAM on your device if possible.");

                _rgbBuffer = (byte*)ffmpeg.av_malloc((ulong)bufferSize);
                if (_rgbBuffer == null)
                    throw new OutOfMemoryException($"Failed to allocate enough memory space to process the video '{_path}'. Try closing other programs, restarting your device, reinstall projectFrameCut, increasing page file size (on Windows platforms)/swapping files (on Linux platforms), or adding more RAM on your device if possible.");

                byte_ptrArray4 tmpData = default;
                int_array4 tmpLinesize = default;
                int fillRet = ffmpeg.av_image_fill_arrays(
                    ref tmpData, ref tmpLinesize,
                    _rgbBuffer, AVPixelFormat.AV_PIX_FMT_BGR48LE,
                    _width, _height, 32);
                if (fillRet < 0) throw new InvalidOperationException("av_image_fill_arrays failed.");

                for (uint i = 0; i < 4; i++)
                {
                    _rgb->data[i] = tmpData[i];
                    _rgb->linesize[i] = tmpLinesize[i];
                }
                _rgb->format = (int)AVPixelFormat.AV_PIX_FMT_BGR48LE;
                _rgb->width = _width;
                _rgb->height = _height;

                _currentFrameNumber = 0;
                Initialized = true;

                Log($"[VideoDecoder] Successfully initialized HDR decoder for {_path}");
            }
            catch (Exception ex)
            {
                Dispose();
                Log(ex, "Init HDRDecoderContext", this);
                throw;
            }
        }


        private void EnsureDecoderReady(uint targetFrame)
        {
            if (Disposed)
                throw new ObjectDisposedException(nameof(HDRDecoderContext), $"Decoder for '{_path}' is already disposed when trying to read frame {targetFrame}.");
            if (!Initialized)
                throw new InvalidOperationException($"Decoder for '{_path}' is not initialized when trying to read frame {targetFrame}.");
            if (_videoStreamIndex < 0 || _width <= 0 || _height <= 0)
                throw new InvalidDataException($"Decoder metadata is invalid for '{_path}' when trying to read frame {targetFrame}.");
            if (IsPointerAddressesNotValid(_fmt) || IsPointerAddressesNotValid(_codec) || IsPointerAddressesNotValid(_pkt) ||
                IsPointerAddressesNotValid(_frm) || IsPointerAddressesNotValid(_rgb) || IsPointerAddressesNotValid(_sws) || _rgbBuffer == null)
                throw new InvalidDataException($"Decoder native state is invalid for '{_path}' when trying to read frame {targetFrame}.");
            if (_rgb->data[0] == null || _rgb->linesize[0] <= 0)
                throw new InvalidDataException($"Decoder RGB buffer is invalid for '{_path}' when trying to read frame {targetFrame}.");
        }

        public IPicture<ushort> GetFrame(uint targetFrame, bool hasAlpha = false) => GetHDRFrame(targetFrame, hasAlpha).DegradeToSDR();

        public IPicture<ushort> GetFrame(uint targetFrame, int sourceX, int sourceY, int sourceWidth, int sourceHeight,
            int targetWidth, int targetHeight, bool hasAlpha = false)
            => GetHDRFrame(targetFrame, sourceX, sourceY, sourceWidth, sourceHeight,
                targetWidth, targetHeight, hasAlpha).DegradeToSDR();

        public IPicture<ushort> GetFrame(uint targetFrame, int targetWidth, int targetHeight, bool hasAlpha = false)
            => GetFrame(targetFrame, 0, 0, _width, _height, targetWidth, targetHeight, hasAlpha);

        public HDRPicture16bpp GetHDRFrame(uint targetFrame, bool hasAlpha = false)
            => GetHDRFrameCore(targetFrame, hasAlpha, null);

        public HDRPicture16bpp GetHDRFrame(uint targetFrame, int sourceX, int sourceY, int sourceWidth, int sourceHeight,
            int targetWidth, int targetHeight, bool hasAlpha = false)
            => GetHDRFrameCore(targetFrame, hasAlpha,
                new VideoFrameRegion(sourceX, sourceY, sourceWidth, sourceHeight, targetWidth, targetHeight));

        public HDRPicture16bpp GetHDRFrame(uint targetFrame, int targetWidth, int targetHeight, bool hasAlpha = false)
            => GetHDRFrame(targetFrame, 0, 0, _width, _height, targetWidth, targetHeight, hasAlpha);

        private HDRPicture16bpp GetHDRFrameCore(uint targetFrame, bool hasAlpha, VideoFrameRegion? region)
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
                    throw new ObjectDisposedException(nameof(HDRDecoderContext), $"Decoder for '{_path}' was disposed while waiting for lock (frame {targetFrame}).");

                EnsureDecoderReady(targetFrame);

                // Try disk cache before decoding
                if (region is null && IVideoSource.EnableDiskCache && _diskCache.TryLoadHDR(targetFrame, out var diskHDRFrame))
                {
                    Index++;
                    return diskHDRFrame;
                }

                if (targetFrame < _currentFrameNumber)
                {
                    SmartSeekTo(targetFrame);
                }

                bool frameFound = false;
                int decodedFrameNumber = _currentFrameNumber;
                while (true)
                {
                    if (!_eof)
                    {
                        int readRet = ffmpeg.av_read_frame(_fmt, _pkt);
                        if (readRet < 0)
                        {
                            _eof = true;
                            ffmpeg.av_packet_unref(_pkt);
                        }
                        else
                        {
                            try
                            {
                                if (_pkt->stream_index == _videoStreamIndex)
                                {
                                    int sendRet = ffmpeg.avcodec_send_packet(_codec, _pkt);
                                    if (sendRet < 0 && sendRet != ffmpeg.AVERROR(ffmpeg.EAGAIN) && sendRet != ffmpeg.AVERROR_EOF)
                                        throw new InvalidDataException($"Decoder failed to send packet for '{_path}' (code {sendRet}).");
                                }
                            }
                            finally
                            {
                                ffmpeg.av_packet_unref(_pkt);
                            }
                        }
                    }
                    else if (!flushSent)
                    {
                        int flushRet = ffmpeg.avcodec_send_packet(_codec, null);
                        if (flushRet < 0 && flushRet != ffmpeg.AVERROR_EOF)
                            throw new InvalidDataException($"Decoder failed to flush packets for '{_path}' (code {flushRet}).");
                        flushSent = true;
                    }

                    while (true)
                    {
                        ffmpeg.av_frame_unref(_frm);
                        int receiveRet = ffmpeg.avcodec_receive_frame(_codec, _frm);
                        if (receiveRet == 0)
                        {
                            if (decodedFrameNumber == targetFrame)
                            {
                                frameFound = true;
                                break;
                            }

                            CacheDecodedFrame((uint)decodedFrameNumber, hasAlpha, targetFrame);
                            decodedFrameNumber++;
                            continue;
                        }

                        if (receiveRet == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveRet == ffmpeg.AVERROR_EOF)
                            break;

                        throw new InvalidDataException($"Decoder failed to receive frame for '{_path}' (code {receiveRet}).");
                    }

                    if (frameFound)
                        break;

                    if (_eof && flushSent)
                        break;

                    if (_totalFrames >= 0 && decodedFrameNumber > _totalFrames)
                        break;

                    // Overshoot detection: if seek landed too far forward, re-seek earlier
                    if (!frameFound && _currentFrameNumber > 0 && decodedFrameNumber > targetFrame + 60)
                    {
                        SmartSeekTo(Math.Max(0, targetFrame - 120));
                        decodedFrameNumber = _currentFrameNumber;
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

                Index++;
                float maximumBrightness = ResolveFrameMaximumBrightness(_frm);
                AVColorTransferCharacteristic transferCharacteristic = _frm->color_trc;

                if (region is VideoFrameRegion requestedRegion)
                {
                    return FFmpegFrameCropScaler.Scale(
                        _frm,
                        requestedRegion.SourceX, requestedRegion.SourceY,
                        requestedRegion.SourceWidth, requestedRegion.SourceHeight,
                        requestedRegion.TargetWidth, requestedRegion.TargetHeight,
                        AVPixelFormat.AV_PIX_FMT_BGR48LE,
                        (data, stride, width, height) => PixelsToHDRPicture(
                            data, stride, width, height, hasAlpha, _path, targetFrame,
                            transferCharacteristic, maximumBrightness, height));
                }

                int scaledRows = ffmpeg.sws_scale(
                    _sws,
                    _frm->data,
                    _frm->linesize,
                    0,
                    _height,
                    _rgb->data,
                    _rgb->linesize
                );
                if (scaledRows <= 0)
                    throw new InvalidDataException($"Decoder failed to convert frame for '{_path}' (sws_scale returned {scaledRows}).");
                if (scaledRows < _height)
                    Log($"[VideoDecoder] sws_scale only processed {scaledRows}/{_height} rows for HDR '{_path}' frame {targetFrame}.", "warning");

                var picture = PixelsToHDRPicture(_rgb->data[0], _rgb->linesize[0], _width, _height, hasAlpha, _path, targetFrame, transferCharacteristic, maximumBrightness, scaledRows);
                CacheFinalFrame(targetFrame, picture);
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
            if (_fps <= 0 || _fmt == null || _videoStreamIndex < 0)
            {
                ffmpeg.av_seek_frame(_fmt, _videoStreamIndex, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
                ffmpeg.avcodec_flush_buffers(_codec);
                _currentFrameNumber = 0;
                _eof = false;
                flushSent = false;
                return;
            }

            var timeBase = _fmt->streams[_videoStreamIndex]->time_base;
            double timeBaseSeconds = ffmpeg.av_q2d(timeBase);
            if (timeBaseSeconds <= 0)
            {
                ffmpeg.av_seek_frame(_fmt, _videoStreamIndex, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
                ffmpeg.avcodec_flush_buffers(_codec);
                _currentFrameNumber = 0;
                _eof = false;
                flushSent = false;
                return;
            }

            double targetTimeSeconds = targetFrame / _fps;
            double seekTimeSeconds = Math.Max(0, targetTimeSeconds - 0.5);
            long seekTimestamp = (long)(seekTimeSeconds / timeBaseSeconds);

            int seekRet = ffmpeg.av_seek_frame(_fmt, _videoStreamIndex, seekTimestamp, ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (seekRet < 0)
            {
                seekRet = ffmpeg.av_seek_frame(_fmt, _videoStreamIndex, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
                if (seekRet < 0)
                {
                    var msg = $"Failed to seek decoder for '{_path}' (code {seekRet}).";
                    if (StrictMode)
                        throw new InvalidOperationException(msg);
                    Log(msg, "warning");
                    throw new InvalidOperationException(msg);
                }
                _currentFrameNumber = 0;
            }
            else
            {
                _currentFrameNumber = Math.Max(0, (int)(seekTimeSeconds * _fps) - 60);
            }

            ffmpeg.avcodec_flush_buffers(_codec);
            _eof = false;
            flushSent = false;
        }

        private void CacheDecodedFrame(uint frameNumber, bool hasAlpha, uint targetFrame)
        {
            if (!IVideoSource.EnableDiskCache)
                return;

            float maximumBrightness = ResolveFrameMaximumBrightness(_frm);
            AVColorTransferCharacteristic transferCharacteristic = _frm->color_trc;

            ffmpeg.sws_scale(
                _sws,
                _frm->data,
                _frm->linesize,
                0,
                _height,
                _rgb->data,
                _rgb->linesize);

            var picture = PixelsToHDRPicture(_rgb->data[0], _rgb->linesize[0], _width, _height, hasAlpha, _path, frameNumber, transferCharacteristic, maximumBrightness, _height);
            CacheFinalFrame(frameNumber, picture);
        }

        private void CacheFinalFrame(uint frameNumber, HDRPicture16bpp picture)
        {
            if (!IVideoSource.EnableDiskCache)
                return;

            _diskCache.SaveHDRFrameAsync(frameNumber, picture);
        }


        private static float ResolveFrameMaximumBrightness(AVFrame* frame)
        {
            float maximumBrightness = DefaultSdrMaximumBrightness;
            if (frame == null)
                return maximumBrightness;

            AVFrameSideData* masteringSideData = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA);
            if (masteringSideData != null && masteringSideData->data != null)
            {
                AVMasteringDisplayMetadata* mastering = (AVMasteringDisplayMetadata*)masteringSideData->data;
                if (mastering->has_luminance != 0)
                {
                    float masteringMaxLuminance = RationalToFloat(mastering->max_luminance);
                    if (masteringMaxLuminance > 0f && float.IsFinite(masteringMaxLuminance))
                    {
                        maximumBrightness = MathF.Max(maximumBrightness, masteringMaxLuminance);
                    }
                }
            }

            AVFrameSideData* contentLightSideData = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL);
            if (contentLightSideData != null && contentLightSideData->data != null)
            {
                AVContentLightMetadata* contentLight = (AVContentLightMetadata*)contentLightSideData->data;
                if (contentLight->MaxCLL > 0)
                {
                    maximumBrightness = MathF.Max(maximumBrightness, (float)contentLight->MaxCLL);
                }
            }

            if (maximumBrightness <= DefaultSdrMaximumBrightness && IsHdrTransfer(frame->color_trc))
            {
                maximumBrightness = DefaultHdrMaximumBrightness;
            }

            return maximumBrightness;
        }

        private static float RationalToFloat(AVRational value)
        {
            if (value.den == 0) return 0f;
            return (float)value.num / value.den;
        }

        private static bool IsHdrTransfer(AVColorTransferCharacteristic transferCharacteristic)
        {
            return transferCharacteristic == AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084
                || transferCharacteristic == AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67;
        }

        private static float DecodePqSignal(float signal)
        {
            const float m1 = 2610f / 16384f;
            const float m2 = 2523f / 32f;
            const float c1 = 3424f / 4096f;
            const float c2 = 2413f / 128f;
            const float c3 = 2392f / 128f;

            signal = Math.Clamp(signal, 0f, 1f);
            float p = MathF.Pow(signal, 1f / m2);
            float numerator = MathF.Max(p - c1, 0f);
            float denominator = c2 - c3 * p;
            if (denominator <= 0f)
                return 0f;

            float linear = MathF.Pow(numerator / denominator, 1f / m1);
            return Math.Clamp(linear, 0f, 1f);
        }

        private static float DecodeHlgSignal(float signal)
        {
            const float a = 0.17883277f;
            const float b = 0.28466892f;
            const float c = 0.55991073f;

            signal = Math.Clamp(signal, 0f, 1f);
            if (signal <= 0.5f)
            {
                return (signal * signal) / 3f;
            }

            return (MathF.Exp((signal - c) / a) + b) / 12f;
        }

        private static float ComputeBrightness(float r, float g, float b, AVColorTransferCharacteristic transferCharacteristic, float maximumBrightness)
        {
            if (!float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b))
                return 0f;

            float signalLuma = Math.Clamp(0.2627f * r + 0.6780f * g + 0.0593f * b, 0f, 1f);
            if (!float.IsFinite(signalLuma))
                return 0f;

            if (transferCharacteristic == AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084)
            {
                float normalizedPqLuminance = DecodePqSignal(signalLuma);
                float luminanceNits = normalizedPqLuminance * PqReferencePeakNits;
                if (maximumBrightness <= 0f || !float.IsFinite(maximumBrightness))
                {
                    return Math.Clamp(normalizedPqLuminance, 0f, 1f);
                }

                return Math.Clamp(luminanceNits / maximumBrightness, 0f, 1f);
            }

            if (transferCharacteristic == AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67)
            {
                float relativeHlgLuminance = DecodeHlgSignal(signalLuma);
                return Math.Clamp(relativeHlgLuminance, 0f, 1f);
            }

            return signalLuma;
        }

        [DebuggerNonUserCode()]
        private static HDRPicture16bpp PixelsToHDRPicture(byte* data, int stride, int width, int height, bool hasAlpha = false, string filePath = "", uint frameIdx = 0, AVColorTransferCharacteristic transferCharacteristic = AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED, float maximumBrightness = DefaultSdrMaximumBrightness, int maxRows = int.MaxValue)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (width <= 0 || height <= 0)
                throw new ArgumentException($"Invalid dimensions: {width}x{height}");
            if (stride <= 0 || stride < width * 6)
                throw new ArgumentException($"Invalid stride {stride} for width {width} (expected at least {width * 6})");

            int size = width * height;
            float maxBrightness = maximumBrightness > 0f && float.IsFinite(maximumBrightness)
                ? maximumBrightness
                : DefaultSdrMaximumBrightness;

            var result = new HDRPicture16bpp(width, height)
            {
                Tag = string.IsNullOrWhiteSpace(filePath)
                    ? (frameIdx == 0 ? null : $"frame #{frameIdx}")
                    : $"{filePath} frame #{frameIdx}",
                r = new ushort[size],
                g = new ushort[size],
                b = new ushort[size],
                Brightness = new float[size],
                MaximumBrightness = maxBrightness,
                HasAlphaChannel = hasAlpha,
                a = hasAlpha ? AllocateFilledAlphaArray(size) : null,
            };

            int validRows = Math.Min(height, maxRows);
            int idx, baseIndex, offset, x, y;
            byte* srcRow;
            for (y = 0; y < validRows; y++)
            {
                srcRow = data + y * stride;
                baseIndex = y * width;
                for (x = 0; x < width; x++)
                {
                    idx = baseIndex + x;
                    offset = x * 6;
                    if (offset + 5 >= stride) break;

                    ushort blue = (ushort)(srcRow[offset] | (srcRow[offset + 1] << 8));
                    ushort green = (ushort)(srcRow[offset + 2] | (srcRow[offset + 3] << 8));
                    ushort red = (ushort)(srcRow[offset + 4] | (srcRow[offset + 5] << 8));

                    result.b[idx] = blue;
                    result.g[idx] = green;
                    result.r[idx] = red;

                    float redSignal = red / ChannelMaxValue;
                    float greenSignal = green / ChannelMaxValue;
                    float blueSignal = blue / ChannelMaxValue;
                    result.Brightness[idx] = ComputeBrightness(redSignal, greenSignal, blueSignal, transferCharacteristic, result.MaximumBrightness);
                }
            }

            result.ProcessStack = new List<PictureProcessStack>
            {
                new PictureProcessStack
                {
                    OperationDisplayName = $"From HDR video '{filePath}', frame #{frameIdx}",
                    Operator = typeof(HDRDecoderContext),
                    ProcessingFuncStackTrace =
#if DEBUG
                        new StackTrace(true)
#else
                        null
#endif
                    ,
                    Properties = new Dictionary<string, object>
                    {
                        { "TransferCharacteristic", transferCharacteristic.ToString() },
                        { "MaximumBrightness", result.MaximumBrightness },
                    }
                }
            };
            return result;
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

                _diskCache?.Dispose();
            }
            else
            {
                Disposed = true;
            }

            if (_rgbBuffer != null) { ffmpeg.av_free(_rgbBuffer); _rgbBuffer = null; }
            if (_rgb != null) { AVFrame* tmp = _rgb; _rgb = null; ffmpeg.av_frame_free(&tmp); }
            if (_frm != null) { AVFrame* tmp = _frm; _frm = null; ffmpeg.av_frame_free(&tmp); }
            if (_pkt != null) { AVPacket* tmp = _pkt; _pkt = null; ffmpeg.av_packet_free(&tmp); }
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            if (_codec != null) { AVCodecContext* tmp = _codec; _codec = null; ffmpeg.avcodec_free_context(&tmp); }
            if (_fmt != null) { AVFormatContext* tmp = _fmt; _fmt = null; ffmpeg.avformat_close_input(&tmp); }
        }

        /// <summary>
        /// Allocates a float array of the specified size, filled entirely with 1.0f.
        /// Replaces Enumerable.Repeat(1f, size).ToArray() to avoid LINQ overhead.
        /// </summary>
        private static float[] AllocateFilledAlphaArray(int size)
        {
            var arr = new float[size];
            Array.Fill(arr, 1f);
            return arr;
        }

        ~HDRDecoderContext()
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
                        if (IsHdrTransfer(par->color_trc))
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

    public sealed unsafe class DecoderContext8Bit : IVideoSource<byte>
    {

        private readonly string _path;
        private AVFormatContext* _fmt = null;
        private AVCodecContext* _codec = null;
        private long _totalFrames;
        private SwsContext* _sws = null;
        private AVPacket* _pkt = null;
        private AVFrame* _frm = null;
        private AVFrame* _rgb = null;
        private byte* _rgbBuffer = null;
        private bool _eof = false;

        private int _videoStreamIndex = -1;
        private int _width = -1;
        private int _height = -1;
        private double _fps = 0.0;
        private int _currentFrameNumber = 0;
        private bool flushSent = false;

        private readonly VideoFrameDiskCache _diskCache;

        public bool Disposed { get; private set; }
        public bool Initialized { get; private set; } = false;

        public long TotalFrames => _totalFrames;

        public double Fps => _fps;

        public int Width => _width;

        public int Height => _height;

        public uint Index { get; set; } = 0;
        public string[] PreferredExtension => [".mp4", ".mov"];
        public string TypeName => "DecoderContext8Bit";


        public int? ResultBitPerPixel => 8;

        public bool EnableLock { get; set; } = true;
        public bool StrictMode { get; set; }
        
        
        private Lock locker = new();

        public DecoderContext8Bit(string path)
        {
            _path = path;
            Initialize();
            if (!string.IsNullOrWhiteSpace(path) && IVideoSource.EnableDiskCache) _diskCache = new VideoFrameDiskCache(_path);
        }

        public IVideoSource CreateNew(string newSource) => new DecoderContext8Bit(newSource);


        public void Initialize()
        {
            if (_path is null || Initialized) return; //VideoSourceCreator needs a instance to get PreferredExtension

            try
            {
                _fmt = ffmpeg.avformat_alloc_context();
                if (_fmt == null) throw new InvalidOperationException("Failed to alloc a context for the Renderer. Please try reboot your device, or reinstall projectFrameCut.");


                fixed (AVFormatContext** fmtPtr = &_fmt)
                {
                    var averr = ffmpeg.avformat_open_input(fmtPtr, _path, null, null);
                    if (averr != 0)
                    {
                        FFmpegHelper.DetectWhyCannotOpenVideo(_path, averr);

                    }
                }

                if (ffmpeg.avformat_find_stream_info(_fmt, null) != 0)
                    throw new InvalidDataException($"File '{_path}' seems don't like a multimedia file.Try install the encoder extension. If you continuously encountering this issue, try install ffmpeg toolkit on your computer, then run this command and observe whether there is any error message:\r\nffprobe {Path.GetFullPath(_path)}");

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
                    throw new NotSupportedException("No suitable decoder found. Try install the codec extension or encode your video again to another format.");

                _codec = ffmpeg.avcodec_alloc_context3(codec);
                if (_codec == null) throw new InvalidOperationException("Failed to alloc a context for the Renderer. Please try reboot your device, or reinstall projectFrameCut.");

                ffmpeg.avcodec_parameters_to_context(_codec, par);
                if (ffmpeg.avcodec_open2(_codec, codec, null) < 0)
                    throw new NotSupportedException("Failed to open decoder. Please reinstall projectFrameCut.");

                _pkt = ffmpeg.av_packet_alloc();
                _frm = ffmpeg.av_frame_alloc();
                _rgb = ffmpeg.av_frame_alloc();
                if (_pkt == null || _frm == null || _rgb == null)
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


                _sws = ffmpeg.sws_getContext(
                    _width, _height, _codec->pix_fmt,
                    _width, _height, AVPixelFormat.AV_PIX_FMT_BGR24,
                    4/* SWS_BICUBIC == 4*/, null, null, null);


                if (_sws == null)
                    throw new InvalidOperationException("Failed to alloc a context for the Renderer. Please try reboot your device, or reinstall projectFrameCut.");

                int bufferSize = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGR24, _width, _height, 32);
                if (bufferSize <= 0) throw new OutOfMemoryException($"Failed to allocate enough memory space to process the video '{_path}'. Try closing other programs, restarting your device, reinstall projectFrameCut, increasing page file size (on Windows platforms)/swapping files (on Linux platforms), or adding more RAM on your device if possible.");

                _rgbBuffer = (byte*)ffmpeg.av_malloc((ulong)bufferSize);
                if (_rgbBuffer == null) throw new OutOfMemoryException($"Failed to allocate enough memory space to process the video '{_path}'. Try closing other programs, restarting your device, reinstall projectFrameCut, increasing page file size (on Windows platforms)/swapping files (on Linux platforms), or adding more RAM on your device if possible.");

                byte_ptrArray4 tmpData = default;
                int_array4 tmpLinesize = default;

                int fillRet = ffmpeg.av_image_fill_arrays(
                    ref tmpData,
                    ref tmpLinesize,
                    _rgbBuffer,
                    AVPixelFormat.AV_PIX_FMT_BGR24,
                    _width,
                    _height,
                    32);
                if (fillRet < 0) throw new InvalidOperationException("av_image_fill_arrays failed.");

                for (uint i = 0; i < 4; i++)
                {
                    _rgb->data[i] = tmpData[i];
                    _rgb->linesize[i] = tmpLinesize[i];
                }

                _rgb->format = (int)AVPixelFormat.AV_PIX_FMT_BGR24;
                _rgb->width = _width;
                _rgb->height = _height;

                _currentFrameNumber = 0;
                _eof = false;
                Initialized = true;

                Log($"[VideoDecoder] Successfully initialized decoder for {_path}");
            }
            catch (Exception ex)
            {
                Log(ex, "Init VideoDecoder", this);
                Dispose();
                throw;
            }
        }


        private void EnsureDecoderReady(uint targetFrame)
        {
            if (Disposed)
                throw new ObjectDisposedException(nameof(DecoderContext8Bit), $"Decoder for '{_path}' is already disposed when trying to read frame {targetFrame}.");
            if (!Initialized)
                throw new InvalidOperationException($"Decoder for '{_path}' is not initialized when trying to read frame {targetFrame}.");
            if (_videoStreamIndex < 0 || _width <= 0 || _height <= 0)
                throw new InvalidDataException($"Decoder metadata is invalid for '{_path}' when trying to read frame {targetFrame}.");
            if (IsPointerAddressesNotValid(_fmt) || IsPointerAddressesNotValid(_codec) || IsPointerAddressesNotValid(_pkt) ||
                IsPointerAddressesNotValid(_frm) || IsPointerAddressesNotValid(_rgb) || IsPointerAddressesNotValid(_sws) || _rgbBuffer == null)
                throw new InvalidDataException($"Decoder native state is invalid for '{_path}' when trying to read frame {targetFrame}.");
            if (_rgb->data[0] == null || _rgb->linesize[0] <= 0)
                throw new InvalidDataException($"Decoder RGB buffer is invalid for '{_path}' when trying to read frame {targetFrame}.");
        }



        [DebuggerNonUserCode()]
        public IPicture<byte> GetFrame(uint targetFrame, bool hasAlpha)
            => GetFrameCore(targetFrame, hasAlpha, null);

        public IPicture<byte> GetFrame(uint targetFrame, int sourceX, int sourceY, int sourceWidth, int sourceHeight,
            int targetWidth, int targetHeight, bool hasAlpha = false)
            => GetFrameCore(targetFrame, hasAlpha,
                new VideoFrameRegion(sourceX, sourceY, sourceWidth, sourceHeight, targetWidth, targetHeight));

        public IPicture<byte> GetFrame(uint targetFrame, int targetWidth, int targetHeight, bool hasAlpha = false)
            => GetFrame(targetFrame, 0, 0, _width, _height, targetWidth, targetHeight, hasAlpha);

        private IPicture<byte> GetFrameCore(uint targetFrame, bool hasAlpha, VideoFrameRegion? region)
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
                    throw new ObjectDisposedException(nameof(DecoderContext8Bit), $"Decoder for '{_path}' was disposed while waiting for lock (frame {targetFrame}).");

                EnsureDecoderReady(targetFrame);

                // Try disk cache before decoding
                if (region is null && IVideoSource.EnableDiskCache && _diskCache.TryLoad8bpp(targetFrame, out var diskFrame))
                {
                    Index++;
                    return diskFrame;
                }

                if (targetFrame < _currentFrameNumber)
                {
                    SmartSeekTo(targetFrame);
                }

                bool frameFound = false;
                int decodedFrameNumber = _currentFrameNumber;
                while (true)
                {
                    if (!_eof)
                    {
                        int readRet = ffmpeg.av_read_frame(_fmt, _pkt);
                        if (readRet < 0)
                        {
                            _eof = true;
                            ffmpeg.av_packet_unref(_pkt);
                        }
                        else
                        {
                            try
                            {
                                if (_pkt->stream_index == _videoStreamIndex)
                                {
                                    int sendRet = ffmpeg.avcodec_send_packet(_codec, _pkt);
                                    if (sendRet < 0 && sendRet != ffmpeg.AVERROR(ffmpeg.EAGAIN) && sendRet != ffmpeg.AVERROR_EOF)
                                        throw new InvalidDataException($"Decoder failed to send packet for '{_path}' (code {sendRet}).");
                                }
                            }
                            finally
                            {
                                ffmpeg.av_packet_unref(_pkt);
                            }
                        }
                    }
                    else if (!flushSent)
                    {
                        int flushRet = ffmpeg.avcodec_send_packet(_codec, null);
                        if (flushRet < 0 && flushRet != ffmpeg.AVERROR_EOF)
                            throw new InvalidDataException($"Decoder failed to flush packets for '{_path}' (code {flushRet}).");
                        flushSent = true;
                    }

                    while (true)
                    {
                        ffmpeg.av_frame_unref(_frm);
                        int receiveRet = ffmpeg.avcodec_receive_frame(_codec, _frm);
                        if (receiveRet == 0)
                        {
                            if (decodedFrameNumber == targetFrame)
                            {
                                frameFound = true;
                                break;
                            }

                            // Cache intermediate frames during forward decode
                            CacheDecodedFrame((uint)decodedFrameNumber, hasAlpha, targetFrame);
                            decodedFrameNumber++;
                            continue;
                        }

                        if (receiveRet == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveRet == ffmpeg.AVERROR_EOF)
                            break;

                        throw new InvalidDataException($"Decoder failed to receive frame for '{_path}' (code {receiveRet}).");
                    }

                    if (frameFound)
                        break;

                    if (_eof && flushSent)
                        break;

                    if (_totalFrames >= 0 && decodedFrameNumber > _totalFrames)
                        break;

                    // Overshoot detection: if seek landed too far forward, re-seek earlier
                    if (!frameFound && _currentFrameNumber > 0 && decodedFrameNumber > targetFrame + 60)
                    {
                        SmartSeekTo(Math.Max(0, targetFrame - 120));
                        decodedFrameNumber = _currentFrameNumber;
                        continue;
                    }
                }

                _currentFrameNumber = decodedFrameNumber + 1;

                if (!frameFound)
                {
                    if (_totalFrames > 0 && targetFrame > 0 && Math.Abs((long)targetFrame - _totalFrames) < 5)
                    {
                        Log($"[VideoDecoder] Frame {targetFrame} not found(may due to rounding), try getting frame {targetFrame - 1} instead.");
                        return GetFrameCore(targetFrame - 1, hasAlpha, region);
                    }

                    double fps = _fps > 0 ? _fps : 1.0;
                    double seconds = targetFrame / fps;
                    throw new OverflowException($"Frame #{targetFrame} (timespan {TimeSpan.FromSeconds(seconds)}) not exist in video '{_path}'.");
                }

                Index++;
                if (_frm->width != _width || _frm->height != _height)
                    Log($"[VideoDecoder] Frame dimensions mismatch in '{_path}': expected {_width}x{_height}, got {_frm->width}x{_frm->height}.", "warning");

                if (region is VideoFrameRegion requestedRegion)
                {
                    return FFmpegFrameCropScaler.Scale(
                        _frm,
                        requestedRegion.SourceX, requestedRegion.SourceY,
                        requestedRegion.SourceWidth, requestedRegion.SourceHeight,
                        requestedRegion.TargetWidth, requestedRegion.TargetHeight,
                        AVPixelFormat.AV_PIX_FMT_BGR24,
                        (data, stride, width, height) => PixelsToPicture(
                            data, stride, width, height, hasAlpha, _path, targetFrame, height));
                }

                int scaledRows = ffmpeg.sws_scale(
                    _sws,
                    _frm->data,
                    _frm->linesize,
                    0,
                    _height,
                    _rgb->data,
                    _rgb->linesize);
                if (scaledRows <= 0)
                    throw new InvalidDataException($"Decoder failed to convert frame for '{_path}' (sws_scale returned {scaledRows}).");
                if (scaledRows < _height)
                    Log($"[VideoDecoder] sws_scale only processed {scaledRows}/{_height} rows for '{_path}' frame {targetFrame}.", "warning");

                var picture = PixelsToPicture(_rgb->data[0], _rgb->linesize[0], _width, _height, hasAlpha, _path, targetFrame, scaledRows);
                CacheFinalFrame(targetFrame, picture);
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
            if (_fps <= 0 || _fmt == null || _videoStreamIndex < 0)
            {
                // Degrade to legacy seek-to-zero
                ffmpeg.av_seek_frame(_fmt, _videoStreamIndex, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
                ffmpeg.avcodec_flush_buffers(_codec);
                _currentFrameNumber = 0;
                _eof = false;
                flushSent = false;
                return;
            }

            var timeBase = _fmt->streams[_videoStreamIndex]->time_base;
            double timeBaseSeconds = ffmpeg.av_q2d(timeBase);
            if (timeBaseSeconds <= 0)
            {
                ffmpeg.av_seek_frame(_fmt, _videoStreamIndex, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
                ffmpeg.avcodec_flush_buffers(_codec);
                _currentFrameNumber = 0;
                _eof = false;
                flushSent = false;
                return;
            }

            // Seek to ~0.5s before target; FFmpeg lands on the nearest keyframe
            double targetTimeSeconds = targetFrame / _fps;
            double seekTimeSeconds = Math.Max(0, targetTimeSeconds - 0.5);
            long seekTimestamp = (long)(seekTimeSeconds / timeBaseSeconds);

            int seekRet = ffmpeg.av_seek_frame(_fmt, _videoStreamIndex, seekTimestamp, ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (seekRet < 0)
            {
                // Fallback: seek to beginning
                seekRet = ffmpeg.av_seek_frame(_fmt, _videoStreamIndex, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
                if (seekRet < 0)
                {
                    var msg = $"Failed to seek decoder for '{_path}' (code {seekRet}).";
                    if (StrictMode)
                        throw new InvalidOperationException(msg);
                    Log(msg, "warning");
                    throw new InvalidOperationException(msg);
                }
                _currentFrameNumber = 0;
            }
            else
            {
                // Conservative estimate: subtract a few frames to avoid overshooting
                _currentFrameNumber = Math.Max(0, (int)(seekTimeSeconds * _fps) - 60);
            }

            ffmpeg.avcodec_flush_buffers(_codec);
            _eof = false;
            flushSent = false;
        }

        private void CacheDecodedFrame(uint frameNumber, bool hasAlpha, uint targetFrame)
        {
            if (!IVideoSource.EnableDiskCache)
                return;

            // Convert and cache this intermediate frame
            ffmpeg.sws_scale(
                _sws,
                _frm->data,
                _frm->linesize,
                0,
                _height,
                _rgb->data,
                _rgb->linesize);

            var picture = PixelsToPicture(_rgb->data[0], _rgb->linesize[0], _width, _height, hasAlpha, _path, frameNumber, _height);
            CacheFinalFrame(frameNumber, picture);
        }

        private void CacheFinalFrame(uint frameNumber, Picture8bpp picture)
        {
            if (!IVideoSource.EnableDiskCache)
                return;

            _diskCache.Save8bppFrameAsync(frameNumber, picture);
        }

        //[DebuggerNonUserCode()]
        private static Picture8bpp PixelsToPicture(byte* data, int stride, int width, int height, bool hasAlpha = false, string filePath = "", uint frameIdx = 0, int maxRows = int.MaxValue)
        {
            // Validate input parameters
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (width <= 0 || height <= 0)
                throw new ArgumentException($"Invalid dimensions: {width}x{height}");
            if (stride <= 0 || stride < width * 3)
                throw new ArgumentException($"Invalid stride {stride} for width {width} (expected at least {width * 3})");
            var size = width * height;
            var result = new Picture8bpp(width, height)
            {
                Tag = string.IsNullOrWhiteSpace(filePath)
                    ? (frameIdx == 0 ? null : $"frame #{frameIdx}")
                    : $"{filePath} frame #{frameIdx}",
                r = new byte[size],
                g = new byte[size],
                b = new byte[size],
            };
            int validRows = Math.Min(height, maxRows);
            int idx, baseIndex, offset, x, y;
            byte* srcRow;
            for (y = 0; y < validRows; y++)
            {
                srcRow = data + y * stride;
                baseIndex = y * width;
                for (x = 0; x < width; x++)
                {
                    idx = baseIndex + x;
                    offset = x * 3;
                    if (offset + 2 >= stride) break;
                    result.r[idx] = srcRow[offset + 2];
                    result.g[idx] = srcRow[offset + 1];
                    result.b[idx] = srcRow[offset + 0];
                }
            }

            result.ProcessStack = new List<PictureProcessStack>
            {
                new PictureProcessStack
                {
                    OperationDisplayName = $"From video '{filePath}', frame #{frameIdx}",
                    Operator = typeof(DecoderContext8Bit),
                    ProcessingFuncStackTrace =
#if DEBUG
                        new StackTrace(true)
#else
                        null
#endif
                    ,
                }
            };
            return result;
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

                _diskCache?.Dispose();
            }
            else
            {
                Disposed = true;
            }

            if (_rgbBuffer != null) { ffmpeg.av_free(_rgbBuffer); _rgbBuffer = null; }
            if (_rgb != null) { AVFrame* tmp = _rgb; _rgb = null; ffmpeg.av_frame_free(&tmp); }
            if (_frm != null) { AVFrame* tmp = _frm; _frm = null; ffmpeg.av_frame_free(&tmp); }
            if (_pkt != null) { AVPacket* tmp = _pkt; _pkt = null; ffmpeg.av_packet_free(&tmp); }
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            if (_codec != null) { AVCodecContext* tmp = _codec; _codec = null; ffmpeg.avcodec_free_context(&tmp); }
            if (_fmt != null) { AVFormatContext* tmp = _fmt; _fmt = null; ffmpeg.avformat_close_input(&tmp); }
        }

        ~DecoderContext8Bit()
        {
            Dispose(disposing: false);
        }
    }
}



