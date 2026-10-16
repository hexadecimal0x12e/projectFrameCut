using projectFrameCut.Render.Contracts;

namespace projectFrameCut.ExternalSourceExample;

public sealed class ExampleMaskSourceProvider : IExternalMaskSourceProvider
{
    public IReadOnlyList<ExternalMaskSourceDescriptor> MaskSources { get; } = [new()
    {
        SourceId = "invert", Name = "Example invert mask", Fields = [new()
        {
            Id = "Enabled", FieldType = 1UL << 8, DefaultValue = "true",
        }],
    }];

    public ValueTask<ExternalVideoFrame> ProcessMaskAsync(ExternalMaskSourceProcessRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Source.SourceId != "invert") throw new ArgumentException("Unknown mask source.");
        var frame = RenderRpcSerializer.Clone(request.Backdrop);
        if (request.Arguments.TryGetValue("Enabled", out var enabled) && !enabled.BooleanValue) return ValueTask.FromResult(frame);
        int stride = frame.Width * (frame.BitsPerChannel / 8);
        for (int y = 0; y < frame.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int i = y * stride; i < (y + 1) * stride; i++)
            {
                frame.Red[i] = (byte)(255 - frame.Red[i]);
                frame.Green[i] = (byte)(255 - frame.Green[i]);
                frame.Blue[i] = (byte)(255 - frame.Blue[i]);
            }
        }
        return ValueTask.FromResult(frame);
    }
}
