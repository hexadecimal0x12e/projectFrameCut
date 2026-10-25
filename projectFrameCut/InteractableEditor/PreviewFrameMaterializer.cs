using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Rendering;
using System.Security.Cryptography;

namespace projectFrameCut.LivePreview;

internal static class PreviewFrameMaterializer
{
    public static IPicture LoadVfd(string path)
    {
        if (Path.GetExtension(path).Equals(".vfd2m", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A preview container's source picture is not its display result.");
        using var stream = PreviewFrameContainer.Open(path);
        if (!PictureExtensions.SharedVfdPictureDecoder.TryLoad(stream, out IPicture? picture) || picture is null)
            throw new InvalidDataException($"Preview source '{path}' is not a valid VFD picture.");
        return picture;
    }

    public static PreviewFrameSource Prepare(PreviewFrameSource frame)
    {
        if (frame.ScRgb is not null || frame.Png is not null) return frame;
        if (frame.PreviewContainer == "vfd2m" || Path.GetExtension(frame.VfdPath).Equals(".vfd2m", StringComparison.OrdinalIgnoreCase))
        {
            var data = PreviewFrameContainer.Read(frame.VfdPath, frame.PreviewEffectHash,
                frame.TargetPixelFormat == PreviewPixelFormat.Rgba16FloatScRgb);
            return FromData(frame, data);
        }
        using var picture = LoadVfd(frame.VfdPath);
        return FromData(frame, PreviewFrameData.Create(picture, "", "", frame.TargetPixelFormat == PreviewPixelFormat.Rgba16FloatScRgb));
    }

    public static PreviewFrameSource FromData(PreviewFrameSource frame, PreviewFrameData data)
        => frame with
        {
            Width = data.Width, Height = data.Height, AlphaMask = data.AlphaMask,
            PreviewEffectHash = data.EffectHash,
            ScRgb = data.ScRgb ? new(data.Pixels, data.Width, data.Height, data.Stride, data.HasTransparency, data.ContentHash) : null,
            Png = data.ScRgb ? null : data.Pixels,
        };

    public static byte[] ToPngBytes(string path)
    {
        if (Path.GetExtension(path).Equals(".vfd2m", StringComparison.OrdinalIgnoreCase))
            return PreviewFrameContainer.Read(path).ToPng();
        using var picture = LoadVfd(path);
        return ToPngBytes(picture);
    }

    public static byte[] ToPngBytes(IPicture picture) => PreviewFrameData.ToPng(picture);

    public static byte[] ToPngBytes(PreviewFrameSource frame)
    {
        if (frame.Png is not null) return frame.Png;
        if (frame.ScRgb is { } pixels)
            return new PreviewFrameData("", frame.PreviewEffectHash, true, pixels.Width, pixels.Height, pixels.Stride,
                pixels.HasTransparency, pixels.ContentHash, pixels.Bytes, frame.AlphaMask ?? []).ToPng();
        return ToPngBytes(frame.VfdPath);
    }

    public static ScRgbFrame ToScRgb(string path)
    {
        if (Path.GetExtension(path).Equals(".vfd2m", StringComparison.OrdinalIgnoreCase))
        {
            var data = PreviewFrameContainer.Read(path, scRgb: true);
            return new(data.Pixels, data.Width, data.Height, data.Stride, data.HasTransparency, data.ContentHash);
        }
        using var picture = LoadVfd(path);
        return ToScRgb(picture);
    }

    public static ScRgbFrame ToScRgb(IPicture picture)
    {
        var data = PreviewFrameData.Create(picture, "", "", true);
        return new(data.Pixels, data.Width, data.Height, data.Stride, data.HasTransparency, data.ContentHash);
    }

    public static ImageSource CreateImageSource(string path)
    {
        var bytes = ToPngBytes(path);
        return ImageSource.FromStream(() => new MemoryStream(bytes, writable: false));
    }
}

internal sealed record ScRgbFrame(byte[] Bytes, int Width, int Height, int Stride, bool HasTransparency, string? CachedContentHash = null)
{
    public string ContentHash { get; } = CachedContentHash ?? Convert.ToHexString(SHA256.HashData(Bytes));
}
