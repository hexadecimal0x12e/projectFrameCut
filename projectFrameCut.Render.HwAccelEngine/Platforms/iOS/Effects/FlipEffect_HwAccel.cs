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
    public partial class FlipEffect_HwAccel
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
            using var pipelineStateFunction = l.CreateFunction("flip_compute");
            _pipelineState = d.CreateComputePipelineState(pipelineStateFunction, out e);
            if (_pipelineState == null)
                throw new Exception(e?.LocalizedDescription);
        }

        private FourChannelResult ComputeFlip(float[] r, float[] g, float[] b, float[] a, int width, int height, bool horizontal, bool vertical)
        {
            InitializePipeline();
            int count = r.Length;
            int bufSize = count * sizeof(float);
            int horiz = horizontal ? 1 : 0;
            int vert = vertical ? 1 : 0;
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
                enc.SetBytes((IntPtr)(&height), (nuint)sizeof(int), 5);
                enc.SetBytes((IntPtr)(&horiz), (nuint)sizeof(int), 6);
                enc.SetBytes((IntPtr)(&vert), (nuint)sizeof(int), 7);
            }

            enc.SetBuffer(rOB, 0, 8);
            enc.SetBuffer(gOB, 0, 9);
            enc.SetBuffer(bOB, 0, 10);
            enc.SetBuffer(aOB, 0, 11);
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
kernel void flip_compute(
    device const float* r [[buffer(0)]], device const float* g [[buffer(1)]],
    device const float* b [[buffer(2)]], device const float* a [[buffer(3)]],
    constant int& w [[buffer(4)]], constant int& h [[buffer(5)]],
    constant int& horiz [[buffer(6)]], constant int& vert [[buffer(7)]],
    device float* rO [[buffer(8)]], device float* gO [[buffer(9)]],
    device float* bO [[buffer(10)]], device float* aO [[buffer(11)]],
    uint id [[thread_position_in_grid]])
{
    int x = int(id % uint(w)); int y = int(id / uint(w));
    int sx = (horiz != 0) ? w - 1 - x : x;
    int sy = (vert != 0) ? h - 1 - y : y;
    int si = sy * w + sx;
    rO[id] = r[si]; gO[id] = g[si]; bO[id] = b[si]; aO[id] = a[si];
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
