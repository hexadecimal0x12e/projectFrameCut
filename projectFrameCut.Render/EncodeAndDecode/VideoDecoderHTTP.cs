using projectFrameCut.Render.RenderAPIBase.Sources;

namespace projectFrameCut.Render.EncodeAndDecode;

public sealed class HttpDecoderContext : IVideoSource<Half>
{
    public static bool Enabled { get; set; } = true;
    public static bool AllowBeDeterministic { get; set; } = true;

    private readonly string? _url;
    private DecoderContextFP16? _decoder;

    public HttpDecoderContext() { }
    public HttpDecoderContext(string url)
    {
        _url = url;
        Initialize();
    }

    public HttpDecoderContext(Stream source, long length, bool leaveOpen = false)
    {
        if (!leaveOpen) source.Dispose();
        throw new NotSupportedException("HttpDecoderContext only supports HTTP URLs.");
    }

    private DecoderContextFP16 Decoder => _decoder ?? throw new InvalidOperationException("The HTTP decoder is not initialized.");
    public string TypeName => nameof(HttpDecoderContext);
    public string[] PreferredExtension => [];
    public bool IsDeterministic => AllowBeDeterministic;
    public bool AllowCachingResult => false;
    public int? ResultBitPerPixel => IPicture.PicturePixelMode.Float16Picture;
    public bool Disposed { get; private set; }
    public bool Initialized => _decoder?.Initialized == true;
    public long TotalFrames => _decoder?.TotalFrames ?? -1;
    public double Fps => _decoder?.Fps ?? 0;
    public int Width => _decoder?.Width ?? 0;
    public int Height => _decoder?.Height ?? 0;
    public uint Index { get => _decoder?.Index ?? 0; set => Decoder.Index = value; }
    public bool EnableLock { get => _decoder?.EnableLock ?? true; set => Decoder.EnableLock = value; }
    public bool StrictMode { get => _decoder?.StrictMode ?? false; set => Decoder.StrictMode = value; }

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (string.IsNullOrWhiteSpace(_url) || Initialized) return;
        if (!Enabled)
            throw new InvalidOperationException("HTTP decoder is disabled by security policy (Security_RemoteContent_EnableHttpDecoder).");
        _decoder = new DecoderContextFP16(_url, true);
    }

    public IVideoSource CreateNew(string newSource) => new HttpDecoderContext(newSource);
    public IVideoSource FromStream(Stream source, long length, bool leaveOpen = false) => new HttpDecoderContext(source, length, leaveOpen);
    public IPicture<Half> GetFrame(uint targetFrame) => Decoder.GetFrame(targetFrame);
    public IPicture<Half> GetFrame(uint targetFrame, int sourceX, int sourceY, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
        => Decoder.GetFrame(targetFrame, sourceX, sourceY, sourceWidth, sourceHeight, targetWidth, targetHeight);
    public IPicture<Half> GetFrame(uint targetFrame, int targetWidth, int targetHeight) => Decoder.GetFrame(targetFrame, targetWidth, targetHeight);

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        _decoder?.Dispose();
    }
}
