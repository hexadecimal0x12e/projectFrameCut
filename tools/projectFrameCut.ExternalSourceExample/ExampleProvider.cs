using projectFrameCut.Render.Contracts;
using System.Buffers.Binary;

namespace projectFrameCut.ExternalSourceExample;

public sealed class ExampleProvider : IExternalVideoSourceProvider
{
    private readonly Dictionary<Guid, ExternalVideoSourceDescriptor> instances = [];
    public IReadOnlyList<ExternalVideoSourceDescriptor> Sources { get; } = new[] { "8bit", "16bit", "alpha", "hdr" }.Select(id => new ExternalVideoSourceDescriptor
    {
        SourceId = id, DecoderName = "Example", Name = "Example " + id, Width = 640, Height = 360,
        TotalFrames = 300, Fps = 30, HasKnownResultBitsPerPixel = true, ResultBitsPerPixel = id == "8bit" ? 8 : 16,
        SupportsHdr = id == "hdr", SupportsAlpha = id is "alpha" or "hdr",
        AllowCachingResult = true,
    }).ToList();

    public ValueTask<ExternalVideoSourceInstance> CreateAsync(ExternalVideoSourceCreateRequest request, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        instances.Add(id, Sources.Single(x => x.SourceId == request.Source.SourceId));
        return ValueTask.FromResult(new ExternalVideoSourceInstance { InstanceId = id, Descriptor = instances[id] });
    }
    public ValueTask<ExternalVideoSourceInstance> InitializeAsync(ExternalVideoSourceStateRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new ExternalVideoSourceInstance { InstanceId = request.InstanceId, Descriptor = instances[request.InstanceId] });
    public ValueTask<ExternalVideoFrame> ReadFrameAsync(ExternalVideoSourceReadRequest request, CancellationToken cancellationToken = default)
    {
        var source = instances[request.State.InstanceId];
        var width = request.UseRegion ? request.TargetWidth : source.Width;
        var height = request.UseRegion ? request.TargetHeight : source.Height;
        var bytes = source.ResultBitsPerPixel / 8;
        var pixels = checked(width * height);
        var frame = new ExternalVideoFrame
        {
            Width = width, Height = height, BitsPerChannel = source.ResultBitsPerPixel,
            Red = new byte[pixels * bytes], Green = new byte[pixels * bytes], Blue = new byte[pixels * bytes],
            Alpha = source.SupportsAlpha ? new byte[pixels * 4] : [],
            Brightness = request.RequestHdr && source.SupportsHdr ? new byte[pixels * 4] : [], MaximumBrightness = 1000,
        };
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                var sx = request.UseRegion ? request.SourceX + x * request.SourceWidth / width : x;
                var sy = request.UseRegion ? request.SourceY + y * request.SourceHeight / height : y;
                var value = (byte)((sx + sy + request.TargetFrame * 7) % 256);
                if (bytes == 1)
                {
                    frame.Red[i] = value;
                    frame.Green[i] = (byte)(255 - value);
                    frame.Blue[i] = 128;
                }
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(frame.Red.AsSpan(i * 2), (ushort)(value * 257));
                    BinaryPrimitives.WriteUInt16LittleEndian(frame.Green.AsSpan(i * 2), (ushort)((255 - value) * 257));
                    BinaryPrimitives.WriteUInt16LittleEndian(frame.Blue.AsSpan(i * 2), 32768);
                }
                if (frame.Alpha.Length > 0) BitConverter.TryWriteBytes(frame.Alpha.AsSpan(i * 4), 0.5f);
                if (frame.Brightness.Length > 0) BitConverter.TryWriteBytes(frame.Brightness.AsSpan(i * 4), 2f);
            }
        }
        return ValueTask.FromResult(frame);
    }
    public ValueTask ReleaseAsync(ExternalVideoSourceStateRequest request, CancellationToken cancellationToken = default)
    {
        instances.Remove(request.InstanceId);
        return ValueTask.CompletedTask;
    }
}
