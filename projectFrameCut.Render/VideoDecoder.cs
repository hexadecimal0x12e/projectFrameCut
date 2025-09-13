using FFmpeg.AutoGen;
using ILGPU.Runtime.Cuda;
using SixLabors.ImageSharp.ColorSpaces;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace projectFrameCut.Render
{
    public unsafe class VideoDecoder : IDisposable
    {
        private readonly string filePath;
        private bool is16Bit;

        public VideoDecoder(string path, bool? Is16Bit = null)
        {
            is16Bit = Is16Bit ?? Path.GetExtension(path) == ".mkv"; //只有ffv1支持16bit

            filePath = path ?? throw new ArgumentNullException(nameof(path));
            decoders.AddOrUpdate(
                filePath,
                path => is16Bit ? new DecoderContext16Bit(path) : new DecoderContext8Bit(path),
                (path, existing) =>
                {
                    if (existing.Disposed) return is16Bit ? new DecoderContext16Bit(path) : new DecoderContext8Bit(path);
                    return existing;
                });
        }

        private static readonly ConcurrentDictionary<string, IDecoderContext> decoders = new();

        #region extract frame 

        public static void ReleaseDecoder(string videoPath)
        {
            if (string.IsNullOrWhiteSpace(videoPath)) return;
            if (decoders.TryRemove(videoPath, out var dec))
            {
                dec.Dispose();
            }
        }

        public Picture ExtractFrame(uint targetFrame) => decoders.TryGetValue(filePath, out var value) ? value.GetFrame(targetFrame) : throw new NullReferenceException($"Video file '{filePath}''s decoder context is not exist.");

        public IDecoderContext Decoder => decoders.TryGetValue(filePath, out var value) ? value : throw new NullReferenceException($"Video file '{filePath}''s decoder context is not exist.");

        #endregion

        #region make video

        public static void ComposeVideo(string sourcePath, int frameRate, string outputPath, string encoder = "libx264", string pixfmt = "yuv420p")
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("sourcePath is null or empty", nameof(sourcePath));
            if (frameRate <= 0) throw new ArgumentException("frameRate must be positive", nameof(frameRate));
            if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("outputPath is null or empty", nameof(outputPath));
            string args = $"-y -framerate {frameRate} -i \"{Path.GetFullPath(sourcePath)}\\frame_%04d.png\" -c:v {encoder} -pix_fmt {pixfmt} \"{outputPath}\"";
            var processInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var process = System.Diagnostics.Process.Start(processInfo))
            {
                if (process == null) throw new InvalidOperationException("Failed to start ffmpeg process.");
                process.OutputDataReceived += (sender, e) => { if (!string.IsNullOrEmpty(e.Data)) Log(e.Data); };
                process.ErrorDataReceived += (sender, e) => { if (!string.IsNullOrEmpty(e.Data)) Log(e.Data); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}");
                }
            }
        }

        #endregion

        #region misc

        public void Dispose()
        {
            ReleaseDecoder(filePath);
        }

        public interface IDecoderContext : IDisposable
        {
            abstract void Initialize();
            abstract Picture GetFrame(uint targetFrame, bool hasAlpha = false);        
            public bool Disposed { get; }
            public long TotalFrames { get; }  
            public double Fps { get; }
            public int Width { get; }
            public int Height { get; }
        }

        #endregion

        public sealed class DecoderContext16Bit : IDecoderContext
        {
            private readonly object locker = new();

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

            private static ushort[] sharedR = Array.Empty<ushort>(), sharedG = Array.Empty<ushort>(), sharedB = Array.Empty<ushort>();


            public DecoderContext16Bit(string path)
            {
                _path = path ?? throw new ArgumentNullException(nameof(path));
                Initialize();
            }


            public void Initialize()
            {
                if (Initialized) throw new InvalidOperationException("DecoderContext has already been initialized.");

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
                                fs.Read(new byte[16]);
                                throw new NotSupportedException($"File '{_path}' seems not like a video file.\r\nIf you continuously encountering this issue, try install ffmpeg toolkit on your computer, then run this command and observe whether there is any error message:\r\nffprobe {Path.GetFullPath(_path)}");


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
                        throw new InvalidDataException($"File '{_path}' seems don't like a multimedia file. Use a media player open it to check. If you continuously encountering this issue, try install ffmpeg toolkit on your computer, then run this command and observe whether there is any error message:\r\nffprobe {Path.GetFullPath(_path)}");

                    for (int i = 0; i < _fmt->nb_streams; i++)
                    {
                        if (_fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                        {
                            _videoStreamIndex = i;
                            break;
                        }
                    }

                    if (_videoStreamIndex < 0)
                        throw new InvalidDataException($"File '{_path}' seems don't like a video file. Use a media player open it to check. If you continuously encountering this issue, try encode your video again to another format.");

                    AVCodecParameters* par = _fmt->streams[_videoStreamIndex]->codecpar;
                    AVCodec* codec = ffmpeg.avcodec_find_decoder(par->codec_id);
                    if (codec == null)
                        throw new NotSupportedException("No suitable decoder found. Try encode your video again to another format or upgrade projectFrameCut.");

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

                    sharedR = new ushort[_width * _height];
                    sharedG = new ushort[_width * _height];
                    sharedB = new ushort[_width * _height];

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
                            ffmpeg.SWS_BICUBIC, null, null, null);

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
                catch
                {
                    Dispose();
                    throw;
                }
                finally
                {
                    Initialized = true;
                }
            }


            public Picture GetFrame(uint targetFrame, bool hasAlpha = false)
            {
                lock (locker)
                {
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
                                        if(_currentFrameNumber++ == targetFrame) goto found;
                                        
                                    }
                                    else if (_totalFrames <= _currentFrameNumber)
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
                }

            not_found:
                double fps = _fps > 0 ? _fps : 1.0;
                double seconds = targetFrame / fps;
                throw new OverflowException($"Frame #{targetFrame} (timespan {TimeSpan.FromSeconds(seconds)}) not exist in video '{_path}'.");

            found:
                ffmpeg.sws_scale(
                                 _sws,
                                 _frm->data,
                                 _frm->linesize,
                                 0,
                                 _height,
                                 _rgb->data,
                                 _rgb->linesize
                                 );

                return PixelsToPicture(_rgb->data[0], _rgb->linesize[0], _width, _height, hasAlpha);
            }


            [DebuggerNonUserCode()]
            private static Picture PixelsToPicture(byte* data, int stride, int width, int height, bool hasAlpha = false)
            {
                var result = new Picture(width, height)
                {
                    r = sharedR.ToArray(),
                    g = sharedG.ToArray(),
                    b = sharedB.ToArray(),
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
                return result.SetAlpha(hasAlpha);
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

        public sealed class DecoderContext8Bit : IDecoderContext
        {
            private readonly object locker = new();

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

            private static ushort[] sharedR = Array.Empty<ushort>(), sharedG = Array.Empty<ushort>(), sharedB = Array.Empty<ushort>();

            public DecoderContext8Bit(string path)
            {
                _path = path ?? throw new ArgumentNullException(nameof(path));
                Initialize();
            }

            public void Initialize()
            {
                if(Initialized) throw new InvalidOperationException("DecoderContext has already been initialized.");
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
                                fs.Read(new byte[16]);
                                throw new NotSupportedException($"File '{_path}' seems not like a video file.\r\nIf you continuously encountering this issue, try install ffmpeg toolkit on your computer, then run this command and observe whether there is any error message:\r\nffprobe {Path.GetFullPath(_path)}");


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
                        throw new InvalidDataException($"File '{_path}' seems don't like a multimedia file. Use a media player open it to check. If you continuously encountering this issue, try install ffmpeg toolkit on your computer, then run this command and observe whether there is any error message:\r\nffprobe {Path.GetFullPath(_path)}");

                    for (int i = 0; i < _fmt->nb_streams; i++)
                    {
                        if (_fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                        {
                            _videoStreamIndex = i;
                            break;
                        }
                    }

                    if (_videoStreamIndex < 0)
                        throw new InvalidDataException($"File '{_path}' seems don't like a video file. Use a media player open it to check. If you continuously encountering this issue, try encode your video again to another format.");

                    AVCodecParameters* par = _fmt->streams[_videoStreamIndex]->codecpar;
                    AVCodec* codec = ffmpeg.avcodec_find_decoder(par->codec_id);
                    if (codec == null)
                        throw new NotSupportedException("No suitable decoder found. Try encode your video again to another format or upgrade projectFrameCut.");

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

                    sharedR = new ushort[_width * _height];
                    sharedG = new ushort[_width * _height];
                    sharedB = new ushort[_width * _height];

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
                        ffmpeg.SWS_BICUBIC, null, null, null);

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
                catch
                {
                    Dispose();
                    throw;
                }
                finally
                {
                    Initialized = true;
                }
            }

            public Picture GetFrame(uint targetFrame, bool hasAlpha)
            {
                lock (locker)
                {
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
                            else if (_totalFrames <= _currentFrameNumber)
                            {
                                goto not_found;
                            }

                            break;
                        }

                        if (_eof && flushSent)
                            break;
                    }

                not_found:
                    double fps = _fps > 0 ? _fps : 1.0;
                    double seconds = targetFrame / fps;
                    throw new OverflowException($"Frame #{targetFrame} (timespan {TimeSpan.FromSeconds(seconds)}) not exist in video '{_path}'.");

                found:
                    ffmpeg.sws_scale(
                                        _sws,
                                        _frm->data,
                                        _frm->linesize,
                                        0,
                                        _height,
                                        _rgb->data,
                                        _rgb->linesize);
                    return PixelsToPicture(_rgb->data[0], _rgb->linesize[0], _width, _height, hasAlpha);

                }
            }

            [DebuggerNonUserCode()]
            private static Picture PixelsToPicture(byte* data, int stride, int width, int height, bool hasAlpha = false)
            {
                var result = new Picture(width, height)
                {
                    r = sharedR.ToArray(),
                    g = sharedG.ToArray(),
                    b = sharedB.ToArray(),
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
                        offset = x * 3;
                        result.r[idx] = (ushort)(srcRow[offset + 2] * 257);
                        result.g[idx] = (ushort)(srcRow[offset + 1] * 257);
                        result.b[idx] = (ushort)(srcRow[offset + 0] * 257);
                    }
                }
                return result.SetAlpha(hasAlpha);
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


}

