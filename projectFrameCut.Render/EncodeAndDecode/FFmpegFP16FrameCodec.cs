using FFmpeg.AutoGen;

namespace projectFrameCut.Render.EncodeAndDecode;

internal static unsafe class FFmpegFP16FrameCodec
{
    public const AVPixelFormat EncoderFormat = AVPixelFormat.AV_PIX_FMT_GBRAPF16LE;

    // FFmpeg 8 的 swscale 只能输入 FP16；解码端在原生边界使用 FP32，随后直接收进 Half 通道。
    public static AVPixelFormat DecoderFormat => ffmpeg.sws_isSupportedOutput(EncoderFormat) != 0
        ? EncoderFormat : AVPixelFormat.AV_PIX_FMT_GBRAPF32LE;

    public static AVFrame* AllocateFrame(int width, int height, AVPixelFormat format)
    {
        AVFrame* frame = ffmpeg.av_frame_alloc();
        if (frame == null) throw new OutOfMemoryException("Failed to allocate a video frame.");
        frame->format = (int)format;
        frame->width = width;
        frame->height = height;
        int result = ffmpeg.av_frame_get_buffer(frame, 32);
        if (result < 0)
        {
            ffmpeg.av_frame_free(&frame);
            FFmpegHelper.Throw(result, "allocate floating-point video frame");
        }
        return frame;
    }

    public static bool HasAlpha(AVFrame* frame)
    {
        var desc = ffmpeg.av_pix_fmt_desc_get((AVPixelFormat)frame->format);
        return desc != null && (desc->flags & ffmpeg.AV_PIX_FMT_FLAG_ALPHA) != 0;
    }

    public static bool IsHdr(AVColorTransferCharacteristic transfer) =>
        transfer is AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 or AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67;

    public static void ApplyDecoderMetadata(AVFrame* frame, AVCodecContext* codec)
    {
        if (frame->color_trc == AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED) frame->color_trc = codec->color_trc;
        if (frame->color_primaries == AVColorPrimaries.AVCOL_PRI_UNSPECIFIED) frame->color_primaries = codec->color_primaries;
        if (frame->colorspace == AVColorSpace.AVCOL_SPC_UNSPECIFIED) frame->colorspace = codec->colorspace;
        if (frame->color_range == AVColorRange.AVCOL_RANGE_UNSPECIFIED) frame->color_range = codec->color_range;
    }

    public static void ConfigureDecoderColorspace(SwsContext* scaler, AVFrame* source)
    {
        ConfigureColorspace(scaler, source->colorspace, IsFullRange((AVPixelFormat)source->format, source->color_range) ? 1 : 0, 1);
    }

    public static void ConfigureEncoderColorspace(SwsContext* scaler, AVCodecContext* encoder)
    {
        ConfigureColorspace(scaler, encoder->colorspace, 1, IsFullRange(encoder->pix_fmt, encoder->color_range) ? 1 : 0);
    }

    private static bool IsFullRange(AVPixelFormat format, AVColorRange range)
    {
        if (range != AVColorRange.AVCOL_RANGE_UNSPECIFIED) return range == AVColorRange.AVCOL_RANGE_JPEG;
        var descriptor = ffmpeg.av_pix_fmt_desc_get(format);
        return descriptor != null && ((descriptor->flags & ffmpeg.AV_PIX_FMT_FLAG_RGB) != 0 || descriptor->nb_components < 3)
            || format is AVPixelFormat.AV_PIX_FMT_YUVJ420P or AVPixelFormat.AV_PIX_FMT_YUVJ422P
                or AVPixelFormat.AV_PIX_FMT_YUVJ444P or AVPixelFormat.AV_PIX_FMT_YUVJ440P or AVPixelFormat.AV_PIX_FMT_YUVJ411P;
    }

