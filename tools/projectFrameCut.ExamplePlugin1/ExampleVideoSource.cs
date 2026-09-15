using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.RenderAPIBase.Sources;

namespace SomePublisher;

public sealed class ExampleVideoSource : IVideoSource
{
    private bool _disposed;

    public ExampleVideoSource(string? path = null) => SourcePath = path;

    public string? SourcePath { get; }
    public string TypeName => "ExampleVideoSource";
    public int? ResultBitPerPixel => 8;
    public string[] PreferredExtension => [".examplevideo"];
    public uint Index { get; set; }
    public long TotalFrames => 300;
    public double Fps => 30;
    public int Width => 320;
    public int Height => 180;
    public bool Disposed => _disposed;
    public bool EnableLock { get; set; }
    public bool StrictMode { get; set; }

    public void Initialize() { }
    public IVideoSource CreateNew(string newSource) => new ExampleVideoSource(newSource);
    public IVideoSource FromStream(System.IO.Stream source, long length, bool leaveOpen = false) => new ExampleVideoSource("stream");

    public IPicture GetFrame(uint targetFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = new Picture8bpp(Width, Height);
        byte shift = (byte)(targetFrame % byte.MaxValue);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = y * Width + x;
                result.r[i] = (byte)((x + shift) % 256);
                result.g[i] = (byte)((y + shift) % 256);
                result.b[i] = (byte)((x + y + shift) % 256);
            }
        }
        return result;
    }

    public void Dispose() => _disposed = true;
}
