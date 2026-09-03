using FFmpeg.AutoGen;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using static projectFrameCut.Shared.Logger;

namespace projectFrameCut.Render.EncodeAndDecode
{
    /// <summary>
    /// FFmpeg input-device based decoder.
    /// Source format: "{inputFormat}:{source}", e.g. "lavfi:testsrc=size=1280x720:rate=30".
    /// </summary>
    public sealed unsafe class FFmpegDeviceDecoderContext : IVideoSource<byte>
    {
        private readonly string _sourceSpec;
        private readonly string _inputFormatName;
        private readonly string _inputSource;

        private AVFormatContext* _fmt = null;
        private AVCodecContext* _codec = null;
        private long _totalFrames;
        private SwsContext* _sws = null;
        private AVPacket* _pkt = null;
        private AVFrame* _frm = null;
        private AVFrame* _rgb = null;
        private byte* _rgbBuffer = null;
        private bool _eof = false;
        private bool _flushSent = false;

        private int _videoStreamIndex = -1;
        private int _width = -1;
        private int _height = -1;
        private double _fps = 0.0;
        private int _currentFrameNumber = 0;

        public bool Disposed { get; private set; }
        public bool Initialized { get; private set; }

        public long TotalFrames => _totalFrames;
        public double Fps => _fps;
        public int Width => _width;
        public int Height => _height;
        public uint Index { get; set; } = 0;

        public string[] PreferredExtension => [];
        public string TypeName => "FFmpegDeviceDecoderContext";
        public int? ResultBitPerPixel => 8;

        public bool EnableLock { get; set; } = true;
        public bool StrictMode { get; set; }
        public bool EnableDiskCache { get; set; }

        private readonly Lock _locker = new();

        public FFmpegDeviceDecoderContext()
        {
            _sourceSpec = null!;
            _inputFormatName = null!;
            _inputSource = null!;
        }

        public FFmpegDeviceDecoderContext(string sourceSpec)
        {
            _sourceSpec = sourceSpec;
            (_inputFormatName, _inputSource) = ParseSourceSpec(sourceSpec);

            if (!string.IsNullOrWhiteSpace(sourceSpec))
            {
                Initialize();
            }
        }

        public IVideoSource CreateNew(string newSource) => new FFmpegDeviceDecoderContext(newSource);

