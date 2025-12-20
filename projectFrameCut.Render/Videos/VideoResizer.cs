using FFmpeg.AutoGen;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace projectFrameCut.Render.Videos
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
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(videoEncoder);
            if (targetWidth <= 0 || targetHeight <= 0) throw new ArgumentOutOfRangeException(nameof(targetWidth));
            if ((targetWidth & 1) != 0 || (targetHeight & 1) != 0)
                throw new ArgumentException("Target resolution must be even when encoding to YUV420P.");

            if (!File.Exists(inputPath))
                throw new FileNotFoundException($"Input file '{inputPath}' doesn't exist.", inputPath);

            ffmpeg.avformat_network_init();

            AVFormatContext* inFmt = null;
            AVFormatContext* outFmt = null;
            AVCodecContext* decCtx = null;
            AVCodecContext* encCtx = null;
            SwsContext* sws = null;
            AVFrame* decFrame = null;
            AVFrame* encFrame = null;
            AVPacket* inPkt = null;
            AVPacket* outPkt = null;
            int[]? streamMappingManaged = null;
            int streamMappingSize = 0;
            long nextVideoPts = 0;

            try
            {
                // -------- Open input --------
                inFmt = ffmpeg.avformat_alloc_context();
                if (inFmt == null) throw new InvalidOperationException("Failed to allocate input AVFormatContext.");

                FFmpegHelper.Throw(ffmpeg.avformat_open_input(&inFmt, inputPath, null, null), "avformat_open_input");
                FFmpegHelper.Throw(ffmpeg.avformat_find_stream_info(inFmt, null), "avformat_find_stream_info");

                int videoStreamIndex = ffmpeg.av_find_best_stream(inFmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
                if (videoStreamIndex < 0)
                    throw new InvalidDataException($"No video stream found in '{inputPath}'.");

                AVStream* inVideoStream = inFmt->streams[videoStreamIndex];
                AVCodecParameters* inVideoPar = inVideoStream->codecpar;

                AVCodec* decoder = ffmpeg.avcodec_find_decoder(inVideoPar->codec_id);
                if (decoder == null) throw new NotSupportedException("No suitable video decoder found.");

                decCtx = ffmpeg.avcodec_alloc_context3(decoder);
                if (decCtx == null) throw new InvalidOperationException("Failed to allocate decoder context.");
                FFmpegHelper.Throw(ffmpeg.avcodec_parameters_to_context(decCtx, inVideoPar), "avcodec_parameters_to_context");
                FFmpegHelper.Throw(ffmpeg.avcodec_open2(decCtx, decoder, null), "avcodec_open2(decoder)");

                // -------- Create output --------
                int ret = ffmpeg.avformat_alloc_output_context2(&outFmt, null, null, outputPath);
                if (ret < 0 || outFmt == null)
                    FFmpegHelper.Throw(ret, "avformat_alloc_output_context2");

                streamMappingSize = (int)inFmt->nb_streams;
                streamMappingManaged = new int[streamMappingSize];
                for (int i = 0; i < streamMappingManaged.Length; i++) streamMappingManaged[i] = -1;

                // Create output streams for non-video (stream copy) first.
                for (int i = 0; i < inFmt->nb_streams; i++)
                {
                    if (i == videoStreamIndex) continue;

                    AVStream* inStream = inFmt->streams[i];
                    AVCodecParameters* inPar = inStream->codecpar;

                    if (inPar->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO &&
                        inPar->codec_type != AVMediaType.AVMEDIA_TYPE_SUBTITLE &&
                        inPar->codec_type != AVMediaType.AVMEDIA_TYPE_DATA)
                    {
                        continue; // ignore attachments/unknown by default
                    }

                    AVStream* outStream = ffmpeg.avformat_new_stream(outFmt, null);
                    if (outStream == null) throw new InvalidOperationException("Failed to create output stream.");

                    FFmpegHelper.Throw(ffmpeg.avcodec_parameters_copy(outStream->codecpar, inPar), "avcodec_parameters_copy");
                    outStream->codecpar->codec_tag = 0;
                    outStream->time_base = inStream->time_base;

                    streamMappingManaged[i] = outStream->index;
                }

                // Create output video stream (re-encode).
                AVCodec* encoder = ffmpeg.avcodec_find_encoder_by_name(videoEncoder);
                if (encoder == null)
                    throw new EntryPointNotFoundException($"Could not find video encoder '{videoEncoder}'.");

                AVStream* outVideoStream = ffmpeg.avformat_new_stream(outFmt, encoder);
                if (outVideoStream == null) throw new InvalidOperationException("Failed to create output video stream.");

                encCtx = ffmpeg.avcodec_alloc_context3(encoder);
                if (encCtx == null) throw new InvalidOperationException("Failed to allocate encoder context.");

                // Choose a reasonable fps / time base.
                AVRational fr = ffmpeg.av_guess_frame_rate(inFmt, inVideoStream, null);
                if (fr.num <= 0 || fr.den <= 0)
                {
                    fr = inVideoStream->avg_frame_rate;
                }
                if (fr.num <= 0 || fr.den <= 0)
                {
                    fr = new AVRational { num = 30, den = 1 };
                }

                // Encoder parameters.
                encCtx->codec_id = encoder->id;
                encCtx->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
                encCtx->width = targetWidth;
                encCtx->height = targetHeight;
                encCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
                encCtx->time_base = new AVRational { num = fr.den, den = fr.num }; // 1/fps
                encCtx->framerate = fr;
                encCtx->gop_size = 12;
                encCtx->max_b_frames = 2;
                encCtx->bit_rate = targetBitrate;

                outVideoStream->time_base = encCtx->time_base;

                if ((outFmt->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                    encCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

                AVDictionary* opts = null;
                if (encCtx->codec_id == AVCodecID.AV_CODEC_ID_H264)
                {
                    ffmpeg.av_dict_set(&opts, "preset", "veryfast", 0);
                    ffmpeg.av_dict_set(&opts, "tune", "zerolatency", 0);
                }
                FFmpegHelper.Throw(ffmpeg.avcodec_open2(encCtx, encoder, &opts), "avcodec_open2(encoder)");
                ffmpeg.av_dict_free(&opts);

                FFmpegHelper.Throw(ffmpeg.avcodec_parameters_from_context(outVideoStream->codecpar, encCtx), "avcodec_parameters_from_context");
                outVideoStream->codecpar->codec_tag = 0;

                streamMappingManaged[videoStreamIndex] = outVideoStream->index;

                if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                {
                    FFmpegHelper.Throw(ffmpeg.avio_open(&outFmt->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE), "avio_open");
                }

                FFmpegHelper.Throw(ffmpeg.avformat_write_header(outFmt, null), "avformat_write_header");

                // -------- Alloc frames/packets & scaler --------
                decFrame = ffmpeg.av_frame_alloc();
                encFrame = ffmpeg.av_frame_alloc();
                inPkt = ffmpeg.av_packet_alloc();
                outPkt = ffmpeg.av_packet_alloc();
                if (decFrame == null || encFrame == null || inPkt == null || outPkt == null)
                    throw new OutOfMemoryException("Failed to allocate frames/packets.");

                encFrame->format = (int)encCtx->pix_fmt;
                encFrame->width = encCtx->width;
                encFrame->height = encCtx->height;
                FFmpegHelper.Throw(ffmpeg.av_frame_get_buffer(encFrame, 32), "av_frame_get_buffer(encFrame)");

                sws = ffmpeg.sws_getContext(
                    decCtx->width, decCtx->height, decCtx->pix_fmt,
                    encCtx->width, encCtx->height, encCtx->pix_fmt,
                    4, null, null, null);
                if (sws == null)
                    throw new InvalidOperationException("Failed to create SWS scaler context.");

                // -------- Transcode loop --------
                while (ffmpeg.av_read_frame(inFmt, inPkt) >= 0)
                {
                    int inIndex = inPkt->stream_index;
                    if (inIndex < 0 || inIndex >= streamMappingSize)
                    {
                        ffmpeg.av_packet_unref(inPkt);
                        continue;
                    }

                    if (inIndex == videoStreamIndex)
                    {
                        // Decode -> Scale -> Encode
                        FFmpegHelper.Throw(ffmpeg.avcodec_send_packet(decCtx, inPkt), "avcodec_send_packet(decoder)");
                        ffmpeg.av_packet_unref(inPkt);

                        while (true)
                        {
                            int recv = ffmpeg.avcodec_receive_frame(decCtx, decFrame);
                            if (recv == ffmpeg.AVERROR(ffmpeg.EAGAIN) || recv == ffmpeg.AVERROR_EOF) break;
                            FFmpegHelper.Throw(recv, "avcodec_receive_frame(decoder)");

                            FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(encFrame), "av_frame_make_writable(encFrame)");

                            ffmpeg.sws_scale(
                                sws,
                                decFrame->data,
                                decFrame->linesize,
                                0,
                                decCtx->height,
                                encFrame->data,
                                encFrame->linesize);

                            long srcPts = decFrame->best_effort_timestamp;
                            if (srcPts == ffmpeg.AV_NOPTS_VALUE)
                            {
                                encFrame->pts = nextVideoPts++;
                            }
                            else
                            {
                                encFrame->pts = ffmpeg.av_rescale_q(srcPts, inVideoStream->time_base, encCtx->time_base);
                                if (encFrame->pts < 0) encFrame->pts = nextVideoPts++;
                                else nextVideoPts = encFrame->pts + 1;
                            }

                            FFmpegHelper.Throw(ffmpeg.avcodec_send_frame(encCtx, encFrame), "avcodec_send_frame(encoder)");

                            while (true)
                            {
                                int er = ffmpeg.avcodec_receive_packet(encCtx, outPkt);
                                if (er == ffmpeg.AVERROR(ffmpeg.EAGAIN) || er == ffmpeg.AVERROR_EOF) break;
                                FFmpegHelper.Throw(er, "avcodec_receive_packet(encoder)");

                                AVStream* outStream = outFmt->streams[outVideoStream->index];
                                ffmpeg.av_packet_rescale_ts(outPkt, encCtx->time_base, outStream->time_base);
                                outPkt->stream_index = outVideoStream->index;

                                FFmpegHelper.Throw(ffmpeg.av_interleaved_write_frame(outFmt, outPkt), "av_interleaved_write_frame(video)");
                                ffmpeg.av_packet_unref(outPkt);
                            }

                            ffmpeg.av_frame_unref(decFrame);
                        }
                    }
                    else
                    {
                        // Stream copy other packets
                        int outIndex = streamMappingManaged[inIndex];
                        if (outIndex < 0)
                        {
                            ffmpeg.av_packet_unref(inPkt);
                            continue;
                        }

                        AVStream* inStream = inFmt->streams[inIndex];
                        AVStream* outStream = outFmt->streams[outIndex];

                        inPkt->stream_index = outIndex;
                        ffmpeg.av_packet_rescale_ts(inPkt, inStream->time_base, outStream->time_base);

                        FFmpegHelper.Throw(ffmpeg.av_interleaved_write_frame(outFmt, inPkt), "av_interleaved_write_frame(copy)");
                        ffmpeg.av_packet_unref(inPkt);
                    }
                }

                // -------- Flush decoder --------
                FFmpegHelper.Throw(ffmpeg.avcodec_send_packet(decCtx, null), "avcodec_send_packet(decoder_flush)");
                while (true)
                {
                    int recv = ffmpeg.avcodec_receive_frame(decCtx, decFrame);
                    if (recv == ffmpeg.AVERROR(ffmpeg.EAGAIN) || recv == ffmpeg.AVERROR_EOF) break;
                    FFmpegHelper.Throw(recv, "avcodec_receive_frame(decoder_flush)");

                    FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(encFrame), "av_frame_make_writable(encFrame)");
                    ffmpeg.sws_scale(
                        sws,
                        decFrame->data,
                        decFrame->linesize,
                        0,
                        decCtx->height,
                        encFrame->data,
                        encFrame->linesize);

                    long srcPts = decFrame->best_effort_timestamp;
                    if (srcPts == ffmpeg.AV_NOPTS_VALUE)
                    {
                        encFrame->pts = nextVideoPts++;
                    }
                    else
                    {
                        encFrame->pts = ffmpeg.av_rescale_q(srcPts, inVideoStream->time_base, encCtx->time_base);
                        if (encFrame->pts < 0) encFrame->pts = nextVideoPts++;
                        else nextVideoPts = encFrame->pts + 1;
                    }

                    FFmpegHelper.Throw(ffmpeg.avcodec_send_frame(encCtx, encFrame), "avcodec_send_frame(encoder_flush_in)");
                    while (true)
                    {
                        int er = ffmpeg.avcodec_receive_packet(encCtx, outPkt);
                        if (er == ffmpeg.AVERROR(ffmpeg.EAGAIN) || er == ffmpeg.AVERROR_EOF) break;
                        FFmpegHelper.Throw(er, "avcodec_receive_packet(encoder_flush_out)");

                        AVStream* outStream = outFmt->streams[streamMappingManaged[videoStreamIndex]];
                        ffmpeg.av_packet_rescale_ts(outPkt, encCtx->time_base, outStream->time_base);
                        outPkt->stream_index = streamMappingManaged[videoStreamIndex];
                        FFmpegHelper.Throw(ffmpeg.av_interleaved_write_frame(outFmt, outPkt), "av_interleaved_write_frame(video_flush)");
                        ffmpeg.av_packet_unref(outPkt);
                    }

                    ffmpeg.av_frame_unref(decFrame);
                }

                // -------- Flush encoder --------
                FFmpegHelper.Throw(ffmpeg.avcodec_send_frame(encCtx, null), "avcodec_send_frame(encoder_null)");
                while (true)
                {
                    int er = ffmpeg.avcodec_receive_packet(encCtx, outPkt);
                    if (er == ffmpeg.AVERROR(ffmpeg.EAGAIN) || er == ffmpeg.AVERROR_EOF) break;
                    FFmpegHelper.Throw(er, "avcodec_receive_packet(encoder_null)");

                    AVStream* outStream = outFmt->streams[streamMappingManaged[videoStreamIndex]];
                    ffmpeg.av_packet_rescale_ts(outPkt, encCtx->time_base, outStream->time_base);
                    outPkt->stream_index = streamMappingManaged[videoStreamIndex];
                    FFmpegHelper.Throw(ffmpeg.av_interleaved_write_frame(outFmt, outPkt), "av_interleaved_write_frame(encoder_null)");
                    ffmpeg.av_packet_unref(outPkt);
                }

                FFmpegHelper.Throw(ffmpeg.av_write_trailer(outFmt), "av_write_trailer");
            }
            finally
            {
                if (outPkt != null) { AVPacket* t = outPkt; outPkt = null; ffmpeg.av_packet_free(&t); }
                if (inPkt != null) { AVPacket* t = inPkt; inPkt = null; ffmpeg.av_packet_free(&t); }
                if (encFrame != null) { AVFrame* t = encFrame; encFrame = null; ffmpeg.av_frame_free(&t); }
                if (decFrame != null) { AVFrame* t = decFrame; decFrame = null; ffmpeg.av_frame_free(&t); }
                if (sws != null) { ffmpeg.sws_freeContext(sws); sws = null; }
                if (encCtx != null) { AVCodecContext* t = encCtx; encCtx = null; ffmpeg.avcodec_free_context(&t); }
                if (decCtx != null) { AVCodecContext* t = decCtx; decCtx = null; ffmpeg.avcodec_free_context(&t); }

                if (outFmt != null)
                {
                    if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && outFmt->pb != null)
                        ffmpeg.avio_closep(&outFmt->pb);

                    AVFormatContext* t = outFmt;
                    outFmt = null;
                    ffmpeg.avformat_free_context(t);
                }

                if (inFmt != null)
                {
                    AVFormatContext* t = inFmt;
                    inFmt = null;
                    ffmpeg.avformat_close_input(&t);
                }
            }
        }
    }
}
