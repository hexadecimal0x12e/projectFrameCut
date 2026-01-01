using FFmpeg.AutoGen;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.EncodeAndDecode
{
    public sealed unsafe class VideoWriter : IVideoWriter
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
        private int colorDepth = 8;
        private bool _inited;

        public bool IsOpened => _fmtCtx != null;

        public uint Index { get; set; } = 0;
        //public string OutputOutputPath { get => OutputPath; }
        public IPicture.PicturePixelMode PixelMode => colorDepth;
        public int Fps => FramePerSecond;

        public uint DurationWritten => Index;

        public static bool DetectCodec(string codec)
        {
            if(FFmpegHelper.CodecUtils.GetCodecsByType(AVMediaType.AVMEDIA_TYPE_VIDEO, true).Find(c => c.Name.Equals(codec, StringComparison.OrdinalIgnoreCase)) != null) 
            {
                return true;
            }
            return  false;
        }


        public void Initialize()
        {
            if (OutputPath is null || _inited == true) return;
            if (Width <= 0 || Height <= 0 || FramePerSecond <= 0) throw new ArgumentOutOfRangeException("You set an invalid width, height or fps.");
            _pixelFormat = typeof(AVPixelFormat).GetEnumValues().Cast<AVPixelFormat>().FirstOrDefault(f => f.ToString().Equals(PixelFormat, StringComparison.OrdinalIgnoreCase), AVPixelFormat.AV_PIX_FMT_NONE);
            if (_pixelFormat is AVPixelFormat.AV_PIX_FMT_NONE)
            {
                throw new ArgumentException($"The pixel format '{PixelFormat}' is not found. Please check the pixel format name.");
            }

            AVFormatContext* oc = null;
            int ret = ffmpeg.avformat_alloc_output_context2(&oc, null, null, OutputPath);
            if (ret < 0 || oc == null)
            {
                try
                {
                    using (var fs = System.IO.File.Create(OutputPath))
                    {
                        fs.WriteByte(1);
                        goto writable;
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new FileLoadException($"projectFrameCut can't write the video file '{OutputPath}' because of no enough privileges. Try restart the projectFrameCut with administrator privileges, or modify the privileges of output dir. (FFmpeg error:{FFmpegHelper.GetErrorString(ret) ?? "unknown"}, code:{ret})", ex);
                }
                catch (DirectoryNotFoundException)
                {
                    throw new DirectoryNotFoundException($"The directory '{Path.GetDirectoryName(OutputPath)}' isn't exist. (FFmpeg error:{FFmpegHelper.GetErrorString(ret) ?? "unknown"}, code:{ret})");
                }
                catch (PathTooLongException ex)
                {
                    throw new FileLoadException($"projectFrameCut can't write the video file '{OutputPath}' because of path is too long. Try modify the temp directory in the settings. (FFmpeg error:{FFmpegHelper.GetErrorString(ret) ?? "unknown"}, code:{ret})", ex);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"projectFrameCut failed to write the file because of '{ex.Message}'. (FFmpeg error:{FFmpegHelper.GetErrorString(ret) ?? "unknown"}, code:{ret})", ex);
                }
            writable:
                FFmpegHelper.Throw(ret, "Prepare the file to write video (avformat_alloc_output_context2)");
            }
            _fmtCtx = oc;

            AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(CodecName);
            if (codec == null) throw new EntryPointNotFoundException($"Could not found a encoder '{CodecName}' for the video file '{OutputPath}'. Try reinstall projectFrameCut.");

            _videoStream = ffmpeg.avformat_new_stream(_fmtCtx, codec);
            if (_videoStream == null) throw new InvalidOperationException("Failed to create a stream to write video.");

            _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            if (_codecCtx == null) throw new InvalidOperationException("Failed to allocate a context for video.");

            _codecCtx->codec_id = codec->id;
            _codecCtx->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            _codecCtx->width = Width;
            _codecCtx->height = Height;
            _codecCtx->pix_fmt = _pixelFormat;
            _codecCtx->time_base = new AVRational { num = 1, den = FramePerSecond };
            _videoStream->time_base = _codecCtx->time_base;
            _codecCtx->framerate = new AVRational { num = FramePerSecond, den = 1 };
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
                FFmpegHelper.Throw(ffmpeg.avio_open(&_fmtCtx->pb, OutputPath, ffmpeg.AVIO_FLAG_WRITE), "avio_open");
            }

            _frameDst = ffmpeg.av_frame_alloc();
            _frameDst->format = (int)_pixelFormat;
            _frameDst->width = Width;
            _frameDst->height = Height;
            FFmpegHelper.Throw(ffmpeg.av_frame_get_buffer(_frameDst, 32), "av_frame_get_buffer(dst)");

            var srcPixFmt =
                (_pixelFormat == AVPixelFormat.AV_PIX_FMT_GBRP16LE ||
                 _pixelFormat == AVPixelFormat.AV_PIX_FMT_YUV420P16LE ||
                 _pixelFormat == AVPixelFormat.AV_PIX_FMT_RGBA64LE ||
                 _pixelFormat == AVPixelFormat.AV_PIX_FMT_BGRA64LE)
                ? AVPixelFormat.AV_PIX_FMT_RGBA64LE
                : AVPixelFormat.AV_PIX_FMT_RGBA;

            colorDepth = (srcPixFmt == AVPixelFormat.AV_PIX_FMT_RGBA64LE) ? 16 : 8;

            _frameSrc = ffmpeg.av_frame_alloc();
            _frameSrc->format = (int)srcPixFmt;
            _frameSrc->width = Width;
            _frameSrc->height = Height;
            FFmpegHelper.Throw(ffmpeg.av_frame_get_buffer(_frameSrc, 32), "av_frame_get_buffer(src)");

            _sws = ffmpeg.sws_getContext(
                Width, Height, srcPixFmt,
                Width, Height, _pixelFormat,
                4, null, null, null);
            // SWS_BICUBIC == 4

            if (_sws == null) throw new InvalidOperationException("Couldn't get the SWS context.");
            Console.WriteLine($"[VideoBuilder] Successfully initialized encoder for {OutputPath}");

            _inited = true;

        }

        public void Append(IPicture<ushort> picture)
        {
            ArgumentNullException.ThrowIfNull(picture);
            if (picture.Width != _width || picture.Height != _height)
                throw new ArgumentException("The result size is different from original size. Please check the source.");
            if (_isDisposed) throw new ObjectDisposedException(nameof(VideoBuilder));

            EnsureHeader();

            FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(_frameSrc), "make frame writable");
            FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(_frameDst), "make frame writable");

            byte* srcData0 = _frameSrc->data[0];
            int srcLinesize = _frameSrc->linesize[0];

            int rLen = picture.r?.Length ?? 0;
            int gLen = picture.g?.Length ?? 0;
            int bLen = picture.b?.Length ?? 0;
            int aLen = picture.a?.Length ?? 0;
            bool hasAlpha = picture.hasAlphaChannel;

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
            if (_isDisposed) throw new ObjectDisposedException(nameof(VideoBuilder));

            EnsureHeader();

            FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(_frameSrc), "make frame writable");
            FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(_frameDst), "make frame writable");

            byte* srcData0 = _frameSrc->data[0];
            int srcLinesize = _frameSrc->linesize[0];

            int rLen = picture.r?.Length ?? 0;
            int gLen = picture.g?.Length ?? 0;
            int bLen = picture.b?.Length ?? 0;
            int aLen = picture.a?.Length ?? 0;
            bool hasAlpha = picture.hasAlphaChannel;

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
            if (source.bitPerPixel == IPicture.PicturePixelMode.UShortPicture) Append((IPicture<ushort>)source);
            else if (source.bitPerPixel == IPicture.PicturePixelMode.BytePicture) Append((IPicture<byte>)source);
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

            Log($"[VideoBuilder] Successfully finished video writer for {OutputPath}, total {Index} frame written.");

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
