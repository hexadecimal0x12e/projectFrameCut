using System.Buffers.Binary;
using projectFrameCut.Render.Contracts;
namespace projectFrameCut.Render.RPCProtocol;

public static class RenderPipeFrame
{
    public static async Task WriteAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length > RenderProtocol.MaxPipeFrameBytes) throw new InvalidDataException("Render pipe frame is too large.");
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        if (!await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > RenderProtocol.MaxPipeFrameBytes) throw new InvalidDataException($"Invalid render pipe frame length: {length}.");
        var payload = new byte[length];
        if (!await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false)) throw new EndOfStreamException("Render pipe frame was truncated.");
        return payload;
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }
}
