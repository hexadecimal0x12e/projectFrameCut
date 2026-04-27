using FFmpeg.AutoGen;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;
using System;
using System.Runtime.InteropServices;

namespace projectFrameCut.Render.EncodeAndDecode
{
    public sealed unsafe class HDRVideoWriter : IVideoWriter
    {
        private const float DefaultSdrMaximumBrightness = 100f;
        private const float DefaultHdrMaximumBrightness = 1000f;
        private const float PqReferencePeakNits = 10000f;
        private const float SdrHdrCrossoverBrightness = 300f;

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

        private string _outputPath = string.Empty;
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

        private string _codecName = string.Empty;
        public string CodecName
        {
            get => _codecName;
            set
            {
                if (_inited) throw new InvalidOperationException("Cannot modify property after initialization");
                _codecName = value;
            }
        }

        private string _pixelFormatString = string.Empty;
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
        private int _sourceColorDepth = 16;
        private bool _inited;

        private bool _enableHdrSignaling;
        private float _sdrHdrReferenceWhite = 203f;
        private float _streamMaximumBrightness = 203f;
        private uint _streamMaxCll = 203;
        private uint _streamMaxFall = 100;
        private bool _preferAppleHevcTag;

        public bool IsOpened => _fmtCtx != null;

        public uint Index { get; set; } = 0;
        public IPicture.PicturePixelMode PixelMode => _sourceColorDepth;
        public int Fps => FramePerSecond;

        public uint DurationWritten => Index;

        public IPicture.PicturePixelMode? TargetPPB => _sourceColorDepth;

        public static bool DetectCodec(string codec)
        {
            if (FFmpegHelper.CodecUtils.GetCodecsByType(AVMediaType.AVMEDIA_TYPE_VIDEO, true).Find(c => c.Name.Equals(codec, StringComparison.OrdinalIgnoreCase)) != null)
            {
                return true;
            }
            return false;
        }

