using FFmpeg.AutoGen;
using projectFrameCut.Render.Compose;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace projectFrameCut.Render.EncodeAndDecode;

internal static unsafe class VideoChunkMerger
{
    public static void Merge(IReadOnlyList<(string Path, uint Duration)> chunks, string outputPath,
        int frameRate, uint totalFrames, AudioComposer<float>? audioComposer,
        Action<double, TimeSpan>? progress, CancellationToken token)
    {
        AVFormatContext* first = null;
        AVFormatContext* input = null;
        AVFormatContext* output = null;
        AVPacket* packet = null;
        AVDictionary* options = null;
        AudioEncoder? audio = null;
        string listPath = outputPath + ".ffconcat";
        var elapsed = Stopwatch.StartNew();
        try
        {
            token.ThrowIfCancellationRequested();
            FFmpegHelper.Throw(ffmpeg.avformat_open_input(&first, chunks[0].Path, null, null), "open first video chunk");
            FFmpegHelper.Throw(ffmpeg.avformat_find_stream_info(first, null), "read first video chunk");
            if (first->nb_streams == 0) throw new InvalidDataException("The first video chunk has no streams.");

            var list = new StringBuilder("ffconcat version 1.0\n");
            long frames = 0;
            var frameTimeBase = new AVRational { num = 1, den = frameRate };
            var microseconds = new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE };
            foreach (var chunk in chunks)
            {
                token.ThrowIfCancellationRequested();
                AVFormatContext* probe = null;
                try
                {
                    FFmpegHelper.Throw(ffmpeg.avformat_open_input(&probe, chunk.Path, null, null), $"open video chunk '{chunk.Path}'");
                    FFmpegHelper.Throw(ffmpeg.avformat_find_stream_info(probe, null), "read video chunk streams");
                    ValidateChunk(first, probe, chunk.Path);
                }
                finally { if (probe != null) ffmpeg.avformat_close_input(&probe); }

                string path = Path.GetRelativePath(Path.GetDirectoryName(listPath)!, chunk.Path).Replace('\\', '/');
                list.Append("file '").Append(path.Replace("'", "'\\''")).Append("'\n");
                // Use cumulative frame boundaries so rounded chunk durations do not accumulate drift.
                long start = ffmpeg.av_rescale_q(frames, frameTimeBase, microseconds);
                frames += chunk.Duration;
                long end = ffmpeg.av_rescale_q(frames, frameTimeBase, microseconds);
                list.Append("duration ").Append(((end - start) / 1_000_000m).ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            if (frames != totalFrames) throw new InvalidDataException("Video chunks do not cover the requested duration.");
            File.WriteAllText(listPath, list.ToString(), new UTF8Encoding(false));

            var concat = FFmpegHelper.FindInputFormatByName("concat");
            if (concat == null) throw new NotSupportedException("This FFmpeg build does not include the concat demuxer.");
            ffmpeg.av_dict_set(&options, "safe", "0", 0);
            ffmpeg.av_dict_set(&options, "protocol_whitelist", "file", 0);
            FFmpegHelper.Throw(ffmpeg.avformat_open_input(&input, listPath, concat, &options), "open video chunk list");
            FFmpegHelper.Throw(ffmpeg.avformat_find_stream_info(input, null), "read concatenated video streams");
            FFmpegHelper.Throw(ffmpeg.avformat_alloc_output_context2(&output, null, null, outputPath), "allocate merged output");
            if (output == null) throw new OutOfMemoryException();
            if (input->nb_streams != first->nb_streams) throw new InvalidDataException("The concatenated video has an unexpected stream count.");
            FFmpegHelper.Throw(ffmpeg.av_dict_copy(&output->metadata, first->metadata, 0), "copy video metadata");
            for (int i = 0; i < input->nb_streams; i++)
            {
                var source = input->streams[i];
                var stream = ffmpeg.avformat_new_stream(output, null);
                if (stream == null) throw new OutOfMemoryException();
                FFmpegHelper.Throw(ffmpeg.avcodec_parameters_copy(stream->codecpar, source->codecpar), "copy video stream parameters");
                stream->codecpar->codec_tag = 0;
                stream->time_base = source->time_base;
                stream->avg_frame_rate = source->avg_frame_rate;
                stream->r_frame_rate = source->r_frame_rate;
                stream->sample_aspect_ratio = source->sample_aspect_ratio;
                stream->disposition = first->streams[i]->disposition;
                FFmpegHelper.Throw(ffmpeg.av_dict_copy(&stream->metadata, first->streams[i]->metadata, 0), "copy video stream metadata");
            }

            if (audioComposer is not null)
            {
                audioComposer.StartFrame = 0;
                audioComposer.Duration = totalFrames;
                audio = new AudioEncoder(output, outputPath, audioComposer, frameRate, token);
            }
            Log($"Merging {chunks.Count} video chunks with FFmpeg stream copy; project audio: {audio is not null}.");
            if ((output->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                FFmpegHelper.Throw(ffmpeg.avio_open(&output->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE), "open merged output");
            FFmpegHelper.Throw(ffmpeg.avformat_write_header(output, null), "write merged header");
            packet = ffmpeg.av_packet_alloc();
            if (packet == null) throw new OutOfMemoryException();

            double completedSeconds = 0;
            long lastProgress = 0;
            int result;
            while ((result = ffmpeg.av_read_frame(input, packet)) >= 0)
            {
                token.ThrowIfCancellationRequested();
                if ((uint)packet->stream_index >= first->nb_streams)
                    throw new InvalidDataException("A video chunk introduced an unexpected stream.");
                var stream = input->streams[packet->stream_index];
                long timestamp = packet->dts != ffmpeg.AV_NOPTS_VALUE ? packet->dts : packet->pts;
                if (timestamp != ffmpeg.AV_NOPTS_VALUE) audio?.WriteUntil(timestamp * ffmpeg.av_q2d(stream->time_base));
                long pts = packet->pts != ffmpeg.AV_NOPTS_VALUE ? packet->pts : packet->dts;
                if (pts != ffmpeg.AV_NOPTS_VALUE)
                    completedSeconds = Math.Max(completedSeconds, (pts + packet->duration) * ffmpeg.av_q2d(stream->time_base));
                ffmpeg.av_packet_rescale_ts(packet, stream->time_base, output->streams[packet->stream_index]->time_base);
                packet->pos = -1;
                FFmpegHelper.Throw(ffmpeg.av_interleaved_write_frame(output, packet), "write merged video packet");
                if (elapsed.ElapsedMilliseconds - lastProgress >= 100)
                {
                    double value = Math.Clamp(completedSeconds * frameRate / totalFrames, 0, 0.999);
                    progress?.Invoke(value, value > 0 ? TimeSpan.FromSeconds(elapsed.Elapsed.TotalSeconds * (1 - value) / value) : TimeSpan.Zero);
                    lastProgress = elapsed.ElapsedMilliseconds;
                }
            }
            if (result != ffmpeg.AVERROR_EOF) FFmpegHelper.Throw(result, "read video chunk packet");
            audio?.Finish();
            token.ThrowIfCancellationRequested();
            FFmpegHelper.Throw(ffmpeg.av_write_trailer(output), "write merged trailer");
            if (output->pb != null) FFmpegHelper.Throw(ffmpeg.avio_closep(&output->pb), "close merged output");
            progress?.Invoke(1, TimeSpan.Zero);
            Log($"Merged {chunks.Count} video chunks in {elapsed.Elapsed.TotalSeconds:F2}s.");
        }
        finally
        {
            audio?.Dispose();
            if (packet != null) ffmpeg.av_packet_free(&packet);
            if (input != null) ffmpeg.avformat_close_input(&input);
            if (first != null) ffmpeg.avformat_close_input(&first);
            if (output != null)
            {
                if (output->pb != null) ffmpeg.avio_closep(&output->pb);
                ffmpeg.avformat_free_context(output);
            }
            ffmpeg.av_dict_free(&options);
            if (File.Exists(listPath)) File.Delete(listPath);
        }
    }

    private static void ValidateChunk(AVFormatContext* first, AVFormatContext* chunk, string path)
    {
        if (first->nb_streams != chunk->nb_streams
            || ReadMetadata(first->metadata, AlphaBrightnessVideoWriter.FormatMetadataKey) != ReadMetadata(chunk->metadata, AlphaBrightnessVideoWriter.FormatMetadataKey))
            throw new InvalidDataException($"Video chunk '{path}' has an incompatible stream layout.");
        for (int i = 0; i < first->nb_streams; i++)
        {
            var a = first->streams[i];
            var b = chunk->streams[i];
            var p = a->codecpar;
            var q = b->codecpar;
            if (p->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO || q->codec_type != p->codec_type
                || p->codec_id != q->codec_id || p->width != q->width || p->height != q->height || p->format != q->format
                || p->profile != q->profile || p->color_range != q->color_range || p->color_space != q->color_space
                || p->color_primaries != q->color_primaries || p->color_trc != q->color_trc
                || a->time_base.num != b->time_base.num || a->time_base.den != b->time_base.den
                || ReadMetadata(a->metadata, AlphaBrightnessVideoWriter.StreamRoleMetadataKey) != ReadMetadata(b->metadata, AlphaBrightnessVideoWriter.StreamRoleMetadataKey)
                || (p->codec_id != AVCodecID.AV_CODEC_ID_H264 && (p->extradata_size != q->extradata_size
                    || !new ReadOnlySpan<byte>(p->extradata, p->extradata_size).SequenceEqual(new ReadOnlySpan<byte>(q->extradata, q->extradata_size)))))
                throw new InvalidDataException($"Video chunk '{path}', stream {i} has different encoding parameters. Render all chunks with the same encoder settings.");

            if (Half.TryParse(ReadMetadata(b->metadata, AlphaBrightnessVideoWriter.MaximumBrightnessMetadataKey), NumberStyles.Float, CultureInfo.InvariantCulture, out Half brightness)
                && Half.IsFinite(brightness) && brightness > Half.Zero)
            {
                Half.TryParse(ReadMetadata(a->metadata, AlphaBrightnessVideoWriter.MaximumBrightnessMetadataKey), NumberStyles.Float, CultureInfo.InvariantCulture, out Half previous);
                ffmpeg.av_dict_set(&a->metadata, AlphaBrightnessVideoWriter.MaximumBrightnessMetadataKey,
                    Half.Max(previous, brightness).ToString(CultureInfo.InvariantCulture), 0);
            }
        }
    }

    private static string? ReadMetadata(AVDictionary* metadata, string key)
    {
        var entry = ffmpeg.av_dict_get(metadata, key, null, 0);
        return entry == null ? null : Marshal.PtrToStringUTF8((nint)entry->value);
    }

    private sealed class AudioEncoder : IDisposable
    {
        private const int SampleRate = 48000;
        private readonly AVFormatContext* output;
        private AVStream* stream;
        private AVCodecContext* encoder;
        private AVFrame* frame;
        private AVPacket* packet;
        private SwrContext* converter;
        private readonly Func<int, int, CancellationToken, FloatAudioSamples> read;
        private readonly CancellationToken token;
        private readonly int totalSamples;
        private readonly int frameSamples;
        private int written;
        private int bufferStart;
        private FloatAudioSamples? buffer;

        public AudioEncoder(AVFormatContext* output, string outputPath, AudioComposer<float> composer, int frameRate, CancellationToken token)
        {
            this.output = output;
            this.token = token;
            read = composer.CreateSampleReader(frameRate, SampleRate, 2, out totalSamples);
            try
            {
                string name = Path.GetExtension(outputPath).ToLowerInvariant() switch
                {
                    ".mkv" or ".webm" => "libvorbis",
                    ".avi" => "libmp3lame",
                    ".wmv" => "wmav2",
                    _ => "aac"
                };
                var codec = ffmpeg.avcodec_find_encoder_by_name(name);
                if (codec == null) throw new NotSupportedException($"Audio encoder '{name}' is unavailable.");
                encoder = ffmpeg.avcodec_alloc_context3(codec);
                if (encoder == null) throw new OutOfMemoryException();
                encoder->sample_rate = SampleRate;
                encoder->time_base = new AVRational { num = 1, den = SampleRate };
                encoder->bit_rate = 192000;
                AVSampleFormat* formats = null;
                int count = 0;
                FFmpegHelper.Throw(ffmpeg.avcodec_get_supported_config(encoder, codec, AVCodecConfig.AV_CODEC_CONFIG_SAMPLE_FORMAT, 0, (void**)&formats, &count), "read audio encoder sample formats");
                encoder->sample_fmt = formats != null && count > 0 ? formats[0] : AVSampleFormat.AV_SAMPLE_FMT_FLTP;
                for (int i = 0; i < count; i++)
                    if (formats[i] == AVSampleFormat.AV_SAMPLE_FMT_FLTP) encoder->sample_fmt = formats[i];
                ffmpeg.av_channel_layout_default(&encoder->ch_layout, 2);
                if ((output->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0) encoder->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
                FFmpegHelper.Throw(ffmpeg.avcodec_open2(encoder, codec, null), "open project audio encoder");
                stream = ffmpeg.avformat_new_stream(output, null);
                if (stream == null) throw new OutOfMemoryException();
                stream->time_base = encoder->time_base;
                FFmpegHelper.Throw(ffmpeg.avcodec_parameters_from_context(stream->codecpar, encoder), "copy project audio parameters");
                frameSamples = encoder->frame_size > 0 ? encoder->frame_size : 1024;
                frame = ffmpeg.av_frame_alloc();
                packet = ffmpeg.av_packet_alloc();
                if (frame == null || packet == null) throw new OutOfMemoryException();
                frame->format = (int)encoder->sample_fmt;
                frame->sample_rate = SampleRate;
                frame->nb_samples = frameSamples;
                FFmpegHelper.Throw(ffmpeg.av_channel_layout_copy(&frame->ch_layout, &encoder->ch_layout), "copy project audio channel layout");
                FFmpegHelper.Throw(ffmpeg.av_frame_get_buffer(frame, 0), "allocate project audio frame");
                SwrContext* swr = null;
                int result = ffmpeg.swr_alloc_set_opts2(&swr, &encoder->ch_layout, encoder->sample_fmt, SampleRate,
                    &encoder->ch_layout, AVSampleFormat.AV_SAMPLE_FMT_FLTP, SampleRate, 0, null);
                converter = swr;
                FFmpegHelper.Throw(result, "allocate project audio converter");
                FFmpegHelper.Throw(ffmpeg.swr_init(converter), "initialize project audio converter");
            }
            catch { Dispose(); throw; }
        }

        public void WriteUntil(double seconds)
        {
            int target = (int)Math.Clamp(Math.Ceiling(seconds * SampleRate), 0, totalSamples);
            byte** data = stackalloc byte*[2];
            while (written < target)
            {
                token.ThrowIfCancellationRequested();
                if (buffer is null || written >= bufferStart + buffer.SampleCount)
                {
                    // Mix in batches while feeding complete frames to the audio encoder.
                    bufferStart = written;
                    buffer = read(written, Math.Min(frameSamples * Math.Max(1, 40960 / frameSamples), totalSamples - written), token);
                }
                int samples = Math.Min(frameSamples, totalSamples - written);
                frame->nb_samples = frameSamples;
                FFmpegHelper.Throw(ffmpeg.av_frame_make_writable(frame), "prepare project audio frame");
                fixed (float* left = buffer.Channels[0])
                fixed (float* right = buffer.Channels[1])
                {
                    data[0] = (byte*)(left + written - bufferStart);
                    data[1] = (byte*)(right + written - bufferStart);
                    int converted = ffmpeg.swr_convert(converter, (byte**)&frame->data, frameSamples, data, samples);
                    FFmpegHelper.Throw(converted, "convert project audio samples");
                    if (converted != samples) throw new InvalidDataException("Project audio conversion returned an unexpected sample count.");
                    frame->nb_samples = converted;
                }
                frame->pts = written;
                Encode(frame);
                written += samples;
            }
        }

        public void Finish()
        {
            WriteUntil((double)totalSamples / SampleRate);
            Encode(null);
        }

        private void Encode(AVFrame* value)
        {
            int result = ffmpeg.avcodec_send_frame(encoder, value);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                Drain();
                result = ffmpeg.avcodec_send_frame(encoder, value);
            }
            FFmpegHelper.Throw(result, "encode project audio frame");
            Drain();
        }

        private void Drain()
        {
            int result;
            while ((result = ffmpeg.avcodec_receive_packet(encoder, packet)) >= 0)
            {
                token.ThrowIfCancellationRequested();
                ffmpeg.av_packet_rescale_ts(packet, encoder->time_base, stream->time_base);
                packet->stream_index = stream->index;
                packet->pos = -1;
                FFmpegHelper.Throw(ffmpeg.av_interleaved_write_frame(output, packet), "write project audio packet");
            }
            if (result != ffmpeg.AVERROR_EOF && result != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                FFmpegHelper.Throw(result, "receive project audio packet");
        }

        public void Dispose()
        {
            AVPacket* p = packet;
            if (p != null) ffmpeg.av_packet_free(&p);
            AVFrame* f = frame;
            if (f != null) ffmpeg.av_frame_free(&f);
            AVCodecContext* c = encoder;
            if (c != null) ffmpeg.avcodec_free_context(&c);
            SwrContext* s = converter;
            if (s != null) ffmpeg.swr_free(&s);
            packet = null;
            frame = null;
            encoder = null;
            converter = null;
        }
    }
}
