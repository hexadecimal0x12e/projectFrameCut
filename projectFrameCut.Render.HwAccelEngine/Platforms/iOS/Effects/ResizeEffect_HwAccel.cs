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
    public partial class ResizeEffect_HwAccel
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
            using var pipelineStateFunction = l.CreateFunction("resize_compute");
            _pipelineState = d.CreateComputePipelineState(pipelineStateFunction, out e);
            if (_pipelineState == null)
                throw new Exception(e?.LocalizedDescription);
        }

        private FourChannelResult ComputeResizeFloat(float[] r, float[] g, float[] b, float[] a, float srcW, float srcH, float dstW, float dstH) => DoResize(r, g, b, a, (int)srcW, (int)srcH, (int)dstW, (int)dstH);
        private FourChannelResult8 ComputeResizeByte(float[] r, float[] g, float[] b, float[] a, float srcW, float srcH, float dstW, float dstH)
        {
            var fr = DoResize(r, g, b, a, (int)srcW, (int)srcH, (int)dstW, (int)dstH);
            int len = fr.R.Length;
            var r8 = new byte[len];
            var g8 = new byte[len];
            var b8 = new byte[len];
            for (int i = 0; i < len; i++)
            {
                r8[i] = (byte)Math.Clamp(fr.R[i] + 0.5f, 0f, 255f);
                g8[i] = (byte)Math.Clamp(fr.G[i] + 0.5f, 0f, 255f);
                b8[i] = (byte)Math.Clamp(fr.B[i] + 0.5f, 0f, 255f);
            }

            return new FourChannelResult8(r8, g8, b8, fr.A);
        }

        private FourChannelResult16 ComputeResizeUshort(float[] r, float[] g, float[] b, float[] a, float srcW, float srcH, float dstW, float dstH)
        {
            var fr = DoResize(r, g, b, a, (int)srcW, (int)srcH, (int)dstW, (int)dstH);
            int len = fr.R.Length;
            var r16 = new ushort[len];
            var g16 = new ushort[len];
            var b16 = new ushort[len];
            for (int i = 0; i < len; i++)
            {
                r16[i] = (ushort)Math.Clamp(fr.R[i] + 0.5f, 0f, 65535f);
                g16[i] = (ushort)Math.Clamp(fr.G[i] + 0.5f, 0f, 65535f);
                b16[i] = (ushort)Math.Clamp(fr.B[i] + 0.5f, 0f, 65535f);
            }

            return new FourChannelResult16(r16, g16, b16, fr.A);
        }

        private unsafe FourChannelResult DoResize(float[] r, float[] g, float[] b, float[] a, int srcW, int srcH, int dstW, int dstH)
        {
            InitializePipeline();
            if (dstW <= 0 || dstH <= 0)
                return new FourChannelResult([], [], [], []);
            int dstLen = dstW * dstH;
            float ratioX = (float)srcW / dstW;
            float ratioY = (float)srcH / dstH;
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
            enc.SetBytes((IntPtr)(&dstW), (nuint)sizeof(int), 8);
            enc.SetBytes((IntPtr)(&srcW), (nuint)sizeof(int), 9);
            enc.SetBytes((IntPtr)(&srcH), (nuint)sizeof(int), 10);
            enc.SetBytes((IntPtr)(&ratioX), (nuint)sizeof(float), 11);
            enc.SetBytes((IntPtr)(&ratioY), (nuint)sizeof(float), 12);
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
kernel void resize_compute(
    device const float* rIn [[buffer(0)]], device const float* gIn [[buffer(1)]],
    device const float* bIn [[buffer(2)]], device const float* aIn [[buffer(3)]],
    device float* rOut [[buffer(4)]], device float* gOut [[buffer(5)]],
    device float* bOut [[buffer(6)]], device float* aOut [[buffer(7)]],
    constant int& dstW [[buffer(8)]], constant int& srcW [[buffer(9)]],
    constant int& srcH [[buffer(10)]],
    constant float& ratioX [[buffer(11)]], constant float& ratioY [[buffer(12)]],
    uint id [[thread_position_in_grid]])
{
    int x = int(id % uint(dstW)); int y = int(id / uint(dstW));
    float sx = clamp((float(x) + 0.5) * ratioX - 0.5, 0.0, float(srcW - 1));
    float sy = clamp((float(y) + 0.5) * ratioY - 0.5, 0.0, float(srcH - 1));
    int x0 = int(sx), y0 = int(sy), x1 = min(x0 + 1, srcW - 1), y1 = min(y0 + 1, srcH - 1);
    float fx = sx - float(x0), fy = sy - float(y0);
    int i00 = y0 * srcW + x0, i10 = y0 * srcW + x1, i01 = y1 * srcW + x0, i11 = y1 * srcW + x1;
    rOut[id] = rIn[i00]*(1-fx)*(1-fy) + rIn[i10]*fx*(1-fy) + rIn[i01]*(1-fx)*fy + rIn[i11]*fx*fy;
    gOut[id] = gIn[i00]*(1-fx)*(1-fy) + gIn[i10]*fx*(1-fy) + gIn[i01]*(1-fx)*fy + gIn[i11]*fx*fy;
    bOut[id] = bIn[i00]*(1-fx)*(1-fy) + bIn[i10]*fx*(1-fy) + bIn[i01]*(1-fx)*fy + bIn[i11]*fx*fy;
    aOut[id] = aIn[i00]*(1-fx)*(1-fy) + aIn[i10]*fx*(1-fy) + aIn[i01]*(1-fx)*fy + aIn[i11]*fx*fy;
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
