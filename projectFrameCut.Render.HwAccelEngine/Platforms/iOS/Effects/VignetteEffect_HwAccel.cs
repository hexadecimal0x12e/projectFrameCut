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
    public partial class VignetteEffect_HwAccel
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
                throw new Exception(error?.LocalizedDescription ?? "Shader error");
            using var pipelineStateFunction = library.CreateFunction("vignette_compute");
            _pipelineState = device.CreateComputePipelineState(pipelineStateFunction, out error);
            if (_pipelineState == null)
                throw new Exception(error?.LocalizedDescription ?? "Pipeline error");
        }

        private FourChannelResult ComputeVignette(float[] r, float[] g, float[] b, float[] a, int width, int height, float strength, float radius)
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
                encoder.SetBytes((IntPtr)(&width), (nuint)sizeof(int), 4);
                encoder.SetBytes((IntPtr)(&height), (nuint)sizeof(int), 5);
                encoder.SetBytes((IntPtr)(&strength), (nuint)sizeof(float), 6);
                encoder.SetBytes((IntPtr)(&radius), (nuint)sizeof(float), 7);
            }

            encoder.SetBuffer(rOBuf, 0, 8);
            encoder.SetBuffer(gOBuf, 0, 9);
            encoder.SetBuffer(bOBuf, 0, 10);
            encoder.SetBuffer(aOBuf, 0, 11);
            var(tgs, tg) = MetalExecutionHelper.ComputeDispatchSizes(count, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup);
            encoder.DispatchThreads(tg, tgs);
            encoder.EndEncoding();
            cb.Commit();
            cb.WaitUntilCompleted();
            MetalExecutionHelper.CheckCommand(cb);
            var rO = new float[count];
            Marshal.Copy(rOBuf.Contents, rO, 0, count);
            var gO = new float[count];
            Marshal.Copy(gOBuf.Contents, gO, 0, count);
            var bO = new float[count];
            Marshal.Copy(bOBuf.Contents, bO, 0, count);
            var aO = new float[count];
            Marshal.Copy(aOBuf.Contents, aO, 0, count);
            return new FourChannelResult(rO, gO, bO, aO);
        }

        private const string ShaderSource = @"
#include <metal_stdlib>
using namespace metal;
kernel void vignette_compute(
    device const float* r [[buffer(0)]], device const float* g [[buffer(1)]],
    device const float* b [[buffer(2)]], device const float* a [[buffer(3)]],
    constant int& w [[buffer(4)]], constant int& h [[buffer(5)]],
    constant float& strength [[buffer(6)]], constant float& radius [[buffer(7)]],
    device float* rO [[buffer(8)]], device float* gO [[buffer(9)]],
    device float* bO [[buffer(10)]], device float* aO [[buffer(11)]],
    uint id [[thread_position_in_grid]])
{
    int x = int(id % uint(w)); int y = int(id / uint(w));
    float cx = float(w) * 0.5; float cy = float(h) * 0.5;
    float dx = (float(x) - cx) / cx; float dy = (float(y) - cy) / cy;
    float dist = sqrt(dx*dx + dy*dy);
    float factor = 1.0;
    if (dist > radius) {
        float t = min((dist - radius) / (1.0 - radius), 1.0);
        factor = 1.0 - t * t * strength;
    }
    rO[id] = r[id] * factor; gO[id] = g[id] * factor;
    bO[id] = b[id] * factor; aO[id] = a[id];
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
