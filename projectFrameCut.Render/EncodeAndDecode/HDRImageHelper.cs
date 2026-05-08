using FFmpeg.AutoGen;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace projectFrameCut.Render.EncodeAndDecode
{
    public static unsafe class HDRImageHelper
    {
        private const float DefaultSdrMaximumBrightness = 100f;
        private const float DefaultHdrMaximumBrightness = 1000f;
        private const float HdrReferencePeakNits = 10000f;

        public static void SaveAsHeif(this HDRPicture16bpp picture, string filePath)
        {
            ArgumentNullException.ThrowIfNull(picture);
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Output path is null or empty.", nameof(filePath));
            if (picture.Width <= 0 || picture.Height <= 0)
                throw new ArgumentOutOfRangeException(nameof(picture), $"Invalid image size: {picture.Width}x{picture.Height}");

            int expectedPixels = checked(picture.Width * picture.Height);
            if (picture.r == null || picture.g == null || picture.b == null)
                throw new InvalidOperationException("HDR picture RGB channel data is missing.");
            if (picture.r.Length != expectedPixels || picture.g.Length != expectedPixels || picture.b.Length != expectedPixels)
                throw new InvalidOperationException("HDR picture RGB channel length does not match Width*Height.");

            string? outputDir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrWhiteSpace(outputDir) || !Directory.Exists(outputDir))
                throw new DirectoryNotFoundException($"The target directory '{outputDir}' does not exist.");

            if (File.Exists(filePath))
                throw new InvalidOperationException($"Target file '{filePath}' already exists.");

            AVFormatContext* fmtCtx = null;
            AVCodecContext* codecCtx = null;
            AVStream* stream = null;
            AVFrame* srcFrame = null;
            AVFrame* dstFrame = null;
            SwsContext* sws = null;
            AVDictionary* codecOpts = null;

            bool headerWritten = false;
            float maximumBrightness = NormalizeMaximumBrightness(picture.MaximumBrightness);
            (uint maxCll, uint maxFall) = ComputeContentLightLevel(picture, maximumBrightness);
            var sw = Stopwatch.StartNew();

            try
            {
                FFmpegHelper.SetupFFmpegLogging(ffmpeg.AV_LOG_WARNING);

                OpenHeifOutputContext(filePath, &fmtCtx);

                AVCodec* codec = FindHevcEncoder(fmtCtx);
                if (codec == null)
                    throw new NotSupportedException("No HEVC encoder found in current FFmpeg build.");

                stream = ffmpeg.avformat_new_stream(fmtCtx, codec);
                if (stream == null)
                    throw new InvalidOperationException("Failed to create HEIF video stream.");

                codecCtx = ffmpeg.avcodec_alloc_context3(codec);
                if (codecCtx == null)
                    throw new InvalidOperationException("Failed to allocate HEVC codec context.");

                AVPixelFormat targetPixelFormat = SelectTargetPixelFormat(codec, picture.Width, picture.Height);
                if (targetPixelFormat == AVPixelFormat.AV_PIX_FMT_NONE)
                    throw new NotSupportedException("No HDR-capable 10-bit pixel format is supported by the selected HEVC encoder.");

                codecCtx->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
                codecCtx->codec_id = codec->id;
                codecCtx->width = picture.Width;
                codecCtx->height = picture.Height;
                codecCtx->pix_fmt = targetPixelFormat;
                codecCtx->time_base = new AVRational { num = 1, den = 1 };
                codecCtx->framerate = new AVRational { num = 1, den = 1 };
                codecCtx->gop_size = 1;
                codecCtx->max_b_frames = 0;
                codecCtx->color_primaries = AVColorPrimaries.AVCOL_PRI_BT2020;
                codecCtx->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084;
                codecCtx->colorspace = AVColorSpace.AVCOL_SPC_BT2020_NCL;
                codecCtx->color_range = AVColorRange.AVCOL_RANGE_MPEG;

                if ((fmtCtx->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                    codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

                ConfigureEncoderOptions(codec, maximumBrightness, maxCll, maxFall, &codecOpts);

                FFmpegHelper.Throw(ffmpeg.avcodec_open2(codecCtx, codec, &codecOpts), "avcodec_open2(HEVC)");
                ffmpeg.av_dict_free(&codecOpts);

                stream->time_base = codecCtx->time_base;
                FFmpegHelper.Throw(ffmpeg.avcodec_parameters_from_context(stream->codecpar, codecCtx), "avcodec_parameters_from_context(HEVC)");

                stream->codecpar->color_primaries = codecCtx->color_primaries;
                stream->codecpar->color_trc = codecCtx->color_trc;
                stream->codecpar->color_space = codecCtx->colorspace;
                stream->codecpar->color_range = codecCtx->color_range;

                if ((fmtCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                {
                    FFmpegHelper.Throw(ffmpeg.avio_open(&fmtCtx->pb, filePath, ffmpeg.AVIO_FLAG_WRITE), "avio_open(HEIF output)");
                }

                FFmpegHelper.Throw(ffmpeg.avformat_write_header(fmtCtx, null), "avformat_write_header(HEIF)");
                headerWritten = true;

                srcFrame = ffmpeg.av_frame_alloc();
                if (srcFrame == null)
                    throw new InvalidOperationException("Failed to allocate source frame.");
                srcFrame->format = (int)AVPixelFormat.AV_PIX_FMT_BGR48LE;
                srcFrame->width = picture.Width;
                srcFrame->height = picture.Height;
                FFmpegHelper.Throw(ffmpeg.av_frame_get_buffer(srcFrame, 32), "av_frame_get_buffer(src BGR48)");

                dstFrame = ffmpeg.av_frame_alloc();
                if (dstFrame == null)
                    throw new InvalidOperationException("Failed to allocate destination frame.");
                dstFrame->format = (int)targetPixelFormat;
                dstFrame->width = picture.Width;
                dstFrame->height = picture.Height;
                FFmpegHelper.Throw(ffmpeg.av_frame_get_buffer(dstFrame, 32), "av_frame_get_buffer(dst HDR)");

                FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(srcFrame), "av_frame_make_writable(src)");
                FillBgr48SourceFrame(srcFrame, picture);

                FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(dstFrame), "av_frame_make_writable(dst)");

                sws = ffmpeg.sws_getContext(
                    picture.Width,
                    picture.Height,
                    AVPixelFormat.AV_PIX_FMT_BGR48LE,
                    picture.Width,
                    picture.Height,
                    targetPixelFormat,
                    4,
                    null,
                    null,
                    null);

                if (sws == null)
                    throw new InvalidOperationException("Failed to create swscale context for HDR HEIF conversion.");

                int scaled = ffmpeg.sws_scale(
                    sws,
                    srcFrame->data,
                    srcFrame->linesize,
                    0,
                    picture.Height,
                    dstFrame->data,
                    dstFrame->linesize);

                if (scaled <= 0)
                    throw new InvalidOperationException($"sws_scale failed for HDR HEIF conversion (returned {scaled}).");

                dstFrame->pts = 0;
                AttachHdrMetadata(dstFrame, maximumBrightness, maxCll, maxFall);

                EncodeFrameAndWritePackets(fmtCtx, codecCtx, stream, dstFrame);
                FlushEncoderAndWritePackets(fmtCtx, codecCtx, stream);

                FFmpegHelper.Throw(ffmpeg.av_write_trailer(fmtCtx), "av_write_trailer(HEIF)");
                headerWritten = false;

                Log($"[HDRImageHelper] Saved HDR HEIC image to '{filePath}', size={picture.Width}x{picture.Height}, MaxBrightness={maximumBrightness:0.###} nits, MaxCLL={maxCll}, MaxFALL={maxFall}. Elapsed={sw.Elapsed}.", "info");
            }
            catch (Exception ex)
            {
                Log($"[HDRImageHelper] Failed to save HDR HEIC image '{filePath}': {ex.Message}", "error");
                throw;
            }
            finally
            {
                if (codecOpts != null)
                {
                    ffmpeg.av_dict_free(&codecOpts);
                }

                if (srcFrame != null)
                {
                    ffmpeg.av_frame_free(&srcFrame);
                }

                if (dstFrame != null)
                {
                    ffmpeg.av_frame_free(&dstFrame);
                }

                if (sws != null)
                {
                    ffmpeg.sws_freeContext(sws);
                    sws = null;
                }

                if (codecCtx != null)
                {
                    ffmpeg.avcodec_free_context(&codecCtx);
                }

                if (fmtCtx != null)
                {
                    if (headerWritten)
                    {
                        try
                        {
                            ffmpeg.av_write_trailer(fmtCtx);
                        }
                        catch
                        {
                            // Keep cleanup non-throwing.
                        }
                    }

                    if (fmtCtx->pb != null)
                    {
                        ffmpeg.avio_closep(&fmtCtx->pb);
                    }

                    ffmpeg.avformat_free_context(fmtCtx);
                    fmtCtx = null;
                }
            }
        }

        private static void OpenHeifOutputContext(string filePath, AVFormatContext** fmtCtx)
        {
            int ret = ffmpeg.avformat_alloc_output_context2(fmtCtx, null, null, filePath);
            if (ret >= 0 && *fmtCtx != null)
                return;

            if (*fmtCtx != null)
            {
                ffmpeg.avformat_free_context(*fmtCtx);
                *fmtCtx = null;
            }

            int heifRet = ffmpeg.avformat_alloc_output_context2(fmtCtx, null, "heif", filePath);
            if (heifRet >= 0 && *fmtCtx != null)
                return;

            if (*fmtCtx != null)
            {
                ffmpeg.avformat_free_context(*fmtCtx);
                *fmtCtx = null;
            }

            int heicRet = ffmpeg.avformat_alloc_output_context2(fmtCtx, null, "heic", filePath);
            if (heicRet >= 0 && *fmtCtx != null)
                return;

            string? defaultErr = FFmpegHelper.GetErrorString(ret);
            string? heifErr = FFmpegHelper.GetErrorString(heifRet);
            string? heicErr = FFmpegHelper.GetErrorString(heicRet);
            throw new NotSupportedException(
                $"Failed to initialize HEIF output context for '{filePath}'. This FFmpeg build may not include HEIF muxer. " +
                $"default='{defaultErr}', heif='{heifErr}', heic='{heicErr}'.");
        }

        private static AVCodec* FindHevcEncoder(AVFormatContext* fmtCtx)
        {
            if (fmtCtx != null && fmtCtx->oformat != null && fmtCtx->oformat->video_codec != AVCodecID.AV_CODEC_ID_NONE)
            {
                AVCodec* byMuxer = ffmpeg.avcodec_find_encoder(fmtCtx->oformat->video_codec);
                if (byMuxer != null)
                    return byMuxer;
            }

            AVCodec* byId = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_HEVC);
            if (byId != null)
                return byId;

            string[] candidates =
            [
                "libx265",
                "hevc",
                "hevc_nvenc",
                "hevc_qsv",
                "hevc_amf",
                "hevc_videotoolbox",
            ];

            for (int i = 0; i < candidates.Length; i++)
            {
                AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(candidates[i]);
                if (codec != null)
                    return codec;
            }

            return null;
        }

        private static AVPixelFormat SelectTargetPixelFormat(AVCodec* codec, int width, int height)
        {
            bool evenSize = ((width & 1) == 0) && ((height & 1) == 0);

            AVPixelFormat[] preferred = evenSize
                ?
                [
                    AVPixelFormat.AV_PIX_FMT_YUV420P10LE,
                    AVPixelFormat.AV_PIX_FMT_YUV444P10LE,
                    AVPixelFormat.AV_PIX_FMT_GBRP10LE,
                ]
                :
                [
                    AVPixelFormat.AV_PIX_FMT_YUV444P10LE,
                    AVPixelFormat.AV_PIX_FMT_GBRP10LE,
                ];

            for (int i = 0; i < preferred.Length; i++)
            {
                if (IsPixelFormatSupported(codec, preferred[i]))
                    return preferred[i];
            }

            return AVPixelFormat.AV_PIX_FMT_NONE;
        }

        private static bool IsPixelFormatSupported(AVCodec* codec, AVPixelFormat pixelFormat)
        {
            if (codec == null || codec->pix_fmts == null)
                return true;

            for (AVPixelFormat* p = codec->pix_fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            {
                if (*p == pixelFormat)
                    return true;
            }

            return false;
        }

        private static void ConfigureEncoderOptions(AVCodec* codec, float maximumBrightness, uint maxCll, uint maxFall, AVDictionary** codecOpts)
        {
            if (codec == null || codec->name == null)
                return;

            string codecName = Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? string.Empty;
            if (!codecName.Equals("libx265", StringComparison.OrdinalIgnoreCase))
                return;

            string masterDisplay = BuildX265MasterDisplay(maximumBrightness);
            string x265Params =
                $"hdr-opt=1:repeat-headers=1:keyint=1:min-keyint=1:scenecut=0:master-display={masterDisplay}:max-cll={maxCll},{maxFall}";

            ffmpeg.av_dict_set(codecOpts, "preset", "slow", 0);
            ffmpeg.av_dict_set(codecOpts, "x265-params", x265Params, 0);
        }

        private static string BuildX265MasterDisplay(float maximumBrightness)
        {
            // BT.2020 primaries + D65 white point.
            // x265 string format: G(x,y)B(x,y)R(x,y)WP(x,y)L(max,min)
            int maxL = (int)Math.Clamp(Math.Round(maximumBrightness * 10000.0), 1, int.MaxValue);
            const int minL = 50; // 0.005 nits in 0.0001 nit units
            return $"G(8500,39850)B(6550,2300)R(35400,14600)WP(15635,16450)L({maxL},{minL})";
        }

        private static float NormalizeMaximumBrightness(float input)
        {
            if (!float.IsFinite(input) || input <= 0f)
                return DefaultHdrMaximumBrightness;

            return Math.Clamp(input, DefaultSdrMaximumBrightness, HdrReferencePeakNits);
        }

        private static (uint MaxCLL, uint MaxFALL) ComputeContentLightLevel(HDRPicture16bpp picture, float maximumBrightness)
        {
            ReadOnlySpan<float> brightness = (picture.Brightness != null && picture.Brightness.Length == picture.Pixels)
                ? picture.Brightness
                : ReadOnlySpan<float>.Empty;

            if (!brightness.IsEmpty)
            {
                double sumNits = 0;
                float maxNits = 0f;

                for (int i = 0; i < brightness.Length; i++)
                {
                    float b = brightness[i];
                    if (!float.IsFinite(b) || b < 0f) b = 0f;
                    float nits = Math.Clamp(b * maximumBrightness, 0f, HdrReferencePeakNits);
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

        private static void FillBgr48SourceFrame(AVFrame* srcFrame, HDRPicture16bpp picture)
        {
            fixed (ushort* pr = picture.r)
            fixed (ushort* pg = picture.g)
            fixed (ushort* pb = picture.b)
            {
                for (int y = 0; y < picture.Height; y++)
                {
                    ushort* row = (ushort*)(srcFrame->data[0] + y * srcFrame->linesize[0]);
                    int baseIndex = y * picture.Width;

                    for (int x = 0; x < picture.Width; x++)
                    {
                        int k = baseIndex + x;
                        int offset = x * 3;
                        row[offset + 0] = pb[k];
                        row[offset + 1] = pg[k];
                        row[offset + 2] = pr[k];
                    }
                }
            }
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

        private static void EncodeFrameAndWritePackets(AVFormatContext* fmtCtx, AVCodecContext* codecCtx, AVStream* stream, AVFrame* frame)
        {
            FFmpegHelper.Throw(ffmpeg.avcodec_send_frame(codecCtx, frame), "avcodec_send_frame(HEIC frame)");
            ReceiveAndWritePackets(fmtCtx, codecCtx, stream, "write frame packet");
        }

        private static void FlushEncoderAndWritePackets(AVFormatContext* fmtCtx, AVCodecContext* codecCtx, AVStream* stream)
        {
            FFmpegHelper.Throw(ffmpeg.avcodec_send_frame(codecCtx, null), "avcodec_send_frame(HEIC flush)");
            ReceiveAndWritePackets(fmtCtx, codecCtx, stream, "write flush packet");
        }

        private static void ReceiveAndWritePackets(AVFormatContext* fmtCtx, AVCodecContext* codecCtx, AVStream* stream, string operation)
        {
            while (true)
            {
                AVPacket* pkt = ffmpeg.av_packet_alloc();
                if (pkt == null)
                    throw new InvalidOperationException("Failed to allocate AVPacket.");

                try
                {
                    int ret = ffmpeg.avcodec_receive_packet(codecCtx, pkt);
                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                        return;

                    FFmpegHelper.Throw(ret, "avcodec_receive_packet(HEIC)");

                    ffmpeg.av_packet_rescale_ts(pkt, codecCtx->time_base, stream->time_base);
                    pkt->stream_index = stream->index;
                    FFmpegHelper.Throw(ffmpeg.av_interleaved_write_frame(fmtCtx, pkt), operation);
                }
                finally
                {
                    ffmpeg.av_packet_free(&pkt);
                }
            }
        }
    }
}
