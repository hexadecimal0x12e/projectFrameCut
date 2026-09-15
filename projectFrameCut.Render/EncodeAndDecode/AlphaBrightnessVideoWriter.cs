using FFmpeg.AutoGen;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Processing.Converting;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.EncodeAndDecode;

[Flags]
public enum AuxiliaryVideoChannels
{
    None = 0,
    Alpha = 1,
    Brightness = 2
}

/// <summary>Writes a projectFrameCut lossless MKV side-channel package.</summary>
public sealed unsafe class AlphaBrightnessVideoWriter : IVideoWriter
{
    public const string FormatMetadataKey = "projectFrameCut.multistream";
    public const string FormatVersion = "1";
    public const string StreamRoleMetadataKey = "projectFrameCut.role";
    public const string AlphaRole = "alpha";
    public const string BrightnessRole = "hdr-brightness";
    public const string MaximumBrightnessMetadataKey = "projectFrameCut.maximum-brightness";

    public int Width { get; set; }
    public int Height { get; set; }
    public string OutputPath { get; set; } = string.Empty;
    public int FramePerSecond { get; set; }
    public string CodecName { get; set; } = "ffv1";
    public string PixelFormat { get; set; } = "AV_PIX_FMT_GBRP16LE";
    public long BitRate { get; set; } = 8_000_000;
    public bool PreferToSpeed { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
    public uint DurationWritten => _index;
    public IPicture.PicturePixelMode? TargetPPB => IPicture.PicturePixelMode.UShortPicture;
    public AuxiliaryVideoChannels Channels { get; set; }

    private VideoWriter? _main;
    private VideoWriter? _alpha;
    private VideoWriter? _brightness;
    private string? _workDirectory;
    private string? _mainPath;
    private string? _alphaPath;
    private string? _brightnessPath;
    private uint _index;
    private float _maximumBrightness;
    private bool _initialized;
    private bool _finished;

    public bool SupportCodec(string codecName) => string.Equals(codecName, "AlphaBrightnessVideoWriter", StringComparison.OrdinalIgnoreCase)
        || string.Equals(codecName, "pjfc-multistream", StringComparison.OrdinalIgnoreCase);

    public void Initialize()
    {
        if (_initialized) return;
        if (Channels == AuxiliaryVideoChannels.None) throw new InvalidOperationException("Select at least one auxiliary channel.");
        if (!OutputPath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Alpha/Brightness video packages must use .mkv.");
        if (Width <= 0 || Height <= 0 || FramePerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(Width));
        if (File.Exists(OutputPath)) throw new InvalidOperationException($"Video file {OutputPath} already exists.");

        _workDirectory = Path.Combine(GlobalPluginHelper.GetCacheRoot(), "pjfc_multistream", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDirectory);
        _mainPath = Path.Combine(_workDirectory, "main.mkv");
        _main = new VideoWriter
        {
            Width = Width, Height = Height, FramePerSecond = FramePerSecond, OutputPath = _mainPath,
            CodecName = CodecName, PixelFormat = PixelFormat, BitRate = BitRate, PreferToSpeed = PreferToSpeed, Metadata = Metadata
        };
        _main.Initialize();

        if (Channels.HasFlag(AuxiliaryVideoChannels.Alpha))
        {
            _alphaPath = Path.Combine(_workDirectory, "alpha.mkv");
            _alpha = CreateAuxiliaryWriter(_alphaPath);
        }
        if (Channels.HasFlag(AuxiliaryVideoChannels.Brightness))
        {
            _brightnessPath = Path.Combine(_workDirectory, "brightness.mkv");
            _brightness = CreateAuxiliaryWriter(_brightnessPath);
        }
        _initialized = true;
    }

    private VideoWriter CreateAuxiliaryWriter(string path)
    {
        var w = new VideoWriter { Width = Width, Height = Height, FramePerSecond = FramePerSecond, OutputPath = path, CodecName = "ffv1", PixelFormat = "AV_PIX_FMT_GRAY16LE", BitRate = BitRate };
        w.Initialize();
        return w;
    }

    public void Append(HDRPicture16bpp picture) => Append((IPicture<ushort>)picture);

    public void Append(IPicture<byte> picture) => throw new NotSupportedException("The multistream writer requires 16-bit pictures.");

    public void Append(IPicture<ushort> picture)
    {
        if (!_initialized || _main is null) throw new InvalidOperationException("Initialize before appending frames.");
        if (picture.Width != Width || picture.Height != Height) throw new ArgumentException("Frame dimensions do not match the writer.");
        int pixels = checked(Width * Height);
        if (_alpha is not null && picture.HasAlphaChannel && picture.a?.Length != pixels)
            throw new InvalidDataException($"Frame #{_index} has an invalid alpha channel.");
        if (_brightness is not null && picture is IHDRPicture<ushort> hdrSource && hdrSource.Brightness?.Length != pixels)
            throw new InvalidDataException($"Frame #{_index} has an invalid HDR brightness channel.");

        if (picture is HDRPicture16bpp hdrPicture)
        {
            using var sdr = hdrPicture.DegradeToSDR(HDRImageDegradeToSDRMode.DiscardBrightnessChannel);
            _main.Append(sdr);
        }
        else
        {
            _main.Append(picture);
        }
        if (picture is IHDRPicture<ushort> hdr && float.IsFinite(hdr.MaximumBrightness) && hdr.MaximumBrightness > 0f)
            _maximumBrightness = Math.Max(_maximumBrightness, hdr.MaximumBrightness);
        if (_alpha is not null)
        {
            using var alpha = CreateGrayPicture(picture, false);
            _alpha.Append(alpha);
        }
        if (_brightness is not null)
        {
            using var brightness = CreateGrayPicture(picture, true);
            _brightness.Append(brightness);
        }
        _index++;
    }

    private Picture16bpp CreateGrayPicture(IPicture<ushort> source, bool brightness)
    {
        int pixels = checked(Width * Height);
        ushort[] values = new ushort[pixels];
        if (brightness && source is IHDRPicture<ushort> hdr && hdr.Brightness?.Length == pixels)
        {
            for (int i = 0; i < pixels; i++) values[i] = (ushort)(Math.Clamp(float.IsFinite(hdr.Brightness[i]) ? hdr.Brightness[i] : 0f, 0f, 1f) * 65535f + .5f);
        }
        else if (!brightness && source.HasAlphaChannel && source.a?.Length == pixels)
        {
            for (int i = 0; i < pixels; i++) values[i] = (ushort)(Math.Clamp(float.IsFinite(source.a[i]) ? source.a[i] : 1f, 0f, 1f) * 65535f + .5f);
        }
        else if (!brightness)
        {
            Array.Fill(values, ushort.MaxValue);
        }
        return new Picture16bpp(Width, Height) { r = values, g = (ushort[])values.Clone(), b = (ushort[])values.Clone() };
    }

    public void Finish()
    {
        if (_finished) return;
        _main?.Finish(); _alpha?.Finish(); _brightness?.Finish();
        if (_index > 0 && _mainPath is not null) Mux(_mainPath, _alphaPath, _brightnessPath, OutputPath, _maximumBrightness > 0f ? _maximumBrightness : 1000f);
        _finished = true;
        Log($"[AlphaBrightnessVideoWriter] Finished {OutputPath}, {_index} frames, channels={Channels}.");
    }

    private static void Mux(string main, string? alpha, string? brightness, string output, float maximumBrightness)
    {
        var inputs = new List<(string path, string role)> { (main, "main") };
        if (alpha is not null) inputs.Add((alpha, AlphaRole));
        if (brightness is not null) inputs.Add((brightness, BrightnessRole));
        AVFormatContext* outputContext = null;
        AVFormatContext*[] inputContexts = new AVFormatContext*[inputs.Count];
        AVPacket*[] packets = new AVPacket*[inputs.Count];
        try
        {
            FFmpegHelper.Throw(ffmpeg.avformat_alloc_output_context2(&outputContext, null, "matroska", output), "allocate multistream output");
            for (int i = 0; i < inputs.Count; i++)
            {
                AVFormatContext* input = null;
                FFmpegHelper.Throw(ffmpeg.avformat_open_input(&input, inputs[i].path, null, null), "open multistream input");
                FFmpegHelper.Throw(ffmpeg.avformat_find_stream_info(input, null), "read multistream input");
                if (input->nb_streams != 1) throw new InvalidDataException("Temporary stream contains an unexpected number of tracks.");
                AVStream* stream = ffmpeg.avformat_new_stream(outputContext, null);
                if (stream == null) throw new InvalidOperationException("Create multistream track failed.");
                FFmpegHelper.Throw(ffmpeg.avcodec_parameters_copy(stream->codecpar, input->streams[0]->codecpar), "copy multistream parameters");
                stream->time_base = input->streams[0]->time_base;
                ffmpeg.av_dict_set(&stream->metadata, StreamRoleMetadataKey, inputs[i].role, 0);
                if (inputs[i].role == BrightnessRole)
                    ffmpeg.av_dict_set(&stream->metadata, MaximumBrightnessMetadataKey, maximumBrightness.ToString(System.Globalization.CultureInfo.InvariantCulture), 0);
                inputContexts[i] = input;
                packets[i] = ffmpeg.av_packet_alloc();
            }
            ffmpeg.av_dict_set(&outputContext->metadata, FormatMetadataKey, FormatVersion, 0);
            FFmpegHelper.Throw(ffmpeg.avio_open(&outputContext->pb, output, ffmpeg.AVIO_FLAG_WRITE), "open multistream output");
            FFmpegHelper.Throw(ffmpeg.avformat_write_header(outputContext, null), "write multistream header");
            var active = Enumerable.Repeat(true, inputContexts.Length).ToArray();
            while (active.Any(v => v))
                for (int i = 0; i < active.Length; i++)
                {
                    if (!active[i]) continue;
                    AVPacket* packet = packets[i];
                    int ret = ffmpeg.av_read_frame(inputContexts[i], packet);
                    if (ret == ffmpeg.AVERROR_EOF) { active[i] = false; continue; }
                    FFmpegHelper.Throw(ret, "read multistream packet");
                    ffmpeg.av_packet_rescale_ts(packet, inputContexts[i]->streams[0]->time_base, outputContext->streams[i]->time_base);
                    packet->stream_index = i;
                    FFmpegHelper.Throw(ffmpeg.av_interleaved_write_frame(outputContext, packet), "write multistream packet");
                    ffmpeg.av_packet_unref(packet);
                }
            FFmpegHelper.Throw(ffmpeg.av_write_trailer(outputContext), "write multistream trailer");
        }
        finally
        {
            for (int i = 0; i < packets.Length; i++) if (packets[i] != null) { AVPacket* q = packets[i]; ffmpeg.av_packet_free(&q); }
            for (int i = 0; i < inputContexts.Length; i++) if (inputContexts[i] != null) { AVFormatContext* q = inputContexts[i]; ffmpeg.avformat_close_input(&q); }
            if (outputContext != null) { if (outputContext->pb != null) ffmpeg.avio_closep(&outputContext->pb); ffmpeg.avformat_free_context(outputContext); }
        }
    }

    public void Dispose()
    {
        try { Finish(); } catch { }
        _main?.Dispose(); _alpha?.Dispose(); _brightness?.Dispose();
        if (_workDirectory is not null) try { Directory.Delete(_workDirectory, true); } catch { }
        GC.SuppressFinalize(this);
    }
}
