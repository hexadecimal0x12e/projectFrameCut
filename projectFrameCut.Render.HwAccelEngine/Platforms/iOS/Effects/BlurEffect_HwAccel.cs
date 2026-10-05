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
    public partial class BlurEffect_HwAccel
    {
        private IMTLComputePipelineState? _pipelineStateH;
        private IMTLComputePipelineState? _pipelineStateV;
        private void InitializePipelines()
        {
            if (_pipelineStateH != null && _pipelineStateV != null)
                return;
            var d = MetalExecutionHelper.Device;
            using var options = new MTLCompileOptions();
            using var l = d.CreateLibrary(ShaderSrc, options, out NSError e);
            if (l == null)
                throw new Exception(e?.LocalizedDescription);
            using var pipelineStateHFunction = l.CreateFunction("blur_horizontal");
            _pipelineStateH = d.CreateComputePipelineState(pipelineStateHFunction, out e);
            if (_pipelineStateH == null)
                throw new Exception(e?.LocalizedDescription);
            using var pipelineStateVFunction = l.CreateFunction("blur_vertical");
            _pipelineStateV = d.CreateComputePipelineState(pipelineStateVFunction, out e);
            if (_pipelineStateV == null)
                throw new Exception(e?.LocalizedDescription);
        }

        private FourChannelResult ComputeBlur(float[] r, float[] g, float[] b, float[] a, int width, float sigma)
        {
            InitializePipelines();
            int count = r.Length;
            int h = count / width;
            int radius = Math.Max(1, (int)MathF.Ceiling(sigma));
            using var rB = MetalExecutionHelper.CreateBuffer(r);
            using var gB = MetalExecutionHelper.CreateBuffer(g);
            using var bB = MetalExecutionHelper.CreateBuffer(b);
            using var aB = MetalExecutionHelper.CreateBuffer(a);
            using var rT = MetalExecutionHelper.AllocBuffer(count);
            using var gT = MetalExecutionHelper.AllocBuffer(count);
            using var bT = MetalExecutionHelper.AllocBuffer(count);
            using var aT = MetalExecutionHelper.AllocBuffer(count);
            var(cb, enc) = MetalExecutionHelper.CreateCommandEncoder();
            using var commandBuffer = cb;
            using var commandEncoder = enc;
            // Horizontal pass
            enc.SetComputePipelineState(_pipelineStateH!);
            enc.SetBuffer(rB, 0, 0);
            enc.SetBuffer(gB, 0, 1);
            enc.SetBuffer(bB, 0, 2);
            enc.SetBuffer(aB, 0, 3);
            unsafe
            {
                enc.SetBytes((IntPtr)(&width), (nuint)sizeof(int), 4);
            }

            unsafe
            {
                enc.SetBytes((IntPtr)(&radius), (nuint)sizeof(int), 5);
            }

            enc.SetBuffer(rT, 0, 6);
            enc.SetBuffer(gT, 0, 7);
            enc.SetBuffer(bT, 0, 8);
            enc.SetBuffer(aT, 0, 9);
            var(tgs, tg) = MetalExecutionHelper.ComputeDispatchSizes(count, (int)_pipelineStateH!.MaxTotalThreadsPerThreadgroup);
            enc.DispatchThreads(tg, tgs);
            // Vertical pass
            enc.SetComputePipelineState(_pipelineStateV!);
            enc.SetBuffer(rT, 0, 0);
            enc.SetBuffer(gT, 0, 1);
            enc.SetBuffer(bT, 0, 2);
            enc.SetBuffer(aT, 0, 3);
            unsafe
            {
                enc.SetBytes((IntPtr)(&width), (nuint)sizeof(int), 4);
            }

            unsafe
            {
                enc.SetBytes((IntPtr)(&h), (nuint)sizeof(int), 5);
            }

            unsafe
            {
                enc.SetBytes((IntPtr)(&radius), (nuint)sizeof(int), 6);
            }

            using var rOB = MetalExecutionHelper.AllocBuffer(count);
            using var gOB = MetalExecutionHelper.AllocBuffer(count);
            using var bOB = MetalExecutionHelper.AllocBuffer(count);
            using var aOB = MetalExecutionHelper.AllocBuffer(count);
            enc.SetBuffer(rOB, 0, 7);
            enc.SetBuffer(gOB, 0, 8);
            enc.SetBuffer(bOB, 0, 9);
            enc.SetBuffer(aOB, 0, 10);
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
kernel void blur_horizontal(
    device const float* r [[buffer(0)]], device const float* g [[buffer(1)]],
    device const float* b [[buffer(2)]], device const float* a [[buffer(3)]],
    constant int& w [[buffer(4)]], constant int& radius [[buffer(5)]],
    device float* rO [[buffer(6)]], device float* gO [[buffer(7)]],
    device float* bO [[buffer(8)]], device float* aO [[buffer(9)]],
    uint id [[thread_position_in_grid]])
{
    int x = int(id % uint(w)); int rs = int(id) - x;
    float sr=0, sg=0, sb=0, sa=0; int cnt=0;
    for (int k = x - radius; k <= x + radius; k++) {
        if (k < 0 || k >= w) continue;
        int col = k; int idx = rs + col;
        sr += r[idx]; sg += g[idx]; sb += b[idx]; sa += a[idx]; cnt++;
    }
    rO[id] = sr/cnt; gO[id] = sg/cnt; bO[id] = sb/cnt; aO[id] = sa/cnt;
}
kernel void blur_vertical(
    device const float* r [[buffer(0)]], device const float* g [[buffer(1)]],
    device const float* b [[buffer(2)]], device const float* a [[buffer(3)]],
    constant int& w [[buffer(4)]], constant int& h [[buffer(5)]], constant int& radius [[buffer(6)]],
    device float* rO [[buffer(7)]], device float* gO [[buffer(8)]],
    device float* bO [[buffer(9)]], device float* aO [[buffer(10)]],
    uint id [[thread_position_in_grid]])
{
    int y = int(id / uint(w)); int col = int(id % uint(w));
    float sr=0, sg=0, sb=0, sa=0; int cnt=0;
    for (int k = y - radius; k <= y + radius; k++) {
        if (k < 0 || k >= h) continue;
        int row = k;
        sr += r[row*w+col]; sg += g[row*w+col]; sb += b[row*w+col]; sa += a[row*w+col]; cnt++;
    }
    rO[id] = sr/cnt; gO[id] = sg/cnt; bO[id] = sb/cnt; aO[id] = sa/cnt;
}
";
        partial void ReleaseNativeResources()
        {
            _pipelineStateH?.Dispose();
            _pipelineStateH = null;
            _pipelineStateV?.Dispose();
            _pipelineStateV = null;
        }
    }
}
#endif
