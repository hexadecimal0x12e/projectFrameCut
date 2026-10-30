using projectFrameCut.Drawing.Processing.Converting;
using projectFrameCut.Shared;
using projectFrameCut.Drawing.Base.ReadWriteConvert;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.RenderAPIBase.Sources;

namespace projectFrameCut.Render.EncodeAndDecode;

public sealed class PngSequenceVideoWriter : IVideoWriter
{
    public int Width { get; set; }
    public int Height { get; set; }
    public string OutputPath { get; set; } = string.Empty;
    public int FramePerSecond { get; set; }
    public string CodecName { get; set; } = "png";
    public string PixelFormat { get; set; } = string.Empty;
    public long BitRate { get; set; }
    public bool PreferToSpeed { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
    public uint StartFrame { get; set; }
    public uint DurationWritten { get; private set; }
    public IPicture.PicturePixelMode? TargetPPB => IPicture.PicturePixelMode.Float16Picture;

    public void Initialize() => Directory.CreateDirectory(OutputPath);
    public bool SupportCodec(string codecName) => codecName == "png";
    public string GetFramePath(uint frameIndex) => Path.Combine(OutputPath, $"frame-{frameIndex:D10}.png");

    public void Append(IPicture<Half> picture)
    {
        using (picture)
        {
            using var stream = new FileStream(GetFramePath(checked(StartFrame + DurationWritten)), FileMode.CreateNew, FileAccess.Write);
            if (picture is HDRPictureFP16 hdr)
            {
                using var sdr = hdr.DegradeToSDR(HDRImageDegradeToSDRMode.NormalizeBrightnessToRGB);
                sdr.SaveToPng(stream);
            }
            else picture.SaveToPng(stream);
            DurationWritten++;
        }
    }

    public void Finish() { }
    public void Dispose() { }
}
