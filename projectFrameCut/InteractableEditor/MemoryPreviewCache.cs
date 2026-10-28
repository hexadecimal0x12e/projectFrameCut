using System.IO.Pipes;
using System.Text.Json;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.RPCProtocol;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.LivePreview;

internal sealed class MemoryPreviewCache : IAsyncDisposable
{
    public string Name { get; } = $"projectFrameCut-preview-{Guid.NewGuid():N}";
    public string Secret { get; } = Guid.NewGuid().ToString("N");
    private readonly NamedPipeServerStream pipe;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task receiver;
    private readonly object gate = new();
    private readonly SortedDictionary<uint, List<(MemoryPreviewHeader Header, PreviewFrameSource Source)>> frames = new();
    private readonly PreviewBufferWindow window;
    private uint current;
    private uint complete;
    private Exception? error;
    private int disposed;

    public MemoryPreviewCache(long maxClipBytes, uint start, double frameRate, MemoryPreviewCache? previous = null)
    {
        window = new(maxClipBytes, frameRate);
        current = complete = start;
        if (previous is not null)
        {
            lock (previous.gate)
            {
                foreach (var frame in previous.frames.Where(f => f.Key >= start))
                    frames.Add(frame.Key, frame.Value);
                previous.frames.Clear();
            }
        }
        Trim(resetWindow: true);
        pipe = MemoryPreviewChannel.CreateServer(Name);
        receiver = Task.Run(ReceiveAsync);
    }

    public uint ClampFrame(uint frame)
    {
        lock (gate) return Math.Min(frame, complete);
    }

    public void Advance(uint frame)
    {
        lock (gate)
        {
            current = frame;
            Trim();
        }
    }

    private void Trim(bool resetWindow = false)
    {
        // A one-frame pool still needs the next frame in flight to hand playback over.
        long end = (long)current + Math.Max(2, window.FrameCount) - 1;
        foreach (var frame in frames.Keys.Where(f => f < current || resetWindow && f > end).ToArray())
            frames.Remove(frame);
    }

    public bool TryGet(uint frame, Guid clip, out PreviewFrameSource source, out bool positioned)
    {
        lock (gate)
        {
            if (error is not null) throw new IOException("Preview channel failed.", error);
            source = null!;
            positioned = false;
            if (!frames.TryGetValue(frame, out var entries)) return false;
            foreach (var entry in entries)
                if (entry.Header.ClipId == clip)
                {
                    source = entry.Source;
                    positioned = entry.Header.Positioned;
                    return true;
                }
            return false;
        }
    }

    private async Task ReceiveAsync()
    {
        var token = cancellation.Token;
        try
        {
            await MemoryPreviewChannel.AuthenticateAsync(pipe, Secret, token).ConfigureAwait(false);
            await pipe.WriteAsync(new byte[] { 1 }, token).ConfigureAwait(false);
            while (true)
            {
                var metadata = await RenderPipeFrame.ReadAsync(pipe, token).ConfigureAwait(false)
                    ?? throw new EndOfStreamException();
                if (metadata[0] != (byte)'{')
                {
                    var next = JsonSerializer.Deserialize<long>(metadata);
                    lock (gate)
                    {
                        complete = checked((uint)(next - 1));
                        Trim();
                    }
                    // Acknowledge the complete frame before waiting for room for more previews.
                    await pipe.WriteAsync(new byte[] { 1 }, token).ConfigureAwait(false);
                    while (true)
                    {
                        lock (gate)
                        {
                            Trim();
                            if (next < (long)current + Math.Max(2, window.FrameCount)) break;
                        }
                        await Task.Delay(10, token).ConfigureAwait(false);
                    }
                    await pipe.WriteAsync(new byte[] { 1 }, token).ConfigureAwait(false);
                    continue;
                }
                var (header, data) = await MemoryPreviewChannel.ReceiveAsync(pipe, token, metadata).ConfigureAwait(false);
                var source = PreviewFrameMaterializer.FromData(
                    new("", data.Width, data.Height, data.ScRgb ? PreviewPixelFormat.Rgba16FloatScRgb : PreviewPixelFormat.EncodedImage, false), data);
                lock (gate)
                {
                    window.Observe(data.Width, data.Height, (long)data.Pixels.Length + data.AlphaMask.Length);
                    if (!frames.TryGetValue(header.Frame, out var entries)) frames.Add(header.Frame, entries = []);
                    var old = entries.FindIndex(e => e.Header.ClipId == header.ClipId);
                    if (old >= 0) entries.RemoveAt(old);
                    entries.Add((header, source));
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                lock (gate) error = ex;
                pipe.Dispose();
                Log(ex, "Receive memory preview", this);
            }
        }
    }

    public void Cancel()
    {
        lock (gate)
            if (disposed == 0) cancellation.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed != 0) return;
            disposed = 1;
            cancellation.Cancel();
        }
        pipe.Dispose();
        await receiver.ConfigureAwait(false);
        cancellation.Dispose();
    }
}