    private static void ConfigureColorspace(SwsContext* scaler, AVColorSpace colorspace, int sourceRange, int targetRange)
    {
        int space = colorspace switch
        {
            AVColorSpace.AVCOL_SPC_BT709 => 1,
            AVColorSpace.AVCOL_SPC_FCC => 4,
            AVColorSpace.AVCOL_SPC_SMPTE240M => 7,
            AVColorSpace.AVCOL_SPC_BT2020_NCL or AVColorSpace.AVCOL_SPC_BT2020_CL => 9,
            _ => 5,
        };
        int* coefficients = ffmpeg.sws_getCoefficients(space);
        if (coefficients == null) return;
        int_array4 values = default;
        for (uint i = 0; i < 4; i++) values[i] = coefficients[i];
        FFmpegHelper.Throw(ffmpeg.sws_setColorspaceDetails(scaler, values, sourceRange, values, targetRange, 0, 1 << 16, 1 << 16),
            "configure floating-point video colorspace");
    }

    public static PictureFP16 Read(AVFrame* frame, bool hasAlpha = false, string source = "", uint frameIndex = 0,
        Type? decoder = null, Half? maximumBrightness = null,
        AVColorTransferCharacteristic transfer = AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED)
    {
        int pixels = checked(frame->width * frame->height);
        Half[] red = ReadPlane(frame, 2), green = ReadPlane(frame, 0), blue = ReadPlane(frame, 1);
        Half[]? alpha = hasAlpha ? ReadPlane(frame, 3) : null;
        if (alpha is not null)
            for (int i = 0; i < pixels; i++) alpha[i] = PictureFP16.Unit((float)alpha[i]);

        PictureFP16 picture;
        if (maximumBrightness is { } peak)
        {
            Half[] brightness = new Half[pixels];
            for (int i = 0; i < pixels; i++)
                brightness[i] = ComputeBrightness((float)red[i], (float)green[i], (float)blue[i], transfer, peak);
            picture = HDRPictureFP16.FromOwnedChannels(frame->width, frame->height, red, green, blue, alpha, brightness, peak);
        }
        else picture = PictureFP16.FromOwnedChannels(frame->width, frame->height, red, green, blue, alpha);

        picture.Tag = $"{source} frame #{frameIndex}";
        if (decoder is not null)
            picture.ProcessStack.Add(new PictureProcessStack
            {
                OperationDisplayName = $"From video '{source}', frame #{frameIndex}",
                Operator = decoder,
                ProcessingFuncStackTrace = null,
                Properties = maximumBrightness is { } maximum ? new()
                {
                    ["TransferCharacteristic"] = transfer.ToString(),
                    ["MaximumBrightness"] = (float)maximum,
                } : null,
            });
        return picture;
    }

    public static Half[] ReadPlane(AVFrame* frame, int plane)
    {
        int width = frame->width, height = frame->height;
        bool fp16 = (AVPixelFormat)frame->format is AVPixelFormat.AV_PIX_FMT_GBRAPF16LE or AVPixelFormat.AV_PIX_FMT_GBRPF16LE;
        if (!fp16 && (AVPixelFormat)frame->format is not (AVPixelFormat.AV_PIX_FMT_GBRAPF32LE
            or AVPixelFormat.AV_PIX_FMT_GBRPF32LE or AVPixelFormat.AV_PIX_FMT_GRAYF32LE))
            throw new NotSupportedException($"Unsupported floating-point frame format {(AVPixelFormat)frame->format}.");
        if (width <= 0 || height <= 0 || frame->data[(uint)plane] == null
            || Math.Abs((long)frame->linesize[(uint)plane]) < (long)width * (fp16 ? sizeof(Half) : sizeof(float)))
            throw new InvalidDataException("Invalid floating-point video plane.");

        Half[] result = new Half[checked(width * height)];
        for (int y = 0; y < height; y++)
        {
            byte* row = frame->data[(uint)plane] + y * frame->linesize[(uint)plane];
            if (fp16) new ReadOnlySpan<Half>(row, width).CopyTo(result.AsSpan(y * width, width));
            else
            {
                float* values = (float*)row;
                for (int x = 0; x < width; x++)
                    result[y * width + x] = (Half)(float.IsFinite(values[x]) ? Math.Clamp(values[x], -65504f, 65504f) : 0);
            }
        }
        return result;
    }

