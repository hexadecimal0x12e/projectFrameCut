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
    public partial class PlaceEffect_HwAccel
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
            using var pipelineStateFunction = l.CreateFunction("place_compute");
            _pipelineState = d.CreateComputePipelineState(pipelineStateFunction, out e);
            if (_pipelineState == null)
                throw new Exception(e?.LocalizedDescription);
        }

        private unsafe FourChannelResult ComputePlace(float[] r, float[] g, float[] b, float[] a, int srcW, int srcH, int startX, int startY, int targetW, int targetH)
        {
            InitializePipeline();
            if (targetW <= 0 || targetH <= 0)
                return new FourChannelResult([], [], [], []);
            int dstLen = targetW * targetH;
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
            enc.SetBuffer(rOB, 0, 4);
            enc.SetBuffer(gOB, 0, 5);
            enc.SetBuffer(bOB, 0, 6);
            enc.SetBuffer(aOB, 0, 7);
            enc.SetBytes((IntPtr)(&targetW), (nuint)sizeof(int), 8);
            enc.SetBytes((IntPtr)(&srcW), (nuint)sizeof(int), 9);
            enc.SetBytes((IntPtr)(&srcH), (nuint)sizeof(int), 10);
            enc.SetBytes((IntPtr)(&startX), (nuint)sizeof(int), 11);
            enc.SetBytes((IntPtr)(&startY), (nuint)sizeof(int), 12);
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
kernel void place_compute(
    device const float* rIn [[buffer(0)]], device const float* gIn [[buffer(1)]],
    device const float* bIn [[buffer(2)]], device const float* aIn [[buffer(3)]],
    device float* rOut [[buffer(4)]], device float* gOut [[buffer(5)]],
    device float* bOut [[buffer(6)]], device float* aOut [[buffer(7)]],
    constant int& targetW [[buffer(8)]], constant int& srcW [[buffer(9)]],
    constant int& srcH [[buffer(10)]],
    constant int& startX [[buffer(11)]], constant int& startY [[buffer(12)]],
    uint id [[thread_position_in_grid]])
{
    int x = int(id % uint(targetW)); int y = int(id / uint(targetW));
    int sx = x - startX; int sy = y - startY;
    if (sx >= 0 && sx < srcW && sy >= 0 && sy < srcH) {
        int si = sy * srcW + sx;
        rOut[id] = rIn[si]; gOut[id] = gIn[si]; bOut[id] = bIn[si]; aOut[id] = aIn[si];
    } else { rOut[id]=0.0; gOut[id]=0.0; bOut[id]=0.0; aOut[id]=0.0; }
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
