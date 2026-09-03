using FFmpeg.AutoGen;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Processing.Converting;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System.Runtime.InteropServices;

namespace projectFrameCut.Render.EncodeAndDecode;

public interface IHDRVideoSource
{
    HDRPicture16bpp GetHDRFrame(uint targetFrame, bool hasAlpha = false);
    HDRPicture16bpp GetHDRFrame(uint targetFrame, int sourceX, int sourceY, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, bool hasAlpha = false);
}

/// <summary>Reads MKV packages produced by <see cref="AlphaBrightnessVideoWriter"/>.</summary>
public sealed unsafe class AlphaBrightnessDecoderContext : IVideoSource<ushort>, IHDRVideoSource
{
    private readonly string _path;
    private DecoderContext16Bit? _main;
    private DecoderContext16Bit? _alpha;
    private DecoderContext16Bit? _brightness;
    private string? _cacheDirectory;
    private float _maximumBrightness = 1000f;
    public bool Disposed { get; private set; }
    public bool Initialized { get; private set; }
    public bool EnableLock { get => _main?.EnableLock ?? true; set { if (_main is not null) _main.EnableLock = value; if (_alpha is not null) _alpha.EnableLock = value; if (_brightness is not null) _brightness.EnableLock = value; } }
    public bool StrictMode { get => true; set { } }
    public string TypeName => nameof(AlphaBrightnessDecoderContext);
    public string[] PreferredExtension => [".mkv"];
    public int? ResultBitPerPixel => 16;
    public uint Index { get; set; }
    public long TotalFrames => _main?.TotalFrames ?? -1;
    public double Fps => _main?.Fps ?? 0;
    public int Width => _main?.Width ?? 0;
    public int Height => _main?.Height ?? 0;

