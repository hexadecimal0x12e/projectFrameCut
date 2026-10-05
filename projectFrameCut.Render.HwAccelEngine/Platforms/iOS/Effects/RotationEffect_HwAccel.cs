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
    public partial class RotationEffect_HwAccel
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
            using var pipelineStateFunction = l.CreateFunction("rotation_compute");
            _pipelineState = d.CreateComputePipelineState(pipelineStateFunction, out e);
            if (_pipelineState == null)
                throw new Exception(e?.LocalizedDescription);
        }

        private FourChannelResult ComputeRotation(float[] r, float[] g, float[] b, float[] a, int srcW, int srcH, int dstW, int dstH, float angleDeg)
        {
            InitializePipeline();
            int srcLen = srcW * srcH;
            int dstLen = dstW * dstH;
            using var rB = MetalExecutionHelper.CreateBuffer(r);
            using var gB = MetalExecutionHelper.CreateBuffer(g);
            using var bB = MetalExecutionHelper.CreateBuffer(b);
            using var aB = MetalExecutionHelper.CreateBuffer(a);
            using var rOB = MetalExecutionHelper.AllocBuffer(dstLen);
            using var gOB = MetalExecutionHelper.AllocBuffer(dstLen);
            using var bOB = MetalExecutionHelper.AllocBuffer(dstLen);
            using var aOB = MetalExecutionHelper.AllocBuffer(dstLen);
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
                enc.SetBytes((IntPtr)(&srcW), (nuint)sizeof(int), 4);
                enc.SetBytes((IntPtr)(&srcH), (nuint)sizeof(int), 5);
                enc.SetBytes((IntPtr)(&dstW), (nuint)sizeof(int), 6);
                enc.SetBytes((IntPtr)(&dstH), (nuint)sizeof(int), 7);
                enc.SetBytes((IntPtr)(&angleDeg), (nuint)sizeof(float), 8);
            }

            enc.SetBuffer(rOB, 0, 9);
            enc.SetBuffer(gOB, 0, 10);
            enc.SetBuffer(bOB, 0, 11);
            enc.SetBuffer(aOB, 0, 12);
            var(tgs, tg) = MetalExecutionHelper.ComputeDispatchSizes(dstLen, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup);
            enc.DispatchThreads(tg, tgs);
            enc.EndEncoding();
            cb.Commit();
            cb.WaitUntilCompleted();
            MetalExecutionHelper.CheckCommand(cb);
            var rO = new float[dstLen];
            Marshal.Copy(rOB.Contents, rO, 0, dstLen);
            var gO = new float[dstLen];
            Marshal.Copy(gOB.Contents, gO, 0, dstLen);
            var bO = new float[dstLen];
            Marshal.Copy(bOB.Contents, bO, 0, dstLen);
            var aO = new float[dstLen];
            Marshal.Copy(aOB.Contents, aO, 0, dstLen);
            return new FourChannelResult(rO, gO, bO, aO);
        }

        private const string ShaderSrc = @"
#include <metal_stdlib>
using namespace metal;
kernel void rotation_compute(
    device const float* r [[buffer(0)]], device const float* g [[buffer(1)]],
    device const float* b [[buffer(2)]], device const float* a [[buffer(3)]],
    constant int& srcW [[buffer(4)]], constant int& srcH [[buffer(5)]],
    constant int& outW [[buffer(6)]], constant int& outH [[buffer(7)]],
    constant float& angleDeg [[buffer(8)]],
    device float* rO [[buffer(9)]], device float* gO [[buffer(10)]],
    device float* bO [[buffer(11)]], device float* aO [[buffer(12)]],
    uint id [[thread_position_in_grid]])
{
    int x = int(id % uint(outW)); int y = int(id / uint(outW));
    float ar = angleDeg * M_PI_F / 180.0;
    float cosA = cos(ar); float sinA = sin(ar);
    float scx = float(srcW) * 0.5; float scy = float(srcH) * 0.5;
    float ocx = float(outW) * 0.5; float ocy = float(outH) * 0.5;
    float ox = float(x) - ocx; float oy = float(y) - ocy;
    float sx = cosA * ox - sinA * oy + scx;
    float sy = sinA * ox + cosA * oy + scy;
    if (sx >= 0.0 && sx < float(srcW) && sy >= 0.0 && sy < float(srcH)) {
        int sx0 = int(sx); int sy0 = int(sy); int sx1 = min(sx0 + 1, srcW - 1); int sy1 = min(sy0 + 1, srcH - 1);
        float fx = sx - float(sx0); float fy = sy - float(sy0);
        int i00 = sy0 * srcW + sx0; int i10 = sy0 * srcW + sx1;
        int i01 = sy1 * srcW + sx0; int i11 = sy1 * srcW + sx1;
        rO[id] = (r[i00]*(1.0-fx)+r[i10]*fx)*(1.0-fy) + (r[i01]*(1.0-fx)+r[i11]*fx)*fy;
        gO[id] = (g[i00]*(1.0-fx)+g[i10]*fx)*(1.0-fy) + (g[i01]*(1.0-fx)+g[i11]*fx)*fy;
        bO[id] = (b[i00]*(1.0-fx)+b[i10]*fx)*(1.0-fy) + (b[i01]*(1.0-fx)+b[i11]*fx)*fy;
        aO[id] = (a[i00]*(1.0-fx)+a[i10]*fx)*(1.0-fy) + (a[i01]*(1.0-fx)+a[i11]*fx)*fy;
    } else { rO[id]=0.0; gO[id]=0.0; bO[id]=0.0; aO[id]=0.0; }
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