        public void Initialize()
        {
            if (_sourceSpec is null || Initialized) return;

            AVDictionary* openOptions = null;
            try
            {
                _fmt = ffmpeg.avformat_alloc_context();
                if (_fmt == null) throw new InvalidOperationException("Failed to alloc a context for FFmpeg device decoder.");

                AVInputFormat* inputFormat = FFmpegHelper.FindInputFormatByName(_inputFormatName);
                if (inputFormat == null)
                {
                    var devices = FFmpegHelper.InputDeviceUtils.GetAllInputDevices();
                    throw new NotSupportedException($"FFmpeg input format '{_inputFormatName}' not found. Source: '{_sourceSpec}'.{Environment.NewLine}In this context these devices are available: {string.Join(", ", devices.Select(c => $"{c.Name}({c.Kind})"))}.");
                }

                fixed (AVFormatContext** fmtPtr = &_fmt)
                {
                    int openRet = ffmpeg.avformat_open_input(fmtPtr, _inputSource, inputFormat, &openOptions);
                    if (openRet < 0)
                    {
                        throw new InvalidDataException($"Failed to open FFmpeg input device '{_sourceSpec}' (code {openRet}, {FFmpegHelper.GetErrorString(openRet) ?? "unknown"}).");
                    }
                }

                if (ffmpeg.avformat_find_stream_info(_fmt, null) < 0)
                {
                    throw new InvalidDataException($"Failed to retrieve stream info from FFmpeg input device '{_sourceSpec}'.");
                }

                _videoStreamIndex = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
                if (_videoStreamIndex < 0)
                {
                    throw new InvalidDataException($"No video stream found in FFmpeg input device '{_sourceSpec}'.");
                }

                AVCodecParameters* par = _fmt->streams[_videoStreamIndex]->codecpar;
                AVCodec* codec = ffmpeg.avcodec_find_decoder(par->codec_id);
                if (codec == null)
                {
                    throw new NotSupportedException($"No suitable decoder found for '{_sourceSpec}'.");
                }

                _codec = ffmpeg.avcodec_alloc_context3(codec);
                if (_codec == null)
                {
                    throw new InvalidOperationException("Failed to alloc codec context for FFmpeg device decoder.");
                }

                ffmpeg.avcodec_parameters_to_context(_codec, par);
                if (ffmpeg.avcodec_open2(_codec, codec, null) < 0)
                {
                    throw new NotSupportedException($"Failed to open decoder for '{_sourceSpec}'.");
                }

                _pkt = ffmpeg.av_packet_alloc();
                _frm = ffmpeg.av_frame_alloc();
                _rgb = ffmpeg.av_frame_alloc();
                if (_pkt == null || _frm == null || _rgb == null)
                {
                    throw new OutOfMemoryException($"Failed to allocate memory for FFmpeg input device '{_sourceSpec}'.");
                }

                _width = _codec->width;
                _height = _codec->height;

                AVRational fr = _codec->framerate;
                if (fr.num == 0 || fr.den == 0)
                {
                    fr = _fmt->streams[_videoStreamIndex]->avg_frame_rate;
                }
                if (fr.num == 0 || fr.den == 0)
                {
                    fr = _fmt->streams[_videoStreamIndex]->r_frame_rate;
                }
                _fps = fr.den != 0 ? ffmpeg.av_q2d(fr) : 0.0;

                if (_width <= 0 || _height <= 0)
                {
                    throw new InvalidDataException($"Invalid video dimensions from '{_sourceSpec}'.");
                }

                long nbFrames = (long)_fmt->streams[_videoStreamIndex]->nb_frames;
                _totalFrames = nbFrames > 0 ? nbFrames : -1;

                _sws = ffmpeg.sws_getContext(
                    _width, _height, _codec->pix_fmt,
                    _width, _height, AVPixelFormat.AV_PIX_FMT_BGR24,
                    4, null, null, null);
                if (_sws == null)
                {
                    throw new InvalidOperationException("Failed to alloc sws context for FFmpeg device decoder.");
                }

                int bufferSize = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGR24, _width, _height, 1);
                if (bufferSize <= 0)
                {
                    throw new OutOfMemoryException("Failed to calculate RGB buffer size.");
                }

                _rgbBuffer = (byte*)ffmpeg.av_malloc((ulong)bufferSize);
                if (_rgbBuffer == null)
                {
                    throw new OutOfMemoryException("Failed to allocate RGB buffer.");
                }

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
                if (fillRet < 0)
                {
                    throw new InvalidOperationException("av_image_fill_arrays failed.");
                }

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
                _flushSent = false;

                Log($"[FFmpegDeviceDecoderContext] Successfully initialized input '{_sourceSpec}'.");
            }
            catch (Exception ex)
            {
                Log(ex, "Init FFmpegDeviceDecoderContext", this);
                Dispose();
                throw;
            }
            finally
            {
                if (openOptions != null)
                {
                    ffmpeg.av_dict_free(&openOptions);
                }
                Initialized = true;
            }
        }

