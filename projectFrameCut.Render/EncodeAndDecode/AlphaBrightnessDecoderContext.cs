using FFmpeg.AutoGen;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Processing.Converting;
using projectFrameCut.Drawing.Processing.Cropping;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System.Globalization;
using System.Runtime.InteropServices;

namespace projectFrameCut.Render.EncodeAndDecode;
/// <summary>Reads MKV packages produced by <see cref="AlphaBrightnessVideoWriter"/>.</summary>
public sealed unsafe class AlphaBrightnessDecoderContext : IVideoSource<ushort>, IHDRVideoSource
{
    private readonly string _path;
    private readonly FFmpegStreamIOContext? _streamIO;
    private readonly Lock _locker = new();
    private AVFormatContext* _format;
    private AVPacket* _packet;
    private TrackDecoder? _main;
    private TrackDecoder? _alpha;
    private TrackDecoder? _brightness;
    private int _mainIndex = -1;
    private int _nextFrame;
    private bool _eof;
    private float _maximumBrightness = 1000f;

    public bool Disposed { get; private set; }
    public bool Initialized { get; private set; }
    public bool EnableLock { get; set; } = true;
    public bool StrictMode { get; set; } = true;
    public string TypeName => nameof(AlphaBrightnessDecoderContext);
    public string[] PreferredExtension => [".mkv"];
    public int? ResultBitPerPixel => 16;
    public uint Index { get; set; }
    public long TotalFrames { get; private set; } = -1;
    public double Fps { get; private set; }
    public int Width => _main?.Width ?? 0;
    public int Height => _main?.Height ?? 0;

    public AlphaBrightnessDecoderContext() { _path = null!; }
    public AlphaBrightnessDecoderContext(string path) { _path = path; Initialize(); }
    public AlphaBrightnessDecoderContext(Stream source, long length, bool leaveOpen = false)
    {
        _path = "<stream>";
        _streamIO = new FFmpegStreamIOContext(source, length, leaveOpen);
        Initialize();
    }

    public IVideoSource CreateNew(string newSource) => new AlphaBrightnessDecoderContext(newSource);
    public IVideoSource FromStream(Stream source, long length, bool leaveOpen = false) => new AlphaBrightnessDecoderContext(source, length, leaveOpen);

    public static bool IsAlphaBrightnessVideo(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        AVFormatContext* c = null;
        try
        {
            if (ffmpeg.avformat_open_input(&c, path, null, null) < 0 || ffmpeg.avformat_find_stream_info(c, null) < 0) return false;
            AVDictionaryEntry* e = ffmpeg.av_dict_get(c->metadata, AlphaBrightnessVideoWriter.FormatMetadataKey, null, 0);
            return e != null && Marshal.PtrToStringAnsi((nint)e->value) == AlphaBrightnessVideoWriter.FormatVersion;
        }
        catch { return false; }
        finally { if (c != null) { AVFormatContext* q = c; ffmpeg.avformat_close_input(&q); } }
    }