    public AlphaBrightnessDecoderContext() { _path = null!; }
    public AlphaBrightnessDecoderContext(string path) { _path = path; Initialize(); }
    public IVideoSource CreateNew(string newSource) => new AlphaBrightnessDecoderContext(newSource);

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
        if (Initialized) return;
        if (string.IsNullOrWhiteSpace(_path)) return;
        if (!IsAlphaBrightnessVideo(_path)) throw new InvalidDataException("This is not a projectFrameCut multistream MKV.");
        AVFormatContext* c = null;
        try
        {
            FFmpegHelper.Throw(ffmpeg.avformat_open_input(&c, _path, null, null), "open multistream package");
            FFmpegHelper.Throw(ffmpeg.avformat_find_stream_info(c, null), "read multistream package");
            int main = -1, alpha = -1, brightness = -1;
            for (int i = 0; i < c->nb_streams; i++)
            {
                AVStream* s = c->streams[i];
                if (s->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO) continue;
                string? role = ReadMetadata(s->metadata, AlphaBrightnessVideoWriter.StreamRoleMetadataKey);
                if (role == "main") main = main < 0 ? i : throw new InvalidDataException("The package contains multiple main streams.");
                else if (role == AlphaBrightnessVideoWriter.AlphaRole) alpha = alpha < 0 ? i : throw new InvalidDataException("The package contains multiple alpha streams.");
                else if (role == AlphaBrightnessVideoWriter.BrightnessRole)
                {
                    brightness = brightness < 0 ? i : throw new InvalidDataException("The package contains multiple brightness streams.");
                    if (float.TryParse(ReadMetadata(s->metadata, AlphaBrightnessVideoWriter.MaximumBrightnessMetadataKey), out var n) && float.IsFinite(n) && n > 0) _maximumBrightness = n;
                }
            }
            if (main != 0 || (alpha < 0 && brightness < 0)) throw new InvalidDataException("The multistream package is missing its declared tracks.");
            ValidateAuxiliary(c, main, alpha); ValidateAuxiliary(c, main, brightness);
            _cacheDirectory = Path.Combine(Path.GetTempPath(), "pjfc_multistream_decode", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_cacheDirectory);
            if (alpha >= 0) _alpha = new DecoderContext16Bit(ExtractStream(c, alpha, "alpha"));
            if (brightness >= 0) _brightness = new DecoderContext16Bit(ExtractStream(c, brightness, "brightness"));
            _main = new DecoderContext16Bit(_path);
            Initialized = true;
        }
        catch { Dispose(); throw; }
        finally { if (c != null) { AVFormatContext* q = c; ffmpeg.avformat_close_input(&q); } }
    }

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

    private string ExtractStream(AVFormatContext* input, int index, string name)
    {
        FFmpegHelper.Throw(ffmpeg.av_seek_frame(input, -1, 0, ffmpeg.AVSEEK_FLAG_BACKWARD), "seek auxiliary extraction input");
        ffmpeg.avformat_flush(input);
        string path = Path.Combine(_cacheDirectory!, name + ".mkv");
        AVFormatContext* output = null;
        AVPacket* packet = null;
        try
        {
            FFmpegHelper.Throw(ffmpeg.avformat_alloc_output_context2(&output, null, "matroska", path), "allocate auxiliary extraction");
            AVStream* stream = ffmpeg.avformat_new_stream(output, null);
            FFmpegHelper.Throw(ffmpeg.avcodec_parameters_copy(stream->codecpar, input->streams[index]->codecpar), "copy auxiliary extraction parameters");
            stream->time_base = input->streams[index]->time_base;
            FFmpegHelper.Throw(ffmpeg.avio_open(&output->pb, path, ffmpeg.AVIO_FLAG_WRITE), "open auxiliary extraction");
            FFmpegHelper.Throw(ffmpeg.avformat_write_header(output, null), "write auxiliary extraction header");
            packet = ffmpeg.av_packet_alloc();
            while (ffmpeg.av_read_frame(input, packet) >= 0)
            {
                if (packet->stream_index == index)
                {
                    ffmpeg.av_packet_rescale_ts(packet, input->streams[index]->time_base, stream->time_base);
                    packet->stream_index = 0;
                    FFmpegHelper.Throw(ffmpeg.av_interleaved_write_frame(output, packet), "write auxiliary extraction packet");
                }
                ffmpeg.av_packet_unref(packet);
            }
            FFmpegHelper.Throw(ffmpeg.av_write_trailer(output), "write auxiliary extraction trailer");
            return path;
        }
        finally
        {
            if (packet != null) ffmpeg.av_packet_free(&packet);
            if (output != null) { if (output->pb != null) ffmpeg.avio_closep(&output->pb); ffmpeg.avformat_free_context(output); }
        }
    }

    public IPicture<ushort> GetFrame(uint targetFrame) => GetHDRFrame(targetFrame, true).DegradeToSDR();
    public IPicture<ushort> GetFrame(uint targetFrame, bool hasAlpha = false) => GetHDRFrame(targetFrame, hasAlpha).DegradeToSDR();
    public HDRPicture16bpp GetHDRFrame(uint targetFrame, bool hasAlpha = false) => GetHDRFrame(targetFrame, 0, 0, Width, Height, Width, Height, hasAlpha);
    public HDRPicture16bpp GetHDRFrame(uint targetFrame, int sourceX, int sourceY, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, bool hasAlpha = false)
    {
        if (_main is null) throw new ObjectDisposedException(nameof(AlphaBrightnessDecoderContext));
        int pixels = checked(targetWidth * targetHeight);
        using var main = _main.GetFrame(targetFrame, sourceX, sourceY, sourceWidth, sourceHeight, targetWidth, targetHeight);
        var picture = new HDRPicture16bpp(targetWidth, targetHeight)
        {
            r = main.r?.ToArray() ?? new ushort[pixels],
            g = main.g?.ToArray() ?? new ushort[pixels],
            b = main.b?.ToArray() ?? new ushort[pixels],
            Brightness = new float[pixels],
            MaximumBrightness = _maximumBrightness
        };
        if (_alpha is not null)
        {
            using var a = _alpha.GetFrame(targetFrame, sourceX, sourceY, sourceWidth, sourceHeight, targetWidth, targetHeight);
            picture.a = new float[pixels]; picture.HasAlphaChannel = true;
            for (int i = 0; i < pixels; i++) picture.a[i] = a.r[i] / 65535f;
        }
        if (_brightness is not null)
        {
            using var b = _brightness.GetFrame(targetFrame, sourceX, sourceY, sourceWidth, sourceHeight, targetWidth, targetHeight);
            picture.Brightness = new float[pixels]; picture.MaximumBrightness = _maximumBrightness;
            for (int i = 0; i < pixels; i++) picture.Brightness[i] = b.r[i] / 65535f;
        }
        Index++; return picture;
    }

    public void Dispose()
    {
        if (Disposed) return;
        _main?.Dispose(); _alpha?.Dispose(); _brightness?.Dispose();
        if (_cacheDirectory is not null) try { Directory.Delete(_cacheDirectory, true); } catch { }
        Disposed = true; GC.SuppressFinalize(this);
    }
}
