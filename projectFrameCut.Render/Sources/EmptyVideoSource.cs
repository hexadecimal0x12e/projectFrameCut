using projectFrameCut.Render.RenderAPIBase.Sources;

namespace projectFrameCut.Render.Sources;

public sealed class EmptyVideoSource : IVideoSource
{
    public bool IsDeterministic => true;

    private const string PathPrefix = "#" + nameof(EmptyVideoSource) + ",";

    public EmptyVideoSource(int width = 1, int height = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
    }

    public string TypeName => nameof(EmptyVideoSource);
    public int? ResultBitPerPixel => IPicture.PicturePixelMode.Float16Picture;
    public string[] PreferredExtension => [];
    public uint Index { get; set; }
    public long TotalFrames => long.MinValue;
    public double Fps => 0;
    public int Width { get; }
    public int Height { get; }
    public bool Disposed { get; private set; }
    public bool EnableLock { get; set; } = true;
    public bool StrictMode { get; set; } = true;

    public static string CreatePath(int width, int height) => $"{PathPrefix}{width}x{height}";

    public static bool IsPath(string? path) => path?.StartsWith(PathPrefix, StringComparison.Ordinal) == true;

    public void Initialize() => ObjectDisposedException.ThrowIf(Disposed, this);

    public IVideoSource CreateNew(string newSource)
    {
        if (IsPath(newSource)) newSource = newSource[PathPrefix.Length..];
        var parts = newSource.Split('x', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], out int width) || !int.TryParse(parts[1], out int height)
            || width <= 0 || height <= 0)
            throw new NotSupportedException($"Invalid empty video source '{newSource}'.");
        LogDiagnostic($"Created empty video source {width}x{height}.");
        return new EmptyVideoSource(width, height);
    }

    public IVideoSource FromStream(Stream source, long length, bool leaveOpen = false) =>
        throw new NotSupportedException("Empty video sources cannot be created from streams.");

    public IPicture GetFrame(uint targetFrame)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        Index = targetFrame == uint.MaxValue ? targetFrame : targetFrame + 1;
        return PictureFP16.GenerateSolidColor(Width, Height, Half.Zero, Half.Zero, Half.Zero, Half.Zero);
    }

    public void Dispose() => Disposed = true;
}
