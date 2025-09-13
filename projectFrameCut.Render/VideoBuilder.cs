using FFmpeg.AutoGen;
using ILGPU;
using ILGPU.Runtime;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace projectFrameCut.Render
{
    public class VideoBuilder :IDisposable    
    {
        string outputPath;
        VideoWriter builder;
        uint index;
        bool running = true, stopped = false;
        public bool StrictMode { get; set; } = true;

        public int minFrameCountToAppend { get; set; } = 30;

        ConcurrentDictionary<uint,Picture> Cache = new();

        public VideoBuilder(string path,  int width, int height, int framerate,string encoder, AVPixelFormat fmt)
        {
            outputPath = path;
            index = 0;
            builder = new(path,width,height,framerate,encoder,fmt);
        }
        
        public bool Disposed { get; private set; }

        public void Append(uint index, Picture frame)
        {

            Cache.AddOrUpdate(index, frame,(_,_) => throw new InvalidOperationException($"Frame #{index} has already added."));
        }

        //public void Clear() => Cache.Clear();

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            builder.Dispose();
        }

        public Thread Build()
        {
            return new Thread(() =>
            {
                Log($"[VideoBuilder] Successfully started writer for {outputPath}");

                do
                {
                    if (Cache.Count == 0) 
                    {
                        Thread.Sleep(100); // 添加休眠避免空转
                        continue;
                    }

                    if (Cache.ContainsKey(index))
                    {
                        builder.Append(Cache.TryRemove(index, out var f) ? f : throw new KeyNotFoundException());
                        Log($"[VideoBuilder] Frame #{index} added.");
                        index++;
                    }
                    //else if (Cache.Keys.Count > 0 && Cache.Keys.Min() > index)
                    //{
                    //    // 如果缓存中最小的帧索引大于当前索引，说明前面的帧丢失了
                    //    Log($"[VideoBuilder] WARN: Frame #{index} missing, skipping to next available frame.");
                    //    index = Cache.Keys.Min();
                    //}
                    else
                    {
                        Thread.Sleep(200); // 减少等待时间以提高响应速度
                    }
                } 
                while (running);
                Thread.Sleep(50);
                stopped = true;
            })
            { 
                Name = $"VideoWriter for {outputPath}",
                Priority = ThreadPriority.Highest
            };


        }

        public void Finish(Accelerator accelerator, Clip[] clip)
        {
            running = false;
            while(!Volatile.Read(ref stopped))
                Thread.Sleep(500);
            
            // 收集所有缺失的帧索引
            var missingFrames = new List<uint>();
            uint currentIndex = index;
            
            while (Cache.Count > 0 || missingFrames.Count > 0)
            {
                // 先处理已有的帧
                if (Cache.ContainsKey(currentIndex))
                {
                    builder.Append(Cache.TryRemove(currentIndex, out var f) ? f : throw new KeyNotFoundException());
                    Log($"[VideoBuilder] Frame #{currentIndex} added.");
                    currentIndex++;
                    continue;
                }
                
                // 收集连续的缺失帧
                if (missingFrames.Count == 0 && !Cache.ContainsKey(currentIndex))
                {
                    uint maxCheck = currentIndex + 100; // 限制一次检查的范围
                    for (uint i = currentIndex; i < maxCheck; i++)
                    {
                        if (!Cache.ContainsKey(i) && (Cache.Count == 0 || i <= Cache.Keys.Max()))
                        {
                            missingFrames.Add(i);
                        }
                        else if (Cache.ContainsKey(i))
                        {
                            break;
                        }
                    }
                    
                    // 批量生成缺失帧
                    if (missingFrames.Count > 0)
                    {
                        Log($"[VideoBuilder] WARN: Frames #{missingFrames[0]}-#{missingFrames[missingFrames.Count-1]} not found, rebuilding {missingFrames.Count} frames...");
                        foreach (var frameIdx in missingFrames)
                        {
                            Timeline.MixtureLayers(Timeline.GetFramesInOneFrame(clip, frameIdx), accelerator, frameIdx);
                        }
                        missingFrames.Clear();
                    }
                    else if (Cache.Count == 0)
                    {
                        break; // 没有更多帧需要处理
                    }
                }
                else if (missingFrames.Count > 0)
                {
                    // 处理一个已重建的帧
                    uint frameToProcess = missingFrames[0];
                    missingFrames.RemoveAt(0);
                    
                    if (Cache.ContainsKey(frameToProcess))
                    {
                        builder.Append(Cache.TryRemove(frameToProcess, out var f) ? f : throw new KeyNotFoundException());
                        Log($"[VideoBuilder] Rebuilt frame #{frameToProcess} added.");
                        
                        if (frameToProcess == currentIndex)
                            currentIndex++;
                    }
                }
                else
                {
                    currentIndex++;
                }
            }
            
            builder.Finish();
            Dispose();
        }

    }

    internal sealed unsafe class VideoWriter : IDisposable
    {
        private readonly int _width;
        private readonly int _height;
        private readonly int _fps;
        private readonly string _outputPath;
        private readonly string _codecName;
        private readonly AVPixelFormat _dstPixFmt;

        private AVFormatContext* _fmtCtx;
        private AVStream* _videoStream;
        private AVCodecContext* _codecCtx;
        private AVFrame* _frameDst;
        private AVFrame* _frameSrc;
        private SwsContext* _sws;
        private int _frameIndex;
        private bool _isHeaderWritten;
        private bool _isDisposed;
        private string _path;
        private int colorDepth = 8;

        public bool IsOpened => _fmtCtx != null;

        public VideoWriter(
            string outputPath,
            int width,
            int height,
            int fps = 30,
            string codecName = "libx264",
            AVPixelFormat dstPixelFormat = AVPixelFormat.AV_PIX_FMT_YUV420P)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPath, nameof(outputPath));
            if (width <= 0 || height <= 0 || fps <= 0) throw new ArgumentOutOfRangeException("You set an invalid width, height or fps.");
            _outputPath = outputPath;
            _width = width;
            _height = height;
            _fps = fps;
            _codecName = codecName;
            _dstPixFmt = dstPixelFormat;
            _path = outputPath;
            FFmpegHelper.RegisterFFmpeg();
            Init();
        }

        private void Init()
        {
            AVFormatContext* oc = null;
            int ret = ffmpeg.avformat_alloc_output_context2(&oc, null, null, _outputPath);
            if (ret < 0 || oc == null)
            {
                try
                {
                    using (var fs = System.IO.File.Create(_outputPath))
                    {
                        fs.WriteByte(1);
                        goto writable;
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new FileLoadException($"projectFrameCut can't write the video file '{_path}' because of no enough privileges. Try restart the projectFrameCut with administrator privileges, or modify the privileges of output dir. (FFmpeg error:{FFmpegHelper.GetErrorString(ret) ?? "unknown"}, code:{ret})", ex);
                }
                catch(DirectoryNotFoundException)
                {
                    throw new DirectoryNotFoundException($"The directory '{Path.GetDirectoryName(_path)}' isn't exist. (FFmpeg error:{FFmpegHelper.GetErrorString(ret) ?? "unknown"}, code:{ret})");
                }
                catch(PathTooLongException ex)
                {
                    throw new FileLoadException($"projectFrameCut can't write the video file '{_path}' because of path is too long. Try modify the temp directory in the settings. (FFmpeg error:{FFmpegHelper.GetErrorString(ret) ?? "unknown"}, code:{ret})", ex);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"We failed to write the file because of '{ex.Message}'. (FFmpeg error:{FFmpegHelper.GetErrorString(ret) ?? "unknown"}, code:{ret})", ex);
                }
                writable:
                FFmpegHelper.Throw(ret, "Prepare the file to write video (avformat_alloc_output_context2)");
            }
            _fmtCtx = oc;

            AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(_codecName);
            if (codec == null) throw new EntryPointNotFoundException($"Could not found a encoder '{_codecName}' for the video file '{_path}'. Try reinstall projectFrameCut.");

            _videoStream = ffmpeg.avformat_new_stream(_fmtCtx, codec);
            if (_videoStream == null) throw new InvalidOperationException("Failed to create a stream to write video.");

            _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (_codecCtx == null) throw new InvalidOperationException("Failed to allocate a context for video.");

            _codecCtx->codec_id = codec->id;
            _codecCtx->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            _codecCtx->width = _width;
            _codecCtx->height = _height;
            _codecCtx->pix_fmt = _dstPixFmt;
            _codecCtx->time_base = new AVRational { num = 1, den = _fps };
            _videoStream->time_base = _codecCtx->time_base;
            _codecCtx->framerate = new AVRational { num = _fps, den = 1 };
            _codecCtx->gop_size = 12;
            _codecCtx->max_b_frames = 2;
            _codecCtx->bit_rate = 4_000_000;

            if ((_fmtCtx->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                _codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

            AVDictionary* opts = null;
            if (_codecCtx->codec_id == AVCodecID.AV_CODEC_ID_H264)
            {
                ffmpeg.av_dict_set(&opts, "preset", "veryfast", 0);
                ffmpeg.av_dict_set(&opts, "tune", "zerolatency", 0);
            }

            FFmpegHelper.Throw(ffmpeg.avcodec_open2(_codecCtx, codec, &opts), "avcodec_open2");
            ffmpeg.av_dict_free(&opts);

            FFmpegHelper.Throw(ffmpeg.avcodec_parameters_from_context(_videoStream->codecpar, _codecCtx),
                "avcodec_parameters_from_context");

            if ((_fmtCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                FFmpegHelper.Throw(ffmpeg.avio_open(&_fmtCtx->pb, _outputPath, ffmpeg.AVIO_FLAG_WRITE), "avio_open");
            }

            _frameDst = ffmpeg.av_frame_alloc();
            _frameDst->format = (int)_dstPixFmt;
            _frameDst->width = _width;
            _frameDst->height = _height;
            FFmpegHelper.Throw(ffmpeg.av_frame_get_buffer(_frameDst, 32), "av_frame_get_buffer(dst)");

            var srcPixFmt =
                (_dstPixFmt == AVPixelFormat.AV_PIX_FMT_GBRP16LE ||
                 _dstPixFmt == AVPixelFormat.AV_PIX_FMT_YUV420P16LE ||
                 _dstPixFmt == AVPixelFormat.AV_PIX_FMT_RGBA64LE ||
                 _dstPixFmt == AVPixelFormat.AV_PIX_FMT_BGRA64LE)
                ? AVPixelFormat.AV_PIX_FMT_RGBA64LE
                : AVPixelFormat.AV_PIX_FMT_RGBA;

            colorDepth = (srcPixFmt == AVPixelFormat.AV_PIX_FMT_RGBA64LE) ? 16 : 8;

            _frameSrc = ffmpeg.av_frame_alloc();
            _frameSrc->format = (int)srcPixFmt;
            _frameSrc->width = _width;
            _frameSrc->height = _height;
            FFmpegHelper.Throw(ffmpeg.av_frame_get_buffer(_frameSrc, 32), "av_frame_get_buffer(src)");

            _sws = ffmpeg.sws_getContext(
                _width, _height, srcPixFmt,
                _width, _height, _dstPixFmt,
                ffmpeg.SWS_BILINEAR, null, null, null);
            if (_sws == null) throw new InvalidOperationException("Couldn't get the SWS context.");
            Console.WriteLine($"[VideoBuilder] Successfully initialized encoder for {_path}");

        }

        public void Append(Picture picture)
        {
            if (picture == null) throw new ArgumentNullException(nameof(picture));
            if (picture.Width != _width || picture.Height != _height)
                throw new ArgumentException("The result size is different from original size. Please check the source.");
            if (_isDisposed) throw new ObjectDisposedException(nameof(VideoBuilder));

            EnsureHeader();

            FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(_frameSrc), "make frame writable");
            FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(_frameDst), "make frame writable");

            byte* srcData0 = _frameSrc->data[0];
            int srcLinesize = _frameSrc->linesize[0];

            fixed (ushort* pr = picture.r)
            fixed (ushort* pg = picture.g)
            fixed (ushort* pb = picture.b)
            fixed (float* pa = picture.a)
            {
                if (colorDepth == 16)
                {
                    for (int y = 0; y < _height; y++)
                    {
                        ushort* row16 = (ushort*)(srcData0 + y * srcLinesize);
                        int baseIndex = y * _width;
                        for (int x = 0; x < _width; x++)
                        {
                            int k = baseIndex + x;
                            ushort r16 = (pr != null && k < picture.r.Length) ? pr[k] : (ushort)0;
                            ushort g16 = (pg != null && k < picture.g.Length) ? pg[k] : (ushort)0;
                            ushort b16 = (pb != null && k < picture.b.Length) ? pb[k] : (ushort)0;

                            ushort a16 = 65535;
                            if (picture.hasAlphaChannel && picture.a != null && pa != null && k < picture.a.Length)
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
                            ushort r16 = pr != null && k < picture.r.Length ? pr[k] : (ushort)0;
                            ushort g16 = pg != null && k < picture.g.Length ? pg[k] : (ushort)0;
                            ushort b16 = pb != null && k < picture.b.Length ? pb[k] : (ushort)0;
                            byte r8 = (byte)(r16 >> 8);
                            byte g8 = (byte)(g16 >> 8);
                            byte b8 = (byte)(b16 >> 8);
                            byte a8 = 255;
                            if (picture.hasAlphaChannel && picture.a != null && pa != null && k < picture.a.Length)
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
                _height,
                _frameDst->data,
                _frameDst->linesize);

            _frameDst->pts = _frameIndex++;

            EncodeFrame(_frameDst);
        }

        private void EnsureHeader()
        {
            if (_isHeaderWritten) return;
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
            if (_isDisposed) return;

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

            Log($"[VideoBuilder] Successfully finished video writer for {_path}");

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

        ~VideoWriter()
        {
            Dispose();
        }
    }

    internal static unsafe class FFmpegHelper
    {
        private static bool _registered;

        public static void RegisterFFmpeg()
        {
            if (_registered) return;
            if (Program.advancedFlags.Contains("ffmpeg_loglevel_debug"))
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_DEBUG);
            else if (Program.advancedFlags.Contains("ffmpeg_loglevel_error"))
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            else if (Program.advancedFlags.Contains("ffmpeg_loglevel_none"))
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
            else
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);
            _registered = true;
        }

        public static void Throw(int err, string api)
        {
            if (err >= 0) return;
            var msg = GetErrorString(err);
            throw new InvalidOperationException
            ($"'{api}' failed during writing the video,{(msg is not null ? $" probably because '{msg}'." : " but we don't know what thing it happens.")} (FFmpeg internal error code:{err})")
            {
                HResult = err
            };
        }

        public static string? GetErrorString(int err)
        {
            const int AV_ERROR_MAX_STRING_SIZE = 1024;
            byte* buffer = stackalloc byte[AV_ERROR_MAX_STRING_SIZE];
            ffmpeg.av_strerror(err, buffer, (ulong)AV_ERROR_MAX_STRING_SIZE);
            return Marshal.PtrToStringAnsi((IntPtr)buffer);
        }
    }
}