    public static void Write(AVFrame* frame, IPicture<Half> picture)
    {
        if (frame == null || frame->format != (int)EncoderFormat)
            throw new InvalidOperationException("The encoder source must be a planar FP16 frame.");
        var channels = new PictureReadChannels<Half>(picture);
        int pixels = checked(frame->width * frame->height);
        if (picture.Width != frame->width || picture.Height != frame->height
            || channels.Red.Length != pixels || channels.Green.Length != pixels || channels.Blue.Length != pixels
            || (picture.HasAlphaChannel && channels.Alpha.Length != pixels))
            throw new ArgumentException("Frame dimensions or channels do not match the writer.", nameof(picture));
        for (int y = 0; y < frame->height; y++)
        {
            int offset = y * frame->width;
            channels.Green.Slice(offset, frame->width).CopyTo(new Span<Half>(frame->data[0] + y * frame->linesize[0], frame->width));
            channels.Blue.Slice(offset, frame->width).CopyTo(new Span<Half>(frame->data[1] + y * frame->linesize[1], frame->width));
            channels.Red.Slice(offset, frame->width).CopyTo(new Span<Half>(frame->data[2] + y * frame->linesize[2], frame->width));
            var alpha = new Span<Half>(frame->data[3] + y * frame->linesize[3], frame->width);
            if (picture.HasAlphaChannel) channels.Alpha.Slice(offset, frame->width).CopyTo(alpha);
            else alpha.Fill(Half.One);
        }
    }

    public static Half ResolveMaximumBrightness(AVFrame* frame)
    {
        float peak = IsHdr(frame->color_trc) ? 1000 : 100;
        bool hasPeak = false;
        var masteringData = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA);
        if (masteringData != null && masteringData->data != null)
        {
            var mastering = (AVMasteringDisplayMetadata*)masteringData->data;
            if (mastering->has_luminance != 0 && mastering->max_luminance.den > 0)
            {
                float value = (float)mastering->max_luminance.num / mastering->max_luminance.den;
                if (float.IsFinite(value) && value > 0)
                {
                    peak = value;
                    hasPeak = true;
                }
            }
        }
        var lightData = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL);
        if (lightData != null && lightData->data != null)
        {
            uint value = ((AVContentLightMetadata*)lightData->data)->MaxCLL;
            if (value > 0) peak = hasPeak ? Math.Max(peak, value) : value;
        }
        return (Half)Math.Clamp(peak, 1, 10000);
    }

    private static Half ComputeBrightness(float r, float g, float b, AVColorTransferCharacteristic transfer, Half peak)
    {
        if (transfer == AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084)
            return PictureFP16.Unit((DecodePq(r) * .2627f + DecodePq(g) * .6780f + DecodePq(b) * .0593f) * 10000 / (float)peak);
        if (transfer == AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67)
            return PictureFP16.Unit(DecodeHlg(r) * .2627f + DecodeHlg(g) * .6780f + DecodeHlg(b) * .0593f);
        return PictureFP16.Unit(r * .2627f + g * .6780f + b * .0593f);
    }

    private static float DecodePq(float value)
    {
        float p = MathF.Pow(Math.Clamp(value, 0, 1), 32f / 2523);
        return MathF.Pow(MathF.Max(p - 3424f / 4096, 0) / MathF.Max(2413f / 128 - 2392f / 128 * p, 1e-6f), 16384f / 2610);
    }

    private static float DecodeHlg(float value)
    {
        value = Math.Clamp(value, 0, 1);
        return value <= .5f ? value * value / 3 : (MathF.Exp((value - .55991073f) / .17883277f) + .28466892f) / 12;
    }
}
