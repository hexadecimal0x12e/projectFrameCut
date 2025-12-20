using FFmpeg.AutoGen;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace projectFrameCut.Render.Videos
{
    public class VideoBuilder : IDisposable
    {
        string outputPath;
        VideoWriter builder;
        uint index;
        bool running = true, stopped = false;
        ConcurrentDictionary<uint, IPicture> Cache = new();

        /// <summary>
        /// When it's true, adding a frame with an existing index will throw an exception, 
        /// or when <see cref="BlockWrite"/> is enabled, writing a frame with an existing index will throw an exception.
        /// </summary>
        public bool StrictMode { get; set; } = true;
        /// <summary>
        /// Call GC to collect unreferenced objects after each frame is written.
        /// </summary>
        public bool DoGCAfterEachWrite { get; set; } = true;
        /// <summary>
        /// Dispose the source <see cref="IPicture"/> when it's written to video.
        /// </summary>
        public bool DisposeFrameAfterEachWrite { get; set; } = true;

        /// <summary>
        /// Don't write frames to cache, write directly to file when appended. 
        /// </summary>
        public bool BlockWrite { get; set; } = false;
        /// <summary>
        /// Generate preview to the specified path when enabled.
        /// </summary>
        public bool EnablePreview { get; set; } = false;
        /// <summary>
        /// Path of the preview image.
        /// </summary>
        public string? PreviewPath { get; set; } = null;
        /// <summary>
        /// the minimum number of frames between generating preview images.
        /// </summary>
        public int minFrameCountToGeneratePreview { get; set; } = 10;
        private uint countSinceLastPreview = 0;

        /// <summary>
        /// The duration (in frames) of the video to be built.
        /// </summary>
        public uint Duration { get; set; }
        public int Width => builder._width;
        public int Height => builder._height;

        public VideoWriter Writer { get => builder; }



        /// <summary>
        /// Indicates whether the frame has been pended to write. 
        /// </summary>
        /// <remarks>
        /// For each Key-Value pair, the key is the frame index.
        /// When value is True means the frame has been written to the video file, 
        /// and when value is False means the frame is still in the cache waiting to be written.
        /// If the key is not present, it means the frame has not been added yet.
        /// </remarks>
        public ConcurrentDictionary<uint, bool> FramePendedToWrite { get; private set; } = new();

        public VideoBuilder(string path, int width, int height, int framerate, string encoder, AVPixelFormat fmt)
        {
            outputPath = path;
            index = 0;
            builder = new(path, width, height, framerate, encoder, fmt);
        }

        public bool Disposed { get; private set; }

        public void Append(uint index, IPicture frame)
        {
            if (index > Duration)
            {
                Log($"[VideoBuilder] WARN: Frame #{index} is out of duration {Duration}, ignored.", "warn");
                if (DisposeFrameAfterEachWrite) frame.Dispose();
                return;
            }
            if (!FramePendedToWrite.TryAdd(index, false))
            {
                if (StrictMode)
                {
                    throw new InvalidOperationException($"Frame #{index} has already been added.");
                }
                else
                {
                    Log($"[VideoBuilder] WARN: Frame #{index} has already been added, ignored.", "warn");
                    if (DisposeFrameAfterEachWrite) frame.Dispose();
                    return;
                }
            }
            if (!BlockWrite)
            {
                Cache.AddOrUpdate(index, frame, (_, _) => throw new InvalidOperationException($"Frame #{index} has already been added."));
                if (EnablePreview && ++countSinceLastPreview >= minFrameCountToGeneratePreview)
                {
                    AsyncSave(index, frame);
                    countSinceLastPreview = 0;
                }
            }
            else
            {
                builder.Append(frame.Resize(Width, Height, false)); //ensure size
                Log($"[VideoBuilder] Frame #{index} added.");
            }

        }

        private void AsyncSave(uint index, IPicture frame)
        {
            new Thread(() =>
            {
                try
                {
                    frame.SaveAsPng8bpp(PreviewPath ?? "preview.png");
                }
                catch (Exception ex)
                {
                    Log($"[VideoBuilder] WARN: Failed to save preview image: {ex.Message}", "warn");
                }
            })
            {
                IsBackground = true,
                Name = "Preview Saver"
            }.Start();
        }

        //public void Clear() => Cache.Clear();

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            if(!builder.Disposed)
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
                        Thread.Sleep(100);
                        continue;
                    }

                    if (Cache.ContainsKey(index))
                    {
                        builder.Append(Cache.TryRemove(index, out var f) ? f : throw new KeyNotFoundException());
                        FramePendedToWrite[index] = true;
                        index++;

                        if (DisposeFrameAfterEachWrite) f.Dispose();
                        if (DoGCAfterEachWrite)
                        {
                            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                            {
                                GC.Collect(2, GCCollectionMode.Forced, true, true);
                                GC.WaitForFullGCComplete();
                            }
                            else //mono don't support full blocking GC
                            {
                                GC.Collect();
                            }

                        }
                        Log($"[VideoBuilder] Frame #{index} wrote.");
                    }
                    else
                    {
                        Thread.Sleep(200);
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

        public void Finish(Func<uint, IPicture> regenerator, uint totalFrames = 0)
        {
            running = false;
            while (!Volatile.Read(ref stopped))
                Thread.Sleep(500);

            var missingFrames = new List<uint>();
            uint currentIndex = index;

            while (Cache.Count > 0 || missingFrames.Count > 0)
            {
                Console.Error.WriteLine($"@@{currentIndex},{totalFrames}");

                if (Cache.ContainsKey(currentIndex))
                {
                    builder.Append(Cache.TryRemove(currentIndex, out var f) ? f : throw new KeyNotFoundException());
                    Log($"[VideoBuilder] Frame #{currentIndex} added.");
                    currentIndex++;
                    continue;
                }

                if (missingFrames.Count == 0 && !Cache.ContainsKey(currentIndex))
                {
                    uint maxCheck = currentIndex + 100;
                    for (uint i = currentIndex; i < maxCheck; i++)
                    {
                        if (!Cache.ContainsKey(i) && (Cache.Count == 0 || i <= Cache.Keys.Max()) && i <= totalFrames)
                        {
                            missingFrames.Add(i);
                        }
                        else if (Cache.ContainsKey(i))
                        {
                            break;
                        }
                    }

                    if (missingFrames.Count > 0)
                    {
                        Log($"[VideoBuilder] WARN: Frames #{missingFrames[0]}-#{missingFrames[missingFrames.Count - 1]} not found, rebuilding {missingFrames.Count} frames...");
                        foreach (var frameIdx in missingFrames)
                        {
                            builder.Append(regenerator(frameIdx));
                        }
                        missingFrames.Clear();
                    }
                    else if (Cache.Count == 0)
                    {
                        break;
                    }
                }
                else if (missingFrames.Count > 0)
                {
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

            Dispose();
        }

        public static implicit operator VideoBuilder(VideoWriter v)
        {
            throw new NotImplementedException();
        }
    }

    public sealed unsafe class VideoWriter : IDisposable
    {
        public readonly int _width;
        public readonly int _height;
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
        public bool _isDisposed;
        private string _path;
        private int colorDepth = 8;

        public bool IsOpened => _fmtCtx != null;
        public bool Disposed => _isDisposed;

        public uint Index { get; set; } = 0;

        public VideoWriter(
            string outputPath,
            int width,
            int height,
            int fps = 30,
            string codecName = "libx264",
            AVPixelFormat dstPixelFormat = AVPixelFormat.AV_PIX_FMT_YUV420P,
            bool disposeAfterWrite = false)
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
            //FFmpegHelper.RegisterFFmpeg();
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
                catch (DirectoryNotFoundException)
                {
                    throw new DirectoryNotFoundException($"The directory '{Path.GetDirectoryName(_path)}' isn't exist. (FFmpeg error:{FFmpegHelper.GetErrorString(ret) ?? "unknown"}, code:{ret})");
                }
                catch (PathTooLongException ex)
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
                4, null, null, null);
            // SWS_BICUBIC == 4

            if (_sws == null) throw new InvalidOperationException("Couldn't get the SWS context.");
            Console.WriteLine($"[VideoBuilder] Successfully initialized encoder for {_path}");

        }

        public void Append(IPicture<ushort> picture)
        {
            ArgumentNullException.ThrowIfNull(picture);
            if (picture.Width != _width || picture.Height != _height)
                throw new ArgumentException($"The result \r\n{picture.GetDiagnosticsInfo()}\r\n size ({picture.Width}*{picture.Height}) is different from original size ({_width}*{_height}). Please check the source.");
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

            Index++;
        }


        public void Append(IPicture<byte> picture)
        {
            if (picture == null) throw new ArgumentNullException(nameof(picture));
            if (picture.Width != _width || picture.Height != _height)
                throw new ArgumentException($"The result \r\n{picture.GetDiagnosticsInfo()}\r\n size ({picture.Width}*{picture.Height}) is different from original size ({_width}*{_height}). Please check the source.");
            if (_isDisposed) throw new ObjectDisposedException(nameof(VideoBuilder));

            EnsureHeader();

            FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(_frameSrc), "make frame writable");
            FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(_frameDst), "make frame writable");

            byte* srcData0 = _frameSrc->data[0];
            int srcLinesize = _frameSrc->linesize[0];

            fixed (byte* pr = picture.r)
            fixed (byte* pg = picture.g)
            fixed (byte* pb = picture.b)
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
                            byte r8 = (pr != null && k < picture.r.Length) ? pr[k] : (byte)0;
                            byte g8 = (pg != null && k < picture.g.Length) ? pg[k] : (byte)0;
                            byte b8 = (pb != null && k < picture.b.Length) ? pb[k] : (byte)0;

                            ushort r16 = (ushort)(r8 * 257);
                            ushort g16 = (ushort)(g8 * 257);
                            ushort b16 = (ushort)(b8 * 257);

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
                            byte r8 = pr != null && k < picture.r.Length ? pr[k] : (byte)0;
                            byte g8 = pg != null && k < picture.g.Length ? pg[k] : (byte)0;
                            byte b8 = pb != null && k < picture.b.Length ? pb[k] : (byte)0;
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

            Index++;
        }

        public void Append(Picture16bpp pic) => Append((IPicture<ushort>)pic);
        public void Append(Picture8bpp pic) => Append((IPicture<byte>)pic);
        public void Append(IPicture source)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source.bitPerPixel == 16) Append((IPicture<ushort>)source);
            else if (source.bitPerPixel == 8) Append((IPicture<byte>)source);
            else throw new NotSupportedException($"Unsupported pixel mode.");
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

            Log($"[VideoBuilder] Successfully finished video writer for {_path}, total {Index} frame written.");

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


}