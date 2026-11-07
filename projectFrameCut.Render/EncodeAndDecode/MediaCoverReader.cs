using FFmpeg.AutoGen;

namespace projectFrameCut.Render.EncodeAndDecode;

public static unsafe class MediaCoverReader
{
    public static PictureFP16? Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        AVFormatContext* format = null;
        try
        {
            FFmpegHelper.Throw(ffmpeg.avformat_open_input(&format, path, null, null), "open media for cover reading");
            FFmpegHelper.Throw(ffmpeg.avformat_find_stream_info(format, null), "read media cover information");
            for (int i = 0; i < format->nb_streams; i++)
            {
                AVStream* stream = format->streams[i];
                if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO
                    && (stream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0
                    && stream->attached_pic.data != null && stream->attached_pic.size > 0)
                    return Decode(stream, path);
            }
            return null;
        }
        finally
        {
            if (format != null) ffmpeg.avformat_close_input(&format);
        }
    }

    private static PictureFP16 Decode(AVStream* stream, string path)
    {
        AVCodec* decoder = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (decoder == null) throw new NotSupportedException($"No decoder is available for the cover in '{path}'.");
        AVCodecContext* codec = null;
        AVFrame* frame = null;
        AVFrame* rgb = null;
        SwsContext* scaler = null;
        try
        {
            codec = ffmpeg.avcodec_alloc_context3(decoder);
            if (codec == null) throw new OutOfMemoryException("Failed to allocate the cover decoder.");
            FFmpegHelper.Throw(ffmpeg.avcodec_parameters_to_context(codec, stream->codecpar), "copy cover codec parameters");
            FFmpegHelper.Throw(ffmpeg.avcodec_open2(codec, decoder, null), "open cover decoder");
            frame = ffmpeg.av_frame_alloc();
            if (frame == null) throw new OutOfMemoryException("Failed to allocate the cover frame.");

            // 内嵌封面是独立图片包，直接软件解码，不进入视频的硬件探测和 seek 流程。
            FFmpegHelper.Throw(ffmpeg.avcodec_send_packet(codec, &stream->attached_pic), "send cover packet");
            int result = ffmpeg.avcodec_receive_frame(codec, frame);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                FFmpegHelper.Throw(ffmpeg.avcodec_send_packet(codec, null), "flush cover decoder");
                result = ffmpeg.avcodec_receive_frame(codec, frame);
            }
            FFmpegHelper.Throw(result, "decode media cover");
            if (frame->width <= 0 || frame->height <= 0)
                throw new InvalidDataException($"The cover in '{path}' has invalid dimensions.");

            FFmpegFP16FrameCodec.ApplyDecoderMetadata(frame, codec);
            rgb = FFmpegFP16FrameCodec.AllocateFrame(frame->width, frame->height, FFmpegFP16FrameCodec.DecoderFormat);
            scaler = ffmpeg.sws_getContext(frame->width, frame->height, (AVPixelFormat)frame->format,
                rgb->width, rgb->height, (AVPixelFormat)rgb->format, 4, null, null, null);
            if (scaler == null) throw new InvalidOperationException("Failed to create the cover color conversion context.");
            FFmpegFP16FrameCodec.ConfigureDecoderColorspace(scaler, frame);
            if (FFmpegHelper.ScaleFrame(scaler, frame, rgb) != rgb->height)
                throw new InvalidDataException($"Failed to convert the complete cover in '{path}'.");
            return FFmpegFP16FrameCodec.Read(rgb, FFmpegFP16FrameCodec.HasAlpha(frame), path, decoder: typeof(MediaCoverReader));
        }
        finally
        {
            if (scaler != null) ffmpeg.sws_freeContext(scaler);
            if (rgb != null) ffmpeg.av_frame_free(&rgb);
            if (frame != null) ffmpeg.av_frame_free(&frame);
            if (codec != null) ffmpeg.avcodec_free_context(&codec);
        }
    }
}
