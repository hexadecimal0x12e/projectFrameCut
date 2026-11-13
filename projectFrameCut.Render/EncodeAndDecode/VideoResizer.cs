using FFmpeg.AutoGen;
using System;
using System.IO;
using System.Threading;

namespace projectFrameCut.Render.EncodeAndDecode
{
    public static unsafe class VideoResizer
    {
        /// <summary>
        /// Re-encode the video stream to change resolution, and stream-copy non-video streams (e.g. audio).
        /// </summary>
        /// <remarks>
        /// Requires FFmpeg binaries available at runtime (same requirement as the existing decoder/writer).
        /// </remarks>
        public static void ReencodeToResolution(
            string inputPath,
            string outputPath,
            int targetWidth,
            int targetHeight,
            string videoEncoder = "libx264",
            int targetBitrate = 4_000_000)
            => Reencode(inputPath, outputPath, targetWidth, targetHeight, videoEncoder, targetBitrate, false, default);

        public static void ReencodeToBitRate(string inputPath, string outputPath, string videoEncoder, long targetBitRate,
            CancellationToken cancellationToken = default)
        {
            string temporaryPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
                $".{Path.GetFileNameWithoutExtension(outputPath)}.{Guid.NewGuid():N}{Path.GetExtension(outputPath)}");
            try
            {
                string[] encoders = FFmpegHelper.IsHardwareVideoEncoder(videoEncoder)
                    ? [videoEncoder, .. FFmpegHelper.GetSoftwareVideoEncoders(videoEncoder)]
                    : [videoEncoder];
                for (int i = 0; i < encoders.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        Reencode(inputPath, temporaryPath, 0, 0, encoders[i], targetBitRate, true, cancellationToken);
                        break;
                    }
                    catch (VideoEncoderException ex) when (i + 1 < encoders.Length)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Log($"Final encoder '{encoders[i]}' failed: {ex.Message}. Trying software encoder '{encoders[i + 1]}'.", "warn");
                        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, outputPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private static void Reencode(string inputPath, string outputPath, int targetWidth, int targetHeight,
            string videoEncoder, long targetBitrate, bool preserveSourceFormat, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(videoEncoder);
            if (!preserveSourceFormat && (targetWidth <= 0 || targetHeight <= 0))
                throw new ArgumentOutOfRangeException(nameof(targetWidth));
            if (!preserveSourceFormat && ((targetWidth & 1) != 0 || (targetHeight & 1) != 0))
                throw new ArgumentException("Target resolution must be even when encoding to YUV420P.");
            if (targetBitrate <= 0) throw new ArgumentOutOfRangeException(nameof(targetBitrate));
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(inputPath)) throw new FileNotFoundException($"Input file '{inputPath}' doesn't exist.", inputPath);
            ffmpeg.avformat_network_init();

            AVFormatContext* input = null;
            AVFormatContext* output = null;
            AVPacket* packet = null;
            var tracks = new Dictionary<int, Track>();
            var copiedStreams = new Dictionary<int, int>();
            try
            {
                FFmpegHelper.Throw(ffmpeg.avformat_open_input(&input, inputPath, null, null), "avformat_open_input");
                FFmpegHelper.Throw(ffmpeg.avformat_find_stream_info(input, null), "avformat_find_stream_info");
                int mainIndex = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
                if (mainIndex < 0) throw new InvalidDataException($"No video stream found in '{inputPath}'.");
                if (preserveSourceFormat)
                {
                    targetWidth = input->streams[mainIndex]->codecpar->width;
                    targetHeight = input->streams[mainIndex]->codecpar->height;
                    Log($"Final video encoding: {videoEncoder}, {targetBitrate} bps, {targetWidth}x{targetHeight}.");
                }
                FFmpegHelper.Throw(ffmpeg.avformat_alloc_output_context2(&output, null, null, outputPath),
                    "avformat_alloc_output_context2");
                if (output == null) throw new OutOfMemoryException();
                if (preserveSourceFormat) ffmpeg.av_dict_copy(&output->metadata, input->metadata, 0);

                for (int i = 0; i < input->nb_streams; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var stream = input->streams[i];
                    var type = stream->codecpar->codec_type;
                    if (i == mainIndex)
                    {
                        if (!preserveSourceFormat) continue;
                        tracks.Add(i, new Track(input, stream, output, targetWidth, targetHeight, videoEncoder, targetBitrate,
                            preserveSourceFormat));
                    }
                    else if (type is AVMediaType.AVMEDIA_TYPE_AUDIO or AVMediaType.AVMEDIA_TYPE_SUBTITLE or AVMediaType.AVMEDIA_TYPE_DATA
                        || preserveSourceFormat && type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        var copy = ffmpeg.avformat_new_stream(output, null);
                        if (copy == null) throw new OutOfMemoryException();
                        FFmpegHelper.Throw(ffmpeg.avcodec_parameters_copy(copy->codecpar, stream->codecpar), "avcodec_parameters_copy");
                        copy->codecpar->codec_tag = 0;
                        copy->time_base = stream->time_base;
                        if (preserveSourceFormat)
                        {
                            ffmpeg.av_dict_copy(&copy->metadata, stream->metadata, 0);
                            copy->disposition = stream->disposition;
                        }
                        copiedStreams.Add(i, copy->index);
                    }
                }
                if (!tracks.ContainsKey(mainIndex))
                    tracks.Add(mainIndex, new Track(input, input->streams[mainIndex], output, targetWidth, targetHeight,
                        videoEncoder, targetBitrate, preserveSourceFormat));
                if ((output->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                    FFmpegHelper.Throw(ffmpeg.avio_open(&output->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE), "avio_open");
                FFmpegHelper.Throw(ffmpeg.avformat_write_header(output, null), "avformat_write_header");
                packet = ffmpeg.av_packet_alloc();
                if (packet == null) throw new OutOfMemoryException();
                int result;
                while ((result = ffmpeg.av_read_frame(input, packet)) >= 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (tracks.TryGetValue(packet->stream_index, out var track)) track.Decode(packet, output, cancellationToken);
                    else if (copiedStreams.TryGetValue(packet->stream_index, out int index))
                    {
                        ffmpeg.av_packet_rescale_ts(packet, input->streams[packet->stream_index]->time_base, output->streams[index]->time_base);
                        packet->stream_index = index;
                        FFmpegHelper.Throw(ffmpeg.av_interleaved_write_frame(output, packet), "av_interleaved_write_frame(copy)");
                    }
                    ffmpeg.av_packet_unref(packet);
                }
                if (result != ffmpeg.AVERROR_EOF) FFmpegHelper.Throw(result, "av_read_frame");
                foreach (var track in tracks.Values) track.Finish(output, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                FFmpegHelper.Throw(ffmpeg.av_write_trailer(output), "av_write_trailer");
            }
            finally
            {
                foreach (var track in tracks.Values) track.Dispose();
                if (packet != null) ffmpeg.av_packet_free(&packet);
                if (output != null)
                {
                    if (output->pb != null) ffmpeg.avio_closep(&output->pb);
                    ffmpeg.avformat_free_context(output);
                }
                if (input != null) ffmpeg.avformat_close_input(&input);
            }
        }

        private sealed class Track : IDisposable
        {
            private AVCodecContext* decoder;
            private AVCodecContext* encoder;
            private AVFrame* source;
            private AVFrame* scaled;
            private AVPacket* packet;
            private SwsContext* scaler;
            private readonly AVStream* input;
            private readonly AVStream* output;
            private readonly bool preserveSourceFormat;
            private long nextPts;

            public Track(AVFormatContext* sourceFormat, AVStream* input, AVFormatContext* format, int width, int height,
                string videoEncoder, long bitrate, bool preserveSourceFormat)
            {
                this.input = input;
                this.preserveSourceFormat = preserveSourceFormat;
                try
                {
                    var codec = ffmpeg.avcodec_find_decoder(input->codecpar->codec_id);
                    if (codec == null) throw new NotSupportedException("No suitable video decoder found.");
                    decoder = ffmpeg.avcodec_alloc_context3(codec);
                    if (decoder == null) throw new OutOfMemoryException();
                    FFmpegHelper.Throw(ffmpeg.avcodec_parameters_to_context(decoder, input->codecpar), "avcodec_parameters_to_context");
                    decoder->pkt_timebase = input->time_base;
                    FFmpegHelper.Throw(ffmpeg.avcodec_open2(decoder, codec, null), "avcodec_open2(decoder)");

                    codec = ffmpeg.avcodec_find_encoder_by_name(videoEncoder);
                    if (codec == null) throw new VideoEncoderException($"Could not find video encoder '{videoEncoder}'.");
                    encoder = ffmpeg.avcodec_alloc_context3(codec);
                    if (encoder == null) throw new OutOfMemoryException();
                    var frameRate = ffmpeg.av_guess_frame_rate(sourceFormat, input, null);
                    if (frameRate.num <= 0 || frameRate.den <= 0) frameRate = input->avg_frame_rate;
                    if (frameRate.num <= 0 || frameRate.den <= 0) frameRate = new() { num = 30, den = 1 };
                    encoder->codec_id = codec->id;
                    encoder->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
                    encoder->width = width;
                    encoder->height = height;
                    encoder->pix_fmt = preserveSourceFormat ? SelectPixelFormat(codec, decoder->pix_fmt) : AVPixelFormat.AV_PIX_FMT_YUV420P;
                    encoder->time_base = new() { num = frameRate.den, den = frameRate.num };
                    if (encoder->time_base.num <= 0 || encoder->time_base.den <= 0)
                        throw new InvalidDataException("The source video has no valid time base.");
                    encoder->framerate = frameRate;
                    encoder->gop_size = 12;
                    encoder->max_b_frames = preserveSourceFormat ? 0 : 2;
                    encoder->bit_rate = bitrate;
                    if (preserveSourceFormat)
                    {
                        encoder->sample_aspect_ratio = input->codecpar->sample_aspect_ratio;
                        encoder->color_range = input->codecpar->color_range;
                        encoder->color_primaries = input->codecpar->color_primaries;
                        encoder->color_trc = input->codecpar->color_trc;
                        encoder->colorspace = input->codecpar->color_space;
                        encoder->chroma_sample_location = input->codecpar->chroma_location;
                    }
                    if ((format->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                        encoder->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
                    AVDictionary* options = null;
                    try
                    {
                        if (!preserveSourceFormat && codec->id == AVCodecID.AV_CODEC_ID_H264)
                        {
                            ffmpeg.av_dict_set(&options, "preset", "veryfast", 0);
                            ffmpeg.av_dict_set(&options, "tune", "zerolatency", 0);
                        }
                        else if (preserveSourceFormat && videoEncoder is "libx264" or "libx265")
                            ffmpeg.av_dict_set(&options, "preset", "veryfast", 0);
                        CheckEncoderResult(ffmpeg.avcodec_open2(encoder, codec, &options), "avcodec_open2(encoder)");
                    }
                    finally { ffmpeg.av_dict_free(&options); }
                    output = ffmpeg.avformat_new_stream(format, null);
                    if (output == null) throw new OutOfMemoryException();
                    FFmpegHelper.Throw(ffmpeg.avcodec_parameters_from_context(output->codecpar, encoder), "avcodec_parameters_from_context");
                    output->codecpar->codec_tag = 0;
                    output->time_base = encoder->time_base;
                    if (preserveSourceFormat)
                    {
                        output->avg_frame_rate = frameRate;
                        output->sample_aspect_ratio = input->sample_aspect_ratio;
                        output->disposition = input->disposition;
                        ffmpeg.av_dict_copy(&output->metadata, input->metadata, 0);
                        for (int i = 0; i < input->codecpar->nb_coded_side_data; i++)
                        {
                            var data = input->codecpar->coded_side_data[i];
                            var copy = ffmpeg.av_packet_side_data_new(&output->codecpar->coded_side_data,
                                &output->codecpar->nb_coded_side_data, data.type, data.size, 0);
                            if (copy == null) throw new OutOfMemoryException();
                            Buffer.MemoryCopy(data.data, copy->data, data.size, data.size);
                        }
                    }
                    source = ffmpeg.av_frame_alloc();
                    scaled = ffmpeg.av_frame_alloc();
                    packet = ffmpeg.av_packet_alloc();
                    if (source == null || scaled == null || packet == null) throw new OutOfMemoryException();
                    scaled->format = (int)encoder->pix_fmt;
                    scaled->width = width;
                    scaled->height = height;
                    FFmpegHelper.Throw(ffmpeg.av_frame_get_buffer(scaled, 32), "av_frame_get_buffer");
                }
                catch { Dispose(); throw; }
            }

            public void Decode(AVPacket* inputPacket, AVFormatContext* format, CancellationToken token)
            {
                FFmpegHelper.Throw(ffmpeg.avcodec_send_packet(decoder, inputPacket), "avcodec_send_packet");
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    int result = ffmpeg.avcodec_receive_frame(decoder, source);
                    if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF) return;
                    FFmpegHelper.Throw(result, "avcodec_receive_frame");
                    scaler = ffmpeg.sws_getCachedContext(scaler, source->width, source->height, (AVPixelFormat)source->format,
                        encoder->width, encoder->height, encoder->pix_fmt, 4, null, null, null);
                    if (scaler == null) throw new InvalidOperationException("Cannot create video scaler.");
                    FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(scaled), "av_frame_make_writable");
                    if (FFmpegHelper.ScaleFrame(scaler, source, scaled) != encoder->height)
                        throw new InvalidDataException("Incomplete scaled video frame.");
                    if (preserveSourceFormat) CopyFrameProperties(scaled, source, input->time_base, encoder->time_base);
                    long pts = source->best_effort_timestamp;
                    scaled->pts = pts == ffmpeg.AV_NOPTS_VALUE ? nextPts : ffmpeg.av_rescale_q(pts, input->time_base, encoder->time_base);
                    if (scaled->pts < 0) scaled->pts = nextPts;
                    nextPts = scaled->pts + 1;
                    CheckEncoderResult(ffmpeg.avcodec_send_frame(encoder, scaled), "avcodec_send_frame");
                    WritePackets(format);
                    ffmpeg.av_frame_unref(source);
                }
            }

            public void Finish(AVFormatContext* format, CancellationToken token)
            {
                Decode(null, format, token);
                CheckEncoderResult(ffmpeg.avcodec_send_frame(encoder, null), "avcodec_send_frame(flush)");
                WritePackets(format);
            }

            private void WritePackets(AVFormatContext* format)
            {
                while (true)
                {
                    int result = ffmpeg.avcodec_receive_packet(encoder, packet);
                    if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF) return;
                    CheckEncoderResult(result, "avcodec_receive_packet");
                    ffmpeg.av_packet_rescale_ts(packet, encoder->time_base, output->time_base);
                    packet->stream_index = output->index;
                    FFmpegHelper.Throw(ffmpeg.av_interleaved_write_frame(format, packet), "av_interleaved_write_frame(video)");
                    ffmpeg.av_packet_unref(packet);
                }
            }

            public void Dispose()
            {
                if (scaler != null) { ffmpeg.sws_freeContext(scaler); scaler = null; }
                fixed (AVPacket** p = &packet) ffmpeg.av_packet_free(p);
                fixed (AVFrame** p = &source) ffmpeg.av_frame_free(p);
                fixed (AVFrame** p = &scaled) ffmpeg.av_frame_free(p);
                fixed (AVCodecContext** p = &decoder) ffmpeg.avcodec_free_context(p);
                fixed (AVCodecContext** p = &encoder) ffmpeg.avcodec_free_context(p);
            }
        }

        private sealed class VideoEncoderException : InvalidOperationException
        {
            public VideoEncoderException(string message) : base(message) { }
        }

        private static void CheckEncoderResult(int result, string operation)
        {
            if (result < 0)
                throw new VideoEncoderException($"{operation}: {FFmpegHelper.GetErrorString(result)} (0x{result:x8}).");
        }

        private static void CopyFrameProperties(AVFrame* target, AVFrame* source, AVRational sourceTimeBase, AVRational targetTimeBase)
        {
            while (target->nb_side_data > 0)
                ffmpeg.av_frame_remove_side_data(target, target->side_data[0]->type);
            ffmpeg.av_dict_free(&target->metadata);
            FFmpegHelper.Throw(ffmpeg.av_frame_copy_props(target, source), "av_frame_copy_props");
            target->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
            target->time_base = targetTimeBase;
            target->duration = ffmpeg.av_rescale_q(source->duration, sourceTimeBase, targetTimeBase);
        }

        private static AVPixelFormat SelectPixelFormat(AVCodec* encoder, AVPixelFormat source)
        {
            AVPixelFormat* formats;
            int count;
            int result = ffmpeg.avcodec_get_supported_config(null, encoder,
                AVCodecConfig.AV_CODEC_CONFIG_PIX_FORMAT, 0, (void**)&formats, &count);
            if (result < 0 || formats == null || count <= 0) return source;
            AVPixelFormat best = AVPixelFormat.AV_PIX_FMT_NONE;
            int depth = FFmpegHelper.GetAVPixelFormatBitsPerPixel(source);
            for (int i = 0; i < count; i++)
            {
                var desc = ffmpeg.av_pix_fmt_desc_get(formats[i]);
                if (desc == null || (desc->flags & ffmpeg.AV_PIX_FMT_FLAG_HWACCEL) != 0) continue;
                if (desc->nb_components > 0 && desc->comp[0].depth < depth) continue;
                if (formats[i] == source) return source;
                best = best == AVPixelFormat.AV_PIX_FMT_NONE
                    ? formats[i] : ffmpeg.av_find_best_pix_fmt_of_2(best, formats[i], source, 1, null);
            }
            if (best == AVPixelFormat.AV_PIX_FMT_NONE)
                throw new VideoEncoderException("The final video encoder does not support the source pixel depth in software frames.");
            return best;
        }
    }
}