        public IPicture<byte> GetFrame(uint targetFrame)
        {
            bool lockTaken = false;
            try
            {
                if (EnableLock)
                {
                    _locker.Enter();
                    lockTaken = true;
                }

                EnsureDecoderReady(targetFrame);

                if (targetFrame < _currentFrameNumber)
                {
                    int seekRet = ffmpeg.av_seek_frame(_fmt, _videoStreamIndex, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
                    if (seekRet < 0)
                    {
                        throw new NotSupportedException(
                            $"FFmpeg device source '{_sourceSpec}' does not support seek-backward (code {seekRet}). " +
                            $"Request frame #{targetFrame} while current frame is {_currentFrameNumber}." );
                    }
                    ffmpeg.avcodec_flush_buffers(_codec);
                    _currentFrameNumber = 0;
                    _eof = false;
                    _flushSent = false;
                }

                while (true)
                {
                    if (!_eof)
                    {
                        int readRet = ffmpeg.av_read_frame(_fmt, _pkt);
                        if (readRet < 0)
                        {
                            _eof = true;
                        }
                        else
                        {
                            if (_pkt->stream_index == _videoStreamIndex)
                            {
                                int sendRet = ffmpeg.avcodec_send_packet(_codec, _pkt);
                                if (sendRet < 0 && sendRet != ffmpeg.AVERROR(ffmpeg.EAGAIN) && sendRet != ffmpeg.AVERROR_EOF)
                                {
                                    throw new InvalidDataException($"Failed to send packet for '{_sourceSpec}' (code {sendRet}).");
                                }
                            }
                            ffmpeg.av_packet_unref(_pkt);
                        }
                    }
                    else if (!_flushSent)
                    {
                        int flushRet = ffmpeg.avcodec_send_packet(_codec, null);
                        if (flushRet < 0 && flushRet != ffmpeg.AVERROR_EOF)
                        {
                            throw new InvalidDataException($"Failed to flush decoder for '{_sourceSpec}' (code {flushRet}).");
                        }
                        _flushSent = true;
                    }

                    while (true)
                    {
                        ffmpeg.av_frame_unref(_frm);
                        int receiveRet = ffmpeg.avcodec_receive_frame(_codec, _frm);
                        if (receiveRet == 0)
                        {
                            if (_currentFrameNumber++ == targetFrame)
                            {
                                return ConvertCurrentFrame(targetFrame);
                            }
                            continue;
                        }

                        if (receiveRet == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                        {
                            break;
                        }
                        if (receiveRet == ffmpeg.AVERROR_EOF)
                        {
                            return HandleFrameNotFound(targetFrame);
                        }

                        throw new InvalidDataException($"Failed to receive frame from '{_sourceSpec}' (code {receiveRet}).");
                    }

                    if (_eof && _flushSent)
                    {
                        return HandleFrameNotFound(targetFrame);
                    }
                }
            }
            finally
            {
                if (lockTaken)
                {
                    _locker.Exit();
                }
            }
        }

        private void EnsureDecoderReady(uint targetFrame)
        {
            if (Disposed)
            {
                throw new ObjectDisposedException(nameof(FFmpegDeviceDecoderContext), $"Decoder for '{_sourceSpec}' is disposed when reading frame {targetFrame}.");
            }
            if (!Initialized)
            {
                throw new InvalidOperationException($"Decoder for '{_sourceSpec}' is not initialized when reading frame {targetFrame}.");
            }
            if (_fmt == null || _codec == null || _pkt == null || _frm == null || _rgb == null || _sws == null || _rgbBuffer == null)
            {
                throw new InvalidDataException($"Decoder native state is invalid for '{_sourceSpec}' when reading frame {targetFrame}.");
            }
            if (_videoStreamIndex < 0 || _width <= 0 || _height <= 0)
            {
                throw new InvalidDataException($"Decoder metadata is invalid for '{_sourceSpec}' when reading frame {targetFrame}.");
            }
        }

        private IPicture<byte> ConvertCurrentFrame(uint targetFrame)
        {
            int scaledRows = ffmpeg.sws_scale(
                _sws,
                _frm->data,
                _frm->linesize,
                0,
                _height,
                _rgb->data,
                _rgb->linesize);
            if (scaledRows <= 0)
            {
                throw new InvalidDataException($"Failed to convert frame for '{_sourceSpec}' (sws_scale returned {scaledRows}).");
            }

            Index++;
            return PixelsToPicture(_rgb->data[0], _rgb->linesize[0], _width, _height, _sourceSpec, targetFrame);
        }

        private IPicture<byte> HandleFrameNotFound(uint targetFrame)
        {
            if (_totalFrames > 0 && targetFrame > 0 && Math.Abs((long)targetFrame - _totalFrames) < 5)
            {
                uint fallbackFrame = targetFrame - 1;
                Log($"[FFmpegDeviceDecoderContext] Frame {targetFrame} not found, fallback to {fallbackFrame}.");
                return GetFrame(fallbackFrame);
            }

            double fps = _fps > 0 ? _fps : 1.0;
            double seconds = targetFrame / fps;
            throw new OverflowException($"Frame #{targetFrame} (timespan {TimeSpan.FromSeconds(seconds)}) does not exist in '{_sourceSpec}'.");
        }

        [DebuggerNonUserCode]
        private static Picture8bpp PixelsToPicture(byte* data, int stride, int width, int height, string source = "", uint frameIdx = 0)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (width <= 0 || height <= 0) throw new ArgumentException($"Invalid dimensions: {width}x{height}");
            if (stride <= 0 || stride < width * 3) throw new ArgumentException($"Invalid stride {stride} for width {width}.");

            int size = width * height;
            var result = new Picture8bpp(width, height)
            {
                r = new byte[size],
                g = new byte[size],
                b = new byte[size],
            };

            result.ProcessStack = new List<PictureProcessStack>
            {
                new PictureProcessStack
                {
                    OperationDisplayName = $"From video '{source}', frame #{frameIdx}",
                    Operator = typeof(FFmpegDeviceDecoderContext),
                    ProcessingFuncStackTrace = new StackTrace(true)
                }
            };

            int idx;
            int baseIndex;
            int offset;
            int x;
            int y;
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

        private static (string inputFormatName, string inputSource) ParseSourceSpec(string sourceSpec)
        {
            if (string.IsNullOrWhiteSpace(sourceSpec))
            {
                return (string.Empty, string.Empty);
            }

            int firstColon = sourceSpec.IndexOf(':');
            if (firstColon <= 0 || firstColon >= sourceSpec.Length - 1)
            {
                throw new ArgumentException(
                    "FFmpeg device source must be '<inputFormat>:<source>', for example 'lavfi:testsrc=size=1280x720:rate=30'.",
                    nameof(sourceSpec));
            }

            string inputFormatName = sourceSpec.Substring(0, firstColon).Trim();
            string inputSource = sourceSpec.Substring(firstColon + 1);

            if (string.IsNullOrWhiteSpace(inputFormatName) || string.IsNullOrWhiteSpace(inputSource))
            {
                throw new ArgumentException(
                    "FFmpeg device source must include both input format and source.",
                    nameof(sourceSpec));
            }

            return (inputFormatName, inputSource);
        }

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;

            if (_rgbBuffer != null) { ffmpeg.av_free(_rgbBuffer); _rgbBuffer = null; }
            if (_rgb != null) { AVFrame* tmp = _rgb; _rgb = null; ffmpeg.av_frame_free(&tmp); }
            if (_frm != null) { AVFrame* tmp = _frm; _frm = null; ffmpeg.av_frame_free(&tmp); }
            if (_pkt != null) { AVPacket* tmp = _pkt; _pkt = null; ffmpeg.av_packet_free(&tmp); }
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            if (_codec != null) { AVCodecContext* tmp = _codec; _codec = null; ffmpeg.avcodec_free_context(&tmp); }
            if (_fmt != null) { AVFormatContext* tmp = _fmt; _fmt = null; ffmpeg.avformat_close_input(&tmp); }
        }

        ~FFmpegDeviceDecoderContext()
        {
            Dispose();
        }
    }
}
