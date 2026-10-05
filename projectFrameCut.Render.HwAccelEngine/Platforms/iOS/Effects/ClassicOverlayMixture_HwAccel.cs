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
    public partial class ClassicOverlayMixture_HwAccel
    {
        private IMTLComputePipelineState? _alphaPipelineState;
        private IMTLComputePipelineState? _colorPipelineState;
        private void InitializePipeline()
        {
            if (_alphaPipelineState != null && _colorPipelineState != null)
                return;
            var device = MetalExecutionHelper.Device;
            using var options = new MTLCompileOptions();
            using var library = device.CreateLibrary(ShaderSource, options, out NSError error);
            if (library == null)
                throw new Exception($"Failed to compile shader: {error?.LocalizedDescription}");
            using var alphaFunction = library.CreateFunction("overlay_alpha_compute");
            _alphaPipelineState = device.CreateComputePipelineState(alphaFunction, out error);
            if (_alphaPipelineState == null)
                throw new Exception($"Failed to create alpha pipeline state: {error?.LocalizedDescription}");
            using var colorFunction = library.CreateFunction("overlay_color_compute");
            _colorPipelineState = device.CreateComputePipelineState(colorFunction, out error);
            if (_colorPipelineState == null)
                throw new Exception($"Failed to create color pipeline state: {error?.LocalizedDescription}");
        }

        protected override (byte[] Color, float[] Alpha) Overlay8(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            using var scope = nativeLock.EnterScope();
            ObjectDisposedException.ThrowIf(disposed, this);
            InitializePipeline();
            var(alpha, raw) = ComputeAlphaAndColor(_alphaPipelineState!, _colorPipelineState!, top, bottom, topAlpha, bottomAlpha, pixelCount);
            var color = new byte[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                float v = raw[i] / 257.0f;
                if (v < 0)
                    v = 0;
                if (v > 255)
                    v = 255;
                color[i] = (byte)v;
            }

            return (color, alpha);
        }

        protected override (ushort[] Color, float[] Alpha) Overlay16(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            using var scope = nativeLock.EnterScope();
            ObjectDisposedException.ThrowIf(disposed, this);
            InitializePipeline();
            var(alpha, raw) = ComputeAlphaAndColor(_alphaPipelineState!, _colorPipelineState!, top, bottom, topAlpha, bottomAlpha, pixelCount);
            var color = new ushort[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                float v = raw[i];
                if (v < 0)
                    v = 0;
                if (v > 65535)
                    v = 65535;
                color[i] = (ushort)v;
            }

            return (color, alpha);
        }

        protected override (float[] Color, float[] Alpha) OverlayHdr(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            using var scope = nativeLock.EnterScope();
            ObjectDisposedException.ThrowIf(disposed, this);
            InitializePipeline();
            var(alpha, raw) = ComputeAlphaAndColor(_alphaPipelineState!, _colorPipelineState!, top, bottom, topAlpha, bottomAlpha, pixelCount);
            return (raw, alpha);
        }

        private const string ShaderSource = @"
#include <metal_stdlib>
using namespace metal;

kernel void overlay_alpha_compute(
    device const float* aAlpha [[ buffer(0) ]],
    device const float* bAlpha [[ buffer(1) ]],
    device float* cAlpha [[ buffer(2) ]],
    uint id [[ thread_position_in_grid ]])
{
    float aA = aAlpha[id];
    float bA = bAlpha[id];

    if (aA == 1.0) {
        cAlpha[id] = 1.0;
    } else if (aA <= 0.05) {
        cAlpha[id] = bA;
    } else {
        float outA = aA + bA * (1.0 - aA);
        if (outA < 1e-6) {
            cAlpha[id] = 0.0;
        } else {
            cAlpha[id] = outA;
        }
    }
}

kernel void overlay_color_compute(
    device const float* aAlpha [[ buffer(0) ]],
    device const float* a [[ buffer(1) ]],
    device const float* bAlpha [[ buffer(2) ]],
    device const float* b [[ buffer(3) ]],
    device float* c [[ buffer(4) ]],
    uint id [[ thread_position_in_grid ]])
{
    float aA = aAlpha[id];
    float bA = bAlpha[id];
    float aVal = a[id];
    float bVal = b[id];

    if (aA == 1.0)
    {
        c[id] = aVal;
    }
    else if (aA <= 0.05)
    {
        c[id] = bVal;
    }
    else
    {
        float outA = aA + bA * (1.0 - aA);
        if (outA < 1e-6)
        {
            c[id] = 0.0;
        }
        else
        {
            float aC = aVal * aA / outA;
            float bC = bVal * bA * (1.0 - aA) / outA;
            float outC = aC + bC;
            outC = clamp(outC, 0.0, 65535.0);
            c[id] = outC;
        }
    }
}
";
        private IMTLComputePipelineState? Approximate_alphaPipelineState;
        private IMTLComputePipelineState? Approximate_colorPipelineState;
        private void ApproximateInitializePipeline()
        {
            if (Approximate_alphaPipelineState != null && Approximate_colorPipelineState != null)
                return;
            var device = MetalExecutionHelper.Device;
            using var options = new MTLCompileOptions();
            using var library = device.CreateLibrary(ApproximateShaderSource, options, out NSError error);
            if (library == null)
                throw new Exception($"Failed to compile shader: {error?.LocalizedDescription}");
            using var alphaFunction = library.CreateFunction("overlay_alpha_compute");
            Approximate_alphaPipelineState = device.CreateComputePipelineState(alphaFunction, out error);
            if (Approximate_alphaPipelineState == null)
                throw new Exception($"Failed to create alpha pipeline state: {error?.LocalizedDescription}");
            using var colorFunction = library.CreateFunction("overlay_color_compute");
            Approximate_colorPipelineState = device.CreateComputePipelineState(colorFunction, out error);
            if (Approximate_colorPipelineState == null)
                throw new Exception($"Failed to create color pipeline state: {error?.LocalizedDescription}");
        }

        protected override (byte[] Color, float[] Alpha) ApproximateOverlay8(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            using var scope = nativeLock.EnterScope();
            ObjectDisposedException.ThrowIf(disposed, this);
            ApproximateInitializePipeline();
            var(alpha, raw) = ComputeAlphaAndColor(Approximate_alphaPipelineState!, Approximate_colorPipelineState!, top, bottom, topAlpha, bottomAlpha, pixelCount);
            var color = new byte[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                float v = raw[i] / 257.0f;
                if (v < 0)
                    v = 0;
                if (v > 255)
                    v = 255;
                color[i] = (byte)v;
            }

            return (color, alpha);
        }

        protected override (ushort[] Color, float[] Alpha) ApproximateOverlay16(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            using var scope = nativeLock.EnterScope();
            ObjectDisposedException.ThrowIf(disposed, this);
            ApproximateInitializePipeline();
            var(alpha, raw) = ComputeAlphaAndColor(Approximate_alphaPipelineState!, Approximate_colorPipelineState!, top, bottom, topAlpha, bottomAlpha, pixelCount);
            var color = new ushort[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                float v = raw[i];
                if (v < 0)
                    v = 0;
                if (v > 65535)
                    v = 65535;
                color[i] = (ushort)v;
            }

            return (color, alpha);
        }

        protected override (float[] Color, float[] Alpha) ApproximateOverlayHdr(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            using var scope = nativeLock.EnterScope();
            ObjectDisposedException.ThrowIf(disposed, this);
            ApproximateInitializePipeline();
            var(alpha, raw) = ComputeAlphaAndColor(Approximate_alphaPipelineState!, Approximate_colorPipelineState!, top, bottom, topAlpha, bottomAlpha, pixelCount);
            return (raw, alpha);
        }

        private const string ApproximateShaderSource = @"
#include <metal_stdlib>
using namespace metal;

kernel void overlay_alpha_compute(
    device const float* aAlpha [[ buffer(0) ]],
    device const float* bAlpha [[ buffer(1) ]],
    device float* cAlpha [[ buffer(2) ]],
    uint id [[ thread_position_in_grid ]])
{
    float aA = aAlpha[id];
    float bA = bAlpha[id];

    if (aA == 1.0) {
        cAlpha[id] = 1.0;
    } else if (aA <= 0.05) {
        cAlpha[id] = bA;
    } else {
        float outA = aA + bA * (1.0 - aA);
        if (outA < 1e-6)
        {
            cAlpha[id] = 0.0;
        }
        else
        {
            cAlpha[id] = outA;
        }
    }
}

kernel void overlay_color_compute(
    device const float* aAlpha [[ buffer(0) ]],
    device const float* aVal [[ buffer(1) ]],
    device const float* bAlpha [[ buffer(2) ]],
    device const float* bVal [[ buffer(3) ]],
    device float* c [[ buffer(4) ]],
    uint id [[ thread_position_in_grid ]])
{
    float aA = aAlpha[id];
    float bA = bAlpha[id];
    float outA = aA + bA * (1.0 - aA);

    if (outA < 1e-6)
    {
        c[id] = 0.0;
    }
    else
    {
        float aC = aVal[id] * aA / outA;
        float bC = bVal[id] * bA * (1.0 - aA) / outA;
        float outC = aC + bC;
        outC = clamp(outC, 0.0, 65535.0);
        c[id] = outC;
    }
}
";
        private static (float[] alpha, float[] rawColor) ComputeAlphaAndColor(IMTLComputePipelineState alphaPs, IMTLComputePipelineState colorPs, float[] a, float[] b, float[] aAlpha, float[] bAlpha, int count)
        {
            using var aBuf = MetalExecutionHelper.CreateBuffer(a);
            using var bBuf = MetalExecutionHelper.CreateBuffer(b);
            using var aaBuf = MetalExecutionHelper.CreateBuffer(aAlpha);
            using var baBuf = MetalExecutionHelper.CreateBuffer(bAlpha);
            using var caBuf = MetalExecutionHelper.AllocBuffer(count);
            using var cBuf = MetalExecutionHelper.AllocBuffer(count);
            var(cb, encoder) = MetalExecutionHelper.CreateCommandEncoder();
            using var commandBuffer = cb;
            using var commandEncoder = encoder;
            // 1. Compute Alpha
            encoder.SetComputePipelineState(alphaPs);
            encoder.SetBuffer(aaBuf, 0, 0);
            encoder.SetBuffer(baBuf, 0, 1);
            encoder.SetBuffer(caBuf, 0, 2);
            var(tgs, tg) = MetalExecutionHelper.ComputeDispatchSizes(count, (int)alphaPs.MaxTotalThreadsPerThreadgroup);
            encoder.DispatchThreads(tg, tgs);
            // 2. Compute Color
            encoder.SetComputePipelineState(colorPs);
            encoder.SetBuffer(aaBuf, 0, 0);
            encoder.SetBuffer(aBuf, 0, 1);
            encoder.SetBuffer(baBuf, 0, 2);
            encoder.SetBuffer(bBuf, 0, 3);
            encoder.SetBuffer(cBuf, 0, 4);
            encoder.DispatchThreads(tg, tgs);
            encoder.EndEncoding();
            cb.Commit();
            cb.WaitUntilCompleted();
            MetalExecutionHelper.CheckCommand(cb);
            float[] resultAlpha = new float[count];
            Marshal.Copy(caBuf.Contents, resultAlpha, 0, count);
            float[] rawColor = new float[count];
            Marshal.Copy(cBuf.Contents, rawColor, 0, count);
            return (resultAlpha, rawColor);
        }

        partial void ReleaseNativeResources()
        {
            _alphaPipelineState?.Dispose();
            _alphaPipelineState = null;
            _colorPipelineState?.Dispose();
            _colorPipelineState = null;
            Approximate_alphaPipelineState?.Dispose();
            Approximate_alphaPipelineState = null;
            Approximate_colorPipelineState?.Dispose();
            Approximate_colorPipelineState = null;
        }
    }
}
#endif
