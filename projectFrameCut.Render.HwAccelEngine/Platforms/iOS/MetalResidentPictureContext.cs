#if IOS || MACCATALYST
using Foundation;
using Metal;
using projectFrameCut.Render.HwAccelEngine.Effect;
using System.Runtime.InteropServices;

namespace projectFrameCut.Render.HwAccelEngine.Platforms.iOS;
internal sealed class MetalResidentPictureContext : ResidentPictureContext
{
    private readonly IMTLComputePipelineState pipeline;
    private readonly IMTLCommandQueue queue;
    private readonly List<IMTLCommandBuffer> commands = [];
    private static readonly Lazy<IMTLComputePipelineState> sharedPipeline = new(CreatePipeline);
    private sealed class Buffer(IMTLBuffer data, int length) : ResidentBuffer
    {
        internal readonly IMTLBuffer Data = data;
        internal override int Length => length;
        internal override void Destroy() => Data.Dispose();
    }

    internal MetalResidentPictureContext()
    {
        pipeline = sharedPipeline.Value;
        queue = MetalExecutionHelper.Device.CreateCommandQueue() ?? throw new InvalidOperationException("Could not create picture command queue.");
    }
    private static IMTLComputePipelineState CreatePipeline()
    {
        using var options = new MTLCompileOptions();
        using var library = MetalExecutionHelper.Device.CreateLibrary(ResidentShaders.Metal, options, out NSError error) ?? throw new InvalidOperationException(error?.LocalizedDescription);
        using var function = library.CreateFunction("resident_compute");
        return MetalExecutionHelper.Device.CreateComputePipelineState(function, out error) ?? throw new InvalidOperationException(error?.LocalizedDescription);
    }

    protected override ResidentBuffer CreateBuffer(int length) => new Buffer(MetalExecutionHelper.Device.CreateBuffer((nuint)length, MTLResourceOptions.StorageModeShared) ?? throw new InvalidOperationException("Could not allocate GPU image."), length);
    protected override void WriteBuffer(ResidentBuffer buffer, byte[] values) => Marshal.Copy(values, 0, ((Buffer)buffer).Data.Contents, values.Length);
    protected override byte[] Read(ResidentBuffer buffer)
    {
        var b = (Buffer)buffer;
        var values = new byte[b.Length];
        Marshal.Copy(b.Data.Contents, values, 0, values.Length);
        return values;
    }

    protected override ResidentBuffer Dispatch(ResidentBuffer source, ResidentBuffer second, float[] parameters, int count)
    {
        int length = checked((int)parameters[3] * (int)parameters[4] * ResidentPixel.Size);
        BeginSubmission();
        var output = RentBuffer(length);
        try
        {
            using var p = UploadBuffer(ResidentPixel.PackParameters(parameters));
            var cb = queue.CommandBuffer() ?? throw new InvalidOperationException("Could not create picture command buffer.");
            KeepUntilComplete(cb);
            using var encoder = cb.ComputeCommandEncoder ?? throw new InvalidOperationException("Could not create picture compute encoder.");
            encoder.SetComputePipelineState(pipeline);
            encoder.SetBuffer(((Buffer)source).Data, 0, 0);
            encoder.SetBuffer(((Buffer)second).Data, 0, 1);
            encoder.SetBuffer(((Buffer)p).Data, 0, 2);
            encoder.SetBuffer(((Buffer)output).Data, 0, 3);
            var(size, grid) = MetalExecutionHelper.ComputeDispatchSizes(count, (int)pipeline.MaxTotalThreadsPerThreadgroup);
            encoder.DispatchThreads(grid, size);
            encoder.EndEncoding();
            commands.EnsureCapacity(commands.Count + 1);
            cb.Commit();
            commands.Add(cb);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    protected override void Synchronize()
    {
        Exception? error = null;
        foreach (var cb in commands)
        {
            cb.WaitUntilCompleted();
            try { MetalExecutionHelper.CheckCommand(cb); }
            catch (Exception ex) { error ??= ex; }
        }
        commands.Clear();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    protected override void Release() => queue.Dispose();
}
#endif
