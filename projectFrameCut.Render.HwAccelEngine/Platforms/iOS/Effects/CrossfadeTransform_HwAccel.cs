#if IOS || MACCATALYST
using Foundation;
using Metal;
using projectFrameCut.Render.HwAccelEngine.Platforms.iOS;
using System.Runtime.InteropServices;

namespace projectFrameCut.Render.HwAccelEngine.Effect;

public partial class CrossfadeTransform_HwAccel
{
    private IMTLComputePipelineState? pipeline;

    private float[] ComputeBlend(float[] left, float[] right, float[] leftAlpha, float[] rightAlpha, float progress)
    {
        if (pipeline == null)
        {
            using var options = new MTLCompileOptions();
            using var library = MetalExecutionHelper.Device.CreateLibrary(Shader, options, out NSError error);
            if (library == null)
                throw new InvalidOperationException($"Crossfade shader: {error?.LocalizedDescription}");
            using var function = library.CreateFunction("crossfade");
            pipeline = MetalExecutionHelper.Device.CreateComputePipelineState(function, out error)
                ?? throw new InvalidOperationException($"Crossfade pipeline: {error?.LocalizedDescription}");
        }
        using var l = MetalExecutionHelper.CreateBuffer(left);
        using var r = MetalExecutionHelper.CreateBuffer(right);
        using var la = MetalExecutionHelper.CreateBuffer(leftAlpha);
        using var ra = MetalExecutionHelper.CreateBuffer(rightAlpha);
        using var output = MetalExecutionHelper.AllocBuffer(left.Length);
        var (cb, encoder) = MetalExecutionHelper.CreateCommandEncoder();
        using var commandBuffer = cb;
        using var commandEncoder = encoder;
        encoder.SetComputePipelineState(pipeline);
        encoder.SetBuffer(l, 0, 0);
        encoder.SetBuffer(r, 0, 1);
        encoder.SetBuffer(la, 0, 2);
        encoder.SetBuffer(ra, 0, 3);
        encoder.SetBuffer(output, 0, 4);
        unsafe
        {
            encoder.SetBytes((IntPtr)(&progress), (nuint)sizeof(float), 5);
        }
        var (groupSize, threads) = MetalExecutionHelper.ComputeDispatchSizes(left.Length, (int)pipeline.MaxTotalThreadsPerThreadgroup);
        encoder.DispatchThreads(threads, groupSize);
        encoder.EndEncoding();
        cb.Commit();
        cb.WaitUntilCompleted();
        MetalExecutionHelper.CheckCommand(cb);
        var result = new float[left.Length];
        Marshal.Copy(output.Contents, result, 0, result.Length);
        return result;
    }

    private const string Shader = """
        #include <metal_stdlib>
        using namespace metal;
        kernel void crossfade(device const float* l [[buffer(0)]], device const float* r [[buffer(1)]],
            device const float* la [[buffer(2)]], device const float* ra [[buffer(3)]],
            device float* output [[buffer(4)]], constant float& p [[buffer(5)]],
            uint i [[thread_position_in_grid]])
        {
            float wl = la[i] * (1 - p), wr = ra[i] * p;
            output[i] = wl + wr > 0 ? (l[i] * wl + r[i] * wr) / (wl + wr) : 0;
        }
        """;

    partial void ReleaseNativeResources()
    {
        pipeline?.Dispose();
        pipeline = null;
    }
}
#endif
