using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.RenderAPIBase.Sources;

namespace SomePublisher;

public sealed class ExampleFrameWriter : IVideoWriter
{
    private System.IO.BinaryWriter? _writer;
    private uint _duration;

    public ExampleFrameWriter(string path) => OutputPath = path;

    public int Width { get; set; }
    public int Height { get; set; }
    public string OutputPath { get; set; }
    public int FramePerSecond { get; set; } = 30;
    public string CodecName { get; set; } = "ExampleFrameStream";
    public string PixelFormat { get; set; } = "rgb24";
    public long BitRate { get; set; }
    public bool PreferToSpeed { get; set; } = true;
    public Dictionary<string, string>? Metadata { get; set; }
    public uint DurationWritten => _duration;
    public IPicture.PicturePixelMode? TargetPPB => IPicture.PicturePixelMode.BytePicture;

    public void Initialize()
    {
        if (string.IsNullOrWhiteSpace(OutputPath)) throw new ArgumentException("An output path is required.", nameof(OutputPath));
        string? directory = Path.GetDirectoryName(Path.GetFullPath(OutputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _writer = new System.IO.BinaryWriter(File.Create(OutputPath));
        _writer.Write("PJFC-EXAMPLE-FRAMES"u8.ToArray());
        _writer.Write(Width);
        _writer.Write(Height);
        _writer.Write(FramePerSecond);
    }

    public bool SupportCodec(string codecName) => string.Equals(codecName, "ExampleFrameStream", StringComparison.OrdinalIgnoreCase);

    public void Append(IPicture<byte> picture)
    {
        EnsureInitialized();
        _writer!.Write(picture.Width);
        _writer.Write(picture.Height);
        _writer.Write(picture.r);
        _writer.Write(picture.g);
        _writer.Write(picture.b);
        _duration++;
    }

    public void Append(IPicture<ushort> picture)
    {
        EnsureInitialized();
        _writer!.Write(picture.Width);
        _writer.Write(picture.Height);
        for (int i = 0; i < picture.Pixels; i++) _writer.Write((byte)(picture.r[i] >> 8));
        for (int i = 0; i < picture.Pixels; i++) _writer.Write((byte)(picture.g[i] >> 8));
        for (int i = 0; i < picture.Pixels; i++) _writer.Write((byte)(picture.b[i] >> 8));
        _duration++;
    }

    public void Finish()
    {
        _writer?.Flush();
        _writer?.Dispose();
        _writer = null;
    }

    public void Dispose() => Finish();

    private void EnsureInitialized()
    {
        if (_writer is null) throw new InvalidOperationException("Initialize the example writer before appending frames.");
    }
}
