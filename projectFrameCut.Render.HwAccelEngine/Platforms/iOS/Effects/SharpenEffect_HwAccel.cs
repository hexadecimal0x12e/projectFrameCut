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
    public partial class SharpenEffect_HwAccel
    {
        private IMTLComputePipelineState? _pipelineState;
        private void InitializePipeline()
        {
            if (_pipelineState != null)
                return;
            var d = MetalExecutionHelper.Device;
            using var options = new MTLCompileOptions();
            using var l = d.CreateLibrary(ShaderSrc, options, out NSError e);
            if (l == null)
                throw new Exception(e?.LocalizedDescription);
            using var pipelineStateFunction = l.CreateFunction("sharpen_compute");
            _pipelineState = d.CreateComputePipelineState(pipelineStateFunction, out e);
            if (_pipelineState == null)
                throw new Exception(e?.LocalizedDescription);
        }

        private FourChannelResult ComputeSharpen(float[] r, float[] g, float[] b, float[] a, int width, float amount)
        {
            InitializePipeline();
            int count = r.Length;
            using var rB = MetalExecutionHelper.CreateBuffer(r);
            using var gB = MetalExecutionHelper.CreateBuffer(g);
            using var bB = MetalExecutionHelper.CreateBuffer(b);
            using var aB = MetalExecutionHelper.CreateBuffer(a);
            using var rOB = MetalExecutionHelper.AllocBuffer(count);
            using var gOB = MetalExecutionHelper.AllocBuffer(count);
            using var bOB = MetalExecutionHelper.AllocBuffer(count);
            using var aOB = MetalExecutionHelper.AllocBuffer(count);
            var(cb, enc) = MetalExecutionHelper.CreateCommandEncoder();
            using var commandBuffer = cb;
            using var commandEncoder = enc;
            enc.SetComputePipelineState(_pipelineState!);
            enc.SetBuffer(rB, 0, 0);
            enc.SetBuffer(gB, 0, 1);
            enc.SetBuffer(bB, 0, 2);
            enc.SetBuffer(aB, 0, 3);
            unsafe
            {
                enc.SetBytes((IntPtr)(&width), (nuint)sizeof(int), 4);
                enc.SetBytes((IntPtr)(&amount), (nuint)sizeof(float), 5);
            }

            enc.SetBuffer(rOB, 0, 6);
            enc.SetBuffer(gOB, 0, 7);
            enc.SetBuffer(bOB, 0, 8);
            enc.SetBuffer(aOB, 0, 9);
            var(tgs, tg) = MetalExecutionHelper.ComputeDispatchSizes(count, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup);
            enc.DispatchThreads(tg, tgs);
            enc.EndEncoding();
            cb.Commit();
            cb.WaitUntilCompleted();
            MetalExecutionHelper.CheckCommand(cb);
            var rO = new float[count];
            Marshal.Copy(rOB.Contents, rO, 0, count);
            var gO = new float[count];
            Marshal.Copy(gOB.Contents, gO, 0, count);
            var bO = new float[count];
            Marshal.Copy(bOB.Contents, bO, 0, count);
            var aO = new float[count];
            Marshal.Copy(aOB.Contents, aO, 0, count);
            return new FourChannelResult(rO, gO, bO, aO);
        }

        private const string ShaderSrc = @"
#include <metal_stdlib>
using namespace metal;
kernel void sharpen_compute(
    device const float* r [[buffer(0)]], device const float* g [[buffer(1)]],
    device const float* b [[buffer(2)]], device const float* a [[buffer(3)]],
    constant int& w [[buffer(4)]], constant float& amount [[buffer(5)]],
    device float* rO [[buffer(6)]], device float* gO [[buffer(7)]],
    device float* bO [[buffer(8)]], device float* aO [[buffer(9)]],
    uint id [[thread_position_in_grid]])
{
    int len = int(uint(w) * uint(w)); // approximate
    int x = int(id % uint(w));
    float origR = r[id], origG = g[id], origB = b[id];
    int left = x > 0 ? int(id) - 1 : int(id);
    int right = x < w - 1 ? int(id) + 1 : int(id);
    int top = id - w; if (top < 0) top = int(id);
    int bottom = id + w; if (bottom >= (int(id/w)+1)*w) bottom = int(id);
    float avgR = (r[left] + r[right] + r[top] + r[bottom]) * 0.25;
    float avgG = (g[left] + g[right] + g[top] + g[bottom]) * 0.25;
    float avgB = (b[left] + b[right] + b[top] + b[bottom]) * 0.25;
    rO[id] = origR + amount * (origR - avgR);
    gO[id] = origG + amount * (origG - avgG);
    bO[id] = origB + amount * (origB - avgB);
    aO[id] = a[id];
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
