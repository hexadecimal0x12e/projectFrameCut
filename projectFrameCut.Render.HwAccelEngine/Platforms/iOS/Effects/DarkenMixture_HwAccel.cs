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
    public partial class DarkenMixture_HwAccel
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
            using var pipelineStateFunction = l.CreateFunction("blend_darken");
            _pipelineState = d.CreateComputePipelineState(pipelineStateFunction, out e);
            if (_pipelineState == null)
                throw new Exception(e?.LocalizedDescription);
        }

        protected override (ushort[] Color, float[] Alpha) ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            using var scope = nativeLock.EnterScope();
            ObjectDisposedException.ThrowIf(disposed, this);
            InitializePipeline();
            return RunBlend(_pipelineState!, top, bottom, topAlpha, bottomAlpha, pixelCount);
        }

        private const string ShaderSrc = @"
#include <metal_stdlib>
using namespace metal;
kernel void blend_darken(
    device const float* top [[buffer(0)]], device const float* bottom [[buffer(1)]],
    device const float* topAlpha [[buffer(2)]], device const float* bottomAlpha [[buffer(3)]],
    device float* outC [[buffer(4)]], device float* outA [[buffer(5)]],
    uint id [[thread_position_in_grid]])
{
    float aA = topAlpha[id]; float bA = bottomAlpha[id];
    float outAlpha = aA + bA * (1.0 - aA);
    if (outAlpha < 1e-6) { outC[id] = 0.0; outA[id] = 0.0; }
    else {
        float blended = min(top[id], bottom[id]);
        float result = (blended * aA + bottom[id] * bA * (1.0 - aA)) / outAlpha;
        outC[id] = clamp(result, 0.0, 65535.0); outA[id] = outAlpha;
    }
}
";
        private static (ushort[] Color, float[] Alpha) RunBlend(IMTLComputePipelineState pipeline, float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            using var topBuf = MetalExecutionHelper.CreateBuffer(top);
            using var bottomBuf = MetalExecutionHelper.CreateBuffer(bottom);
            using var tAlphaBuf = MetalExecutionHelper.CreateBuffer(topAlpha);
            using var bAlphaBuf = MetalExecutionHelper.CreateBuffer(bottomAlpha);
            using var outCBuf = MetalExecutionHelper.AllocBuffer(pixelCount);
            using var outABuf = MetalExecutionHelper.AllocBuffer(pixelCount);
            var(cb, enc) = MetalExecutionHelper.CreateCommandEncoder();
            using var commandBuffer = cb;
            using var commandEncoder = enc;
            enc.SetComputePipelineState(pipeline);
            enc.SetBuffer(topBuf, 0, 0);
            enc.SetBuffer(bottomBuf, 0, 1);
            enc.SetBuffer(tAlphaBuf, 0, 2);
            enc.SetBuffer(bAlphaBuf, 0, 3);
            enc.SetBuffer(outCBuf, 0, 4);
            enc.SetBuffer(outABuf, 0, 5);
            var(tgs, tg) = MetalExecutionHelper.ComputeDispatchSizes(pixelCount, (int)pipeline.MaxTotalThreadsPerThreadgroup);
            enc.DispatchThreads(tg, tgs);
            enc.EndEncoding();
            cb.Commit();
            cb.WaitUntilCompleted();
            MetalExecutionHelper.CheckCommand(cb);
            // GPU outputs float; Marshal.Copy lacks ushort[] overload — read as float then convert
            var floatOut = new float[pixelCount];
            Marshal.Copy(outCBuf.Contents, floatOut, 0, pixelCount);
            var ushortOut = new ushort[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                float v = floatOut[i];
                if (v < 0f)
                    v = 0f;
                if (v > 65535f)
                    v = 65535f;
                ushortOut[i] = (ushort)v;
            }

            var alphaOut = new float[pixelCount];
            Marshal.Copy(outABuf.Contents, alphaOut, 0, pixelCount);
            return (ushortOut, alphaOut);
        }

        partial void ReleaseNativeResources()
        {
            _pipelineState?.Dispose();
            _pipelineState = null;
        }
    }
}
#endif
