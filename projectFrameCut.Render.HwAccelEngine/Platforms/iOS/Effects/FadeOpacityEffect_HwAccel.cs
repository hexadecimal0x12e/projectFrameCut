#if IOS || MACCATALYST
using Metal;
using Foundation;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.HwAccelEngine.Platforms.iOS;

namespace projectFrameCut.Render.HwAccelEngine.Effect
{
    public partial class FadeOpacityEffect_HwAccel
    {
        private IMTLComputePipelineState? _pipelineState;
        private void InitializePipeline()
        {
            if (_pipelineState != null)
                return;
            var device = MetalExecutionHelper.Device;
            using var options = new MTLCompileOptions();
            using var library = device.CreateLibrary(ShaderSource, options, out NSError error);
            if (library == null)
                throw new Exception($"Failed to compile: {error?.LocalizedDescription}");
            using var func = library.CreateFunction("opacity_compute");
            _pipelineState = device.CreateComputePipelineState(func, out error);
            if (_pipelineState == null)
                throw new Exception($"Pipeline failed: {error?.LocalizedDescription}");
        }

        private FourChannelResult ComputeOpacity(float[] r, float[] g, float[] b, float[] a, float opacity)
        {
            InitializePipeline();
            int count = r.Length;
            using var rBuf = MetalExecutionHelper.CreateBuffer(r);
            using var gBuf = MetalExecutionHelper.CreateBuffer(g);
            using var bBuf = MetalExecutionHelper.CreateBuffer(b);
            using var aBuf = MetalExecutionHelper.CreateBuffer(a);
            using var rOBuf = MetalExecutionHelper.AllocBuffer(count);
            using var gOBuf = MetalExecutionHelper.AllocBuffer(count);
            using var bOBuf = MetalExecutionHelper.AllocBuffer(count);
            using var aOBuf = MetalExecutionHelper.AllocBuffer(count);
            var(cb, encoder) = MetalExecutionHelper.CreateCommandEncoder();
            using var commandBuffer = cb;
            using var commandEncoder = encoder;
            encoder.SetComputePipelineState(_pipelineState!);
            encoder.SetBuffer(rBuf, 0, 0);
            encoder.SetBuffer(gBuf, 0, 1);
            encoder.SetBuffer(bBuf, 0, 2);
            encoder.SetBuffer(aBuf, 0, 3);
            unsafe
            {
                encoder.SetBytes((IntPtr)(&opacity), (nuint)sizeof(float), 4);
            }

            encoder.SetBuffer(rOBuf, 0, 5);
            encoder.SetBuffer(gOBuf, 0, 6);
            encoder.SetBuffer(bOBuf, 0, 7);
            encoder.SetBuffer(aOBuf, 0, 8);
            var(tgs, tg) = MetalExecutionHelper.ComputeDispatchSizes(count, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup);
            encoder.DispatchThreads(tg, tgs);
            encoder.EndEncoding();
            cb.Commit();
            cb.WaitUntilCompleted();
            MetalExecutionHelper.CheckCommand(cb);
            var rO = new float[count];
            var gO = new float[count];
            var bO = new float[count];
            var aO = new float[count];
            Marshal.Copy(rOBuf.Contents, rO, 0, count);
            Marshal.Copy(gOBuf.Contents, gO, 0, count);
            Marshal.Copy(bOBuf.Contents, bO, 0, count);
            Marshal.Copy(aOBuf.Contents, aO, 0, count);
            return new FourChannelResult(rO, gO, bO, aO);
        }

        private const string ShaderSource = @"
#include <metal_stdlib>
using namespace metal;
kernel void opacity_compute(
    device const float* r [[buffer(0)]], device const float* g [[buffer(1)]],
    device const float* b [[buffer(2)]], device const float* a [[buffer(3)]],
    constant float& opacity [[buffer(4)]],
    device float* rO [[buffer(5)]], device float* gO [[buffer(6)]],
    device float* bO [[buffer(7)]], device float* aO [[buffer(8)]],
    uint id [[thread_position_in_grid]])
{
    rO[id] = r[id]; gO[id] = g[id]; bO[id] = b[id];
    aO[id] = a[id] * opacity;
}
";
        partial void ReleaseNativeResources()
        {
            _pipelineState?.Dispose();
            _pipelineState = null;
        }
    }
}
#endif