        bool IVideoWriter.SupportCodec(string codecName)
        {
            if (string.Equals(codecName, "HDRVideoWriter", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(codecName, "HDRWriter", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return DetectCodec(codecName);
        }

        public void Initialize()
        {
            if (OutputPath is null || _inited) return;
            if (Width <= 0 || Height <= 0 || FramePerSecond <= 0) throw new ArgumentOutOfRangeException("You set an invalid width, height or fps.");
            if (Path.GetDirectoryName(OutputPath) is not string p || !Directory.Exists(p)) throw new DirectoryNotFoundException($"The target directory '{Path.GetDirectoryName(OutputPath)}' does not exist or it's invalid.");
            if (File.Exists(OutputPath)) throw new InvalidOperationException($"Video file {OutputPath} already exists.");
            if (!Enum.TryParse(PixelFormat, out _pixelFormat) || _pixelFormat == AVPixelFormat.AV_PIX_FMT_NONE)
            {
                throw new ArgumentException($"The pixel format '{PixelFormat}' is not found. Please check the pixel format name.");
            }

            _enableHdrSignaling = IsHdrPixelFormat(_pixelFormat);
            _preferAppleHevcTag = IsMp4FamilyOutput(OutputPath);

            AVFormatContext* oc = null;
            int ret = ffmpeg.avformat_alloc_output_context2(&oc, null, null, OutputPath);
            if (ret < 0 || oc == null)
            {
                try
                {
                    using (var fs = System.IO.File.OpenWrite(OutputPath))
                    {
                        fs.WriteByte(1);
                        FFmpegHelper.Throw(ret, "Init video stream (avformat_alloc_output_context2)");
                    }
                }
                catch (DirectoryNotFoundException)
                {
                    throw new DirectoryNotFoundException($"The target directory '{Path.GetDirectoryName(OutputPath)}' not exist. (FFmpeg error:{FFmpegHelper.GetErrorString(ret) ?? "unknown"}, code:{ret})");
                }
                catch (PathTooLongException ex)
                {
                    throw new FileLoadException($"projectFrameCut can't write the video file '{OutputPath}' because of path is too long. Try modify the temp directory in the settings. (FFmpeg error:{FFmpegHelper.GetErrorString(ret) ?? "unknown"}, code:{ret})", ex);
                }
                catch (IOException io)
                {
                    throw new IOException($"projectFrameCut can't write the target video file '{OutputPath}' because I/O operation fail: {io.Message} \r\n(FFmpeg error:{FFmpegHelper.GetErrorString(ret) ?? "unknown"}, code:{ret})", io);
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new FileLoadException($"projectFrameCut can't write the target video file '{OutputPath}' because of no enough privileges. Try modify the privileges of output dir. (FFmpeg error:{FFmpegHelper.GetErrorString(ret) ?? "unknown"}, code:{ret})", ex);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"projectFrameCut failed to write the file because of '{ex.Message}'. (FFmpeg error:{FFmpegHelper.GetErrorString(ret) ?? "unknown"}, code:{ret})", ex);
                }
            }
            _fmtCtx = oc;

            AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(CodecName);
            if (codec == null) throw new EntryPointNotFoundException($"Could not found the encoder '{CodecName}'. Try install codec extension-pack, or reinstall projectFrameCut.");

            if (_enableHdrSignaling && _preferAppleHevcTag && codec->id == AVCodecID.AV_CODEC_ID_HEVC)
            {
                AVPixelFormat requestedPixelFormat = _pixelFormat;
                AVPixelFormat adjustedPixelFormat = SelectAppleCompatibleHevcHdrPixelFormat(requestedPixelFormat);
                if (adjustedPixelFormat != requestedPixelFormat)
                {
                    _pixelFormat = adjustedPixelFormat;
                    Log($"[HDRVideoWriter] Adjusted HDR HEVC pixel format for Apple compatibility: {requestedPixelFormat} -> {_pixelFormat}.");
                }
            }

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
            _codecCtx->max_b_frames = _enableHdrSignaling ? 0 : 2;
            _codecCtx->bit_rate = 8_000_000;

            if (_enableHdrSignaling)
            {
                _codecCtx->color_primaries = AVColorPrimaries.AVCOL_PRI_BT2020;
                _codecCtx->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084;
                _codecCtx->colorspace = AVColorSpace.AVCOL_SPC_BT2020_NCL;
                _codecCtx->color_range = AVColorRange.AVCOL_RANGE_MPEG;
            }

            if ((_fmtCtx->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                _codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

            AVDictionary* opts = null;
            if (_codecCtx->codec_id == AVCodecID.AV_CODEC_ID_H264)
            {
                ffmpeg.av_dict_set(&opts, "preset", "veryfast", 0);
                ffmpeg.av_dict_set(&opts, "tune", "zerolatency", 0);
            }

            if (_enableHdrSignaling)
            {
                ConfigureHdrEncoderOptions(codec, &opts, _streamMaximumBrightness, _streamMaxCll, _streamMaxFall);
            }

            FFmpegHelper.Throw(ffmpeg.avcodec_open2(_codecCtx, codec, &opts), "Open target codec stream");
            ffmpeg.av_dict_free(&opts);

            FFmpegHelper.Throw(ffmpeg.avcodec_parameters_from_context(_videoStream->codecpar, _codecCtx),
                "avcodec_parameters_from_context");

            if (_preferAppleHevcTag && _codecCtx->codec_id == AVCodecID.AV_CODEC_ID_HEVC)
            {
                uint hvc1 = MakeFourCC('h', 'v', 'c', '1');
                _codecCtx->codec_tag = hvc1;
                _videoStream->codecpar->codec_tag = hvc1;
            }

            if (_enableHdrSignaling)
            {
                _videoStream->codecpar->color_primaries = _codecCtx->color_primaries;
                _videoStream->codecpar->color_trc = _codecCtx->color_trc;
                _videoStream->codecpar->color_space = _codecCtx->colorspace;
                _videoStream->codecpar->color_range = _codecCtx->color_range;
            }

            if ((_fmtCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                FFmpegHelper.Throw(ffmpeg.avio_open(&_fmtCtx->pb, OutputPath, ffmpeg.AVIO_FLAG_WRITE), "Open target audio/video stream");
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
                 _pixelFormat == AVPixelFormat.AV_PIX_FMT_BGRA64LE ||
                 _enableHdrSignaling)
                ? AVPixelFormat.AV_PIX_FMT_RGBA64LE
                : AVPixelFormat.AV_PIX_FMT_RGBA;

            _sourceColorDepth = (srcPixFmt == AVPixelFormat.AV_PIX_FMT_RGBA64LE) ? 16 : 8;

            _frameSrc = ffmpeg.av_frame_alloc();
            _frameSrc->format = (int)srcPixFmt;
            _frameSrc->width = Width;
            _frameSrc->height = Height;
            FFmpegHelper.Throw(ffmpeg.av_frame_get_buffer(_frameSrc, 32), "av_frame_get_buffer(src)");

            _sws = ffmpeg.sws_getContext(
                Width, Height, srcPixFmt,
                Width, Height, _pixelFormat,
                4, null, null, null);

            if (_sws == null) throw new InvalidOperationException("Couldn't get the SWS context.");

            // Keep BT.2020 matrix on both sides to match HDR signaling and avoid device-side hue shifts.
            const int SWS_CS_BT2020 = 9;
            int* srcCoeffs = ffmpeg.sws_getCoefficients(SWS_CS_BT2020);
            int* dstCoeffs = ffmpeg.sws_getCoefficients(SWS_CS_BT2020);
            if (srcCoeffs != null && dstCoeffs != null)
            {
                var srcColorSpace = new int_array4();
                srcColorSpace.UpdateFrom(new[] { srcCoeffs[0], srcCoeffs[1], srcCoeffs[2], srcCoeffs[3] });

                var dstColorSpace = new int_array4();
                dstColorSpace.UpdateFrom(new[] { dstCoeffs[0], dstCoeffs[1], dstCoeffs[2], dstCoeffs[3] });

                ffmpeg.sws_setColorspaceDetails(_sws, srcColorSpace, 1, dstColorSpace, 0, 0, 1 << 16, 1 << 16);
                Log("[HDRVideoWriter] Applied sws colorspace details: BT.2020 source <-> BT.2020 destination.");
            }
            else
            {
                Log("[HDRVideoWriter] WARNING: sws_getCoefficients returned null, using default sws colorspace conversion.");
            }

            Log($"[HDRVideoWriter] Successfully initialized encoder for {OutputPath}");

            _inited = true;
        }

        public void Append(HDRPicture16bpp picture)
        {
            Append((IPicture<ushort>)picture);
        }

        public void Append(IPicture<ushort> picture)
        {
            ArgumentNullException.ThrowIfNull(picture);
            if (picture.Width != _width || picture.Height != _height)
                throw new ArgumentException("The result size is different from original size. Please check the source.");
            if (_isDisposed) throw new ObjectDisposedException(nameof(HDRVideoWriter));

            EnsureHeader();

            FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(_frameSrc), "make frame writable");
            FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(_frameDst), "make frame writable");

            if (_enableHdrSignaling && picture is IHDRPicture<ushort> hdrPicture)
            {
                FillHdrSourceFrame(hdrPicture);
            }
            else if (_enableHdrSignaling)
            {
                FillSdrUshortSourceFrameAsHdr(picture);
            }
            else
            {
                FillUshortSourceFrame(picture);
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
            _frameDst->duration = 1;

            if (_enableHdrSignaling)
            {
                AttachHdrMetadata(_frameDst, _streamMaximumBrightness, _streamMaxCll, _streamMaxFall);
            }

            EncodeFrame(_frameDst);

            Index++;
        }

        public void Append(IPicture<byte> picture)
        {
            if (picture == null) throw new ArgumentNullException(nameof(picture));
            if (picture.Width != Width || picture.Height != Height)
                throw new ArgumentException("The result size is different from original size. Please check the source.");
            if (_isDisposed) throw new ObjectDisposedException(nameof(HDRVideoWriter));

            EnsureHeader();

            FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(_frameSrc), "make frame writable");
            FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(_frameDst), "make frame writable");

            if (_enableHdrSignaling)
            {
                FillSdrByteSourceFrameAsHdr(picture);
            }
            else
            {
                FillByteSourceFrame(picture);
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
            _frameDst->duration = 1;

            if (_enableHdrSignaling)
            {
                AttachHdrMetadata(_frameDst, _streamMaximumBrightness, _streamMaxCll, _streamMaxFall);
            }

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
            else throw new NotSupportedException("Unsupported pixel mode.");
        }

        private void FillUshortSourceFrame(IPicture<ushort> picture)
        {
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
                if (_sourceColorDepth == 16)
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
        }

        private void FillByteSourceFrame(IPicture<byte> picture)
        {
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
                if (_sourceColorDepth == 16)
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
        }

        private void FillSdrUshortSourceFrameAsHdr(IPicture<ushort> picture)
        {
            byte* srcData0 = _frameSrc->data[0];
            int srcLinesize = _frameSrc->linesize[0];

            int rLen = picture.r?.Length ?? 0;
            int gLen = picture.g?.Length ?? 0;
            int bLen = picture.b?.Length ?? 0;
            int aLen = picture.a?.Length ?? 0;
            bool hasAlpha = picture.hasAlphaChannel;

            float maxLumaSignal = 0f;

            fixed (ushort* pr = picture.r)
            fixed (ushort* pg = picture.g)
            fixed (ushort* pb = picture.b)
            fixed (float* pa = picture.a)
            {
                for (int y = 0; y < _height; y++)
                {
                    ushort* row16 = (ushort*)(srcData0 + y * srcLinesize);
                    int baseIndex = y * _width;
                    for (int x = 0; x < _width; x++)
                    {
                        int k = baseIndex + x;

                        float rSignal = ((pr != null && k < rLen) ? pr[k] : (ushort)0) / 65535f;
                        float gSignal = ((pg != null && k < gLen) ? pg[k] : (ushort)0) / 65535f;
                        float bSignal = ((pb != null && k < bLen) ? pb[k] : (ushort)0) / 65535f;

                        float rPq = ConvertSdrSignalToPq(rSignal, _sdrHdrReferenceWhite);
                        float gPq = ConvertSdrSignalToPq(gSignal, _sdrHdrReferenceWhite);
                        float bPq = ConvertSdrSignalToPq(bSignal, _sdrHdrReferenceWhite);

                        float lumaSignal = Math.Clamp(0.2627f * rSignal + 0.6780f * gSignal + 0.0593f * bSignal, 0f, 1f);
                        if (lumaSignal > maxLumaSignal) maxLumaSignal = lumaSignal;

                        ushort a16 = 65535;
                        if (hasAlpha && pa != null && k < aLen)
                        {
                            float af = pa[k];
                            if (!float.IsFinite(af)) af = 1f;
                            af = Math.Clamp(af, 0f, 1f);
                            a16 = (ushort)(af * 65535f + 0.5f);
                        }

                        int off = x * 4;
                        row16[off + 0] = (ushort)(rPq * 65535f + 0.5f);
                        row16[off + 1] = (ushort)(gPq * 65535f + 0.5f);
                        row16[off + 2] = (ushort)(bPq * 65535f + 0.5f);
                        row16[off + 3] = a16;
                    }
                }
            }

            UpdateStreamLightLevelFromSignal(maxLumaSignal, _sdrHdrReferenceWhite);
        }

        private void FillSdrByteSourceFrameAsHdr(IPicture<byte> picture)
        {
            byte* srcData0 = _frameSrc->data[0];
            int srcLinesize = _frameSrc->linesize[0];

            int rLen = picture.r?.Length ?? 0;
            int gLen = picture.g?.Length ?? 0;
            int bLen = picture.b?.Length ?? 0;
            int aLen = picture.a?.Length ?? 0;
            bool hasAlpha = picture.hasAlphaChannel;

            float maxLumaSignal = 0f;

            fixed (byte* pr = picture.r)
            fixed (byte* pg = picture.g)
            fixed (byte* pb = picture.b)
            fixed (float* pa = picture.a)
            {
                for (int y = 0; y < _height; y++)
                {
                    ushort* row16 = (ushort*)(srcData0 + y * srcLinesize);
                    int baseIndex = y * _width;
                    for (int x = 0; x < _width; x++)
                    {
                        int k = baseIndex + x;

                        float rSignal = ((pr != null && k < rLen) ? pr[k] : (byte)0) / 255f;
                        float gSignal = ((pg != null && k < gLen) ? pg[k] : (byte)0) / 255f;
                        float bSignal = ((pb != null && k < bLen) ? pb[k] : (byte)0) / 255f;

                        float rPq = ConvertSdrSignalToPq(rSignal, _sdrHdrReferenceWhite);
                        float gPq = ConvertSdrSignalToPq(gSignal, _sdrHdrReferenceWhite);
                        float bPq = ConvertSdrSignalToPq(bSignal, _sdrHdrReferenceWhite);

                        float lumaSignal = Math.Clamp(0.2627f * rSignal + 0.6780f * gSignal + 0.0593f * bSignal, 0f, 1f);
                        if (lumaSignal > maxLumaSignal) maxLumaSignal = lumaSignal;

                        ushort a16 = 65535;
                        if (hasAlpha && pa != null && k < aLen)
                        {
                            float af = pa[k];
                            if (!float.IsFinite(af)) af = 1f;
                            af = Math.Clamp(af, 0f, 1f);
                            a16 = (ushort)(af * 65535f + 0.5f);
                        }

                        int off = x * 4;
                        row16[off + 0] = (ushort)(rPq * 65535f + 0.5f);
                        row16[off + 1] = (ushort)(gPq * 65535f + 0.5f);
                        row16[off + 2] = (ushort)(bPq * 65535f + 0.5f);
                        row16[off + 3] = a16;
                    }
                }
            }

            UpdateStreamLightLevelFromSignal(maxLumaSignal, _sdrHdrReferenceWhite);
        }

        private void FillHdrSourceFrame(IHDRPicture<ushort> picture)
        {
            byte* srcData0 = _frameSrc->data[0];
            int srcLinesize = _frameSrc->linesize[0];

            int rLen = picture.r?.Length ?? 0;
            int gLen = picture.g?.Length ?? 0;
            int bLen = picture.b?.Length ?? 0;
            int aLen = picture.a?.Length ?? 0;
            bool hasAlpha = picture.hasAlphaChannel;
            int pixels = _width * _height;

            float[]? brightness = (picture.Brightness != null && picture.Brightness.Length == pixels)
                ? picture.Brightness
                : null;

            float frameMaximumBrightness = NormalizeMaximumBrightness(picture.MaximumBrightness);
            var lightLevel = ComputeContentLightLevel(picture, brightness, frameMaximumBrightness);
            _streamMaximumBrightness = Math.Max(_streamMaximumBrightness, frameMaximumBrightness);
            _streamMaxCll = Math.Max(_streamMaxCll, lightLevel.MaxCLL);
            _streamMaxFall = Math.Max(_streamMaxFall, lightLevel.MaxFALL);

            bool treatAsSdrSource = frameMaximumBrightness <= SdrHdrCrossoverBrightness;

            fixed (ushort* pr = picture.r)
            fixed (ushort* pg = picture.g)
            fixed (ushort* pb = picture.b)
            fixed (float* pa = picture.a)
            fixed (float* pBrightness = brightness)
            {
                for (int y = 0; y < _height; y++)
                {
                    ushort* row16 = (ushort*)(srcData0 + y * srcLinesize);
                    int baseIndex = y * _width;
                    for (int x = 0; x < _width; x++)
                    {
                        int k = baseIndex + x;
                        float r = ((pr != null && k < rLen) ? pr[k] : (ushort)0) / 65535f;
                        float g = ((pg != null && k < gLen) ? pg[k] : (ushort)0) / 65535f;
                        float b = ((pb != null && k < bLen) ? pb[k] : (ushort)0) / 65535f;

                        float mappedR;
                        float mappedG;
                        float mappedB;

                        if (treatAsSdrSource)
                        {
                            mappedR = ConvertSdrSignalToPq(r, frameMaximumBrightness);
                            mappedG = ConvertSdrSignalToPq(g, frameMaximumBrightness);
                            mappedB = ConvertSdrSignalToPq(b, frameMaximumBrightness);
                        }
                        else
                        {
                            // True HDR path: RGB channels are already signal-domain values from decoder/composition.
                            // Reconstructing luma from Brightness and re-scaling RGB here can introduce hue shift.
                            mappedR = Math.Clamp(r, 0f, 1f);
                            mappedG = Math.Clamp(g, 0f, 1f);
                            mappedB = Math.Clamp(b, 0f, 1f);
                        }

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
                        row16[off + 0] = (ushort)(mappedR * 65535f + 0.5f);
                        row16[off + 1] = (ushort)(mappedG * 65535f + 0.5f);
                        row16[off + 2] = (ushort)(mappedB * 65535f + 0.5f);
                        row16[off + 3] = a16;
                    }
                }
            }
        }

        private void EnsureHeader()
        {
            if (_isHeaderWritten) return;

            AVDictionary* muxerOpts = null;
            if (_preferAppleHevcTag)
            {
                ffmpeg.av_dict_set(&muxerOpts, "movflags", "+faststart+write_colr", 0);
            }

            FFmpegHelper.Throw(ffmpeg.avformat_write_header(_fmtCtx, &muxerOpts), "avformat_write_header");
            ffmpeg.av_dict_free(&muxerOpts);
            _isHeaderWritten = true;
        }

        private void EncodeFrame(AVFrame* frame)
        {
            FFmpegHelper.Throw(ffmpeg.avcodec_send_frame(_codecCtx, frame), "avcodec_send_frame");

            while (true)
            {
                AVPacket* pkt = ffmpeg.av_packet_alloc();
                if (pkt == null)
                {
                    throw new InvalidOperationException("Failed to allocate AVPacket.");
                }

                int ret = ffmpeg.avcodec_receive_packet(_codecCtx, pkt);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                {
                    ffmpeg.av_packet_free(&pkt);
                    return;
                }

                FFmpegHelper.Throw(ret, "avcodec_receive_packet");

                if (pkt->duration <= 0)
                {
                    pkt->duration = 1;
                }

                ffmpeg.av_packet_rescale_ts(pkt, _codecCtx->time_base, _videoStream->time_base);
                pkt->stream_index = _videoStream->index;

                FFmpegHelper.Throw(ffmpeg.av_interleaved_write_frame(_fmtCtx, pkt), "av_interleaved_write_frame");
                ffmpeg.av_packet_free(&pkt);
            }
        }

        public void Finish()
        {
            if (_isDisposed || Index <= 0) return;

            FFmpegHelper.Throw(ffmpeg.avcodec_send_frame(_codecCtx, null), "avcodec_send_frame(flush)");
            while (true)
            {
                AVPacket* pkt = ffmpeg.av_packet_alloc();
                if (pkt == null)
                {
                    throw new InvalidOperationException("Failed to allocate AVPacket.");
                }

                int ret = ffmpeg.avcodec_receive_packet(_codecCtx, pkt);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                {
                    ffmpeg.av_packet_free(&pkt);
                    break;
                }

                FFmpegHelper.Throw(ret, "avcodec_receive_packet(flush)");
                if (pkt->duration <= 0)
                {
                    pkt->duration = 1;
                }
                ffmpeg.av_packet_rescale_ts(pkt, _codecCtx->time_base, _videoStream->time_base);
                pkt->stream_index = _videoStream->index;
                FFmpegHelper.Throw(ffmpeg.av_interleaved_write_frame(_fmtCtx, pkt), "write_frame(flush)");
                ffmpeg.av_packet_free(&pkt);
            }

            if (_isHeaderWritten)
            {
                FFmpegHelper.Throw(ffmpeg.av_write_trailer(_fmtCtx), "av_write_trailer");
            }

            Log($"[HDRVideoWriter] Successfully finished video writer for {OutputPath}, total {Index} frame written.");
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

        ~HDRVideoWriter()
        {
            try
            {
                ReleaseUnmanaged();
            }
            catch
            {
            }
        }

        private static bool IsHdrPixelFormat(AVPixelFormat fmt)
        {
            return fmt == AVPixelFormat.AV_PIX_FMT_YUV420P10LE
                || fmt == AVPixelFormat.AV_PIX_FMT_YUV422P10LE
                || fmt == AVPixelFormat.AV_PIX_FMT_YUV444P10LE
                || fmt == AVPixelFormat.AV_PIX_FMT_YUV420P12LE
                || fmt == AVPixelFormat.AV_PIX_FMT_YUV422P12LE
                || fmt == AVPixelFormat.AV_PIX_FMT_YUV444P12LE
                || fmt == AVPixelFormat.AV_PIX_FMT_P010LE
                || fmt == AVPixelFormat.AV_PIX_FMT_P012LE
                || fmt == AVPixelFormat.AV_PIX_FMT_GBRP10LE
                || fmt == AVPixelFormat.AV_PIX_FMT_GBRP12LE;
        }

        private static bool IsMp4FamilyOutput(string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return false;
            }

            string ext = Path.GetExtension(outputPath);
            return ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase);
        }

        private static uint MakeFourCC(char c0, char c1, char c2, char c3)
        {
            return (uint)c0
                | ((uint)c1 << 8)
                | ((uint)c2 << 16)
                | ((uint)c3 << 24);
        }

        private static AVPixelFormat SelectAppleCompatibleHevcHdrPixelFormat(AVPixelFormat requested)
        {
            if (requested == AVPixelFormat.AV_PIX_FMT_YUV420P10LE || requested == AVPixelFormat.AV_PIX_FMT_P010LE)
            {
                return requested;
            }

            if (requested == AVPixelFormat.AV_PIX_FMT_P012LE)
            {
                return AVPixelFormat.AV_PIX_FMT_P010LE;
            }

            return AVPixelFormat.AV_PIX_FMT_YUV420P10LE;
        }

        private static void ConfigureHdrEncoderOptions(AVCodec* codec, AVDictionary** opts, float maximumBrightness, uint maxCll, uint maxFall)
        {
            if (codec == null || codec->name == null)
            {
                return;
            }

            string codecName = Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? string.Empty;
            if (!codecName.Equals("libx265", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string masterDisplay = BuildX265MasterDisplay(maximumBrightness);
            string x265Params =
                $"hdr-opt=1:repeat-headers=1:master-display={masterDisplay}:max-cll={maxCll},{maxFall}";

            ffmpeg.av_dict_set(opts, "x265-params", x265Params, 0);
        }

        private static string BuildX265MasterDisplay(float maximumBrightness)
        {
            int maxL = (int)Math.Clamp(Math.Round(maximumBrightness * 10000.0), 1, int.MaxValue);
            const int minL = 50;
            return $"G(8500,39850)B(6550,2300)R(35400,14600)WP(15635,16450)L({maxL},{minL})";
        }

        private static float NormalizeMaximumBrightness(float input)
        {
            if (!float.IsFinite(input) || input <= 0f)
                return DefaultHdrMaximumBrightness;

            return Math.Clamp(input, DefaultSdrMaximumBrightness, PqReferencePeakNits);
        }

        private static (uint MaxCLL, uint MaxFALL) ComputeContentLightLevel(IPicture<ushort> picture, float[]? brightness, float maximumBrightness)
        {
            if (brightness != null && brightness.Length == picture.Pixels)
            {
                double sumNits = 0;
                float maxNits = 0f;

                for (int i = 0; i < brightness.Length; i++)
                {
                    float b = brightness[i];
                    if (!float.IsFinite(b) || b < 0f) b = 0f;
                    if (b > 1f) b = 1f;
                    float nits = Math.Clamp(b * maximumBrightness, 0f, PqReferencePeakNits);
                    if (nits > maxNits) maxNits = nits;
                    sumNits += nits;
                }

                uint maxCll = (uint)Math.Clamp((int)Math.Round(maxNits), 1, 65535);
                uint maxFall = (uint)Math.Clamp((int)Math.Round(sumNits / brightness.Length), 1, 65535);
                if (maxFall > maxCll) maxFall = maxCll;
                return (maxCll, maxFall);
            }

            double sumSignal = 0;
            float maxSignal = 0f;
            for (int i = 0; i < picture.Pixels; i++)
            {
                float r = picture.r[i] / 65535f;
                float g = picture.g[i] / 65535f;
                float b = picture.b[i] / 65535f;
                float luma = Math.Clamp(0.2627f * r + 0.6780f * g + 0.0593f * b, 0f, 1f);
                if (luma > maxSignal) maxSignal = luma;
                sumSignal += luma;
            }

            uint fallbackMaxCll = (uint)Math.Clamp((int)Math.Round(maxSignal * maximumBrightness), 1, 65535);
            uint fallbackMaxFall = (uint)Math.Clamp((int)Math.Round((sumSignal / picture.Pixels) * maximumBrightness), 1, 65535);
            if (fallbackMaxFall > fallbackMaxCll) fallbackMaxFall = fallbackMaxCll;
            return (fallbackMaxCll, fallbackMaxFall);
        }

        private static float EncodePqSignal(float normalizedLuminance)
        {
            float l = Math.Clamp(normalizedLuminance, 0f, 1f);

            const float m1 = 2610f / 16384f;
            const float m2 = 2523f / 32f;
            const float c1 = 3424f / 4096f;
            const float c2 = 2413f / 128f;
            const float c3 = 2392f / 128f;

            if (l <= 0f)
            {
                return 0f;
            }

            double p = Math.Pow(l, m1);
            double num = c1 + c2 * p;
            double den = 1.0 + c3 * p;
            double e = Math.Pow(num / den, m2);
            return (float)Math.Clamp(e, 0.0, 1.0);
        }

        private static float SrgbToLinear(float value)
        {
            float v = Math.Clamp(value, 0f, 1f);
            if (v <= 0.04045f)
            {
                return v / 12.92f;
            }

            return (float)Math.Pow((v + 0.055f) / 1.055f, 2.4f);
        }

        private static float ConvertSdrSignalToPq(float signal, float targetMaxNits)
        {
            float linear = SrgbToLinear(signal);
            float nits = Math.Clamp(linear * targetMaxNits, 0f, PqReferencePeakNits);
            return EncodePqSignal(nits / PqReferencePeakNits);
        }

        private void UpdateStreamLightLevelFromSignal(float maxSignal, float referenceWhiteNits)
        {
            float clampedSignal = Math.Clamp(maxSignal, 0f, 1f);
            float clampedRef = NormalizeMaximumBrightness(referenceWhiteNits);
            float maxNits = clampedSignal * clampedRef;

            _streamMaximumBrightness = Math.Max(_streamMaximumBrightness, clampedRef);

            uint derivedMaxCll = (uint)Math.Clamp((int)Math.Round(maxNits), 1, 65535);
            uint derivedMaxFall = (uint)Math.Clamp((int)Math.Round(maxNits * 0.6f), 1, 65535);
            if (derivedMaxFall > derivedMaxCll) derivedMaxFall = derivedMaxCll;

            _streamMaxCll = Math.Max(_streamMaxCll, derivedMaxCll);
            _streamMaxFall = Math.Max(_streamMaxFall, derivedMaxFall);
        }

        private static void AttachHdrMetadata(AVFrame* frame, float maximumBrightness, uint maxCll, uint maxFall)
        {
            frame->color_primaries = AVColorPrimaries.AVCOL_PRI_BT2020;
            frame->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084;
            frame->colorspace = AVColorSpace.AVCOL_SPC_BT2020_NCL;
            frame->color_range = AVColorRange.AVCOL_RANGE_MPEG;

            AVFrameSideData* masteringSideData = ffmpeg.av_frame_new_side_data(
                frame,
                AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA,
                (ulong)sizeof(AVMasteringDisplayMetadata));

            if (masteringSideData != null && masteringSideData->data != null)
            {
                AVMasteringDisplayMetadata* mastering = (AVMasteringDisplayMetadata*)masteringSideData->data;
                *mastering = default;

                mastering->has_primaries = 1;
                mastering->has_luminance = 1;

                AVRational_array2 red = default;
                red.UpdateFrom(new[]
                {
                    new AVRational { num = 35400, den = 50000 },
                    new AVRational { num = 14600, den = 50000 }
                });

                AVRational_array2 green = default;
                green.UpdateFrom(new[]
                {
                    new AVRational { num = 8500, den = 50000 },
                    new AVRational { num = 39850, den = 50000 }
                });

                AVRational_array2 blue = default;
                blue.UpdateFrom(new[]
                {
                    new AVRational { num = 6550, den = 50000 },
                    new AVRational { num = 2300, den = 50000 }
                });

                AVRational_array3x2 primaries = default;
                primaries.UpdateFrom(new[] { red, green, blue });
                mastering->display_primaries = primaries;

                AVRational_array2 whitePoint = default;
                whitePoint.UpdateFrom(new[]
                {
                    new AVRational { num = 15635, den = 50000 },
                    new AVRational { num = 16450, den = 50000 }
                });
                mastering->white_point = whitePoint;
                mastering->max_luminance = new AVRational
                {
                    num = (int)Math.Clamp(Math.Round(maximumBrightness * 10000.0), 1, int.MaxValue),
                    den = 10000
                };
                mastering->min_luminance = new AVRational { num = 50, den = 10000 };
            }

            AVFrameSideData* contentLightSideData = ffmpeg.av_frame_new_side_data(
                frame,
                AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL,
                (ulong)sizeof(AVContentLightMetadata));

            if (contentLightSideData != null && contentLightSideData->data != null)
            {
                AVContentLightMetadata* contentLight = (AVContentLightMetadata*)contentLightSideData->data;
                *contentLight = default;
                contentLight->MaxCLL = maxCll;
                contentLight->MaxFALL = maxFall;
            }
        }
    }
}
