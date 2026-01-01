using FFmpeg.AutoGen;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace projectFrameCut.Render.EncodeAndDecode
{
    public sealed unsafe class DecoderContextHW : IVideoSource
    {
        private readonly string _path;
        private AVFormatContext* _fmt = null;
        private AVCodecContext* _codec = null;
        private AVBufferRef* _hwDeviceCtx = null;
        private long _totalFrames;
        private SwsContext* _sws = null;
        private AVPacket* _pkt = null;
        private AVFrame* _frm = null;
        private AVFrame* _swFrame = null;
        private AVFrame* _rgb = null;
        private byte* _rgbBuffer = null;
        private bool _eof = false;

        private int _videoStreamIndex = -1;
        private int _width = -1;
        private int _height = -1;
        private double _fps = 0.0;
        private int _currentFrameNumber = 0;
        private bool flushSent = false;
        private AVPixelFormat _lastPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        private AVHWDeviceType _hwDeviceType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;

        public bool Disposed { get; private set; }
        public bool Initialized { get; private set; } = false;

        public long TotalFrames => _totalFrames;

        public double Fps => _fps;

        public int Width => _width;

        public int Height => _height;

        public uint Index { get; set; } = 0;
        public string[] PreferredExtension => [".mp4", ".mov", ".mkv"];

        public int? ResultBitPerPixel => 8;

        public bool EnableLock { get; set; } = false;
        private Lock locker = new();

        public DecoderContextHW(string path)
        {
            _path = path;
            Initialize();
        }

        public IVideoSource CreateNew(string newSource) => new DecoderContextHW(newSource);

        public void Initialize()
        {
            if (_path is null || Initialized) return;

            try
            {
                _fmt = ffmpeg.avformat_alloc_context();
                if (_fmt == null) throw new InvalidOperationException("Failed to alloc a context for the Renderer.");

                fixed (AVFormatContext** fmtPtr = &_fmt)
                {
                    if (ffmpeg.avformat_open_input(fmtPtr, _path, null, null) != 0)
                    {
                        var fi = new FileInfo(_path);
                        if (!fi.Exists)
                        {
                            throw new FileNotFoundException($"The video file '{_path}' doesn't exist.");
                        }
                        throw new FileNotFoundException($"Cannot open video file '{_path}'");
                    }
                }

                if (ffmpeg.avformat_find_stream_info(_fmt, null) != 0)
                    throw new InvalidDataException($"File '{_path}' seems don't like a multimedia file.");

                for (int i = 0; i < _fmt->nb_streams; i++)
                {
                    if (_fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        _videoStreamIndex = i;
                        break;
                    }
                }

                if (_videoStreamIndex < 0)
                    throw new InvalidDataException($"File '{_path}' seems don't like a video file.");

                AVCodecParameters* par = _fmt->streams[_videoStreamIndex]->codecpar;
                AVCodec* codec = ffmpeg.avcodec_find_decoder(par->codec_id);
                if (codec == null)
                    throw new NotSupportedException("No suitable decoder found.");

                _codec = ffmpeg.avcodec_alloc_context3(codec);
                if (_codec == null) throw new InvalidOperationException("Failed to alloc a context for the Renderer.");

                ffmpeg.avcodec_parameters_to_context(_codec, par);

                // Hardware Acceleration Init
                _hwDeviceType = GetBestHWDeviceType();
                if (_hwDeviceType != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
                {
                    AVBufferRef* hwDeviceCtx = null;
                    if (ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, _hwDeviceType, null, null, 0) >= 0)
                    {
                        _hwDeviceCtx = hwDeviceCtx;
                        _codec->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);
                    }
                }

                if (ffmpeg.avcodec_open2(_codec, codec, null) < 0)
                    throw new NotSupportedException("Failed to open decoder.");

                _pkt = ffmpeg.av_packet_alloc();
                _frm = ffmpeg.av_frame_alloc();
                _swFrame = ffmpeg.av_frame_alloc();
                _rgb = ffmpeg.av_frame_alloc();

                if (_pkt == null || _frm == null || _rgb == null || _swFrame == null)
                    throw new OutOfMemoryException($"Failed to allocate enough memory space.");

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

                int bufferSize = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGR24, _width, _height, 1);
                if (bufferSize <= 0) throw new OutOfMemoryException($"Failed to allocate enough memory space.");

                _rgbBuffer = (byte*)ffmpeg.av_malloc((ulong)bufferSize);
                if (_rgbBuffer == null) throw new OutOfMemoryException($"Failed to allocate enough memory space.");

                byte_ptrArray4 tmpData = default;
                int_array4 tmpLinesize = default;

                int fillRet = ffmpeg.av_image_fill_arrays(
                    ref tmpData,
                    ref tmpLinesize,
                    _rgbBuffer,
                    AVPixelFormat.AV_PIX_FMT_BGR24,
                    _width,
                    _height,
                    1);
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
            }
            catch (Exception)
            {
                Dispose();
                throw;
            }
            finally
            {
                Initialized = true;
            }
        }

        private AVHWDeviceType GetBestHWDeviceType()
        {
            var available = new List<AVHWDeviceType>();
            var type = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
            while ((type = ffmpeg.av_hwdevice_iterate_types(type)) != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
            {
                available.Add(type);
            }

            if (available.Contains(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA)) return AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA;
            if (available.Contains(AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2)) return AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2;
            if (available.Contains(AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA)) return AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA;
            if (available.Contains(AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI)) return AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI;
            if (available.Contains(AVHWDeviceType.AV_HWDEVICE_TYPE_QSV)) return AVHWDeviceType.AV_HWDEVICE_TYPE_QSV;
            
            return available.Count > 0 ? available[0] : AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
        }

        [DebuggerNonUserCode()]
        public IPicture GetFrame(uint targetFrame, bool hasAlpha)
        {
            if (EnableLock) locker.Enter();
            if (targetFrame < _currentFrameNumber)
            {
                ffmpeg.av_seek_frame(_fmt, _videoStreamIndex, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
                ffmpeg.avcodec_flush_buffers(_codec);
                _currentFrameNumber = 0;
                _eof = false;
                flushSent = false;
            }

            while (true)
            {
                if (!_eof)
                {
                    if (ffmpeg.av_read_frame(_fmt, _pkt) < 0)
                    {
                        _eof = true;
                        ffmpeg.av_packet_unref(_pkt);
                    }
                    else
                    {
                        if (_pkt->stream_index == _videoStreamIndex)
                        {
                            ffmpeg.avcodec_send_packet(_codec, _pkt);
                        }
                        ffmpeg.av_packet_unref(_pkt);
                    }
                }
                else if (!flushSent)
                {
                    ffmpeg.avcodec_send_packet(_codec, null);
                    flushSent = true;
                }

                while (true)
                {
                    ffmpeg.av_frame_unref(_frm);
                    int ret = ffmpeg.avcodec_receive_frame(_codec, _frm);
                    if (ret == 0)
                    {
                        if (_currentFrameNumber++ == targetFrame)
                        {
                            goto found;
                        }
                        continue;
                    }
                    else if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                    {
                        break;
                    }
                    else if (_totalFrames < _currentFrameNumber)
                    {
                        goto not_found;
                    }
                    break;
                }

                if (_eof && flushSent)
                    break;
            }

        not_found:
            if (EnableLock) locker.Exit();
            if (Math.Abs(targetFrame - TotalFrames) < 5)
            {
                return GetFrame(targetFrame - 1, hasAlpha);
            }
            double fps = _fps > 0 ? _fps : 1.0;
            double seconds = targetFrame / fps;
            throw new OverflowException($"Frame #{targetFrame} (timespan {TimeSpan.FromSeconds(seconds)}) not exist in video '{_path}'.");

        found:
            Index++;
            AVFrame* srcFrame = _frm;

            // Handle HW frame transfer
            if (IsHWFormat((AVPixelFormat)_frm->format))
            {
                ffmpeg.av_frame_unref(_swFrame);
                if (ffmpeg.av_hwframe_transfer_data(_swFrame, _frm, 0) >= 0)
                {
                    srcFrame = _swFrame;
                }
            }

            // Initialize or re-initialize SWS context if format changed
            if (_sws == null || _lastPixelFormat != (AVPixelFormat)srcFrame->format)
            {
                _lastPixelFormat = (AVPixelFormat)srcFrame->format;
                if (_sws != null) ffmpeg.sws_freeContext(_sws);
                
                _sws = ffmpeg.sws_getContext(
                    _width, _height, _lastPixelFormat,
                    _width, _height, AVPixelFormat.AV_PIX_FMT_BGR24,
                    4, null, null, null);
            }

            if (_sws != null)
            {
                ffmpeg.sws_scale(
                    _sws,
                    srcFrame->data,
                    srcFrame->linesize,
                    0,
                    _height,
                    _rgb->data,
                    _rgb->linesize);
            }

            if (EnableLock) locker.Exit();
            return PixelsToPicture(_rgb->data[0], _rgb->linesize[0], _width, _height, hasAlpha, _path, targetFrame);
        }

        private bool IsHWFormat(AVPixelFormat fmt)
        {
            return fmt == AVPixelFormat.AV_PIX_FMT_D3D11 ||
                   fmt == AVPixelFormat.AV_PIX_FMT_DXVA2_VLD ||
                   fmt == AVPixelFormat.AV_PIX_FMT_CUDA ||
                   fmt == AVPixelFormat.AV_PIX_FMT_VAAPI ||
                   fmt == AVPixelFormat.AV_PIX_FMT_QSV ||
                   fmt == AVPixelFormat.AV_PIX_FMT_VIDEOTOOLBOX ||
                   fmt == AVPixelFormat.AV_PIX_FMT_MEDIACODEC;
        }

        private static Picture8bpp PixelsToPicture(byte* data, int stride, int width, int height, bool hasAlpha = false, string filePath = "", uint frameIdx = 0)
        {
            var size = width * height;
            var result = new Picture8bpp(width, height)
            {
                r = new byte[size],
                g = new byte[size],
                b = new byte[size],
            };
            result.ProcessStack = $"From video '{filePath}', frame #{frameIdx} (HW)";
            int idx, baseIndex, offset, x, y;
            byte* srcRow;
            for (y = 0; y < height; y++)
            {
                srcRow = data + y * stride;
                baseIndex = y * width;
                for (x = 0; x < width; x++)
                {
                    idx = baseIndex + x;
                    offset = x * 3;
                    result.r[idx] = srcRow[offset + 2];
                    result.g[idx] = srcRow[offset + 1];
                    result.b[idx] = srcRow[offset + 0];
                }
            }

            return result;
        }

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;

            if (_rgbBuffer != null) { ffmpeg.av_free(_rgbBuffer); _rgbBuffer = null; }
            if (_rgb != null) { AVFrame* tmp = _rgb; _rgb = null; ffmpeg.av_frame_free(&tmp); }
            if (_swFrame != null) { AVFrame* tmp = _swFrame; _swFrame = null; ffmpeg.av_frame_free(&tmp); }
            if (_frm != null) { AVFrame* tmp = _frm; _frm = null; ffmpeg.av_frame_free(&tmp); }
            if (_pkt != null) { AVPacket* tmp = _pkt; _pkt = null; ffmpeg.av_packet_free(&tmp); }
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            if (_codec != null) { 
                if (_hwDeviceCtx != null) {
                    ffmpeg.av_buffer_unref(&_codec->hw_device_ctx);
                }
                AVCodecContext* tmp = _codec; _codec = null; ffmpeg.avcodec_free_context(&tmp); 
            }
            if (_hwDeviceCtx != null) { AVBufferRef* tmp = _hwDeviceCtx; _hwDeviceCtx = null; ffmpeg.av_buffer_unref(&tmp); }
            if (_fmt != null) { AVFormatContext* tmp = _fmt; _fmt = null; ffmpeg.avformat_close_input(&tmp); }
        }

        ~DecoderContextHW()
        {
            Dispose();
        }
    }
}
