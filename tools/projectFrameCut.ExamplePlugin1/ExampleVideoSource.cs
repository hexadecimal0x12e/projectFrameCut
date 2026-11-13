using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.RenderAPIBase.Sources;

namespace SomePublisher;

public sealed class ExampleVideoSource : IVideoSource<Half>
{
    public bool IsDeterministic => true;

    private bool _disposed;

    public ExampleVideoSource(string? path = null) => SourcePath = path;

    public string? SourcePath { get; }
    public string TypeName => "ExampleVideoSource";
    public int? ResultBitPerPixel => IPicture.PicturePixelMode.Float16Picture;
    public bool AllowCachingResult => true;
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

    public IPicture<Half> GetFrame(uint targetFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Half[] red = new Half[Width * Height], green = new Half[Width * Height], blue = new Half[Width * Height];
        int shift = (int)(targetFrame % 255);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = y * Width + x;
                red[i] = (Half)(((x + shift) % 256) / 255f);
                green[i] = (Half)(((y + shift) % 256) / 255f);
                blue[i] = (Half)(((x + y + shift) % 256) / 255f);
            }
        }
        return PictureFP16.FromOwnedChannels(Width, Height, red, green, blue);
    }

    public void Dispose() => _disposed = true;
}
