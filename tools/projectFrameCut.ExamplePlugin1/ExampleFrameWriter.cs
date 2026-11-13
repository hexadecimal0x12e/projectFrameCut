using System.Runtime.InteropServices;
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
    public string PixelFormat { get; set; } = "gbrpf16le";
    public long BitRate { get; set; }
    public bool PreferToSpeed { get; set; } = true;
    public Dictionary<string, string>? Metadata { get; set; }
    public uint DurationWritten => _duration;
    public IPicture.PicturePixelMode? TargetPPB => IPicture.PicturePixelMode.Float16Picture;

    public void Initialize()
    {
        if (string.IsNullOrWhiteSpace(OutputPath)) throw new ArgumentException("An output path is required.", nameof(OutputPath));
        string? directory = Path.GetDirectoryName(Path.GetFullPath(OutputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _writer = new System.IO.BinaryWriter(File.Create(OutputPath));
        _writer.Write("PJFC-EXAMPLE-FP16"u8.ToArray());
        _writer.Write(Width);
        _writer.Write(Height);
        _writer.Write(FramePerSecond);
    }

    public bool SupportCodec(string codecName) => string.Equals(codecName, "ExampleFrameStream", StringComparison.OrdinalIgnoreCase);

    public void Append(IPicture<Half> picture)
    {
        EnsureInitialized();
        if (picture.Width != Width || picture.Height != Height) throw new ArgumentException("Frame dimensions do not match the writer.");
        var channels = new PictureReadChannels<Half>(picture);
        _writer!.Write(picture.Width);
        _writer.Write(picture.Height);
        _writer.Write(MemoryMarshal.AsBytes(channels.Red));
        _writer.Write(MemoryMarshal.AsBytes(channels.Green));
        _writer.Write(MemoryMarshal.AsBytes(channels.Blue));
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
