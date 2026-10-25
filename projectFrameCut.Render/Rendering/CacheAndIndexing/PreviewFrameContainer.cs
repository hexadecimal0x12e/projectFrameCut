using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.ReadWriteConvert;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;

namespace projectFrameCut.Render.Rendering;

public static class PreviewFrameContainer
{
    private static readonly VfdV2PictureEncoder encoder = new(VfdCompression.Zstandard, zstandardLevel: 1);
    private static readonly object[] gates = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();

    public static object Gate(string path) => gates[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(path) % (uint)gates.Length];

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
    {
        using var stream = Open(path);
        return Read(stream, expectedEffectHash, scRgb);
    }

    public static PreviewFrameData Read(Stream stream, string? expectedEffectHash = null, bool? scRgb = null)
    {
        SeekMetadata(stream);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        long length = reader.ReadInt64();
        if (length < 0 || length > int.MaxValue || length > stream.Length - stream.Position - 4)
            throw new InvalidDataException("Invalid preview metadata length.");
        using var metadata = new CheckedReadStream(stream, length);
        using var data = new BinaryReader(metadata, System.Text.Encoding.UTF8, leaveOpen: true);
        if (data.ReadUInt32() != 0x31435046) throw new InvalidDataException("Unsupported preview metadata.");
        string sourceHash = data.ReadString(), effectHash = data.ReadString();
        bool isScRgb = data.ReadBoolean();
        int width = data.ReadInt32(), height = data.ReadInt32(), stride = data.ReadInt32();
        bool transparent = data.ReadBoolean();
        string contentHash = data.ReadString();
        int pixelsLength = data.ReadInt32(), maskLength = data.ReadInt32();
        if (width <= 0 || height <= 0 || pixelsLength <= 0 || maskLength != (checked((long)width * height) + 7) / 8
            || (long)pixelsLength + maskLength != metadata.Length - metadata.Position
            || (isScRgb && (stride != checked(width * 8) || pixelsLength != checked((long)stride * height))))
            throw new InvalidDataException("Invalid preview pixel dimensions.");
        if ((expectedEffectHash is { Length: > 0 } && expectedEffectHash != effectHash) || (scRgb.HasValue && scRgb.Value != isScRgb))
            throw new InvalidDataException("The cached preview belongs to a different effect or output format.");
        byte[] pixels = data.ReadBytes(pixelsLength), mask = data.ReadBytes(maskLength);
        if (metadata.Position != length || reader.ReadUInt32() != metadata.Checksum)
            throw new InvalidDataException("Preview metadata checksum mismatch.");
        if (Convert.ToHexString(SHA256.HashData(pixels)) != contentHash) throw new InvalidDataException("Preview pixel hash mismatch.");
        return new(sourceHash, effectHash, isScRgb, width, height, stride, transparent, contentHash, pixels, mask);
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
        if (!PictureExtensions.SharedVfdPictureDecoder.TryLoad(source, out IPicture? picture) || picture is null)
            throw new InvalidDataException("Invalid cached preview source.");
        return picture;
    }

    public static void Save(string path, IPicture source, PreviewFrameData data, CancellationToken token)
        => Write(path, source, data, null, token);

    public static void ReplaceMetadata(string path, IPicture source, PreviewFrameData data, CancellationToken token)
        => Write(path, source, data, File.Exists(path) ? path : null, token);

    private static void Write(string path, IPicture source, PreviewFrameData data, string? copyFrom, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using var header = new MemoryStream();
            using (var writer = new BinaryWriter(header, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(0x31435046u);
                writer.Write(data.SourceHash); writer.Write(data.EffectHash); writer.Write(data.ScRgb);
                writer.Write(data.Width); writer.Write(data.Height); writer.Write(data.Stride); writer.Write(data.HasTransparency);
                writer.Write(data.ContentHash); writer.Write(data.Pixels.Length); writer.Write(data.AlphaMask.Length);
            }
            using var metadata = new PartsStream([header.ToArray(), data.Pixels, data.AlphaMask]);
            token.ThrowIfCancellationRequested();
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
            {
                if (copyFrom is null)
                {
                    using var downloaded = source is IHwAccelPicture gpu ? gpu.Download() : null;
                    encoder.Save(downloaded ?? source, output, metadata, metadata.Length);
                }
                else
                {
                    using var input = Open(copyFrom);
                    long end = SeekMetadata(input);
                    input.Position = 0;
                    byte[] buffer = new byte[65536];
                    while (end > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        int count = input.Read(buffer, 0, (int)Math.Min(end, buffer.Length));
                        if (count == 0) throw new EndOfStreamException();
                        output.Write(buffer, 0, count);
                        end -= count;
                    }
                    using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
                    writer.Write(metadata.Length);
                    using var checkedMetadata = new CheckedReadStream(metadata, metadata.Length);
                    checkedMetadata.CopyTo(output, 65536);
                    writer.Write(checkedMetadata.Checksum);
                }
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
