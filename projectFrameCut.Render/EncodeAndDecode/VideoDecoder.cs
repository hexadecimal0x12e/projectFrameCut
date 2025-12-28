using FFmpeg.AutoGen;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using SixLabors.ImageSharp.ColorSpaces;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace projectFrameCut.Render.EncodeAndDecode
{

    public sealed unsafe class DecoderContext16Bit : IVideoSource
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

        public bool Disposed { get; private set; }
        public bool Initialized { get; private set; } = false;

        public long TotalFrames => _totalFrames;

        public double Fps => _fps;

        public int Width => _width;

        public int Height => _height;

        public uint Index { get; set; } = 0;

        public string[] PreferredExtension => [".mkv"];
        public int? ResultBitPerPixel => 8;

        public bool EnableLock { get; set; } = false;
        private Lock locker = new();

        public DecoderContext16Bit(string path)
        {
            _path = path;
            Initialize();
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
                        var fi = new FileInfo(_path);
                        if (!fi.Exists)
                        {
                            throw new FileNotFoundException($"The video file '{_path}' doesn't exist.");
                        }

                        if (fi.Length == 0)
                        {
                            throw new ArgumentNullException($"The video file '{_path}' is empty.");
                        }

                        try
                        {
                            FileStream fs = new FileStream(_path, FileMode.Open);
#pragma warning disable CA2022 // 避免使用 "Stream.Read" 进行不准确读取
                            fs.Read(new byte[16]);
#pragma warning restore CA2022 // 避免使用 "Stream.Read" 进行不准确读取
                            throw new InvalidDataException($"File '{_path}' seems don't like a video file. Try install the encoder extension. If you continuously encountering this issue, try encode your video again to another format.");


                        }
                        catch (NotSupportedException)
                        {
                            var errstr = FFmpegHelper.GetErrorString(averr);
                            throw new InvalidOperationException($"Cannot open the video file '{_path}' because of '{errstr}'");
                        }
                        catch (IOException ex)
                        {
                            throw new FileLoadException($"projectFrameCut can't read the video file '{_path}', it's maybe because of an error:'{ex.Message}'", ex);
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            throw new FileLoadException($"projectFrameCut can't read the video file '{_path}' because of no enough privileges. Try restart the projectFrameCut with administrator privileges, or modify", ex);
                        }
                        catch (Exception ex)
                        {
                            throw new NotSupportedException($"Failed to open the video file '{_path}', it's maybe because of an error:'{ex.Message}'. Try restart render, or reboot your computer. If you continuously encountering this issue, try install ffmpeg toolkit on your computer, then run this command and observe whether there is any error message:\r\nffprobe {Path.GetFullPath(_path)}");
                        }


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
                        4, null, null, null);
                // SWS_BICUBIC == 4

                if (_sws == null)
                    throw new InvalidOperationException("Failed to alloc a context for the Renderer. Please try reboot your device, or reinstall projectFrameCut.");

                int bufferSize = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGR48LE, _width, _height, 1);
                if (bufferSize <= 0) throw new OutOfMemoryException($"Failed to allocate enough memory space to process the video '{_path}'. Try closing other programs, restarting your device, reinstall projectFrameCut, increasing page file size (on Windows platforms)/swapping files (on Linux platforms), or adding more RAM on your device if possible.");

                _rgbBuffer = (byte*)ffmpeg.av_malloc((ulong)bufferSize);
                if (_rgbBuffer == null)
                    throw new OutOfMemoryException($"Failed to allocate enough memory space to process the video '{_path}'. Try closing other programs, restarting your device, reinstall projectFrameCut, increasing page file size (on Windows platforms)/swapping files (on Linux platforms), or adding more RAM on your device if possible.");

                _rgb->data[0] = _rgbBuffer;
                _rgb->linesize[0] = _width * 6;

                _currentFrameNumber = 0;

                Log($"[VideoDecoder] Successfully initialized decoder for {_path}");
            }
            catch (Exception ex)
            {
                Dispose();
                Log(ex, "Init VideoDecoder", this);
                throw;
            }
            finally
            {
                Initialized = true;
            }
        }


        public IPicture GetFrame(uint targetFrame, bool hasAlpha = false)
        {
            if (EnableLock) locker.Enter();

            if (targetFrame < _currentFrameNumber)
            {
                ffmpeg.av_seek_frame(_fmt, _videoStreamIndex, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
                ffmpeg.avcodec_flush_buffers(_codec);
                _currentFrameNumber = 0;
            }

            while (ffmpeg.av_read_frame(_fmt, _pkt) >= 0)
            {

                try
                {
                    if (_pkt->stream_index == _videoStreamIndex)
                    {
                        if (ffmpeg.avcodec_send_packet(_codec, _pkt) < 0) continue;

                        while (true)
                        {
                            ffmpeg.av_frame_unref(_frm);
                            if (ffmpeg.avcodec_receive_frame(_codec, _frm) == 0)
                            {
                                if (_currentFrameNumber++ == targetFrame) goto found;

                            }
                            else if (_totalFrames < _currentFrameNumber)
                            {
                                goto not_found;
                            }
                            break;

                        }
                    }
                }
                catch (Exception)
                {
                    throw;
                }
                finally
                {
                    ffmpeg.av_packet_unref(_pkt);
                }

            }

        not_found:
            if (EnableLock) locker.Exit();
            if (Math.Abs(targetFrame - TotalFrames) < 5)
            {
                Log($"[VideoDecoder] Frame {targetFrame} not found(may due to rounding), try getting frame {targetFrame - TotalFrames} instead.");
                return GetFrame(targetFrame - 1, hasAlpha);
            }
            double fps = _fps > 0 ? _fps : 1.0;
            double seconds = targetFrame / fps;
            throw new OverflowException($"Frame #{targetFrame} (timespan {TimeSpan.FromSeconds(seconds)}) not exist in video '{_path}'.");

        found:
            Index++;
            ffmpeg.sws_scale(
                             _sws,
                             _frm->data,
                             _frm->linesize,
                             0,
                             _height,
                             _rgb->data,
                             _rgb->linesize
                             );
            if (EnableLock) locker.Exit();
            return PixelsToPicture(_rgb->data[0], _rgb->linesize[0], _width, _height, hasAlpha,_path,targetFrame);
        }


        [DebuggerNonUserCode()]
        private static IPicture PixelsToPicture(byte* data, int stride, int width, int height, bool hasAlpha = false, string filePath = "", uint frameIdx = 0)
        {
            var size = width * height;
            var result = new Picture(width, height)
            {
                r = new ushort[size],
                g = new ushort[size],
                b = new ushort[size],
            };
            int idx, baseIndex, offset, x, y;
            byte* srcRow;
            for (y = 0; y < height; y++)
            {
                srcRow = data + y * stride;
                baseIndex = y * width;
                for (x = 0; x < width; x++)
                {
                    idx = baseIndex + x;
                    offset = x * 6;

                    result.b[idx] = (ushort)(srcRow[offset] | (srcRow[offset + 1] << 8));
                    result.g[idx] = (ushort)(srcRow[offset + 2] | (srcRow[offset + 3] << 8));
                    result.r[idx] = (ushort)(srcRow[offset + 4] | (srcRow[offset + 5] << 8));

                }
            }
            result.ProcessStack = $"From video '{filePath}', frame #{frameIdx}";
            return result;
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

        ~DecoderContext16Bit()
        {
            Dispose();
        }
    }

    public sealed unsafe class DecoderContext8Bit : IVideoSource
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


        public bool Disposed { get; private set; }
        public bool Initialized { get; private set; } = false;

        public long TotalFrames => _totalFrames;

        public double Fps => _fps;

        public int Width => _width;

        public int Height => _height;

        public uint Index { get; set; } = 0;
        public string[] PreferredExtension => [".mp4",".mov"];

        public int? ResultBitPerPixel => 8;

        public bool EnableLock { get; set; } = false;
        private Lock locker = new();

        public DecoderContext8Bit(string path)
        {
            _path = path;
            Initialize();
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
                    if (ffmpeg.avformat_open_input(fmtPtr, _path, null, null) != 0)
                    {
                        var fi = new FileInfo(_path);
                        if (!fi.Exists)
                        {
                            throw new FileNotFoundException($"The video file '{_path}' doesn't exist.");
                        }

                        if (fi.Length == 0)
                        {
                            throw new ArgumentNullException($"The video file '{_path}' is empty.");
                        }

                        try
                        {
                            FileStream fs = new FileStream(_path, FileMode.Open);
#pragma warning disable CA2022 
                            fs.Read(new byte[16]);
#pragma warning restore CA2022
                            throw new NotSupportedException($"File '{_path}' seems not like a video file. Try install the codec extension. \r\nIf you continuously encountering this issue, try install ffmpeg toolkit on your computer, then run this command and observe whether there is any error message:\r\nffprobe {Path.GetFullPath(_path)}");


                        }
                        catch (NotSupportedException)
                        {
                            throw;
                        }
                        catch (IOException ex)
                        {
                            throw new FileLoadException($"projectFrameCut can't read the video file '{_path}', it's maybe because of an error:'{ex.Message}'", ex);
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            throw new FileLoadException($"projectFrameCut can't read the video file '{_path}' because of no enough privileges. Try restart the projectFrameCut with administrator privileges, or modify", ex);
                        }
                        catch (Exception ex)
                        {
                            throw new NotSupportedException($"Failed to open the video file '{_path}', it's maybe because of an error:'{ex.Message}'. Try restart render, or reboot your computer. If you continuously encountering this issue, try install ffmpeg toolkit on your computer, then run this command and observe whether there is any error message:\r\nffprobe {Path.GetFullPath(_path)}");
                        }


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
                    4, null, null, null);
                // SWS_BICUBIC == 4

                if (_sws == null)
                    throw new InvalidOperationException("Failed to alloc a context for the Renderer. Please try reboot your device, or reinstall projectFrameCut.");

                int bufferSize = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGR24, _width, _height, 1);
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

                Log($"[VideoDecoder] Successfully initialized decoder for {_path}");
            }
            catch (Exception ex)
            {
                Log(ex, "Init VideoDecoder", this);
                Dispose();
                throw;
            }
            finally
            {
                Initialized = true;
            }
        }



        [DebuggerNonUserCode()]
        public IPicture GetFrame(uint targetFrame, bool hasAlpha)
        {
            if(EnableLock) locker.Enter();
            if (targetFrame < _currentFrameNumber)
            {
                ffmpeg.av_seek_frame(_fmt, _videoStreamIndex, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
                ffmpeg.avcodec_flush_buffers(_codec);
                _currentFrameNumber = 0;
                _eof = false;
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
                    if (ffmpeg.avcodec_receive_frame(_codec, _frm) == 0)
                    {
                        if (_currentFrameNumber++ == targetFrame)
                        {
                            goto found;
                        }

                        continue;
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
                Log($"[VideoDecoder] Frame {targetFrame} not found(may due to rounding), try getting frame {targetFrame - 1} instead.");
                return GetFrame(targetFrame - 1, hasAlpha);
            }
            double fps = _fps > 0 ? _fps : 1.0;
            double seconds = targetFrame / fps;
            throw new OverflowException($"Frame #{targetFrame} (timespan {TimeSpan.FromSeconds(seconds)}) not exist in video '{_path}'.");

        found:
            Index++;
            ffmpeg.sws_scale(
                                _sws,
                                _frm->data,
                                _frm->linesize,
                                0,
                                _height,
                                _rgb->data,
                                _rgb->linesize);
            if (EnableLock) locker.Exit();
            return PixelsToPicture(_rgb->data[0], _rgb->linesize[0], _width, _height, hasAlpha,_path,targetFrame);


        }

        //[DebuggerNonUserCode()]
        private static Picture8bpp PixelsToPicture(byte* data, int stride, int width, int height, bool hasAlpha = false, string filePath = "", uint frameIdx = 0)
        {
            var size = width * height;
            var result = new Picture8bpp(width, height)
            {
                r = new byte[size],
                g = new byte[size],
                b = new byte[size],
            };
            result.ProcessStack = $"From video '{filePath}', frame #{frameIdx}";
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
            if (_frm != null) { AVFrame* tmp = _frm; _frm = null; ffmpeg.av_frame_free(&tmp); }
            if (_pkt != null) { AVPacket* tmp = _pkt; _pkt = null; ffmpeg.av_packet_free(&tmp); }
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            if (_codec != null) { AVCodecContext* tmp = _codec; _codec = null; ffmpeg.avcodec_free_context(&tmp); }
            if (_fmt != null) { AVFormatContext* tmp = _fmt; _fmt = null; ffmpeg.avformat_close_input(&tmp); }
        }

        ~DecoderContext8Bit()
        {
            Dispose();
        }
    }
}