    public void Initialize()
    {
        if (Initialized || (_path is null && _streamIO is null)) return;
        try
        {
            _format = ffmpeg.avformat_alloc_context();
            if (_format == null) throw new OutOfMemoryException("Failed to allocate the multistream format context.");
            fixed (AVFormatContext** p = &_format)
            {
                int result = _streamIO is null
                    ? ffmpeg.avformat_open_input(p, _path, null, null)
                    : _streamIO.Open(p);
                FFmpegHelper.Throw(result, "open multistream package");
            }
            FFmpegHelper.Throw(CheckIO(ffmpeg.avformat_find_stream_info(_format, null)), "read multistream package");

            AVDictionaryEntry* format = ffmpeg.av_dict_get(_format->metadata, AlphaBrightnessVideoWriter.FormatMetadataKey, null, 0);
            if (format == null || Marshal.PtrToStringAnsi((nint)format->value) != AlphaBrightnessVideoWriter.FormatVersion)
                throw new InvalidDataException("This is not a projectFrameCut multistream MKV.");

            int alpha = -1, brightness = -1;
            for (int i = 0; i < _format->nb_streams; i++)
            {
                AVStream* stream = _format->streams[i];
                if (stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO) continue;
                string? role = ReadMetadata(stream->metadata, AlphaBrightnessVideoWriter.StreamRoleMetadataKey);
                if (role == "main") _mainIndex = _mainIndex < 0 ? i : throw new InvalidDataException("The package contains multiple main streams.");
                else if (role == AlphaBrightnessVideoWriter.AlphaRole) alpha = alpha < 0 ? i : throw new InvalidDataException("The package contains multiple alpha streams.");
                else if (role == AlphaBrightnessVideoWriter.BrightnessRole)
                {
                    brightness = brightness < 0 ? i : throw new InvalidDataException("The package contains multiple brightness streams.");
                    if (float.TryParse(ReadMetadata(stream->metadata, AlphaBrightnessVideoWriter.MaximumBrightnessMetadataKey), NumberStyles.Float, CultureInfo.InvariantCulture, out float n) && float.IsFinite(n) && n > 0) _maximumBrightness = n;
                }
            }

            if (_mainIndex != 0 || (alpha < 0 && brightness < 0)) throw new InvalidDataException("The multistream package is missing its declared tracks.");
            ValidateAuxiliary(_format, _mainIndex, alpha);
            ValidateAuxiliary(_format, _mainIndex, brightness);

            _main = new TrackDecoder(_format, _mainIndex, AVPixelFormat.AV_PIX_FMT_BGR48LE, true);
            if (alpha >= 0) _alpha = new TrackDecoder(_format, alpha, AVPixelFormat.AV_PIX_FMT_GRAY16LE, false);
            if (brightness >= 0) _brightness = new TrackDecoder(_format, brightness, AVPixelFormat.AV_PIX_FMT_GRAY16LE, false);
            _packet = ffmpeg.av_packet_alloc();
            if (_packet == null) throw new OutOfMemoryException("Failed to allocate the multistream packet.");

            AVStream* mainStream = _format->streams[_mainIndex];
            AVRational rate = mainStream->avg_frame_rate;
            if (rate.num == 0 || rate.den == 0) rate = mainStream->r_frame_rate;
            Fps = rate.den == 0 ? 0 : ffmpeg.av_q2d(rate);
            if (Fps <= 0) throw new InvalidDataException("The multistream package has no valid frame rate.");
            TotalFrames = mainStream->nb_frames;
            if (TotalFrames <= 0 && mainStream->duration > 0)
                TotalFrames = (long)Math.Ceiling(mainStream->duration * ffmpeg.av_q2d(mainStream->time_base) * Fps);

            Initialized = true;
            Log($"[AlphaBrightnessDecoderContext] Initialized progressive multistream decoder for {_path}.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public IPicture<ushort> GetFrame(uint targetFrame) => GetHDRFrame(targetFrame, true).DegradeToSDR();
    public IPicture<ushort> GetFrame(uint targetFrame, bool hasAlpha = false) => GetHDRFrame(targetFrame, hasAlpha).DegradeToSDR();
    public HDRPicture16bpp GetHDRFrame(uint targetFrame, bool hasAlpha = false) => GetHDRFrame(targetFrame, 0, 0, Width, Height, Width, Height, hasAlpha);

    public HDRPicture16bpp GetHDRFrame(uint targetFrame, int sourceX, int sourceY, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, bool hasAlpha = false)
    {
        if (Disposed) throw new ObjectDisposedException(nameof(AlphaBrightnessDecoderContext));
        if (!Initialized || _main is null) throw new InvalidOperationException("The multistream decoder is not initialized.");
        if (targetFrame > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(targetFrame));
        if (sourceX < 0 || sourceY < 0 || sourceWidth <= 0 || sourceHeight <= 0 || sourceX > Width - sourceWidth || sourceY > Height - sourceHeight)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "The crop rectangle must be inside the decoded frame.");
        if (targetWidth <= 0 || targetHeight <= 0) throw new ArgumentOutOfRangeException(nameof(targetWidth));

        if (EnableLock) _locker.Enter();
        try
        {
            int target = (int)targetFrame;
            if (target != _nextFrame) Seek(target);
            DecodeUntil(target);

            ushort[] main = _main.Take(target);
            ushort[]? alpha = _alpha?.Take(target);
            ushort[]? brightness = _brightness?.Take(target);
            int pixels = Width * Height;
            var picture = new HDRPicture16bpp(Width, Height)
            {
                r = new ushort[pixels],
                g = new ushort[pixels],
                b = new ushort[pixels],
                Brightness = new float[pixels],
                MaximumBrightness = _maximumBrightness
            };
            for (int i = 0; i < pixels; i++)
            {
                picture.b[i] = main[i * 3];
                picture.g[i] = main[i * 3 + 1];
                picture.r[i] = main[i * 3 + 2];
            }
            if (alpha is not null)
            {
                picture.a = new float[pixels];
                picture.HasAlphaChannel = true;
                for (int i = 0; i < pixels; i++) picture.a[i] = alpha[i] / 65535f;
            }
            if (brightness is not null)
                for (int i = 0; i < pixels; i++) picture.Brightness[i] = brightness[i] / 65535f;

            _nextFrame = checked(target + 1);
            Index++;
            if (sourceX == 0 && sourceY == 0 && sourceWidth == Width && sourceHeight == Height && targetWidth == Width && targetHeight == Height) return picture;

            try
            {
                if (sourceX != 0 || sourceY != 0 || sourceWidth != Width || sourceHeight != Height)
                {
                    HDRPicture16bpp cropped = (HDRPicture16bpp)PictureCropper.Crop(picture, sourceX, sourceY, sourceWidth, sourceHeight);
                    picture.Dispose();
                    picture = cropped;
                }
                if (picture.Width != targetWidth || picture.Height != targetHeight)
                {
                    HDRPicture16bpp resized = (HDRPicture16bpp)((IHDRPicture<ushort>)picture).Resize(targetWidth, targetHeight, false);
                    picture.Dispose();
                    picture = resized;
                }
                return picture;
            }
            catch
            {
                picture.Dispose();
                throw;
            }
        }
        finally
        {
            if (EnableLock) _locker.Exit();
        }
    }

    private void DecodeUntil(int target)
    {
        while (!HasFrame(target))
        {
            DrainAll(target);
            if (HasFrame(target)) break;

            int read = CheckIO(ffmpeg.av_read_frame(_format, _packet));
            if (read >= 0)
            {
                try
                {
                    TrackDecoder? track = GetTrack(_packet->stream_index);
                    if (track is not null)
                    {
                        int sent = ffmpeg.avcodec_send_packet(track.Codec, _packet);
                        if (sent == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                        {
                            track.Drain(target, Fps);
                            sent = ffmpeg.avcodec_send_packet(track.Codec, _packet);
                        }
                        if (sent < 0) FFmpegHelper.Throw(sent, "send multistream packet");
                    }
                }
                finally { ffmpeg.av_packet_unref(_packet); }
                continue;
            }

            if (!_eof)
            {
                _eof = true;
                _main!.Flush();
                _alpha?.Flush();
                _brightness?.Flush();
                DrainAll(target);
            }
            if (!HasFrame(target)) throw new OverflowException($"Frame #{target} does not exist in multistream video '{_path}'.");
        }
    }

    private void DrainAll(int target)
    {
        _main!.Drain(target, Fps);
        _alpha?.Drain(target, Fps);
        _brightness?.Drain(target, Fps);
    }

    private bool HasFrame(int target) =>
        _main!.Has(target) && (_alpha?.Has(target) ?? true) && (_brightness?.Has(target) ?? true);

    private TrackDecoder? GetTrack(int streamIndex)
    {
        if (_main?.StreamIndex == streamIndex) return _main;
        if (_alpha?.StreamIndex == streamIndex) return _alpha;
        return _brightness?.StreamIndex == streamIndex ? _brightness : null;
    }

    private void Seek(int target)
    {
        AVStream* stream = _format->streams[_mainIndex];
        double timeBase = ffmpeg.av_q2d(stream->time_base);
        double seconds = Math.Max(0, target / Fps - 0.5);
        long start = stream->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : stream->start_time;
        long timestamp = timeBase > 0 ? start + (long)(seconds / timeBase) : start;
        int result = CheckIO(ffmpeg.av_seek_frame(_format, _mainIndex, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD));
        if (result < 0)
        {
            seconds = 0;
            FFmpegHelper.Throw(CheckIO(ffmpeg.av_seek_frame(_format, _mainIndex, start, ffmpeg.AVSEEK_FLAG_BACKWARD)), "seek multistream package");
        }
        ffmpeg.avformat_flush(_format);
        int fallbackFrame = Math.Max(0, (int)Math.Floor(seconds * Fps));
        _main!.Reset(fallbackFrame);
        _alpha?.Reset(fallbackFrame);
        _brightness?.Reset(fallbackFrame);
        ffmpeg.av_packet_unref(_packet);
        _eof = false;
    }

    private int CheckIO(int result) => _streamIO?.Check(result) ?? result;

    private static string? ReadMetadata(AVDictionary* metadata, string key)
    {
        AVDictionaryEntry* e = ffmpeg.av_dict_get(metadata, key, null, 0);
        return e == null ? null : Marshal.PtrToStringAnsi((nint)e->value);
    }

    private static void ValidateAuxiliary(AVFormatContext* c, int main, int auxiliary)
    {
        if (auxiliary < 0) return;
        AVCodecParameters* a = c->streams[main]->codecpar;
        AVCodecParameters* b = c->streams[auxiliary]->codecpar;
        if (a->width != b->width || a->height != b->height || b->format != (int)AVPixelFormat.AV_PIX_FMT_GRAY16LE)
            throw new InvalidDataException("Auxiliary stream dimensions or pixel format do not match the main stream.");
        if (c->streams[main]->time_base.num != c->streams[auxiliary]->time_base.num || c->streams[main]->time_base.den != c->streams[auxiliary]->time_base.den)
            throw new InvalidDataException("Auxiliary stream time base does not match the main stream.");
    }

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        _main?.Dispose();
        _alpha?.Dispose();
        _brightness?.Dispose();
        if (_packet != null) { AVPacket* p = _packet; _packet = null; ffmpeg.av_packet_free(&p); }
        if (_format != null) { AVFormatContext* f = _format; _format = null; ffmpeg.avformat_close_input(&f); }
        _streamIO?.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class TrackDecoder : IDisposable
    {
        private readonly AVFormatContext* _format;
        private readonly AVPixelFormat _outputFormat;
        private readonly bool _color;
        private readonly AVFrame* _frame;
        private readonly AVFrame* _output;
        private readonly byte* _buffer;
        private readonly Dictionary<int, ushort[]> _frames = [];
        private SwsContext* _sws;
        private AVPixelFormat _inputFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        private int _fallbackFrame;
        private bool _flushed;

        public int StreamIndex { get; }
        public int Width { get; }
        public int Height { get; }
        public AVCodecContext* Codec { get; private set; }

        public TrackDecoder(AVFormatContext* format, int streamIndex, AVPixelFormat outputFormat, bool color)
        {
            _format = format;
            StreamIndex = streamIndex;
            _outputFormat = outputFormat;
            _color = color;
            AVCodecParameters* parameters = format->streams[streamIndex]->codecpar;
            Width = parameters->width;
            Height = parameters->height;
            try
            {
                AVCodec* decoder = ffmpeg.avcodec_find_decoder(parameters->codec_id);
                if (decoder == null) throw new NotSupportedException($"No decoder is available for multistream track {streamIndex}.");
                Codec = ffmpeg.avcodec_alloc_context3(decoder);
                if (Codec == null) throw new OutOfMemoryException("Failed to allocate a multistream codec context.");
                FFmpegHelper.Throw(ffmpeg.avcodec_parameters_to_context(Codec, parameters), "copy multistream codec parameters");
                FFmpegHelper.Throw(ffmpeg.avcodec_open2(Codec, decoder, null), "open multistream codec");

                _frame = ffmpeg.av_frame_alloc();
                _output = ffmpeg.av_frame_alloc();
                int size = ffmpeg.av_image_get_buffer_size(outputFormat, Width, Height, 1);
                _buffer = size > 0 ? (byte*)ffmpeg.av_malloc((ulong)size) : null;
                if (_frame == null || _output == null || _buffer == null) throw new OutOfMemoryException("Failed to allocate multistream conversion buffers.");
                byte_ptrArray4 data = default;
                int_array4 lines = default;
                FFmpegHelper.Throw(ffmpeg.av_image_fill_arrays(ref data, ref lines, _buffer, outputFormat, Width, Height, 1), "prepare multistream conversion buffer");
                for (uint i = 0; i < 4; i++) { _output->data[i] = data[i]; _output->linesize[i] = lines[i]; }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Drain(int target, double fps)
        {
            while (true)
            {
                ffmpeg.av_frame_unref(_frame);
                int result = ffmpeg.avcodec_receive_frame(Codec, _frame);
                if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF) return;
                FFmpegHelper.Throw(result, "decode multistream frame");

                int frameNumber;
                if (VideoDecoderTimestamp.TryGetFrameNumber(_frame, _format->streams[StreamIndex], fps, out int timestampFrame))
                {
                    frameNumber = timestampFrame;
                    _fallbackFrame = Math.Max(_fallbackFrame + 1, frameNumber + 1);
                }
                else frameNumber = _fallbackFrame++;
                if (frameNumber < target || _frames.ContainsKey(frameNumber)) continue;
                _frames[frameNumber] = ConvertFrame();
            }
        }

        private ushort[] ConvertFrame()
        {
            AVPixelFormat input = (AVPixelFormat)_frame->format;
            if (_sws == null || _inputFormat != input)
            {
                if (_sws != null) ffmpeg.sws_freeContext(_sws);
                _inputFormat = input;
                _sws = ffmpeg.sws_getContext(Width, Height, input, Width, Height, _outputFormat, 4, null, null, null);
                if (_sws == null) throw new InvalidOperationException("Failed to create a multistream conversion context.");
            }
            FFmpegHelper.Throw(ffmpeg.sws_scale(_sws, _frame->data, _frame->linesize, 0, Height, _output->data, _output->linesize), "convert multistream frame");

            int channels = _color ? 3 : 1;
            ushort[] result = new ushort[Width * Height * channels];
            for (int y = 0; y < Height; y++)
            {
                ushort* row = (ushort*)(_output->data[0] + y * _output->linesize[0]);
                new ReadOnlySpan<ushort>(row, Width * channels).CopyTo(result.AsSpan(y * Width * channels, Width * channels));
            }
            return result;
        }

        public bool Has(int frame) => _frames.ContainsKey(frame);
        public ushort[] Take(int frame)
        {
            if (!_frames.Remove(frame, out ushort[]? data)) throw new InvalidOperationException($"Multistream track {StreamIndex} did not decode frame {frame}.");
            return data;
        }

        public void Flush()
        {
            if (_flushed) return;
            int result = ffmpeg.avcodec_send_packet(Codec, null);
            if (result < 0 && result != ffmpeg.AVERROR_EOF) FFmpegHelper.Throw(result, "flush multistream codec");
            _flushed = true;
        }

        public void Reset(int fallbackFrame)
        {
            ffmpeg.avcodec_flush_buffers(Codec);
            _frames.Clear();
            _fallbackFrame = fallbackFrame;
            _flushed = false;
        }

        public void Dispose()
        {
            _frames.Clear();
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            if (_buffer != null) ffmpeg.av_free(_buffer);
            if (_output != null) { AVFrame* f = _output; ffmpeg.av_frame_free(&f); }
            if (_frame != null) { AVFrame* f = _frame; ffmpeg.av_frame_free(&f); }
            if (Codec != null) { AVCodecContext* c = Codec; Codec = null; ffmpeg.avcodec_free_context(&c); }
        }
    }
}
