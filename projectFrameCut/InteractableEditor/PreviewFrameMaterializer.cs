using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Rendering;
using System.Security.Cryptography;

namespace projectFrameCut.LivePreview;

internal static class PreviewFrameMaterializer
{
    public static IPicture LoadVfd(string path)
    {
        if (PreviewFrameContainer.IsPreviewContainer(path))
            throw new InvalidDataException("A preview container's source picture is not its display result.");
        using var stream = PreviewFrameContainer.Open(path);
        if (!PictureExtensions.SharedVfdPictureDecoder.TryLoadFP16(stream, out var picture) || picture is null)
            throw new InvalidDataException($"Preview source '{path}' is not a valid VFD picture.");
        return picture;
    }

    public static PreviewFrameSource Prepare(PreviewFrameSource frame, PreviewFrameBuffer? buffer = null)
    {
        if (frame.ScRgb is { IsCurrent: true } || frame.Png is not null) return frame;
        Func<int, byte[]>? allocatePixels = buffer is null ? null : buffer.GetBytes;
        if (frame.PreviewContainer is "vfd2m" or PreviewFrameContainer.ViewContainer || PreviewFrameContainer.IsPreviewContainer(frame.VfdPath))
        {
            var data = PreviewFrameContainer.Read(frame.VfdPath, frame.PreviewEffectHash,
                frame.TargetPixelFormat == PreviewPixelFormat.Rgba16FloatScRgb, allocatePixels);
            return FromData(frame, data, buffer);
        }
        using var picture = LoadVfd(frame.VfdPath);
        return FromData(frame, PreviewFrameData.Create(picture, "", "",
            frame.TargetPixelFormat == PreviewPixelFormat.Rgba16FloatScRgb, allocatePixels), buffer);
    }

    public static PreviewFrameSource FromData(PreviewFrameSource frame, PreviewFrameData data, PreviewFrameBuffer? buffer = null)
        => frame with
        {
            Width = data.Width, Height = data.Height, AlphaMask = data.AlphaMask,
            PreviewEffectHash = data.EffectHash,
            ScRgb = data.ScRgb ? new(data.Pixels, data.Width, data.Height, data.Stride, data.HasTransparency, data.ContentHash)
                { Buffer = buffer, BufferVersion = buffer?.Version ?? 0 } : null,
            Png = data.ScRgb ? null : data.Pixels,
        };

    public static byte[] ToPngBytes(string path)
    {
        if (PreviewFrameContainer.IsPreviewContainer(path))
            return PreviewFrameContainer.Read(path).ToPng();
        using var picture = LoadVfd(path);
        return ToPngBytes(picture);
    }

    public static byte[] ToPngBytes(IPicture picture) => PreviewFrameData.ToPng(picture);

    public static byte[] ToPngBytes(PreviewFrameSource frame)
    {
        if (frame.Png is not null) return frame.Png;
        if (frame.ScRgb is { } pixels)
        {
            lock (pixels.PixelsGate)
            {
                if (pixels.IsCurrent)
                    return new PreviewFrameData("", frame.PreviewEffectHash, true, pixels.Width, pixels.Height, pixels.Stride,
                        pixels.HasTransparency, pixels.ContentHash, pixels.Bytes, frame.AlphaMask ?? []).ToPng();
            }
        }
        return ToPngBytes(frame.VfdPath);
    }

    public static ScRgbFrame ToScRgb(string path)
    {
        if (PreviewFrameContainer.IsPreviewContainer(path))
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
    internal PreviewFrameBuffer? Buffer { get; init; }
    internal long BufferVersion { get; init; }
    internal object PixelsGate => Buffer?.Gate ?? this;
    internal bool IsCurrent => Buffer is null || Volatile.Read(ref Buffer.Version) == BufferVersion;
}

internal sealed class PreviewFrameBuffer
{
    public object Gate { get; } = new();
    public long Version;
    private byte[] bytes = [];

    public byte[] GetBytes(int length)
    {
        if (bytes.Length != length) bytes = GC.AllocateUninitializedArray<byte>(length);
        return bytes;
    }
}
