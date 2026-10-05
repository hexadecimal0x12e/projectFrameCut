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
    public partial class RemoveColorEffect_HwAccel
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
                throw new Exception($"Failed to compile shader: {error?.LocalizedDescription}");
            using var function = library.CreateFunction("remove_color_compute");
            _pipelineState = device.CreateComputePipelineState(function, out error);
            if (_pipelineState == null)
                throw new Exception($"Failed to create pipeline state: {error?.LocalizedDescription}");
        }

        private float[] ComputeRemoveColor(float[] r, float[] g, float[] b, float[] a, float targetR, float targetG, float targetB, float range, int pixels)
        {
            InitializePipeline();
            uint lowR = targetR > range ? (uint)(targetR - range) : 0u;
            uint highR = (uint)Math.Min(65535, targetR + range);
            uint lowG = targetG > range ? (uint)(targetG - range) : 0u;
            uint highG = (uint)Math.Min(65535, targetG + range);
            uint lowB = targetB > range ? (uint)(targetB - range) : 0u;
            uint highB = (uint)Math.Min(65535, targetB + range);
            int count = pixels;
            int bufferSize = count * sizeof(float);
            using var rBuf = MetalExecutionHelper.CreateBuffer(r);
            using var gBuf = MetalExecutionHelper.CreateBuffer(g);
            using var bBuf = MetalExecutionHelper.CreateBuffer(b);
            using var aBuf = MetalExecutionHelper.CreateBuffer(a);
            using var outABuf = MetalExecutionHelper.AllocBuffer(count);
            var(cb, encoder) = MetalExecutionHelper.CreateCommandEncoder();
            using var commandBuffer = cb;
            using var commandEncoder = encoder;
            encoder.SetComputePipelineState(_pipelineState!);
            encoder.SetBuffer(rBuf, 0, 0);
            encoder.SetBuffer(gBuf, 0, 1);
            encoder.SetBuffer(bBuf, 0, 2);
            encoder.SetBuffer(aBuf, 0, 3);
            encoder.SetBuffer(outABuf, 0, 4);
            unsafe
            {
                encoder.SetBytes((IntPtr)(&lowR), (nuint)sizeof(uint), 5);
                encoder.SetBytes((IntPtr)(&highR), (nuint)sizeof(uint), 6);
                encoder.SetBytes((IntPtr)(&lowG), (nuint)sizeof(uint), 7);
                encoder.SetBytes((IntPtr)(&highG), (nuint)sizeof(uint), 8);
                encoder.SetBytes((IntPtr)(&lowB), (nuint)sizeof(uint), 9);
                encoder.SetBytes((IntPtr)(&highB), (nuint)sizeof(uint), 10);
            }

            var(tgs, tg) = MetalExecutionHelper.ComputeDispatchSizes(count, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup);
            encoder.DispatchThreads(tg, tgs);
            encoder.EndEncoding();
            cb.Commit();
            cb.WaitUntilCompleted();
            MetalExecutionHelper.CheckCommand(cb);
            float[] result = new float[count];
            Marshal.Copy(outABuf.Contents, result, 0, count);
            return result;
        }

        private const string ShaderSource = @"
#include <metal_stdlib>
using namespace metal;

kernel void remove_color_compute(
    device const float* r [[ buffer(0) ]],
    device const float* g [[ buffer(1) ]],
    device const float* b [[ buffer(2) ]],
    device const float* a [[ buffer(3) ]],
    device float* outA [[ buffer(4) ]],
    constant uint& lowR [[ buffer(5) ]],
    constant uint& highR [[ buffer(6) ]],
    constant uint& lowG [[ buffer(7) ]],
    constant uint& highG [[ buffer(8) ]],
    constant uint& lowB [[ buffer(9) ]],
    constant uint& highB [[ buffer(10) ]],
    uint id [[ thread_position_in_grid ]])
{
    float curR = r[id];
    float curG = g[id];
    float curB = b[id];

    bool matchR = (curR >= float(lowR) && curR <= float(highR));
    bool matchG = (curG >= float(lowG) && curG <= float(highG));
    bool matchB = (curB >= float(lowB) && curB <= float(highB));

    if (matchR && matchG && matchB) {
        outA[id] = 0.0;
    } else {
        outA[id] = a[id];
    }
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
