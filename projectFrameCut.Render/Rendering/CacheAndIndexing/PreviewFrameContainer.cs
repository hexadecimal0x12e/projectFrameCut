using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.ReadWriteConvert;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;

namespace projectFrameCut.Render.Rendering;

public static class PreviewFrameContainer
{
    public const string ViewExtension = ".pfcv";
    public const string ViewContainer = "pfcv";
    private static readonly VfdV2PictureEncoder encoder = new(VfdCompression.Zstandard, zstandardLevel: 1);
    private static readonly object[] gates = Enumerable.Range(0, 1024).Select(_ => new object()).ToArray();

    public static object Gate(string path) => gates[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(GetSourceKey(Path.GetFullPath(path))) % (uint)gates.Length];

    public static bool IsPreviewContainer(string path)
        => Path.GetExtension(path).Equals(".vfd2m", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path).Equals(ViewExtension, StringComparison.OrdinalIgnoreCase);

    public static string GetContainer(string path)
        => Path.GetExtension(path).Equals(ViewExtension, StringComparison.OrdinalIgnoreCase) ? ViewContainer : "vfd2m";

    internal static string GetSourceKey(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (!name.StartsWith("dynamic_vfd2m_", StringComparison.Ordinal)
            && !name.StartsWith("dynamic_pfcv_", StringComparison.Ordinal)) return path;
        int index = name.LastIndexOf('_');
        return name.Length - index - 1 == 67 && name.AsSpan(index + 1).StartsWith("fh-", StringComparison.Ordinal)
            ? path[..(path.Length - Path.GetFileName(path).Length)] + name[..index] + Path.GetExtension(path) : path;
    }

    internal static string GetSourcePrefix(string path) => Path.ChangeExtension(GetSourceKey(path), null) + "_";

