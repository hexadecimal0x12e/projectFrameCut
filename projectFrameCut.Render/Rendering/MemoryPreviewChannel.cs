using System.IO.Pipes;
using System.Text.Json;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RPCProtocol;

namespace projectFrameCut.Render.Rendering;

public sealed record MemoryPreviewHeader(Guid ClipId, uint Frame, bool Positioned, PreviewFrameInfo Info);

public static class MemoryPreviewChannel
{
    public static NamedPipeServerStream CreateServer(string name) => new(name, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    public static async Task<NamedPipeClientStream> ConnectAsync(string name, string secret, CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(10000, token).ConfigureAwait(false);
            await RenderPipeFrame.WriteAsync(pipe, System.Text.Encoding.UTF8.GetBytes(secret), token).ConfigureAwait(false);
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }

    public static async Task AuthenticateAsync(NamedPipeServerStream pipe, string secret, CancellationToken token)
    {
        await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
        var bytes = await RenderPipeFrame.ReadAsync(pipe, token).ConfigureAwait(false);
        if (bytes is null || System.Text.Encoding.UTF8.GetString(bytes) != secret)
            throw new InvalidDataException("Invalid preview channel token.");
    }

    public static async Task SendAsync(Stream pipe, MemoryPreviewHeader header, PreviewFrameData data, CancellationToken token)
    {
        await RenderPipeFrame.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(header), token).ConfigureAwait(false);
        await pipe.WriteAsync(data.Pixels, token).ConfigureAwait(false);
        await pipe.WriteAsync(data.AlphaMask, token).ConfigureAwait(false);
    }

    public static async Task<(MemoryPreviewHeader Header, PreviewFrameData Data)> ReceiveAsync(Stream pipe, CancellationToken token, byte[]? metadata = null)
    {
        var header = JsonSerializer.Deserialize<MemoryPreviewHeader>(metadata ?? await RenderPipeFrame.ReadAsync(pipe, token).ConfigureAwait(false)
            ?? throw new EndOfStreamException()) ?? throw new InvalidDataException("Missing preview header.");
        var i = header.Info;
        if (i.Width <= 0 || i.Height <= 0 || i.PixelsLength <= 0 || i.MaskLength < 0
            || i.MaskLength > ((long)i.Width * i.Height + 7) / 8
            || (i.ScRgb && (i.Stride != (long)i.Width * 8 || i.PixelsLength != (long)i.Stride * i.Height)))
            throw new InvalidDataException("Invalid preview dimensions.");
        var pixels = GC.AllocateUninitializedArray<byte>(i.PixelsLength);
        var mask = GC.AllocateUninitializedArray<byte>(i.MaskLength);
        await pipe.ReadExactlyAsync(pixels, token).ConfigureAwait(false);
        await pipe.ReadExactlyAsync(mask, token).ConfigureAwait(false);
        return (header, new(i.SourceHash, i.EffectHash, i.ScRgb, i.Width, i.Height, i.Stride,
            i.HasTransparency, i.ContentHash, pixels, mask));
    }
}
