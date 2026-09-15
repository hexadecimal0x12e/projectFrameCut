using FFmpeg.AutoGen;

namespace projectFrameCut.Render.EncodeAndDecode;

internal readonly record struct VideoFrameRegion(
    int SourceX, int SourceY, int SourceWidth, int SourceHeight, int TargetWidth, int TargetHeight);

/// <summary>
/// Crops in the decoded frame's native pixel format and performs crop, scale and RGB conversion
/// in one libswscale pass. Only the final-sized RGB buffer is exposed to managed code.
/// </summary>
internal static unsafe class FFmpegFrameCropScaler
{
    internal unsafe delegate TResult PixelReader<TResult>(byte* data, int stride, int width, int height);

    public static TResult Scale<TResult>(AVFrame* source, int sourceX, int sourceY, int sourceWidth, int sourceHeight,
        int targetWidth, int targetHeight, AVPixelFormat targetFormat, PixelReader<TResult> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (source == null)
            throw new ArgumentNullException(nameof(source));
        if (sourceX < 0 || sourceY < 0 || sourceWidth <= 0 || sourceHeight <= 0 ||
            sourceX > source->width - sourceWidth || sourceY > source->height - sourceHeight)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "The crop rectangle must be inside the decoded frame.");
        if (targetWidth <= 0 || targetHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetWidth), "The output size must be positive.");

        AVFrame* cropped = ffmpeg.av_frame_clone(source);
        AVFrame* output = ffmpeg.av_frame_alloc();
        SwsContext* scaler = null;
        if (cropped == null || output == null)
        {
            if (cropped != null) ffmpeg.av_frame_free(&cropped);
            if (output != null) ffmpeg.av_frame_free(&output);
            throw new OutOfMemoryException("Failed to allocate FFmpeg crop/scale frames.");
        }

        try
        {
            cropped->crop_left = (ulong)sourceX;
            cropped->crop_top = (ulong)sourceY;
            cropped->crop_right = (ulong)(cropped->width - sourceX - sourceWidth);
            cropped->crop_bottom = (ulong)(cropped->height - sourceY - sourceHeight);

            // Exact odd-coordinate crops are important for editor semantics. FFmpeg may otherwise
            // round the origin to the chroma subsampling alignment.
            int cropRet = ffmpeg.av_frame_apply_cropping(cropped, 1 /* AV_FRAME_CROP_UNALIGNED */);
            if (cropRet < 0)
                throw new InvalidDataException($"FFmpeg failed to apply the crop rectangle (code {cropRet}).");

            output->format = (int)targetFormat;
            output->width = targetWidth;
            output->height = targetHeight;
            int bufferRet = ffmpeg.av_frame_get_buffer(output, 32);
            if (bufferRet < 0)
                throw new OutOfMemoryException($"FFmpeg failed to allocate the scaled frame buffer (code {bufferRet}).");

            scaler = ffmpeg.sws_getContext(
                cropped->width, cropped->height, (AVPixelFormat)cropped->format,
                targetWidth, targetHeight, targetFormat,
                4 /* SWS_BICUBIC */, null, null, null);
            if (scaler == null)
                throw new InvalidOperationException("FFmpeg failed to create the crop/scale conversion context.");

            int scaledRows = ffmpeg.sws_scale(
                scaler, cropped->data, cropped->linesize, 0, cropped->height,
                output->data, output->linesize);
            if (scaledRows != targetHeight)
                throw new InvalidDataException($"FFmpeg crop/scale produced {scaledRows}/{targetHeight} rows.");

            return reader(output->data[0], output->linesize[0], targetWidth, targetHeight);
        }
        finally
        {
            if (scaler != null) ffmpeg.sws_freeContext(scaler);
            ffmpeg.av_frame_free(&output);
            ffmpeg.av_frame_free(&cropped);
        }
    }
}