    public static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);

    // Image blocks can be skipped without allocating or decompressing the source picture.
    private static long SeekMetadata(Stream stream)
    {
        Span<byte> header = stackalloc byte[24];
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("VFCD"u8) || header[4] != 0xFF || header[5] != 2 || header[9] != 1)
            throw new InvalidDataException("Not a VFD2M preview container.");
        while (true)
        {
            stream.ReadExactly(header[..12]);
            int raw = BinaryPrimitives.ReadInt32LittleEndian(header), stored = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            if (raw == 0 && stored == 0 && BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) == 0) return stream.Position;
            if (raw <= 0 || raw > 1048576 || stored == 0 || stored == int.MinValue || (stored < 0 && stored != -raw)
                || Math.Abs((long)stored) > stream.Length - stream.Position)
                throw new InvalidDataException("Invalid VFD2M picture block.");
            stream.Seek(Math.Abs((long)stored), SeekOrigin.Current);
        }
    }

    public static PreviewFrameData Read(string path, string? expectedEffectHash = null, bool? scRgb = null)
        => Read(path, expectedEffectHash, scRgb, null);

    public static PreviewFrameData Read(string path, string? expectedEffectHash, bool? scRgb,
        Func<int, byte[]>? allocatePixels)
    {
        lock (Gate(path))
        {
            using var stream = Open(path);
            return Read(stream, expectedEffectHash, scRgb, allocatePixels);
        }
    }

    public static PreviewFrameData Read(Stream stream, string? expectedEffectHash = null, bool? scRgb = null)
        => Read(stream, expectedEffectHash, scRgb, null);

    public static PreviewFrameData Read(Stream stream, string? expectedEffectHash, bool? scRgb,
        Func<int, byte[]>? allocatePixels)
    {
        long length = ReadMetadataLength(stream);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        using var metadata = new CheckedReadStream(stream, length);
        using var data = new BinaryReader(metadata, System.Text.Encoding.UTF8, leaveOpen: true);
        var info = ReadInfo(data, expectedEffectHash, scRgb);
        byte[] pixels = info.ScRgb && allocatePixels is not null
            ? allocatePixels(info.PixelsLength) : GC.AllocateUninitializedArray<byte>(info.PixelsLength);
        if (pixels.Length != info.PixelsLength) throw new InvalidDataException("Invalid preview pixel buffer length.");
        metadata.ReadExactly(pixels);
        byte[] mask = data.ReadBytes(info.MaskLength);
        if (metadata.Position != length || reader.ReadUInt32() != metadata.Checksum)
            throw new InvalidDataException("Preview metadata checksum mismatch.");
        if (Convert.ToHexString(SHA256.HashData(pixels)) != info.ContentHash) throw new InvalidDataException("Preview pixel hash mismatch.");
        return new(info.SourceHash, info.EffectHash, info.ScRgb, info.Width, info.Height, info.Stride,
            info.HasTransparency, info.ContentHash, pixels, mask);
    }

    // Pixel integrity is checked when the frame is materialized.
    public static PreviewFrameInfo ReadInfo(string path, string? expectedEffectHash = null, bool? scRgb = null)
    {
        lock (Gate(path))
        {
            using var stream = Open(path);
            using var metadata = new CheckedReadStream(stream, ReadMetadataLength(stream));
            using var reader = new BinaryReader(metadata, System.Text.Encoding.UTF8, leaveOpen: true);
            return ReadInfo(reader, expectedEffectHash, scRgb);
        }
    }

    private static long ReadMetadataLength(Stream stream)
    {
        Span<byte> magic = stackalloc byte[4];
        stream.ReadExactly(magic);
        if (!magic.SequenceEqual("PFCV"u8))
        {
            stream.Seek(-magic.Length, SeekOrigin.Current);
            SeekMetadata(stream);
        }
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        long length = reader.ReadInt64();
        if (length < 0 || length > int.MaxValue || length > stream.Length - stream.Position - 4)
            throw new InvalidDataException("Invalid preview metadata length.");
        return length;
    }

    private static PreviewFrameInfo ReadInfo(BinaryReader data, string? expectedEffectHash, bool? scRgb)
    {
        if (data.ReadUInt32() != 0x31435046) throw new InvalidDataException("Unsupported preview metadata.");
        string sourceHash = data.ReadString(), effectHash = data.ReadString();
        bool isScRgb = data.ReadBoolean();
        int width = data.ReadInt32(), height = data.ReadInt32(), stride = data.ReadInt32();
        bool transparent = data.ReadBoolean();
        string contentHash = data.ReadString();
        int pixelsLength = data.ReadInt32(), maskLength = data.ReadInt32();
        if (width <= 0 || height <= 0 || pixelsLength <= 0 || maskLength != (checked((long)width * height) + 7) / 8
            || (long)pixelsLength + maskLength != data.BaseStream.Length - data.BaseStream.Position
            || (isScRgb && (stride != checked(width * 8) || pixelsLength != checked((long)stride * height))))
            throw new InvalidDataException("Invalid preview pixel dimensions.");
        if ((expectedEffectHash is { Length: > 0 } && expectedEffectHash != effectHash) || (scRgb.HasValue && scRgb.Value != isScRgb))
            throw new InvalidDataException("The cached preview belongs to a different effect or output format.");
        return new(sourceHash, effectHash, isScRgb, width, height, stride, transparent, contentHash, pixelsLength, maskLength);
    }

    public static IPicture LoadSource(string path)
    {
        using var stream = Open(path);
        // Source recovery does not depend on an intact derived metadata trailer.
        Span<byte> header = stackalloc byte[24];
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("VFCD"u8) || header[4] != 0xFF || header[5] != 2)
            throw new InvalidDataException("Invalid VFD2M source header.");
        header[9] = 0;
        using var source = new HeaderStream(header.ToArray(), stream);
        if (!PictureExtensions.SharedVfdPictureDecoder.TryLoadFP16(source, out var picture) || picture is null)
            throw new InvalidDataException("Invalid cached preview source.");
        return picture;
    }

    public static void Save(string path, IPicture source, PreviewFrameData data, CancellationToken token)
    {
        lock (Gate(path)) Write(path, source, data, token);
    }

    public static void SaveView(string path, PreviewFrameData data, CancellationToken token)
    {
        lock (Gate(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using var metadata = CreateMetadata(data);
                token.ThrowIfCancellationRequested();
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
                using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write("PFCV"u8);
                    writer.Write(metadata.Length);
                    using var checkedMetadata = new CheckedReadStream(metadata, metadata.Length);
                    checkedMetadata.CopyTo(output, 65536);
                    writer.Write(checkedMetadata.Checksum);
                }
                token.ThrowIfCancellationRequested();
                File.Move(temp, path, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    public static void ReplaceMetadata(string path, IPicture source, PreviewFrameData data, CancellationToken token)
    {
        lock (Gate(path))
        {
            if (File.Exists(path)) ReplaceMetadata(path, data, token);
            else Write(path, source, data, token);
        }
    }

    public static void ReplaceMetadata(string path, PreviewFrameData data, CancellationToken token)
    {
        using var metadata = CreateMetadata(data);
        lock (Gate(path))
        {
            string temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                using (var input = Open(path))
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
                {
                    long remaining = SeekMetadata(input);
                    input.Position = 0;
                    while (remaining > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        int count = (int)Math.Min(buffer.Length, remaining);
                        input.ReadExactly(buffer.AsSpan(0, count));
                        output.Write(buffer, 0, count);
                        remaining -= count;
                    }
                    using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
                    writer.Write(metadata.Length);
                    using var checkedMetadata = new CheckedReadStream(metadata, metadata.Length);
                    checkedMetadata.CopyTo(output, 65536);
                    writer.Write(checkedMetadata.Checksum);
                }
                token.ThrowIfCancellationRequested();
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }

    private static PartsStream CreateMetadata(PreviewFrameData data)
    {
        using var header = new MemoryStream();
        using (var writer = new BinaryWriter(header, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x31435046u);
            writer.Write(data.SourceHash); writer.Write(data.EffectHash); writer.Write(data.ScRgb);
            writer.Write(data.Width); writer.Write(data.Height); writer.Write(data.Stride); writer.Write(data.HasTransparency);
            writer.Write(data.ContentHash); writer.Write(data.Pixels.Length); writer.Write(data.AlphaMask.Length);
        }
        return new([header.ToArray(), data.Pixels, data.AlphaMask]);
    }

    private static void Write(string path, IPicture source, PreviewFrameData data, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using var metadata = CreateMetadata(data);
            token.ThrowIfCancellationRequested();
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
            {
                using var downloaded = source is IHwAccelPicture gpu ? gpu.Download() : null;
                encoder.Save(downloaded ?? source, output, metadata, metadata.Length);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private sealed class HeaderStream(byte[] header, Stream tail) : ReadStream
    {
        private int position;
        public override int Read(Span<byte> buffer)
        {
            if (position == header.Length) return tail.Read(buffer);
            int count = Math.Min(buffer.Length, header.Length - position);
            header.AsSpan(position, count).CopyTo(buffer);
            position += count;
            return count;
        }
    }

    private sealed class PartsStream(byte[][] parts) : ReadStream
    {
        private int part, offset;
        public override long Length => parts.Sum(p => (long)p.Length);
        public override int Read(Span<byte> buffer)
        {
            while (part < parts.Length && offset == parts[part].Length)
            {
                part++;
                offset = 0;
            }
            if (part == parts.Length) return 0;
            int count = Math.Min(buffer.Length, parts[part].Length - offset);
            parts[part].AsSpan(offset, count).CopyTo(buffer);
            offset += count;
            return count;
        }
    }

    private sealed class CheckedReadStream(Stream input, long length) : ReadStream
    {
        private readonly Crc32 crc = new();
        private long remaining = length;
        public uint Checksum => crc.GetCurrentHashAsUInt32();
        public override long Length => length;
        public override long Position { get => length - remaining; set => throw new NotSupportedException(); }
        public override int Read(Span<byte> buffer)
        {
            int count = input.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
            if (count == 0 && remaining != 0) throw new EndOfStreamException();
            crc.Append(buffer[..count]);
            remaining -= count;
            return count;
        }
    }

    private abstract class ReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public abstract override int Read(Span<byte> buffer);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
