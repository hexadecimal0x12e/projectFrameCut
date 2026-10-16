using projectFrameCut.Render.RenderAPIBase.Sources;

namespace projectFrameCut.Render.Sources;

public sealed class EmptyVideoSource : IVideoSource
{
    private const string PathPrefix = "#" + nameof(EmptyVideoSource) + ",";

    public EmptyVideoSource(int width = 1, int height = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
    }

    public string TypeName => nameof(EmptyVideoSource);
    public int? ResultBitPerPixel => 8;
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

    public void Initialize() => ObjectDisposedException.ThrowIf(Disposed, this);

    public IVideoSource CreateNew(string newSource)
    {
        if (newSource.StartsWith(PathPrefix, StringComparison.Ordinal)) newSource = newSource[PathPrefix.Length..];
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
        return Picture8bpp.GenerateSolidColor(Width, Height, 0, 0, 0, 0);
    }

    public void Dispose() => Disposed = true;
}
