using Metal;
using Foundation;
using projectFrameCut.Render.HwAccelContracts;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.HwAccelEngine.Platforms.iOS
{
    public class MetalComputerHelper
    {
        private static IMTLDevice? _device;
        public static IMTLDevice Device => _device ??= MTLDevice.SystemDefault ?? throw new NotSupportedException("Metal is not supported on this device.");

        private static IMTLCommandQueue? _commandQueue;
        public static IMTLCommandQueue CommandQueue => _commandQueue ??= Device.CreateCommandQueue() ?? throw new InvalidOperationException("Could not create command queue.");

        public static (IMTLCommandBuffer commandBuffer, IMTLComputeCommandEncoder encoder) CreateCommandEncoder()
        {
            var cb = CommandQueue.CommandBuffer() ?? throw new Exception("Failed to create command buffer");
            var enc = cb.ComputeCommandEncoder ?? throw new Exception("Failed to create compute encoder");
            return (cb, enc);
        }

        public static unsafe IMTLBuffer CreateBuffer(float[] data)
        {
            fixed (float* ptr = data)
                return Device.CreateBuffer((IntPtr)ptr, (nuint)(data.Length * sizeof(float)), MTLResourceOptions.StorageModeShared)
                       ?? throw new Exception("Failed to create buffer");
        }

        public static IMTLBuffer AllocBuffer(int floatCount)
            => Device.CreateBuffer((nuint)(floatCount * sizeof(float)), MTLResourceOptions.StorageModeShared)
               ?? throw new Exception("Failed to allocate buffer");

        public static void RegisterComputerBridge()
        {
            //todo: plugin
        }

        public static (MTLSize threadGroupSize, MTLSize threadGroups) ComputeDispatchSizes(int count, int maxThreadsPerGroup)
        {
            var tgs = new MTLSize(Math.Min(count, maxThreadsPerGroup), 1, 1);
            var tg = new MTLSize((count + tgs.Width - 1) / tgs.Width, 1, 1);
            return (tgs, tg);
        }
    }

    #region Overlay / ApproximateOverlay 共用 GPU 核心

    /// <summary>Overlay 与 ApproximateOverlay 共用的 GPU 调度核心（alpha + raw color）。</summary>
    internal static class OverlayGpuCore
    {
        public static (float[] alpha, float[] rawColor) ComputeAlphaAndColor(
            IMTLComputePipelineState alphaPs, IMTLComputePipelineState colorPs,
            float[] a, float[] b, float[] aAlpha, float[] bAlpha, int count)
        {
            var aBuf = MetalComputerHelper.CreateBuffer(a);
            var bBuf = MetalComputerHelper.CreateBuffer(b);
            var aaBuf = MetalComputerHelper.CreateBuffer(aAlpha);
            var baBuf = MetalComputerHelper.CreateBuffer(bAlpha);
            var caBuf = MetalComputerHelper.AllocBuffer(count);
            var cBuf = MetalComputerHelper.AllocBuffer(count);

            var (cb, encoder) = MetalComputerHelper.CreateCommandEncoder();

            // 1. Compute Alpha
            encoder.SetComputePipelineState(alphaPs);
            encoder.SetBuffer(aaBuf, 0, 0);
            encoder.SetBuffer(baBuf, 0, 1);
            encoder.SetBuffer(caBuf, 0, 2);

            var (tgs, tg) = MetalComputerHelper.ComputeDispatchSizes(count, (int)alphaPs.MaxTotalThreadsPerThreadgroup);
            encoder.DispatchThreadgroups(tg, tgs);

            // 2. Compute Color
            encoder.SetComputePipelineState(colorPs);
            encoder.SetBuffer(aaBuf, 0, 0);
            encoder.SetBuffer(aBuf, 0, 1);
            encoder.SetBuffer(baBuf, 0, 2);
            encoder.SetBuffer(bBuf, 0, 3);
            encoder.SetBuffer(cBuf, 0, 4);

            encoder.DispatchThreadgroups(tg, tgs);
            encoder.EndEncoding();
            cb.Commit();
            cb.WaitUntilCompleted();

            float[] resultAlpha = new float[count];
            Marshal.Copy(caBuf.Contents, resultAlpha, 0, count);

            float[] rawColor = new float[count];
            Marshal.Copy(cBuf.Contents, rawColor, 0, count);

            return (resultAlpha, rawColor);
        }
    }

    #endregion

    #region OverlayComputer

    public class OverlayComputer : IOverlayComputer, IComputer
    {
        public string FromPlugin => "projectFrameCut.iOS.MetalAccelerater.MetalComputers";
        public string SupportedEffectOrMixture => "Overlay";

        private IMTLComputePipelineState? _alphaPipelineState;
        private IMTLComputePipelineState? _colorPipelineState;

        private void InitializePipeline()
        {
            if (_alphaPipelineState != null && _colorPipelineState != null) return;

            var device = MetalComputerHelper.Device;
            var library = device.CreateLibrary(ShaderSource, new MTLCompileOptions(), out NSError error);
            if (library == null) throw new Exception($"Failed to compile shader: {error?.LocalizedDescription}");

            var alphaFunction = library.CreateFunction("overlay_alpha_compute");
            _alphaPipelineState = device.CreateComputePipelineState(alphaFunction, out error);
            if (_alphaPipelineState == null) throw new Exception($"Failed to create alpha pipeline state: {error?.LocalizedDescription}");

            var colorFunction = library.CreateFunction("overlay_color_compute");
            _colorPipelineState = device.CreateComputePipelineState(colorFunction, out error);
            if (_colorPipelineState == null) throw new Exception($"Failed to create color pipeline state: {error?.LocalizedDescription}");
        }

        // ========== 强类型接口 IOverlayComputer ==========

        public BlendResult8 Overlay8(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            InitializePipeline();
            var (alpha, raw) = OverlayGpuCore.ComputeAlphaAndColor(
                _alphaPipelineState!, _colorPipelineState!,
                top, bottom, topAlpha, bottomAlpha, pixelCount);

            var color = new byte[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                float v = raw[i] / 257.0f;
                if (v < 0) v = 0;
                if (v > 255) v = 255;
                color[i] = (byte)v;
            }
            return new BlendResult8(color, alpha);
        }

        public BlendResult16 Overlay16(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            InitializePipeline();
            var (alpha, raw) = OverlayGpuCore.ComputeAlphaAndColor(
                _alphaPipelineState!, _colorPipelineState!,
                top, bottom, topAlpha, bottomAlpha, pixelCount);

            var color = new ushort[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                float v = raw[i];
                if (v < 0) v = 0;
                if (v > 65535) v = 65535;
                color[i] = (ushort)v;
            }
            return new BlendResult16(color, alpha);
        }

        public BlendResultHdr OverlayHdr(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            InitializePipeline();
            var (alpha, raw) = OverlayGpuCore.ComputeAlphaAndColor(
                _alphaPipelineState!, _colorPipelineState!,
                top, bottom, topAlpha, bottomAlpha, pixelCount);

            return new BlendResultHdr(raw, alpha);
        }

        // ========== 后向兼容 IComputer ==========

        public unsafe object[] Compute(object[] args)
        {
            var A = (float[])args[0];
            var B = (float[])args[1];
            var aAlpha = args.Length > 2 ? (args[2] as float[]) : null;
            var bAlpha = args.Length > 3 ? (args[3] as float[]) : null;
            var outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;

            if (aAlpha == null) { aAlpha = new float[A.Length]; Array.Fill(aAlpha, 1f); }
            if (bAlpha == null) { bAlpha = new float[A.Length]; Array.Fill(bAlpha, 1f); }

            int count = A.Length;
            if (outputBpp == 8)
            {
                var r = Overlay8(A, B, aAlpha, bAlpha, count);
                return new object[] { r.Color, r.Alpha };
            }
            if (outputBpp == 16)
            {
                var r = Overlay16(A, B, aAlpha, bAlpha, count);
                return new object[] { r.Color, r.Alpha };
            }
            var hdr = OverlayHdr(A, B, aAlpha, bAlpha, count);
            return new object[] { hdr.Color, hdr.Alpha };
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
    }

    #endregion

    #region ApproximateOverlayComputer

    public class ApproximateOverlayComputer : IApproximateOverlayComputer, IComputer
    {
        public string FromPlugin => "projectFrameCut.iOS.MetalAccelerater.MetalComputers";
        public string SupportedEffectOrMixture => "OverlayApproximate";

        private IMTLComputePipelineState? _alphaPipelineState;
        private IMTLComputePipelineState? _colorPipelineState;

        private void InitializePipeline()
        {
            if (_alphaPipelineState != null && _colorPipelineState != null) return;

            var device = MetalComputerHelper.Device;
            var library = device.CreateLibrary(ShaderSource, new MTLCompileOptions(), out NSError error);
            if (library == null) throw new Exception($"Failed to compile shader: {error?.LocalizedDescription}");

            var alphaFunction = library.CreateFunction("overlay_alpha_compute");
            _alphaPipelineState = device.CreateComputePipelineState(alphaFunction, out error);
            if (_alphaPipelineState == null) throw new Exception($"Failed to create alpha pipeline state: {error?.LocalizedDescription}");

            var colorFunction = library.CreateFunction("overlay_color_compute");
            _colorPipelineState = device.CreateComputePipelineState(colorFunction, out error);
            if (_colorPipelineState == null) throw new Exception($"Failed to create color pipeline state: {error?.LocalizedDescription}");
        }

        // ========== 强类型接口 IApproximateOverlayComputer ==========

        public BlendResult8 ApproximateOverlay8(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            InitializePipeline();
            var (alpha, raw) = OverlayGpuCore.ComputeAlphaAndColor(
                _alphaPipelineState!, _colorPipelineState!,
                top, bottom, topAlpha, bottomAlpha, pixelCount);

            var color = new byte[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                float v = raw[i] / 257.0f;
                if (v < 0) v = 0;
                if (v > 255) v = 255;
                color[i] = (byte)v;
            }
            return new BlendResult8(color, alpha);
        }

        public BlendResult16 ApproximateOverlay16(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            InitializePipeline();
            var (alpha, raw) = OverlayGpuCore.ComputeAlphaAndColor(
                _alphaPipelineState!, _colorPipelineState!,
                top, bottom, topAlpha, bottomAlpha, pixelCount);

            var color = new ushort[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                float v = raw[i];
                if (v < 0) v = 0;
                if (v > 65535) v = 65535;
                color[i] = (ushort)v;
            }
            return new BlendResult16(color, alpha);
        }

        public BlendResultHdr ApproximateOverlayHdr(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            InitializePipeline();
            var (alpha, raw) = OverlayGpuCore.ComputeAlphaAndColor(
                _alphaPipelineState!, _colorPipelineState!,
                top, bottom, topAlpha, bottomAlpha, pixelCount);

            return new BlendResultHdr(raw, alpha);
        }

        // ========== 后向兼容 IComputer ==========

        public unsafe object[] Compute(object[] args)
        {
            var A = (float[])args[0];
            var B = (float[])args[1];
            var aAlpha = args.Length > 2 ? (args[2] as float[]) : null;
            var bAlpha = args.Length > 3 ? (args[3] as float[]) : null;
            var outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;

            if (aAlpha == null) { aAlpha = new float[A.Length]; Array.Fill(aAlpha, 1f); }
            if (bAlpha == null) { bAlpha = new float[A.Length]; Array.Fill(bAlpha, 1f); }

            int count = A.Length;
            if (outputBpp == 8)
            {
                var r = ApproximateOverlay8(A, B, aAlpha, bAlpha, count);
                return new object[] { r.Color, r.Alpha };
            }
            if (outputBpp == 16)
            {
                var r = ApproximateOverlay16(A, B, aAlpha, bAlpha, count);
                return new object[] { r.Color, r.Alpha };
            }
            var hdr = ApproximateOverlayHdr(A, B, aAlpha, bAlpha, count);
            return new object[] { hdr.Color, hdr.Alpha };
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
    }

    #endregion

    #region RemoveColorComputer

    public class RemoveColorComputer : IRemoveColorComputer, IComputer
    {
        public string FromPlugin => "projectFrameCut.iOS.MetalAccelerater.MetalComputers";
        public string SupportedEffectOrMixture => "RemoveColor";

        private IMTLComputePipelineState? _pipelineState;

        private void InitializePipeline()
        {
            if (_pipelineState != null) return;

            var device = MetalComputerHelper.Device;
            var library = device.CreateLibrary(ShaderSource, new MTLCompileOptions(), out NSError error);
            if (library == null) throw new Exception($"Failed to compile shader: {error?.LocalizedDescription}");

            var function = library.CreateFunction("remove_color_compute");
            _pipelineState = device.CreateComputePipelineState(function, out error);
            if (_pipelineState == null) throw new Exception($"Failed to create pipeline state: {error?.LocalizedDescription}");
        }

        // ========== 强类型接口 IRemoveColorComputer ==========

        public float[] ComputeRemoveColor(float[] r, float[] g, float[] b, float[] a,
            float targetR, float targetG, float targetB, float range, int pixels)
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

            var rBuf = MetalComputerHelper.CreateBuffer(r);
            var gBuf = MetalComputerHelper.CreateBuffer(g);
            var bBuf = MetalComputerHelper.CreateBuffer(b);
            var aBuf = MetalComputerHelper.CreateBuffer(a);
            var outABuf = MetalComputerHelper.AllocBuffer(count);

            var (cb, encoder) = MetalComputerHelper.CreateCommandEncoder();
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

            var (tgs, tg) = MetalComputerHelper.ComputeDispatchSizes(count, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup);
            encoder.DispatchThreadgroups(tg, tgs);

            encoder.EndEncoding();
            cb.Commit();
            cb.WaitUntilCompleted();

            float[] result = new float[count];
            Marshal.Copy(outABuf.Contents, result, 0, count);

            return result;
        }

        // ========== 后向兼容 IComputer ==========

        public unsafe object[] Compute(object[] args)
        {
            // args: [aR, aG, aB, sourceA, [toRemoveR], [toRemoveG], [toRemoveB], [range]]
            var aR = (float[])args[0];
            var aG = (float[])args[1];
            var aB = (float[])args[2];
            var sourceA = (float[])args[3];

            var targetR = ((float[])args[4])[0];
            var targetG = ((float[])args[5])[0];
            var targetB = ((float[])args[6])[0];
            var range = ((float[])args[7])[0];

            int count = aR.Length;
            var result = ComputeRemoveColor(aR, aG, aB, sourceA, targetR, targetG, targetB, range, count);
            return new object[] { result };
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
    }

    #endregion

    #region OpacityComputer

    public class OpacityComputer : IOpacityComputer, IComputer, ISessionComputer
    {
        public string FromPlugin => "projectFrameCut.iOS.MetalAccelerater.MetalComputers";
        public string SupportedEffectOrMixture => "FadeOpacity";

        private IMTLComputePipelineState? _pipelineState;

        private void InitializePipeline()
        {
            if (_pipelineState != null) return;
            var device = MetalComputerHelper.Device;
            var library = device.CreateLibrary(ShaderSource, new MTLCompileOptions(), out NSError error);
            if (library == null) throw new Exception($"Failed to compile: {error?.LocalizedDescription}");
            var func = library.CreateFunction("opacity_compute");
            _pipelineState = device.CreateComputePipelineState(func, out error);
            if (_pipelineState == null) throw new Exception($"Pipeline failed: {error?.LocalizedDescription}");
        }

        // ========== 强类型接口 IOpacityComputer ==========

        public FourChannelResult ComputeOpacity(float[] r, float[] g, float[] b, float[] a, float opacity)
        {
            InitializePipeline();
            int count = r.Length;

            var rBuf = MetalComputerHelper.CreateBuffer(r);
            var gBuf = MetalComputerHelper.CreateBuffer(g);
            var bBuf = MetalComputerHelper.CreateBuffer(b);
            var aBuf = MetalComputerHelper.CreateBuffer(a);
            var rOBuf = MetalComputerHelper.AllocBuffer(count);
            var gOBuf = MetalComputerHelper.AllocBuffer(count);
            var bOBuf = MetalComputerHelper.AllocBuffer(count);
            var aOBuf = MetalComputerHelper.AllocBuffer(count);

            var (cb, encoder) = MetalComputerHelper.CreateCommandEncoder();
            encoder.SetComputePipelineState(_pipelineState!);
            encoder.SetBuffer(rBuf, 0, 0); encoder.SetBuffer(gBuf, 0, 1);
            encoder.SetBuffer(bBuf, 0, 2); encoder.SetBuffer(aBuf, 0, 3);
            unsafe { encoder.SetBytes((IntPtr)(&opacity), (nuint)sizeof(float), 4); }
            encoder.SetBuffer(rOBuf, 0, 5); encoder.SetBuffer(gOBuf, 0, 6);
            encoder.SetBuffer(bOBuf, 0, 7); encoder.SetBuffer(aOBuf, 0, 8);

            var (tgs, tg) = MetalComputerHelper.ComputeDispatchSizes(count, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup);
            encoder.DispatchThreadgroups(tg, tgs);
            encoder.EndEncoding(); cb.Commit(); cb.WaitUntilCompleted();

            var rO = new float[count]; var gO = new float[count]; var bO = new float[count]; var aO = new float[count];
            Marshal.Copy(rOBuf.Contents, rO, 0, count); Marshal.Copy(gOBuf.Contents, gO, 0, count);
            Marshal.Copy(bOBuf.Contents, bO, 0, count); Marshal.Copy(aOBuf.Contents, aO, 0, count);

            return new FourChannelResult(rO, gO, bO, aO);
        }

        // ========== 后向兼容 IComputer ==========

        public unsafe object[] Compute(object[] args)
        {
            var rIn = (float[])args[0]; var gIn = (float[])args[1]; var bIn = (float[])args[2]; var aIn = (float[])args[3];
            float opacity = Convert.ToSingle(args[4]);
            var result = ComputeOpacity(rIn, gIn, bIn, aIn, opacity);
            return [result.R, result.G, result.B, result.A];
        }

        // ========== ISessionComputer ==========

        bool ISessionComputer.SupportsBatching => false;

        IGpuEffectSession ISessionComputer.CreateSession(float[] r, float[] g, float[] b, float[] a, int width, int height)
            => new MetalGpuEffectSession(r, g, b, a, width, height);

        void ISessionComputer.ExecuteOnSession(IGpuEffectSession session, IReadOnlyDictionary<string, object> parameters)
        {
            var sess = (MetalGpuEffectSession)session;
            InitializePipeline();
            int count = sess.Length;

            var (cb, encoder) = MetalComputerHelper.CreateCommandEncoder();
            encoder.SetComputePipelineState(_pipelineState!);
            encoder.SetBuffer(sess.CurBufR, 0, 0); encoder.SetBuffer(sess.CurBufG, 0, 1);
            encoder.SetBuffer(sess.CurBufB, 0, 2); encoder.SetBuffer(sess.CurBufA, 0, 3);
            float opacity = Convert.ToSingle(parameters["Opacity"]);
            unsafe { encoder.SetBytes((IntPtr)(&opacity), (nuint)sizeof(float), 4); }
            encoder.SetBuffer(sess.AltBufR, 0, 5); encoder.SetBuffer(sess.AltBufG, 0, 6);
            encoder.SetBuffer(sess.AltBufB, 0, 7); encoder.SetBuffer(sess.AltBufA, 0, 8);

            var (tgs, tg) = MetalComputerHelper.ComputeDispatchSizes(count, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup);
            encoder.DispatchThreadgroups(tg, tgs);
            encoder.EndEncoding(); cb.Commit(); cb.WaitUntilCompleted();

            sess.SwapBuffers();
        }

        private const string ShaderSource = @"
#include <metal_stdlib>
using namespace metal;
kernel void opacity_compute(
    device const float* r [[buffer(0)]], device const float* g [[buffer(1)]],
    device const float* b [[buffer(2)]], device const float* a [[buffer(3)]],
    constant float& opacity [[buffer(4)]],
    device float* rO [[buffer(5)]], device float* gO [[buffer(6)]],
    device float* bO [[buffer(7)]], device float* aO [[buffer(8)]],
    uint id [[thread_position_in_grid]])
{
    rO[id] = r[id]; gO[id] = g[id]; bO[id] = b[id];
    aO[id] = a[id] * opacity;
}
";
    }

    #endregion

    #region VignetteComputer

    public class VignetteComputer : IVignetteComputer, IComputer, ISessionComputer
    {
        public string FromPlugin => "projectFrameCut.iOS.MetalAccelerater.MetalComputers";
        public string SupportedEffectOrMixture => "Vignette";

        private IMTLComputePipelineState? _pipelineState;

        private void InitializePipeline()
        {
            if (_pipelineState != null) return;
            var device = MetalComputerHelper.Device;
            var library = device.CreateLibrary(ShaderSource, new MTLCompileOptions(), out NSError error);
            if (library == null) throw new Exception(error?.LocalizedDescription ?? "Shader error");
            _pipelineState = device.CreateComputePipelineState(library.CreateFunction("vignette_compute"), out error);
            if (_pipelineState == null) throw new Exception(error?.LocalizedDescription ?? "Pipeline error");
        }

        // ========== 强类型接口 IVignetteComputer ==========

        public FourChannelResult ComputeVignette(float[] r, float[] g, float[] b, float[] a,
            int width, int height, float strength, float radius)
        {
            InitializePipeline();
            int count = r.Length;

            var rBuf = MetalComputerHelper.CreateBuffer(r);
            var gBuf = MetalComputerHelper.CreateBuffer(g);
            var bBuf = MetalComputerHelper.CreateBuffer(b);
            var aBuf = MetalComputerHelper.CreateBuffer(a);
            var rOBuf = MetalComputerHelper.AllocBuffer(count);
            var gOBuf = MetalComputerHelper.AllocBuffer(count);
            var bOBuf = MetalComputerHelper.AllocBuffer(count);
            var aOBuf = MetalComputerHelper.AllocBuffer(count);

            var (cb, encoder) = MetalComputerHelper.CreateCommandEncoder();
            encoder.SetComputePipelineState(_pipelineState!);
            encoder.SetBuffer(rBuf, 0, 0); encoder.SetBuffer(gBuf, 0, 1);
            encoder.SetBuffer(bBuf, 0, 2); encoder.SetBuffer(aBuf, 0, 3);
            unsafe
            {
                encoder.SetBytes((IntPtr)(&width), (nuint)sizeof(int), 4);
                encoder.SetBytes((IntPtr)(&height), (nuint)sizeof(int), 5);
                encoder.SetBytes((IntPtr)(&strength), (nuint)sizeof(float), 6);
                encoder.SetBytes((IntPtr)(&radius), (nuint)sizeof(float), 7);
            }
            encoder.SetBuffer(rOBuf, 0, 8); encoder.SetBuffer(gOBuf, 0, 9);
            encoder.SetBuffer(bOBuf, 0, 10); encoder.SetBuffer(aOBuf, 0, 11);

            var (tgs, tg) = MetalComputerHelper.ComputeDispatchSizes(count, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup);
            encoder.DispatchThreadgroups(tg, tgs);
            encoder.EndEncoding(); cb.Commit(); cb.WaitUntilCompleted();

            var rO = new float[count]; Marshal.Copy(rOBuf.Contents, rO, 0, count);
            var gO = new float[count]; Marshal.Copy(gOBuf.Contents, gO, 0, count);
            var bO = new float[count]; Marshal.Copy(bOBuf.Contents, bO, 0, count);
            var aO = new float[count]; Marshal.Copy(aOBuf.Contents, aO, 0, count);

            return new FourChannelResult(rO, gO, bO, aO);
        }

        // ========== 后向兼容 IComputer ==========

        public unsafe object[] Compute(object[] args)
        {
            var rIn = (float[])args[0]; var gIn = (float[])args[1];
            var bIn = (float[])args[2]; var aIn = (float[])args[3];
            int w = Convert.ToInt32(args[4]), h = Convert.ToInt32(args[5]);
            float strength = Convert.ToSingle(args[6]), radius = Convert.ToSingle(args[7]);
            var result = ComputeVignette(rIn, gIn, bIn, aIn, w, h, strength, radius);
            return [result.R, result.G, result.B, result.A];
        }

        // ========== ISessionComputer ==========

        bool ISessionComputer.SupportsBatching => false;

        IGpuEffectSession ISessionComputer.CreateSession(float[] r, float[] g, float[] b, float[] a, int width, int height)
            => new MetalGpuEffectSession(r, g, b, a, width, height);

        void ISessionComputer.ExecuteOnSession(IGpuEffectSession session, IReadOnlyDictionary<string, object> parameters)
        {
            var sess = (MetalGpuEffectSession)session;
            InitializePipeline();
            int count = sess.Length;
            int w = parameters.TryGetValue("BuiltIn.TargetWidth", out var wObj) ? Convert.ToInt32(wObj) : sess.Width;
            int h = parameters.TryGetValue("BuiltIn.TargetHeight", out var hObj) ? Convert.ToInt32(hObj) : sess.Height;
            float strength = Convert.ToSingle(parameters["Strength"]);
            float radius = Convert.ToSingle(parameters["Radius"]);

            var (cb, encoder) = MetalComputerHelper.CreateCommandEncoder();
            encoder.SetComputePipelineState(_pipelineState!);
            encoder.SetBuffer(sess.CurBufR, 0, 0); encoder.SetBuffer(sess.CurBufG, 0, 1);
            encoder.SetBuffer(sess.CurBufB, 0, 2); encoder.SetBuffer(sess.CurBufA, 0, 3);
            unsafe
            {
                encoder.SetBytes((IntPtr)(&w), (nuint)sizeof(int), 4);
                encoder.SetBytes((IntPtr)(&h), (nuint)sizeof(int), 5);
                encoder.SetBytes((IntPtr)(&strength), (nuint)sizeof(float), 6);
                encoder.SetBytes((IntPtr)(&radius), (nuint)sizeof(float), 7);
            }
            encoder.SetBuffer(sess.AltBufR, 0, 8); encoder.SetBuffer(sess.AltBufG, 0, 9);
            encoder.SetBuffer(sess.AltBufB, 0, 10); encoder.SetBuffer(sess.AltBufA, 0, 11);

            var (tgs, tg) = MetalComputerHelper.ComputeDispatchSizes(count, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup);
            encoder.DispatchThreadgroups(tg, tgs);
            encoder.EndEncoding(); cb.Commit(); cb.WaitUntilCompleted();

            sess.SwapBuffers();
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
    }

    #endregion

    #region FlipComputer

    public class FlipComputer : IFlipComputer, IComputer, ISessionComputer
    {
        public string FromPlugin => "projectFrameCut.iOS.MetalAccelerater.MetalComputers";
        public string SupportedEffectOrMixture => "Flip";

        private IMTLComputePipelineState? _pipelineState;

        private void InitializePipeline()
        {
            if (_pipelineState != null) return;
            var d = MetalComputerHelper.Device;
            var l = d.CreateLibrary(ShaderSrc, new MTLCompileOptions(), out NSError e);
            if (l == null) throw new Exception(e?.LocalizedDescription);
            _pipelineState = d.CreateComputePipelineState(l.CreateFunction("flip_compute"), out e);
            if (_pipelineState == null) throw new Exception(e?.LocalizedDescription);
        }

        // ========== 强类型接口 IFlipComputer ==========

        public FourChannelResult ComputeFlip(float[] r, float[] g, float[] b, float[] a,
            int width, int height, bool horizontal, bool vertical)
        {
            InitializePipeline();
            int count = r.Length;
            int bufSize = count * sizeof(float);
            int horiz = horizontal ? 1 : 0;
            int vert = vertical ? 1 : 0;

            var rB = MetalComputerHelper.CreateBuffer(r);
            var gB = MetalComputerHelper.CreateBuffer(g);
            var bB = MetalComputerHelper.CreateBuffer(b);
            var aB = MetalComputerHelper.CreateBuffer(a);
            var rOB = MetalComputerHelper.AllocBuffer(count);
            var gOB = MetalComputerHelper.AllocBuffer(count);
            var bOB = MetalComputerHelper.AllocBuffer(count);
            var aOB = MetalComputerHelper.AllocBuffer(count);

            var (cb, enc) = MetalComputerHelper.CreateCommandEncoder();
            enc.SetComputePipelineState(_pipelineState!);
            enc.SetBuffer(rB, 0, 0); enc.SetBuffer(gB, 0, 1);
            enc.SetBuffer(bB, 0, 2); enc.SetBuffer(aB, 0, 3);
            unsafe
            {
                enc.SetBytes((IntPtr)(&width), (nuint)sizeof(int), 4);
                enc.SetBytes((IntPtr)(&height), (nuint)sizeof(int), 5);
                enc.SetBytes((IntPtr)(&horiz), (nuint)sizeof(int), 6);
                enc.SetBytes((IntPtr)(&vert), (nuint)sizeof(int), 7);
            }
            enc.SetBuffer(rOB, 0, 8); enc.SetBuffer(gOB, 0, 9);
            enc.SetBuffer(bOB, 0, 10); enc.SetBuffer(aOB, 0, 11);

            var (tgs, tg) = MetalComputerHelper.ComputeDispatchSizes(count, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup);
            enc.DispatchThreadgroups(tg, tgs);
            enc.EndEncoding(); cb.Commit(); cb.WaitUntilCompleted();

            var rO = new float[count]; Marshal.Copy(rOB.Contents, rO, 0, count);
            var gO = new float[count]; Marshal.Copy(gOB.Contents, gO, 0, count);
            var bO = new float[count]; Marshal.Copy(bOB.Contents, bO, 0, count);
            var aO = new float[count]; Marshal.Copy(aOB.Contents, aO, 0, count);

            return new FourChannelResult(rO, gO, bO, aO);
        }

        // ========== 后向兼容 IComputer ==========

        public unsafe object[] Compute(object[] args)
        {
            var rIn = (float[])args[0]; var gIn = (float[])args[1];
            var bIn = (float[])args[2]; var aIn = (float[])args[3];
            int w = Convert.ToInt32(args[4]), h = Convert.ToInt32(args[5]);
            bool horiz = Convert.ToBoolean(args[6]), vert = Convert.ToBoolean(args[7]);
            var result = ComputeFlip(rIn, gIn, bIn, aIn, w, h, horiz, vert);
            return [result.R, result.G, result.B, result.A];
        }

        // ========== ISessionComputer ==========

        bool ISessionComputer.SupportsBatching => false;

        IGpuEffectSession ISessionComputer.CreateSession(float[] r, float[] g, float[] b, float[] a, int width, int height)
            => new MetalGpuEffectSession(r, g, b, a, width, height);

        void ISessionComputer.ExecuteOnSession(IGpuEffectSession session, IReadOnlyDictionary<string, object> parameters)
        {
            var sess = (MetalGpuEffectSession)session;
            InitializePipeline();
            int count = sess.Length;
            int w = parameters.TryGetValue("BuiltIn.TargetWidth", out var wObj) ? Convert.ToInt32(wObj) : sess.Width;
            int h = parameters.TryGetValue("BuiltIn.TargetHeight", out var hObj) ? Convert.ToInt32(hObj) : sess.Height;
            int horiz = Convert.ToBoolean(parameters["Horizontal"]) ? 1 : 0;
            int vert = Convert.ToBoolean(parameters["Vertical"]) ? 1 : 0;

            var (cb, enc) = MetalComputerHelper.CreateCommandEncoder();
            enc.SetComputePipelineState(_pipelineState!);
            enc.SetBuffer(sess.CurBufR, 0, 0); enc.SetBuffer(sess.CurBufG, 0, 1);
            enc.SetBuffer(sess.CurBufB, 0, 2); enc.SetBuffer(sess.CurBufA, 0, 3);
            unsafe
            {
                enc.SetBytes((IntPtr)(&w), (nuint)sizeof(int), 4);
                enc.SetBytes((IntPtr)(&h), (nuint)sizeof(int), 5);
                enc.SetBytes((IntPtr)(&horiz), (nuint)sizeof(int), 6);
                enc.SetBytes((IntPtr)(&vert), (nuint)sizeof(int), 7);
            }
            enc.SetBuffer(sess.AltBufR, 0, 8); enc.SetBuffer(sess.AltBufG, 0, 9);
            enc.SetBuffer(sess.AltBufB, 0, 10); enc.SetBuffer(sess.AltBufA, 0, 11);

            var (tgs, tg) = MetalComputerHelper.ComputeDispatchSizes(count, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup);
            enc.DispatchThreadgroups(tg, tgs);
            enc.EndEncoding(); cb.Commit(); cb.WaitUntilCompleted();

            sess.SwapBuffers();
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
    }

    #endregion

    #region SharpenComputer

    public class SharpenComputer : ISharpenComputer, IComputer, ISessionComputer
    {
        public string FromPlugin => "projectFrameCut.iOS.MetalAccelerater.MetalComputers";
        public string SupportedEffectOrMixture => "Sharpen";

        private IMTLComputePipelineState? _pipelineState;

        private void InitializePipeline()
        {
            if (_pipelineState != null) return;
            var d = MetalComputerHelper.Device;
            var l = d.CreateLibrary(ShaderSrc, new MTLCompileOptions(), out NSError e);
            if (l == null) throw new Exception(e?.LocalizedDescription);
            _pipelineState = d.CreateComputePipelineState(l.CreateFunction("sharpen_compute"), out e);
            if (_pipelineState == null) throw new Exception(e?.LocalizedDescription);
        }

        // ========== 强类型接口 ISharpenComputer ==========

        public FourChannelResult ComputeSharpen(float[] r, float[] g, float[] b, float[] a,
            int width, float amount)
        {
            InitializePipeline();
            int count = r.Length;

            var rB = MetalComputerHelper.CreateBuffer(r);
            var gB = MetalComputerHelper.CreateBuffer(g);
            var bB = MetalComputerHelper.CreateBuffer(b);
            var aB = MetalComputerHelper.CreateBuffer(a);
            var rOB = MetalComputerHelper.AllocBuffer(count);
            var gOB = MetalComputerHelper.AllocBuffer(count);
            var bOB = MetalComputerHelper.AllocBuffer(count);
            var aOB = MetalComputerHelper.AllocBuffer(count);

            var (cb, enc) = MetalComputerHelper.CreateCommandEncoder();
            enc.SetComputePipelineState(_pipelineState!);
            enc.SetBuffer(rB, 0, 0); enc.SetBuffer(gB, 0, 1);
            enc.SetBuffer(bB, 0, 2); enc.SetBuffer(aB, 0, 3);
            unsafe
            {
                enc.SetBytes((IntPtr)(&width), (nuint)sizeof(int), 4);
                enc.SetBytes((IntPtr)(&amount), (nuint)sizeof(float), 5);
            }
            enc.SetBuffer(rOB, 0, 6); enc.SetBuffer(gOB, 0, 7);
            enc.SetBuffer(bOB, 0, 8); enc.SetBuffer(aOB, 0, 9);

            var (tgs, tg) = MetalComputerHelper.ComputeDispatchSizes(count, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup);
            enc.DispatchThreadgroups(tg, tgs);
            enc.EndEncoding(); cb.Commit(); cb.WaitUntilCompleted();

            var rO = new float[count]; Marshal.Copy(rOB.Contents, rO, 0, count);
            var gO = new float[count]; Marshal.Copy(gOB.Contents, gO, 0, count);
            var bO = new float[count]; Marshal.Copy(bOB.Contents, bO, 0, count);
            var aO = new float[count]; Marshal.Copy(aOB.Contents, aO, 0, count);

            return new FourChannelResult(rO, gO, bO, aO);
        }

        // ========== 后向兼容 IComputer ==========

        public unsafe object[] Compute(object[] args)
        {
            var rIn = (float[])args[0]; var gIn = (float[])args[1];
            var bIn = (float[])args[2]; var aIn = (float[])args[3];
            int w = Convert.ToInt32(args[4]); float amount = Convert.ToSingle(args[5]);
            var result = ComputeSharpen(rIn, gIn, bIn, aIn, w, amount);
            return [result.R, result.G, result.B, result.A];
        }

        // ========== ISessionComputer ==========

        bool ISessionComputer.SupportsBatching => false;

        IGpuEffectSession ISessionComputer.CreateSession(float[] r, float[] g, float[] b, float[] a, int width, int height)
            => new MetalGpuEffectSession(r, g, b, a, width, height);

        void ISessionComputer.ExecuteOnSession(IGpuEffectSession session, IReadOnlyDictionary<string, object> parameters)
        {
            var sess = (MetalGpuEffectSession)session;
            InitializePipeline();
            int count = sess.Length;
            int w = parameters.TryGetValue("BuiltIn.TargetWidth", out var wObj) ? Convert.ToInt32(wObj) : sess.Width;
            float amount = Convert.ToSingle(parameters["Amount"]);

            var (cb, enc) = MetalComputerHelper.CreateCommandEncoder();
            enc.SetComputePipelineState(_pipelineState!);
            enc.SetBuffer(sess.CurBufR, 0, 0); enc.SetBuffer(sess.CurBufG, 0, 1);
            enc.SetBuffer(sess.CurBufB, 0, 2); enc.SetBuffer(sess.CurBufA, 0, 3);
            unsafe
            {
                enc.SetBytes((IntPtr)(&w), (nuint)sizeof(int), 4);
                enc.SetBytes((IntPtr)(&amount), (nuint)sizeof(float), 5);
            }
            enc.SetBuffer(sess.AltBufR, 0, 6); enc.SetBuffer(sess.AltBufG, 0, 7);
            enc.SetBuffer(sess.AltBufB, 0, 8); enc.SetBuffer(sess.AltBufA, 0, 9);

            var (tgs, tg) = MetalComputerHelper.ComputeDispatchSizes(count, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup);
            enc.DispatchThreadgroups(tg, tgs);
            enc.EndEncoding(); cb.Commit(); cb.WaitUntilCompleted();

            sess.SwapBuffers();
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
    }

    #endregion

    #region RotationComputer

    public class RotationComputer : IRotationComputer, IComputer, ISessionComputer
    {
        public string FromPlugin => "projectFrameCut.iOS.MetalAccelerater.MetalComputers";
        public string SupportedEffectOrMixture => "Rotation";

        private IMTLComputePipelineState? _pipelineState;

        private void InitializePipeline()
        {
            if (_pipelineState != null) return;
            var d = MetalComputerHelper.Device;
            var l = d.CreateLibrary(ShaderSrc, new MTLCompileOptions(), out NSError e);
            if (l == null) throw new Exception(e?.LocalizedDescription);
            _pipelineState = d.CreateComputePipelineState(l.CreateFunction("rotation_compute"), out e);
            if (_pipelineState == null) throw new Exception(e?.LocalizedDescription);
        }

        // ========== 强类型接口 IRotationComputer ==========

        public FourChannelResult ComputeRotation(float[] r, float[] g, float[] b, float[] a,
            int srcW, int srcH, int dstW, int dstH, float angleDeg)
        {
            InitializePipeline();
            int srcLen = srcW * srcH;
            int dstLen = dstW * dstH;

            var rB = MetalComputerHelper.CreateBuffer(r);
            var gB = MetalComputerHelper.CreateBuffer(g);
            var bB = MetalComputerHelper.CreateBuffer(b);
            var aB = MetalComputerHelper.CreateBuffer(a);
            var rOB = MetalComputerHelper.AllocBuffer(dstLen);
            var gOB = MetalComputerHelper.AllocBuffer(dstLen);
            var bOB = MetalComputerHelper.AllocBuffer(dstLen);
            var aOB = MetalComputerHelper.AllocBuffer(dstLen);

            var (cb, enc) = MetalComputerHelper.CreateCommandEncoder();
            enc.SetComputePipelineState(_pipelineState!);
            enc.SetBuffer(rB, 0, 0); enc.SetBuffer(gB, 0, 1);
            enc.SetBuffer(bB, 0, 2); enc.SetBuffer(aB, 0, 3);
            unsafe
            {
                enc.SetBytes((IntPtr)(&srcW), (nuint)sizeof(int), 4);
                enc.SetBytes((IntPtr)(&srcH), (nuint)sizeof(int), 5);
                enc.SetBytes((IntPtr)(&dstW), (nuint)sizeof(int), 6);
                enc.SetBytes((IntPtr)(&dstH), (nuint)sizeof(int), 7);
                enc.SetBytes((IntPtr)(&angleDeg), (nuint)sizeof(float), 8);
            }
            enc.SetBuffer(rOB, 0, 9); enc.SetBuffer(gOB, 0, 10);
            enc.SetBuffer(bOB, 0, 11); enc.SetBuffer(aOB, 0, 12);

            var (tgs, tg) = MetalComputerHelper.ComputeDispatchSizes(dstLen, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup);
            enc.DispatchThreadgroups(tg, tgs);
            enc.EndEncoding(); cb.Commit(); cb.WaitUntilCompleted();

            var rO = new float[dstLen]; Marshal.Copy(rOB.Contents, rO, 0, dstLen);
            var gO = new float[dstLen]; Marshal.Copy(gOB.Contents, gO, 0, dstLen);
            var bO = new float[dstLen]; Marshal.Copy(bOB.Contents, bO, 0, dstLen);
            var aO = new float[dstLen]; Marshal.Copy(aOB.Contents, aO, 0, dstLen);

            return new FourChannelResult(rO, gO, bO, aO);
        }

        // ========== 后向兼容 IComputer ==========

        public unsafe object[] Compute(object[] args)
        {
            var rIn = (float[])args[0]; var gIn = (float[])args[1];
            var bIn = (float[])args[2]; var aIn = (float[])args[3];
            int srcW = Convert.ToInt32(args[4]), srcH = Convert.ToInt32(args[5]);
            int outW = Convert.ToInt32(args[6]), outH = Convert.ToInt32(args[7]);
            float angle = Convert.ToSingle(args[8]);
            var result = ComputeRotation(rIn, gIn, bIn, aIn, srcW, srcH, outW, outH, angle);
            return [result.R, result.G, result.B, result.A];
        }

        // ========== ISessionComputer ==========

        bool ISessionComputer.SupportsBatching => false;

        IGpuEffectSession ISessionComputer.CreateSession(float[] r, float[] g, float[] b, float[] a, int width, int height)
            => new MetalGpuEffectSession(r, g, b, a, width, height);

        void ISessionComputer.ExecuteOnSession(IGpuEffectSession session, IReadOnlyDictionary<string, object> parameters)
        {
            var sess = (MetalGpuEffectSession)session;
            InitializePipeline();
            int count = sess.Length;
            int outW = parameters.TryGetValue("BuiltIn.TargetWidth", out var wObj) ? Convert.ToInt32(wObj) : sess.Width;
            int outH = parameters.TryGetValue("BuiltIn.TargetHeight", out var hObj) ? Convert.ToInt32(hObj) : sess.Height;
            float angleDeg = Convert.ToSingle(parameters["Angle"]);
            float angleRad = angleDeg * MathF.PI / 180f;
            float cosA = MathF.Cos(-angleRad);
            float sinA = MathF.Sin(-angleRad);

            var (cb, enc) = MetalComputerHelper.CreateCommandEncoder();
            enc.SetComputePipelineState(_pipelineState!);
            enc.SetBuffer(sess.CurBufR, 0, 0); enc.SetBuffer(sess.CurBufG, 0, 1);
            enc.SetBuffer(sess.CurBufB, 0, 2); enc.SetBuffer(sess.CurBufA, 0, 3);
            unsafe
            {
                enc.SetBytes((IntPtr)(&outW), (nuint)sizeof(int), 4);
                enc.SetBytes((IntPtr)(&outH), (nuint)sizeof(int), 5);
                enc.SetBytes((IntPtr)(&outW), (nuint)sizeof(int), 6);
                enc.SetBytes((IntPtr)(&outH), (nuint)sizeof(int), 7);
                enc.SetBytes((IntPtr)(&angleDeg), (nuint)sizeof(float), 8);
            }
            enc.SetBuffer(sess.AltBufR, 0, 9); enc.SetBuffer(sess.AltBufG, 0, 10);
            enc.SetBuffer(sess.AltBufB, 0, 11); enc.SetBuffer(sess.AltBufA, 0, 12);

            var (tgs, tg) = MetalComputerHelper.ComputeDispatchSizes(count, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup);
            enc.DispatchThreadgroups(tg, tgs);
            enc.EndEncoding(); cb.Commit(); cb.WaitUntilCompleted();

            sess.SwapBuffers();
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
    float cosA = cos(-ar); float sinA = sin(-ar);
    float scx = float(srcW) * 0.5; float scy = float(srcH) * 0.5;
    float ocx = float(outW) * 0.5; float ocy = float(outH) * 0.5;
    float ox = float(x) - ocx; float oy = float(y) - ocy;
    float sx = cosA * ox - sinA * oy + scx;
    float sy = sinA * ox + cosA * oy + scy;
    if (sx >= 0.0 && sx < float(srcW - 1) && sy >= 0.0 && sy < float(srcH - 1)) {
        int sx0 = int(sx); int sy0 = int(sy); int sx1 = sx0 + 1; int sy1 = sy0 + 1;
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
    }

    #endregion

    #region BlurComputer

    public class BlurComputer : IBlurComputer, IComputer, ISessionComputer
    {
        public string FromPlugin => "projectFrameCut.iOS.MetalAccelerater.MetalComputers";
        public string SupportedEffectOrMixture => "Blur";

        private IMTLComputePipelineState? _pipelineStateH;
        private IMTLComputePipelineState? _pipelineStateV;

        private void InitializePipelines()
        {
            if (_pipelineStateH != null && _pipelineStateV != null) return;
            var d = MetalComputerHelper.Device;
            var l = d.CreateLibrary(ShaderSrc, new MTLCompileOptions(), out NSError e);
            if (l == null) throw new Exception(e?.LocalizedDescription);
            _pipelineStateH = d.CreateComputePipelineState(l.CreateFunction("blur_horizontal"), out e);
            if (_pipelineStateH == null) throw new Exception(e?.LocalizedDescription);
            _pipelineStateV = d.CreateComputePipelineState(l.CreateFunction("blur_vertical"), out e);
            if (_pipelineStateV == null) throw new Exception(e?.LocalizedDescription);
        }

        // ========== 强类型接口 IBlurComputer ==========

        public FourChannelResult ComputeBlur(float[] r, float[] g, float[] b, float[] a,
            int width, float sigma)
        {
            InitializePipelines();
            int count = r.Length;
            int h = count / width;
            int radius = Math.Max(1, (int)MathF.Ceiling(sigma));

            var rB = MetalComputerHelper.CreateBuffer(r);
            var gB = MetalComputerHelper.CreateBuffer(g);
            var bB = MetalComputerHelper.CreateBuffer(b);
            var aB = MetalComputerHelper.CreateBuffer(a);
            var rT = MetalComputerHelper.AllocBuffer(count);
            var gT = MetalComputerHelper.AllocBuffer(count);
            var bT = MetalComputerHelper.AllocBuffer(count);
            var aT = MetalComputerHelper.AllocBuffer(count);

            var (cb, enc) = MetalComputerHelper.CreateCommandEncoder();

            // Horizontal pass
            enc.SetComputePipelineState(_pipelineStateH!);
            enc.SetBuffer(rB, 0, 0); enc.SetBuffer(gB, 0, 1);
            enc.SetBuffer(bB, 0, 2); enc.SetBuffer(aB, 0, 3);
            unsafe { enc.SetBytes((IntPtr)(&width), (nuint)sizeof(int), 4); }
            unsafe { enc.SetBytes((IntPtr)(&radius), (nuint)sizeof(int), 5); }
            enc.SetBuffer(rT, 0, 6); enc.SetBuffer(gT, 0, 7);
            enc.SetBuffer(bT, 0, 8); enc.SetBuffer(aT, 0, 9);

            var (tgs, tg) = MetalComputerHelper.ComputeDispatchSizes(count, (int)_pipelineStateH!.MaxTotalThreadsPerThreadgroup);
            enc.DispatchThreadgroups(tg, tgs);

            // Vertical pass
            enc.SetComputePipelineState(_pipelineStateV!);
            enc.SetBuffer(rT, 0, 0); enc.SetBuffer(gT, 0, 1);
            enc.SetBuffer(bT, 0, 2); enc.SetBuffer(aT, 0, 3);
            unsafe { enc.SetBytes((IntPtr)(&width), (nuint)sizeof(int), 4); }
            unsafe { enc.SetBytes((IntPtr)(&h), (nuint)sizeof(int), 5); }
            unsafe { enc.SetBytes((IntPtr)(&radius), (nuint)sizeof(int), 6); }

            var rOB = MetalComputerHelper.AllocBuffer(count);
            var gOB = MetalComputerHelper.AllocBuffer(count);
            var bOB = MetalComputerHelper.AllocBuffer(count);
            var aOB = MetalComputerHelper.AllocBuffer(count);
            enc.SetBuffer(rOB, 0, 7); enc.SetBuffer(gOB, 0, 8);
            enc.SetBuffer(bOB, 0, 9); enc.SetBuffer(aOB, 0, 10);

            enc.DispatchThreadgroups(tg, tgs);
            enc.EndEncoding(); cb.Commit(); cb.WaitUntilCompleted();

            var rO = new float[count]; Marshal.Copy(rOB.Contents, rO, 0, count);
            var gO = new float[count]; Marshal.Copy(gOB.Contents, gO, 0, count);
            var bO = new float[count]; Marshal.Copy(bOB.Contents, bO, 0, count);
            var aO = new float[count]; Marshal.Copy(aOB.Contents, aO, 0, count);

            return new FourChannelResult(rO, gO, bO, aO);
        }

        // ========== 后向兼容 IComputer ==========

        public unsafe object[] Compute(object[] args)
        {
            var rIn = (float[])args[0]; var gIn = (float[])args[1];
            var bIn = (float[])args[2]; var aIn = (float[])args[3];
            int w = Convert.ToInt32(args[4]);
            float sigma = Convert.ToSingle(args[5]);
            var result = ComputeBlur(rIn, gIn, bIn, aIn, w, sigma);
            return [result.R, result.G, result.B, result.A];
        }

        // ========== ISessionComputer ==========

        bool ISessionComputer.SupportsBatching => false;

        IGpuEffectSession ISessionComputer.CreateSession(float[] r, float[] g, float[] b, float[] a, int width, int height)
            => new MetalGpuEffectSession(r, g, b, a, width, height);

        void ISessionComputer.ExecuteOnSession(IGpuEffectSession session, IReadOnlyDictionary<string, object> parameters)
        {
            var sess = (MetalGpuEffectSession)session;
            InitializePipelines();
            int count = sess.Length;
            int w = parameters.TryGetValue("BuiltIn.TargetWidth", out var wObj) ? Convert.ToInt32(wObj) : sess.Width;
            float sigma = Convert.ToSingle(parameters["Sigma"]);
            int radius = Math.Max(1, (int)MathF.Ceiling(sigma));
            int h = count / w;

            var rT = MetalComputerHelper.AllocBuffer(count);
            var gT = MetalComputerHelper.AllocBuffer(count);
            var bT = MetalComputerHelper.AllocBuffer(count);
            var aT = MetalComputerHelper.AllocBuffer(count);

            var (cb, enc) = MetalComputerHelper.CreateCommandEncoder();

            enc.SetComputePipelineState(_pipelineStateH!);
            enc.SetBuffer(sess.CurBufR, 0, 0); enc.SetBuffer(sess.CurBufG, 0, 1);
            enc.SetBuffer(sess.CurBufB, 0, 2); enc.SetBuffer(sess.CurBufA, 0, 3);
            unsafe { enc.SetBytes((IntPtr)(&w), (nuint)sizeof(int), 4); }
            unsafe { enc.SetBytes((IntPtr)(&radius), (nuint)sizeof(int), 5); }
            enc.SetBuffer(rT, 0, 6); enc.SetBuffer(gT, 0, 7);
            enc.SetBuffer(bT, 0, 8); enc.SetBuffer(aT, 0, 9);

            var (tgs, tg) = MetalComputerHelper.ComputeDispatchSizes(count, (int)_pipelineStateH!.MaxTotalThreadsPerThreadgroup);
            enc.DispatchThreadgroups(tg, tgs);

            enc.SetComputePipelineState(_pipelineStateV!);
            enc.SetBuffer(rT, 0, 0); enc.SetBuffer(gT, 0, 1);
            enc.SetBuffer(bT, 0, 2); enc.SetBuffer(aT, 0, 3);
            unsafe { enc.SetBytes((IntPtr)(&w), (nuint)sizeof(int), 4); }
            unsafe { enc.SetBytes((IntPtr)(&h), (nuint)sizeof(int), 5); }
            unsafe { enc.SetBytes((IntPtr)(&radius), (nuint)sizeof(int), 6); }
            enc.SetBuffer(sess.AltBufR, 0, 7); enc.SetBuffer(sess.AltBufG, 0, 8);
            enc.SetBuffer(sess.AltBufB, 0, 9); enc.SetBuffer(sess.AltBufA, 0, 10);

            enc.DispatchThreadgroups(tg, tgs);
            enc.EndEncoding(); cb.Commit(); cb.WaitUntilCompleted();

            sess.SwapBuffers();
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
        int col = clamp(k, 0, w - 1); int idx = rs + col;
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
        int row = clamp(k, 0, h - 1);
        sr += r[row*w+col]; sg += g[row*w+col]; sb += b[row*w+col]; sa += a[row*w+col]; cnt++;
    }
    rO[id] = sr/cnt; gO[id] = sg/cnt; bO[id] = sb/cnt; aO[id] = sa/cnt;
}
";
    }

    #endregion

    #region ColorAdjustmentComputer

    public class ColorAdjustmentComputer : IColorAdjustmentComputer, IComputer, ISessionComputer
    {
        public string FromPlugin => "projectFrameCut.iOS.MetalAccelerater.MetalComputers";
        public string SupportedEffectOrMixture => "ColorAdjustment";

        private IMTLComputePipelineState? _pipelineState;

        private void InitializePipeline()
        {
            if (_pipelineState != null) return;
            var d = MetalComputerHelper.Device;
            var l = d.CreateLibrary(ShaderSrc, new MTLCompileOptions(), out NSError e);
            if (l == null) throw new Exception(e?.LocalizedDescription);
            _pipelineState = d.CreateComputePipelineState(l.CreateFunction("coloradjust_compute"), out e);
            if (_pipelineState == null) throw new Exception(e?.LocalizedDescription);
        }

        // ========== 强类型接口 IColorAdjustmentComputer ==========

        public FourChannelResult ComputeColorAdjustment(
            float[] r, float[] g, float[] b, float[] a,
            int width, int height,
            float brightness, float contrast, float saturation, float hue,
            float gamma, float vibrance, float temperature, bool invert,
            float grayscale, float opacity, float maxVal)
        {
            InitializePipeline();
            int count = r.Length;
            float invertF = invert ? 1f : 0f;

            var rB = MetalComputerHelper.CreateBuffer(r);
            var gB = MetalComputerHelper.CreateBuffer(g);
            var bB = MetalComputerHelper.CreateBuffer(b);
            var aB = MetalComputerHelper.CreateBuffer(a);
            var rOB = MetalComputerHelper.AllocBuffer(count);
            var gOB = MetalComputerHelper.AllocBuffer(count);
            var bOB = MetalComputerHelper.AllocBuffer(count);
            var aOB = MetalComputerHelper.AllocBuffer(count);

            var (cb, enc) = MetalComputerHelper.CreateCommandEncoder();
            enc.SetComputePipelineState(_pipelineState!);
            enc.SetBuffer(rB, 0, 0); enc.SetBuffer(gB, 0, 1);
            enc.SetBuffer(bB, 0, 2); enc.SetBuffer(aB, 0, 3);
            unsafe
            {
                enc.SetBytes((IntPtr)(&brightness), (nuint)sizeof(float), 4);
                enc.SetBytes((IntPtr)(&contrast), (nuint)sizeof(float), 5);
                enc.SetBytes((IntPtr)(&saturation), (nuint)sizeof(float), 6);
                enc.SetBytes((IntPtr)(&hue), (nuint)sizeof(float), 7);
                enc.SetBytes((IntPtr)(&gamma), (nuint)sizeof(float), 8);
                enc.SetBytes((IntPtr)(&vibrance), (nuint)sizeof(float), 9);
                enc.SetBytes((IntPtr)(&temperature), (nuint)sizeof(float), 10);
                enc.SetBytes((IntPtr)(&invertF), (nuint)sizeof(float), 11);
                enc.SetBytes((IntPtr)(&grayscale), (nuint)sizeof(float), 12);
                enc.SetBytes((IntPtr)(&opacity), (nuint)sizeof(float), 13);
                enc.SetBytes((IntPtr)(&maxVal), (nuint)sizeof(float), 14);
            }
            enc.SetBuffer(rOB, 0, 15); enc.SetBuffer(gOB, 0, 16);
            enc.SetBuffer(bOB, 0, 17); enc.SetBuffer(aOB, 0, 18);

            var (tgs, tg) = MetalComputerHelper.ComputeDispatchSizes(count, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup);
            enc.DispatchThreadgroups(tg, tgs);
            enc.EndEncoding(); cb.Commit(); cb.WaitUntilCompleted();

            var rO = new float[count]; Marshal.Copy(rOB.Contents, rO, 0, count);
            var gO = new float[count]; Marshal.Copy(gOB.Contents, gO, 0, count);
            var bO = new float[count]; Marshal.Copy(bOB.Contents, bO, 0, count);
            var aO = new float[count]; Marshal.Copy(aOB.Contents, aO, 0, count);

            return new FourChannelResult(rO, gO, bO, aO);
        }

        // ========== 后向兼容 IComputer ==========

        public unsafe object[] Compute(object[] args)
        {
            var rIn = (float[])args[0]; var gIn = (float[])args[1];
            var bIn = (float[])args[2]; var aIn = (float[])args[3];
            float brightness = Convert.ToSingle(args[4]), contrast = Convert.ToSingle(args[5]);
            float saturation = Convert.ToSingle(args[6]), hue = Convert.ToSingle(args[7]);
            float gamma = Convert.ToSingle(args[8]), vibrance = Convert.ToSingle(args[9]);
            float temperature = Convert.ToSingle(args[10]), invertF = Convert.ToSingle(args[11]);
            float grayscale = Convert.ToSingle(args[12]), opacity = Convert.ToSingle(args[13]);
            float maxV = Convert.ToSingle(args[14]);
            int count = rIn.Length;

            // 注意：IColorAdjustmentComputer 含 width/height 参数但当前 shader 未使用，
            // 此处传入 0 保持接口兼容，GPU 内核不依赖这两个值。
            var result = ComputeColorAdjustment(
                rIn, gIn, bIn, aIn, 0, 0,
                brightness, contrast, saturation, hue,
                gamma, vibrance, temperature, invertF > 0.5f,
                grayscale, opacity, maxV);
            return [result.R, result.G, result.B, result.A];
        }

        // ========== ISessionComputer ==========

        bool ISessionComputer.SupportsBatching => false;

        IGpuEffectSession ISessionComputer.CreateSession(float[] r, float[] g, float[] b, float[] a, int width, int height)
            => new MetalGpuEffectSession(r, g, b, a, width, height);

        void ISessionComputer.ExecuteOnSession(IGpuEffectSession session, IReadOnlyDictionary<string, object> parameters)
        {
            var sess = (MetalGpuEffectSession)session;
            InitializePipeline();
            int count = sess.Length;
            float brightness = Convert.ToSingle(parameters["Brightness"]);
            float contrast = Convert.ToSingle(parameters["Contrast"]);
            float saturation = Convert.ToSingle(parameters["Saturation"]);
            float hue = Convert.ToSingle(parameters["Hue"]);
            float gamma = Convert.ToSingle(parameters["Gamma"]);
            float vibrance = Convert.ToSingle(parameters["Vibrance"]);
            float temperature = Convert.ToSingle(parameters["Temperature"]);
            float invertF = Convert.ToBoolean(parameters["Invert"]) ? 1f : 0f;
            float grayscale = Convert.ToSingle(parameters["Grayscale"]);
            float opacity = Convert.ToSingle(parameters["Opacity"]);
            float maxV = 65535f;

            var (cb, enc) = MetalComputerHelper.CreateCommandEncoder();
            enc.SetComputePipelineState(_pipelineState!);
            enc.SetBuffer(sess.CurBufR, 0, 0); enc.SetBuffer(sess.CurBufG, 0, 1);
            enc.SetBuffer(sess.CurBufB, 0, 2); enc.SetBuffer(sess.CurBufA, 0, 3);
            unsafe
            {
                enc.SetBytes((IntPtr)(&brightness), (nuint)sizeof(float), 4);
                enc.SetBytes((IntPtr)(&contrast), (nuint)sizeof(float), 5);
                enc.SetBytes((IntPtr)(&saturation), (nuint)sizeof(float), 6);
                enc.SetBytes((IntPtr)(&hue), (nuint)sizeof(float), 7);
                enc.SetBytes((IntPtr)(&gamma), (nuint)sizeof(float), 8);
                enc.SetBytes((IntPtr)(&vibrance), (nuint)sizeof(float), 9);
                enc.SetBytes((IntPtr)(&temperature), (nuint)sizeof(float), 10);
                enc.SetBytes((IntPtr)(&invertF), (nuint)sizeof(float), 11);
                enc.SetBytes((IntPtr)(&grayscale), (nuint)sizeof(float), 12);
                enc.SetBytes((IntPtr)(&opacity), (nuint)sizeof(float), 13);
                enc.SetBytes((IntPtr)(&maxV), (nuint)sizeof(float), 14);
            }
            enc.SetBuffer(sess.AltBufR, 0, 15); enc.SetBuffer(sess.AltBufG, 0, 16);
            enc.SetBuffer(sess.AltBufB, 0, 17); enc.SetBuffer(sess.AltBufA, 0, 18);

            var (tgs, tg) = MetalComputerHelper.ComputeDispatchSizes(count, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup);
            enc.DispatchThreadgroups(tg, tgs);
            enc.EndEncoding(); cb.Commit(); cb.WaitUntilCompleted();

            sess.SwapBuffers();
        }

        private const string ShaderSrc = @"
#include <metal_stdlib>
using namespace metal;
kernel void coloradjust_compute(
    device const float* r [[buffer(0)]], device const float* g [[buffer(1)]],
    device const float* b [[buffer(2)]], device const float* a [[buffer(3)]],
    constant float& brightness [[buffer(4)]], constant float& contrast [[buffer(5)]],
    constant float& saturation [[buffer(6)]], constant float& hue [[buffer(7)]],
    constant float& gamma [[buffer(8)]], constant float& vibrance [[buffer(9)]],
    constant float& temperature [[buffer(10)]], constant float& invertF [[buffer(11)]],
    constant float& grayscale [[buffer(12)]], constant float& opacity [[buffer(13)]],
    constant float& maxV [[buffer(14)]],
    device float* rO [[buffer(15)]], device float* gO [[buffer(16)]],
    device float* bO [[buffer(17)]], device float* aO [[buffer(18)]],
    uint id [[thread_position_in_grid]])
{
    float cr = r[id], cg = g[id], cb = b[id], ca = a[id];
    float bf = brightness - 1.0;
    cr = bf>=0.0 ? cr+(maxV-cr)*bf : cr*(1.0+bf);
    cg = bf>=0.0 ? cg+(maxV-cg)*bf : cg*(1.0+bf);
    cb = bf>=0.0 ? cb+(maxV-cb)*bf : cb*(1.0+bf);
    cr = ((cr/maxV-0.5)*contrast+0.5)*maxV;
    cg = ((cg/maxV-0.5)*contrast+0.5)*maxV;
    cb = ((cb/maxV-0.5)*contrast+0.5)*maxV;
    float gray = 0.2126*cr + 0.7152*cg + 0.0722*cb;
    cr = gray + saturation*(cr-gray);
    cg = gray + saturation*(cg-gray);
    cb = gray + saturation*(cb-gray);
    // Hue
    { float nr=cr/maxV, ng=cg/maxV, nb=cb/maxV;
      float cmx=fmax(fmax(nr,ng),nb), cmn=fmin(fmin(nr,ng),nb), delta=cmx-cmn;
      float hh=0.0, ss, ll;
      if (delta>0.0) { if(cmx==nr) hh=60.0*fmod((ng-nb)/delta,6.0); else if(cmx==ng) hh=60.0*((nb-nr)/delta+2.0); else hh=60.0*((nr-ng)/delta+4.0); if(hh<0.0) hh+=360.0; }
      ll=(cmx+cmn)*0.5; ss=(ll>0.0&&ll<1.0)?delta/(1.0-fabs(2.0*ll-1.0)):0.0;
      hh+=hue; if(hh<0.0) hh+=360.0; if(hh>=360.0) hh-=360.0;
      if(ss<0.000001) { cr=ll*maxV; cg=ll*maxV; cb=ll*maxV; }
      else { float qq=ll<0.5?ll*(1.0+ss):ll+ss-ll*ss; float p=2.0*ll-qq; float hN=hh/360.0;
        float Tr=hN+0.333333; if(Tr<0.0)Tr+=1.0; if(Tr>1.0)Tr-=1.0;
        float Tg=hN; if(Tg<0.0)Tg+=1.0; if(Tg>1.0)Tg-=1.0;
        float Tb=hN-0.333333; if(Tb<0.0)Tb+=1.0; if(Tb>1.0)Tb-=1.0;
        float h2r(float t) { return t<0.166667?p+(qq-p)*6.0*t:t<0.5?qq:t<0.666667?p+(qq-p)*(0.666667-t)*6.0:p; }
        cr=h2r(Tr)*maxV; cg=h2r(Tg)*maxV; cb=h2r(Tb)*maxV; }
    }
    float invG = 1.0 / fmax(gamma, 0.001);
    cr = maxV * pow(cr/maxV, invG);
    cg = maxV * pow(cg/maxV, invG);
    cb = maxV * pow(cb/maxV, invG);
    float vSat = 1.0 + vibrance * 0.5;
    float vGray = 0.2126*cr + 0.7152*cg + 0.0722*cb;
    cr = vGray + vSat*(cr-vGray); cg = vGray + vSat*(cg-vGray); cb = vGray + vSat*(cb-vGray);
    cr *= 1.0 + temperature * 0.01; cb *= 1.0 - temperature * 0.01;
    if (invertF > 0.5) { cr = maxV - cr; cg = maxV - cg; cb = maxV - cb; }
    float lum = 0.2126*cr + 0.7152*cg + 0.0722*cb;
    float gs = grayscale >= 1.0 ? 1.0 : 1.0 - grayscale;
    rO[id] = lum + gs*(cr - lum);
    gO[id] = lum + gs*(cg - lum);
    bO[id] = lum + gs*(cb - lum);
    aO[id] = ca * opacity;
}
";
    }

    #endregion

    #region MetalGpuEffectSession

    internal class MetalGpuEffectSession : IGpuEffectSession
    {
        public int Width { get; }
        public int Height { get; }
        public bool HasAlpha { get; }
        public int Length { get; }

        public IMTLBuffer CurBufR { get; private set; }
        public IMTLBuffer CurBufG { get; private set; }
        public IMTLBuffer CurBufB { get; private set; }
        public IMTLBuffer CurBufA { get; private set; }
        public IMTLBuffer AltBufR { get; private set; }
        public IMTLBuffer AltBufG { get; private set; }
        public IMTLBuffer AltBufB { get; private set; }
        public IMTLBuffer AltBufA { get; private set; }

        public MetalGpuEffectSession(float[] r, float[] g, float[] b, float[] a, int width, int height)
        {
            Width = width;
            Height = height;
            HasAlpha = a != null;
            Length = r.Length;

            CurBufR = MetalComputerHelper.CreateBuffer(r);
            CurBufG = MetalComputerHelper.CreateBuffer(g);
            CurBufB = MetalComputerHelper.CreateBuffer(b);
            CurBufA = MetalComputerHelper.CreateBuffer(a);

            AltBufR = MetalComputerHelper.AllocBuffer(Length);
            AltBufG = MetalComputerHelper.AllocBuffer(Length);
            AltBufB = MetalComputerHelper.AllocBuffer(Length);
            AltBufA = MetalComputerHelper.AllocBuffer(Length);
        }

        public void SwapBuffers()
        {
            (CurBufR, AltBufR) = (AltBufR, CurBufR);
            (CurBufG, AltBufG) = (AltBufG, CurBufG);
            (CurBufB, AltBufB) = (AltBufB, CurBufB);
            (CurBufA, AltBufA) = (AltBufA, CurBufA);
        }

        public (float[] r, float[] g, float[] b, float[] a) Download()
        {
            var rO = new float[Length]; Marshal.Copy(CurBufR.Contents, rO, 0, Length);
            var gO = new float[Length]; Marshal.Copy(CurBufG.Contents, gO, 0, Length);
            var bO = new float[Length]; Marshal.Copy(CurBufB.Contents, bO, 0, Length);
            var aO = new float[Length]; Marshal.Copy(CurBufA.Contents, aO, 0, Length);
            return (rO, gO, bO, aO);
        }

        public void Dispose() { }
    }

    #endregion

    #region ResizeComputer

    public class ResizeComputer : IResizeComputer, IComputer
    {
        public string FromPlugin => "projectFrameCut.iOS.MetalAccelerater.MetalComputers";
        public string SupportedEffectOrMixture => "Resize";

        private IMTLComputePipelineState? _pipelineState;

        private void InitializePipeline()
        {
            if (_pipelineState != null) return;
            var d = MetalComputerHelper.Device;
            var l = d.CreateLibrary(ShaderSrc, new MTLCompileOptions(), out NSError e);
            if (l == null) throw new Exception(e?.LocalizedDescription);
            _pipelineState = d.CreateComputePipelineState(l.CreateFunction("resize_compute"), out e);
            if (_pipelineState == null) throw new Exception(e?.LocalizedDescription);
        }

        // ========== 强类型接口 IResizeComputer ==========

        public FourChannelResult ComputeResizeFloat(float[] r, float[] g, float[] b, float[] a,
            float srcW, float srcH, float dstW, float dstH)
            => DoResize(r, g, b, a, (int)srcW, (int)srcH, (int)dstW, (int)dstH);

        public FourChannelResult8 ComputeResizeByte(float[] r, float[] g, float[] b, float[] a,
            float srcW, float srcH, float dstW, float dstH)
        {
            var fr = DoResize(r, g, b, a, (int)srcW, (int)srcH, (int)dstW, (int)dstH);
            int len = fr.R.Length;
            var r8 = new byte[len]; var g8 = new byte[len]; var b8 = new byte[len];
            for (int i = 0; i < len; i++)
            {
                r8[i] = (byte)Math.Clamp(fr.R[i] / 257f, 0f, 255f);
                g8[i] = (byte)Math.Clamp(fr.G[i] / 257f, 0f, 255f);
                b8[i] = (byte)Math.Clamp(fr.B[i] / 257f, 0f, 255f);
            }
            return new FourChannelResult8(r8, g8, b8, fr.A);
        }

        public FourChannelResult16 ComputeResizeUshort(float[] r, float[] g, float[] b, float[] a,
            float srcW, float srcH, float dstW, float dstH)
        {
            var fr = DoResize(r, g, b, a, (int)srcW, (int)srcH, (int)dstW, (int)dstH);
            int len = fr.R.Length;
            var r16 = new ushort[len]; var g16 = new ushort[len]; var b16 = new ushort[len];
            for (int i = 0; i < len; i++)
            {
                r16[i] = (ushort)Math.Clamp(fr.R[i], 0f, 65535f);
                g16[i] = (ushort)Math.Clamp(fr.G[i], 0f, 65535f);
                b16[i] = (ushort)Math.Clamp(fr.B[i], 0f, 65535f);
            }
            return new FourChannelResult16(r16, g16, b16, fr.A);
        }

        private unsafe FourChannelResult DoResize(float[] r, float[] g, float[] b, float[] a,
            int srcW, int srcH, int dstW, int dstH)
        {
            InitializePipeline();
            if (dstW <= 0 || dstH <= 0) return new FourChannelResult([], [], [], []);
            int dstLen = dstW * dstH;
            float ratioX = (float)srcW / dstW;
            float ratioY = (float)srcH / dstH;

            var rB = MetalComputerHelper.CreateBuffer(r);
            var gB = MetalComputerHelper.CreateBuffer(g);
            var bB = MetalComputerHelper.CreateBuffer(b);
            var aB = MetalComputerHelper.CreateBuffer(a);
            var rOB = MetalComputerHelper.AllocBuffer(dstLen);
            var gOB = MetalComputerHelper.AllocBuffer(dstLen);
            var bOB = MetalComputerHelper.AllocBuffer(dstLen);
            var aOB = MetalComputerHelper.AllocBuffer(dstLen);

            var (cb, enc) = MetalComputerHelper.CreateCommandEncoder();
            enc.SetComputePipelineState(_pipelineState!);
            enc.SetBuffer(rB, 0, 0); enc.SetBuffer(gB, 0, 1);
            enc.SetBuffer(bB, 0, 2); enc.SetBuffer(aB, 0, 3);
            enc.SetBuffer(rOB, 0, 4); enc.SetBuffer(gOB, 0, 5);
            enc.SetBuffer(bOB, 0, 6); enc.SetBuffer(aOB, 0, 7);
            enc.SetBytes((IntPtr)(&dstW), (nuint)sizeof(int), 8);
            enc.SetBytes((IntPtr)(&srcW), (nuint)sizeof(int), 9);
            enc.SetBytes((IntPtr)(&srcH), (nuint)sizeof(int), 10);
            enc.SetBytes((IntPtr)(&ratioX), (nuint)sizeof(float), 11);
            enc.SetBytes((IntPtr)(&ratioY), (nuint)sizeof(float), 12);

            var (tgs, tg) = MetalComputerHelper.ComputeDispatchSizes(dstLen, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup);
            enc.DispatchThreadgroups(tg, tgs);
            enc.EndEncoding(); cb.Commit(); cb.WaitUntilCompleted();

            var rO = new float[dstLen]; Marshal.Copy(rOB.Contents, rO, 0, dstLen);
            var gO = new float[dstLen]; Marshal.Copy(gOB.Contents, gO, 0, dstLen);
            var bO = new float[dstLen]; Marshal.Copy(bOB.Contents, bO, 0, dstLen);
            var aO = new float[dstLen]; Marshal.Copy(aOB.Contents, aO, 0, dstLen);
            return new FourChannelResult(rO, gO, bO, aO);
        }

        // ========== 后向兼容 IComputer ==========

        public unsafe object[] Compute(object[] args)
        {
            var rIn = (float[])args[0]; var gIn = (float[])args[1];
            var bIn = (float[])args[2]; var aIn = (float[])args[3];
            var srcW = (float)args[4]; var srcH = (float)args[5];
            var dstW = (float)args[6]; var dstH = (float)args[7];
            bool wantByte = args.Length > 8 && args[8] is int pt && pt == 8;
            bool wantUShort = args.Length > 8 && args[8] is int pt2 && pt2 == 16;

            if (wantByte)
            {
                var r = ComputeResizeByte(rIn, gIn, bIn, aIn, srcW, srcH, dstW, dstH);
                return [r.R, r.G, r.B, r.A];
            }
            if (wantUShort)
            {
                var r = ComputeResizeUshort(rIn, gIn, bIn, aIn, srcW, srcH, dstW, dstH);
                return [r.R, r.G, r.B, r.A];
            }
            var rf = ComputeResizeFloat(rIn, gIn, bIn, aIn, srcW, srcH, dstW, dstH);
            return [rf.R, rf.G, rf.B, rf.A];
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
    int srcX = int(float(x) * ratioX); int srcY = int(float(y) * ratioY);
    srcX = clamp(srcX, 0, srcW - 1); srcY = clamp(srcY, 0, srcH - 1);
    int si = srcY * srcW + srcX;
    rOut[id] = rIn[si]; gOut[id] = gIn[si]; bOut[id] = bIn[si]; aOut[id] = aIn[si];
}
";
    }

    #endregion

    #region CropComputer

    public class CropComputer : ICropComputer, IComputer
    {
        public string FromPlugin => "projectFrameCut.iOS.MetalAccelerater.MetalComputers";
        public string SupportedEffectOrMixture => "Crop";

        private IMTLComputePipelineState? _pipelineState;

        private void InitializePipeline()
        {
            if (_pipelineState != null) return;
            var d = MetalComputerHelper.Device;
            var l = d.CreateLibrary(ShaderSrc, new MTLCompileOptions(), out NSError e);
            if (l == null) throw new Exception(e?.LocalizedDescription);
            _pipelineState = d.CreateComputePipelineState(l.CreateFunction("crop_compute"), out e);
            if (_pipelineState == null) throw new Exception(e?.LocalizedDescription);
        }

        // ========== 强类型接口 ICropComputer ==========

        public unsafe FourChannelResult ComputeCrop(float[] r, float[] g, float[] b, float[] a,
            int srcW, int srcH, int startX, int startY, int cropW, int cropH)
        {
            InitializePipeline();
            if (cropW <= 0 || cropH <= 0) return new FourChannelResult([], [], [], []);
            int dstLen = cropW * cropH;

            var rB = MetalComputerHelper.CreateBuffer(r);
            var gB = MetalComputerHelper.CreateBuffer(g);
            var bB = MetalComputerHelper.CreateBuffer(b);
            var aB = MetalComputerHelper.CreateBuffer(a);
            var rOB = MetalComputerHelper.AllocBuffer(dstLen);
            var gOB = MetalComputerHelper.AllocBuffer(dstLen);
            var bOB = MetalComputerHelper.AllocBuffer(dstLen);
            var aOB = MetalComputerHelper.AllocBuffer(dstLen);

            var (cb, enc) = MetalComputerHelper.CreateCommandEncoder();
            enc.SetComputePipelineState(_pipelineState!);
            enc.SetBuffer(rB, 0, 0); enc.SetBuffer(gB, 0, 1);
            enc.SetBuffer(bB, 0, 2); enc.SetBuffer(aB, 0, 3);
            enc.SetBuffer(rOB, 0, 4); enc.SetBuffer(gOB, 0, 5);
            enc.SetBuffer(bOB, 0, 6); enc.SetBuffer(aOB, 0, 7);
            enc.SetBytes((IntPtr)(&cropW), (nuint)sizeof(int), 8);
            enc.SetBytes((IntPtr)(&srcW), (nuint)sizeof(int), 9);
            enc.SetBytes((IntPtr)(&srcH), (nuint)sizeof(int), 10);
            enc.SetBytes((IntPtr)(&startX), (nuint)sizeof(int), 11);
            enc.SetBytes((IntPtr)(&startY), (nuint)sizeof(int), 12);

            var (tgs, tg) = MetalComputerHelper.ComputeDispatchSizes(dstLen, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup);
            enc.DispatchThreadgroups(tg, tgs);
            enc.EndEncoding(); cb.Commit(); cb.WaitUntilCompleted();

            var rO = new float[dstLen]; Marshal.Copy(rOB.Contents, rO, 0, dstLen);
            var gO = new float[dstLen]; Marshal.Copy(gOB.Contents, gO, 0, dstLen);
            var bO = new float[dstLen]; Marshal.Copy(bOB.Contents, bO, 0, dstLen);
            var aO = new float[dstLen]; Marshal.Copy(aOB.Contents, aO, 0, dstLen);
            return new FourChannelResult(rO, gO, bO, aO);
        }

        // ========== 后向兼容 IComputer ==========

        public unsafe object[] Compute(object[] args)
        {
            var rIn = (float[])args[0]; var gIn = (float[])args[1];
            var bIn = (float[])args[2]; var aIn = (float[])args[3];
            int srcW = Convert.ToInt32(args[4]), srcH = Convert.ToInt32(args[5]);
            int startX = Convert.ToInt32(args[6]), startY = Convert.ToInt32(args[7]);
            int cropW = Convert.ToInt32(args[8]), cropH = Convert.ToInt32(args[9]);
            var result = ComputeCrop(rIn, gIn, bIn, aIn, srcW, srcH, startX, startY, cropW, cropH);
            return [result.R, result.G, result.B, result.A];
        }

        private const string ShaderSrc = @"
#include <metal_stdlib>
using namespace metal;
kernel void crop_compute(
    device const float* rIn [[buffer(0)]], device const float* gIn [[buffer(1)]],
    device const float* bIn [[buffer(2)]], device const float* aIn [[buffer(3)]],
    device float* rOut [[buffer(4)]], device float* gOut [[buffer(5)]],
    device float* bOut [[buffer(6)]], device float* aOut [[buffer(7)]],
    constant int& cropW [[buffer(8)]], constant int& srcW [[buffer(9)]],
    constant int& srcH [[buffer(10)]],
    constant int& startX [[buffer(11)]], constant int& startY [[buffer(12)]],
    uint id [[thread_position_in_grid]])
{
    int x = int(id % uint(cropW)); int y = int(id / uint(cropW));
    int sx = startX + x; int sy = startY + y;
    if (sx >= 0 && sx < srcW && sy >= 0 && sy < srcH) {
        int si = sy * srcW + sx;
        rOut[id] = rIn[si]; gOut[id] = gIn[si]; bOut[id] = bIn[si]; aOut[id] = aIn[si];
    } else { rOut[id]=0.0; gOut[id]=0.0; bOut[id]=0.0; aOut[id]=0.0; }
}
";
    }

    #endregion

    #region PlaceComputer

    public class PlaceComputer : IPlaceComputer, IComputer
    {
        public string FromPlugin => "projectFrameCut.iOS.MetalAccelerater.MetalComputers";
        public string SupportedEffectOrMixture => "Place";

        private IMTLComputePipelineState? _pipelineState;

        private void InitializePipeline()
        {
            if (_pipelineState != null) return;
            var d = MetalComputerHelper.Device;
            var l = d.CreateLibrary(ShaderSrc, new MTLCompileOptions(), out NSError e);
            if (l == null) throw new Exception(e?.LocalizedDescription);
            _pipelineState = d.CreateComputePipelineState(l.CreateFunction("place_compute"), out e);
            if (_pipelineState == null) throw new Exception(e?.LocalizedDescription);
        }

        // ========== 强类型接口 IPlaceComputer ==========

        public unsafe FourChannelResult ComputePlace(float[] r, float[] g, float[] b, float[] a,
            int srcW, int srcH, int startX, int startY, int targetW, int targetH)
        {
            InitializePipeline();
            if (targetW <= 0 || targetH <= 0) return new FourChannelResult([], [], [], []);
            int dstLen = targetW * targetH;

            var rB = MetalComputerHelper.CreateBuffer(r);
            var gB = MetalComputerHelper.CreateBuffer(g);
            var bB = MetalComputerHelper.CreateBuffer(b);
            var aB = MetalComputerHelper.CreateBuffer(a);
            var rOB = MetalComputerHelper.AllocBuffer(dstLen);
            var gOB = MetalComputerHelper.AllocBuffer(dstLen);
            var bOB = MetalComputerHelper.AllocBuffer(dstLen);
            var aOB = MetalComputerHelper.AllocBuffer(dstLen);

            var (cb, enc) = MetalComputerHelper.CreateCommandEncoder();
            enc.SetComputePipelineState(_pipelineState!);
            enc.SetBuffer(rB, 0, 0); enc.SetBuffer(gB, 0, 1);
            enc.SetBuffer(bB, 0, 2); enc.SetBuffer(aB, 0, 3);
            enc.SetBuffer(rOB, 0, 4); enc.SetBuffer(gOB, 0, 5);
            enc.SetBuffer(bOB, 0, 6); enc.SetBuffer(aOB, 0, 7);
            enc.SetBytes((IntPtr)(&targetW), (nuint)sizeof(int), 8);
            enc.SetBytes((IntPtr)(&srcW), (nuint)sizeof(int), 9);
            enc.SetBytes((IntPtr)(&srcH), (nuint)sizeof(int), 10);
            enc.SetBytes((IntPtr)(&startX), (nuint)sizeof(int), 11);
            enc.SetBytes((IntPtr)(&startY), (nuint)sizeof(int), 12);

            var (tgs, tg) = MetalComputerHelper.ComputeDispatchSizes(dstLen, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup);
            enc.DispatchThreadgroups(tg, tgs);
            enc.EndEncoding(); cb.Commit(); cb.WaitUntilCompleted();

            var rO = new float[dstLen]; Marshal.Copy(rOB.Contents, rO, 0, dstLen);
            var gO = new float[dstLen]; Marshal.Copy(gOB.Contents, gO, 0, dstLen);
            var bO = new float[dstLen]; Marshal.Copy(bOB.Contents, bO, 0, dstLen);
            var aO = new float[dstLen]; Marshal.Copy(aOB.Contents, aO, 0, dstLen);
            return new FourChannelResult(rO, gO, bO, aO);
        }

        // ========== 后向兼容 IComputer ==========

        public unsafe object[] Compute(object[] args)
        {
            var rIn = (float[])args[0]; var gIn = (float[])args[1];
            var bIn = (float[])args[2]; var aIn = (float[])args[3];
            int srcW = Convert.ToInt32(args[4]), srcH = Convert.ToInt32(args[5]);
            int startX = Convert.ToInt32(args[6]), startY = Convert.ToInt32(args[7]);
            int targetW = Convert.ToInt32(args[8]), targetH = Convert.ToInt32(args[9]);
            var result = ComputePlace(rIn, gIn, bIn, aIn, srcW, srcH, startX, startY, targetW, targetH);
            return [result.R, result.G, result.B, result.A];
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
    }

    #endregion

    #region Blend Metal Helper

    internal static class BlendMetalHelper
    {
        public static BlendResult16 ComputeBlend(
            IMTLComputePipelineState pipeline,
            float[] top, float[] bottom,
            float[] topAlpha, float[] bottomAlpha,
            int pixelCount)
        {
            var topBuf = MetalComputerHelper.CreateBuffer(top);
            var bottomBuf = MetalComputerHelper.CreateBuffer(bottom);
            var tAlphaBuf = MetalComputerHelper.CreateBuffer(topAlpha);
            var bAlphaBuf = MetalComputerHelper.CreateBuffer(bottomAlpha);
            var outCBuf = MetalComputerHelper.AllocBuffer(pixelCount);
            var outABuf = MetalComputerHelper.AllocBuffer(pixelCount);

            var (cb, enc) = MetalComputerHelper.CreateCommandEncoder();
            enc.SetComputePipelineState(pipeline);
            enc.SetBuffer(topBuf, 0, 0);
            enc.SetBuffer(bottomBuf, 0, 1);
            enc.SetBuffer(tAlphaBuf, 0, 2);
            enc.SetBuffer(bAlphaBuf, 0, 3);
            enc.SetBuffer(outCBuf, 0, 4);
            enc.SetBuffer(outABuf, 0, 5);

            var (tgs, tg) = MetalComputerHelper.ComputeDispatchSizes(pixelCount, (int)pipeline.MaxTotalThreadsPerThreadgroup);
            enc.DispatchThreadgroups(tg, tgs);
            enc.EndEncoding(); cb.Commit(); cb.WaitUntilCompleted();

            // GPU outputs float; Marshal.Copy lacks ushort[] overload — read as float then convert
            var floatOut = new float[pixelCount];
            Marshal.Copy(outCBuf.Contents, floatOut, 0, pixelCount);
            var ushortOut = new ushort[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                float v = floatOut[i];
                if (v < 0f) v = 0f;
                if (v > 65535f) v = 65535f;
                ushortOut[i] = (ushort)v;
            }

            var alphaOut = new float[pixelCount];
            Marshal.Copy(outABuf.Contents, alphaOut, 0, pixelCount);
            return new BlendResult16(ushortOut, alphaOut);
        }
    }

    #endregion

    #region BlendAddComputer

    public class BlendAddComputer : IBlendModeComputer, IComputer
    {
        public string FromPlugin => "projectFrameCut.iOS.MetalAccelerater.MetalComputers";
        public string SupportedEffectOrMixture => "AddComputer";

        private IMTLComputePipelineState? _pipelineState;

        private void InitializePipeline()
        {
            if (_pipelineState != null) return;
            var d = MetalComputerHelper.Device;
            var l = d.CreateLibrary(ShaderSrc, new MTLCompileOptions(), out NSError e);
            if (l == null) throw new Exception(e?.LocalizedDescription);
            _pipelineState = d.CreateComputePipelineState(l.CreateFunction("blend_add"), out e);
            if (_pipelineState == null) throw new Exception(e?.LocalizedDescription);
        }

        public BlendResult16 ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            InitializePipeline();
            return BlendMetalHelper.ComputeBlend(_pipelineState!, top, bottom, topAlpha, bottomAlpha, pixelCount);
        }

        public object[] Compute(object[] args)
        {
            var A = (float[])args[0]; var B = (float[])args[1];
            var aAlpha = args.Length > 2 ? (args[2] as float[]) : null;
            var bAlpha = args.Length > 3 ? (args[3] as float[]) : null;
            var outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            var pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : A.Length;
            if (aAlpha == null) { aAlpha = new float[pixelCount]; Array.Fill(aAlpha, 1f); }
            if (bAlpha == null) { bAlpha = new float[pixelCount]; Array.Fill(bAlpha, 1f); }
            var result = ComputeBlend(A, B, aAlpha, bAlpha, pixelCount);
            if (outputBpp == 8)
            {
                var bOut = new byte[pixelCount];
                for (int i = 0; i < pixelCount; i++) { float v = result.Color[i] / 257f; bOut[i] = (byte)Math.Clamp(v, 0f, 255f); }
                return [bOut, result.Alpha];
            }
            if (outputBpp == 16)
                return [result.Color, result.Alpha];
            var floatOut = new float[pixelCount];
            for (int i = 0; i < pixelCount; i++) floatOut[i] = result.Color[i];
            return [floatOut, result.Alpha];
        }

        private const string ShaderSrc = @"
#include <metal_stdlib>
using namespace metal;
kernel void blend_add(
    device const float* top [[buffer(0)]], device const float* bottom [[buffer(1)]],
    device const float* topAlpha [[buffer(2)]], device const float* bottomAlpha [[buffer(3)]],
    device float* outC [[buffer(4)]], device float* outA [[buffer(5)]],
    uint id [[thread_position_in_grid]])
{
    float aA = topAlpha[id]; float bA = bottomAlpha[id];
    float outAlpha = aA + bA * (1.0 - aA);
    if (outAlpha < 1e-6) { outC[id] = 0.0; outA[id] = 0.0; }
    else {
        float blended = min(top[id] + bottom[id], 65535.0);
        float result = (blended * aA + bottom[id] * bA * (1.0 - aA)) / outAlpha;
        outC[id] = clamp(result, 0.0, 65535.0); outA[id] = outAlpha;
    }
}
";
    }

    #endregion

    #region BlendSubtractComputer

    public class BlendSubtractComputer : IBlendModeComputer, IComputer
    {
        public string FromPlugin => "projectFrameCut.iOS.MetalAccelerater.MetalComputers";
        public string SupportedEffectOrMixture => "SubtractComputer";

        private IMTLComputePipelineState? _pipelineState;

        private void InitializePipeline()
        {
            if (_pipelineState != null) return;
            var d = MetalComputerHelper.Device;
            var l = d.CreateLibrary(ShaderSrc, new MTLCompileOptions(), out NSError e);
            if (l == null) throw new Exception(e?.LocalizedDescription);
            _pipelineState = d.CreateComputePipelineState(l.CreateFunction("blend_subtract"), out e);
            if (_pipelineState == null) throw new Exception(e?.LocalizedDescription);
        }

        public BlendResult16 ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            InitializePipeline();
            return BlendMetalHelper.ComputeBlend(_pipelineState!, top, bottom, topAlpha, bottomAlpha, pixelCount);
        }

        public object[] Compute(object[] args)
        {
            var A = (float[])args[0]; var B = (float[])args[1];
            var aAlpha = args.Length > 2 ? (args[2] as float[]) : null;
            var bAlpha = args.Length > 3 ? (args[3] as float[]) : null;
            var outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            var pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : A.Length;
            if (aAlpha == null) { aAlpha = new float[pixelCount]; Array.Fill(aAlpha, 1f); }
            if (bAlpha == null) { bAlpha = new float[pixelCount]; Array.Fill(bAlpha, 1f); }
            var result = ComputeBlend(A, B, aAlpha, bAlpha, pixelCount);
            if (outputBpp == 8)
            {
                var bOut = new byte[pixelCount];
                for (int i = 0; i < pixelCount; i++) { float v = result.Color[i] / 257f; bOut[i] = (byte)Math.Clamp(v, 0f, 255f); }
                return [bOut, result.Alpha];
            }
            if (outputBpp == 16)
                return [result.Color, result.Alpha];
            var floatOut = new float[pixelCount];
            for (int i = 0; i < pixelCount; i++) floatOut[i] = result.Color[i];
            return [floatOut, result.Alpha];
        }

        private const string ShaderSrc = @"
#include <metal_stdlib>
using namespace metal;
kernel void blend_subtract(
    device const float* top [[buffer(0)]], device const float* bottom [[buffer(1)]],
    device const float* topAlpha [[buffer(2)]], device const float* bottomAlpha [[buffer(3)]],
    device float* outC [[buffer(4)]], device float* outA [[buffer(5)]],
    uint id [[thread_position_in_grid]])
{
    float aA = topAlpha[id]; float bA = bottomAlpha[id];
    float outAlpha = aA + bA * (1.0 - aA);
    if (outAlpha < 1e-6) { outC[id] = 0.0; outA[id] = 0.0; }
    else {
        float blended = max(bottom[id] - top[id], 0.0);
        float result = (blended * aA + bottom[id] * bA * (1.0 - aA)) / outAlpha;
        outC[id] = clamp(result, 0.0, 65535.0); outA[id] = outAlpha;
    }
}
";
    }

    #endregion

    #region BlendMultiplyComputer

    public class BlendMultiplyComputer : IBlendModeComputer, IComputer
    {
        public string FromPlugin => "projectFrameCut.iOS.MetalAccelerater.MetalComputers";
        public string SupportedEffectOrMixture => "MultiplyComputer";

        private IMTLComputePipelineState? _pipelineState;

        private void InitializePipeline()
        {
            if (_pipelineState != null) return;
            var d = MetalComputerHelper.Device;
            var l = d.CreateLibrary(ShaderSrc, new MTLCompileOptions(), out NSError e);
            if (l == null) throw new Exception(e?.LocalizedDescription);
            _pipelineState = d.CreateComputePipelineState(l.CreateFunction("blend_multiply"), out e);
            if (_pipelineState == null) throw new Exception(e?.LocalizedDescription);
        }

        public BlendResult16 ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            InitializePipeline();
            return BlendMetalHelper.ComputeBlend(_pipelineState!, top, bottom, topAlpha, bottomAlpha, pixelCount);
        }

        public object[] Compute(object[] args)
        {
            var A = (float[])args[0]; var B = (float[])args[1];
            var aAlpha = args.Length > 2 ? (args[2] as float[]) : null;
            var bAlpha = args.Length > 3 ? (args[3] as float[]) : null;
            var outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            var pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : A.Length;
            if (aAlpha == null) { aAlpha = new float[pixelCount]; Array.Fill(aAlpha, 1f); }
            if (bAlpha == null) { bAlpha = new float[pixelCount]; Array.Fill(bAlpha, 1f); }
            var result = ComputeBlend(A, B, aAlpha, bAlpha, pixelCount);
            if (outputBpp == 8)
            {
                var bOut = new byte[pixelCount];
                for (int i = 0; i < pixelCount; i++) { float v = result.Color[i] / 257f; bOut[i] = (byte)Math.Clamp(v, 0f, 255f); }
                return [bOut, result.Alpha];
            }
            if (outputBpp == 16)
                return [result.Color, result.Alpha];
            var floatOut = new float[pixelCount];
            for (int i = 0; i < pixelCount; i++) floatOut[i] = result.Color[i];
            return [floatOut, result.Alpha];
        }

        private const string ShaderSrc = @"
#include <metal_stdlib>
using namespace metal;
kernel void blend_multiply(
    device const float* top [[buffer(0)]], device const float* bottom [[buffer(1)]],
    device const float* topAlpha [[buffer(2)]], device const float* bottomAlpha [[buffer(3)]],
    device float* outC [[buffer(4)]], device float* outA [[buffer(5)]],
    uint id [[thread_position_in_grid]])
{
    float aA = topAlpha[id]; float bA = bottomAlpha[id];
    float outAlpha = aA + bA * (1.0 - aA);
    if (outAlpha < 1e-6) { outC[id] = 0.0; outA[id] = 0.0; }
    else {
        float blended = top[id] * bottom[id] / 65535.0;
        float result = (blended * aA + bottom[id] * bA * (1.0 - aA)) / outAlpha;
        outC[id] = clamp(result, 0.0, 65535.0); outA[id] = outAlpha;
    }
}
";
    }

    #endregion

    #region BlendScreenComputer

    public class BlendScreenComputer : IBlendModeComputer, IComputer
    {
        public string FromPlugin => "projectFrameCut.iOS.MetalAccelerater.MetalComputers";
        public string SupportedEffectOrMixture => "ScreenComputer";

        private IMTLComputePipelineState? _pipelineState;

        private void InitializePipeline()
        {
            if (_pipelineState != null) return;
            var d = MetalComputerHelper.Device;
            var l = d.CreateLibrary(ShaderSrc, new MTLCompileOptions(), out NSError e);
            if (l == null) throw new Exception(e?.LocalizedDescription);
            _pipelineState = d.CreateComputePipelineState(l.CreateFunction("blend_screen"), out e);
            if (_pipelineState == null) throw new Exception(e?.LocalizedDescription);
        }

        public BlendResult16 ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            InitializePipeline();
            return BlendMetalHelper.ComputeBlend(_pipelineState!, top, bottom, topAlpha, bottomAlpha, pixelCount);
        }

        public object[] Compute(object[] args)
        {
            var A = (float[])args[0]; var B = (float[])args[1];
            var aAlpha = args.Length > 2 ? (args[2] as float[]) : null;
            var bAlpha = args.Length > 3 ? (args[3] as float[]) : null;
            var outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            var pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : A.Length;
            if (aAlpha == null) { aAlpha = new float[pixelCount]; Array.Fill(aAlpha, 1f); }
            if (bAlpha == null) { bAlpha = new float[pixelCount]; Array.Fill(bAlpha, 1f); }
            var result = ComputeBlend(A, B, aAlpha, bAlpha, pixelCount);
            if (outputBpp == 8)
            {
                var bOut = new byte[pixelCount];
                for (int i = 0; i < pixelCount; i++) { float v = result.Color[i] / 257f; bOut[i] = (byte)Math.Clamp(v, 0f, 255f); }
                return [bOut, result.Alpha];
            }
            if (outputBpp == 16)
                return [result.Color, result.Alpha];
            var floatOut = new float[pixelCount];
            for (int i = 0; i < pixelCount; i++) floatOut[i] = result.Color[i];
            return [floatOut, result.Alpha];
        }

        private const string ShaderSrc = @"
#include <metal_stdlib>
using namespace metal;
kernel void blend_screen(
    device const float* top [[buffer(0)]], device const float* bottom [[buffer(1)]],
    device const float* topAlpha [[buffer(2)]], device const float* bottomAlpha [[buffer(3)]],
    device float* outC [[buffer(4)]], device float* outA [[buffer(5)]],
    uint id [[thread_position_in_grid]])
{
    float aA = topAlpha[id]; float bA = bottomAlpha[id];
    float outAlpha = aA + bA * (1.0 - aA);
    if (outAlpha < 1e-6) { outC[id] = 0.0; outA[id] = 0.0; }
    else {
        float blended = 65535.0 - (65535.0 - top[id]) * (65535.0 - bottom[id]) / 65535.0;
        float result = (blended * aA + bottom[id] * bA * (1.0 - aA)) / outAlpha;
        outC[id] = clamp(result, 0.0, 65535.0); outA[id] = outAlpha;
    }
}
";
    }

    #endregion

    #region BlendOverlayBlendComputer

    public class BlendOverlayBlendComputer : IBlendModeComputer, IComputer
    {
        public string FromPlugin => "projectFrameCut.iOS.MetalAccelerater.MetalComputers";
        public string SupportedEffectOrMixture => "OverlayBlendComputer";

        private IMTLComputePipelineState? _pipelineState;

        private void InitializePipeline()
        {
            if (_pipelineState != null) return;
            var d = MetalComputerHelper.Device;
            var l = d.CreateLibrary(ShaderSrc, new MTLCompileOptions(), out NSError e);
            if (l == null) throw new Exception(e?.LocalizedDescription);
            _pipelineState = d.CreateComputePipelineState(l.CreateFunction("blend_overlayblend"), out e);
            if (_pipelineState == null) throw new Exception(e?.LocalizedDescription);
        }

        public BlendResult16 ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            InitializePipeline();
            return BlendMetalHelper.ComputeBlend(_pipelineState!, top, bottom, topAlpha, bottomAlpha, pixelCount);
        }

        public object[] Compute(object[] args)
        {
            var A = (float[])args[0]; var B = (float[])args[1];
            var aAlpha = args.Length > 2 ? (args[2] as float[]) : null;
            var bAlpha = args.Length > 3 ? (args[3] as float[]) : null;
            var outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            var pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : A.Length;
            if (aAlpha == null) { aAlpha = new float[pixelCount]; Array.Fill(aAlpha, 1f); }
            if (bAlpha == null) { bAlpha = new float[pixelCount]; Array.Fill(bAlpha, 1f); }
            var result = ComputeBlend(A, B, aAlpha, bAlpha, pixelCount);
            if (outputBpp == 8)
            {
                var bOut = new byte[pixelCount];
                for (int i = 0; i < pixelCount; i++) { float v = result.Color[i] / 257f; bOut[i] = (byte)Math.Clamp(v, 0f, 255f); }
                return [bOut, result.Alpha];
            }
            if (outputBpp == 16)
                return [result.Color, result.Alpha];
            var floatOut = new float[pixelCount];
            for (int i = 0; i < pixelCount; i++) floatOut[i] = result.Color[i];
            return [floatOut, result.Alpha];
        }

        private const string ShaderSrc = @"
#include <metal_stdlib>
using namespace metal;
kernel void blend_overlayblend(
    device const float* top [[buffer(0)]], device const float* bottom [[buffer(1)]],
    device const float* topAlpha [[buffer(2)]], device const float* bottomAlpha [[buffer(3)]],
    device float* outC [[buffer(4)]], device float* outA [[buffer(5)]],
    uint id [[thread_position_in_grid]])
{
    float aA = topAlpha[id]; float bA = bottomAlpha[id];
    float outAlpha = aA + bA * (1.0 - aA);
    if (outAlpha < 1e-6) { outC[id] = 0.0; outA[id] = 0.0; }
    else {
        float blended;
        if (bottom[id] < 32768.0)
            blended = 2.0 * top[id] * bottom[id] / 65535.0;
        else
            blended = 65535.0 - 2.0 * (65535.0 - top[id]) * (65535.0 - bottom[id]) / 65535.0;
        float result = (blended * aA + bottom[id] * bA * (1.0 - aA)) / outAlpha;
        outC[id] = clamp(result, 0.0, 65535.0); outA[id] = outAlpha;
    }
}
";
    }

    #endregion

    #region BlendDarkenComputer

    public class BlendDarkenComputer : IBlendModeComputer, IComputer
    {
        public string FromPlugin => "projectFrameCut.iOS.MetalAccelerater.MetalComputers";
        public string SupportedEffectOrMixture => "DarkenComputer";

        private IMTLComputePipelineState? _pipelineState;

        private void InitializePipeline()
        {
            if (_pipelineState != null) return;
            var d = MetalComputerHelper.Device;
            var l = d.CreateLibrary(ShaderSrc, new MTLCompileOptions(), out NSError e);
            if (l == null) throw new Exception(e?.LocalizedDescription);
            _pipelineState = d.CreateComputePipelineState(l.CreateFunction("blend_darken"), out e);
            if (_pipelineState == null) throw new Exception(e?.LocalizedDescription);
        }

        public BlendResult16 ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            InitializePipeline();
            return BlendMetalHelper.ComputeBlend(_pipelineState!, top, bottom, topAlpha, bottomAlpha, pixelCount);
        }

        public object[] Compute(object[] args)
        {
            var A = (float[])args[0]; var B = (float[])args[1];
            var aAlpha = args.Length > 2 ? (args[2] as float[]) : null;
            var bAlpha = args.Length > 3 ? (args[3] as float[]) : null;
            var outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            var pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : A.Length;
            if (aAlpha == null) { aAlpha = new float[pixelCount]; Array.Fill(aAlpha, 1f); }
            if (bAlpha == null) { bAlpha = new float[pixelCount]; Array.Fill(bAlpha, 1f); }
            var result = ComputeBlend(A, B, aAlpha, bAlpha, pixelCount);
            if (outputBpp == 8)
            {
                var bOut = new byte[pixelCount];
                for (int i = 0; i < pixelCount; i++) { float v = result.Color[i] / 257f; bOut[i] = (byte)Math.Clamp(v, 0f, 255f); }
                return [bOut, result.Alpha];
            }
            if (outputBpp == 16)
                return [result.Color, result.Alpha];
            var floatOut = new float[pixelCount];
            for (int i = 0; i < pixelCount; i++) floatOut[i] = result.Color[i];
            return [floatOut, result.Alpha];
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
    }

    #endregion

    #region BlendLightenComputer

    public class BlendLightenComputer : IBlendModeComputer, IComputer
    {
        public string FromPlugin => "projectFrameCut.iOS.MetalAccelerater.MetalComputers";
        public string SupportedEffectOrMixture => "LightenComputer";

        private IMTLComputePipelineState? _pipelineState;

        private void InitializePipeline()
        {
            if (_pipelineState != null) return;
            var d = MetalComputerHelper.Device;
            var l = d.CreateLibrary(ShaderSrc, new MTLCompileOptions(), out NSError e);
            if (l == null) throw new Exception(e?.LocalizedDescription);
            _pipelineState = d.CreateComputePipelineState(l.CreateFunction("blend_lighten"), out e);
            if (_pipelineState == null) throw new Exception(e?.LocalizedDescription);
        }

        public BlendResult16 ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            InitializePipeline();
            return BlendMetalHelper.ComputeBlend(_pipelineState!, top, bottom, topAlpha, bottomAlpha, pixelCount);
        }

        public object[] Compute(object[] args)
        {
            var A = (float[])args[0]; var B = (float[])args[1];
            var aAlpha = args.Length > 2 ? (args[2] as float[]) : null;
            var bAlpha = args.Length > 3 ? (args[3] as float[]) : null;
            var outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            var pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : A.Length;
            if (aAlpha == null) { aAlpha = new float[pixelCount]; Array.Fill(aAlpha, 1f); }
            if (bAlpha == null) { bAlpha = new float[pixelCount]; Array.Fill(bAlpha, 1f); }
            var result = ComputeBlend(A, B, aAlpha, bAlpha, pixelCount);
            if (outputBpp == 8)
            {
                var bOut = new byte[pixelCount];
                for (int i = 0; i < pixelCount; i++) { float v = result.Color[i] / 257f; bOut[i] = (byte)Math.Clamp(v, 0f, 255f); }
                return [bOut, result.Alpha];
            }
            if (outputBpp == 16)
                return [result.Color, result.Alpha];
            var floatOut = new float[pixelCount];
            for (int i = 0; i < pixelCount; i++) floatOut[i] = result.Color[i];
            return [floatOut, result.Alpha];
        }

        private const string ShaderSrc = @"
#include <metal_stdlib>
using namespace metal;
kernel void blend_lighten(
    device const float* top [[buffer(0)]], device const float* bottom [[buffer(1)]],
    device const float* topAlpha [[buffer(2)]], device const float* bottomAlpha [[buffer(3)]],
    device float* outC [[buffer(4)]], device float* outA [[buffer(5)]],
    uint id [[thread_position_in_grid]])
{
    float aA = topAlpha[id]; float bA = bottomAlpha[id];
    float outAlpha = aA + bA * (1.0 - aA);
    if (outAlpha < 1e-6) { outC[id] = 0.0; outA[id] = 0.0; }
    else {
        float blended = max(top[id], bottom[id]);
        float result = (blended * aA + bottom[id] * bA * (1.0 - aA)) / outAlpha;
        outC[id] = clamp(result, 0.0, 65535.0); outA[id] = outAlpha;
    }
}
";
    }

    #endregion

    #region BlendDifferenceComputer

    public class BlendDifferenceComputer : IBlendModeComputer, IComputer
    {
        public string FromPlugin => "projectFrameCut.iOS.MetalAccelerater.MetalComputers";
        public string SupportedEffectOrMixture => "DifferenceComputer";

        private IMTLComputePipelineState? _pipelineState;

        private void InitializePipeline()
        {
            if (_pipelineState != null) return;
            var d = MetalComputerHelper.Device;
            var l = d.CreateLibrary(ShaderSrc, new MTLCompileOptions(), out NSError e);
            if (l == null) throw new Exception(e?.LocalizedDescription);
            _pipelineState = d.CreateComputePipelineState(l.CreateFunction("blend_difference"), out e);
            if (_pipelineState == null) throw new Exception(e?.LocalizedDescription);
        }

        public BlendResult16 ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            InitializePipeline();
            return BlendMetalHelper.ComputeBlend(_pipelineState!, top, bottom, topAlpha, bottomAlpha, pixelCount);
        }

        public object[] Compute(object[] args)
        {
            var A = (float[])args[0]; var B = (float[])args[1];
            var aAlpha = args.Length > 2 ? (args[2] as float[]) : null;
            var bAlpha = args.Length > 3 ? (args[3] as float[]) : null;
            var outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            var pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : A.Length;
            if (aAlpha == null) { aAlpha = new float[pixelCount]; Array.Fill(aAlpha, 1f); }
            if (bAlpha == null) { bAlpha = new float[pixelCount]; Array.Fill(bAlpha, 1f); }
            var result = ComputeBlend(A, B, aAlpha, bAlpha, pixelCount);
            if (outputBpp == 8)
            {
                var bOut = new byte[pixelCount];
                for (int i = 0; i < pixelCount; i++) { float v = result.Color[i] / 257f; bOut[i] = (byte)Math.Clamp(v, 0f, 255f); }
                return [bOut, result.Alpha];
            }
            if (outputBpp == 16)
                return [result.Color, result.Alpha];
            var floatOut = new float[pixelCount];
            for (int i = 0; i < pixelCount; i++) floatOut[i] = result.Color[i];
            return [floatOut, result.Alpha];
        }

        private const string ShaderSrc = @"
#include <metal_stdlib>
using namespace metal;
kernel void blend_difference(
    device const float* top [[buffer(0)]], device const float* bottom [[buffer(1)]],
    device const float* topAlpha [[buffer(2)]], device const float* bottomAlpha [[buffer(3)]],
    device float* outC [[buffer(4)]], device float* outA [[buffer(5)]],
    uint id [[thread_position_in_grid]])
{
    float aA = topAlpha[id]; float bA = bottomAlpha[id];
    float outAlpha = aA + bA * (1.0 - aA);
    if (outAlpha < 1e-6) { outC[id] = 0.0; outA[id] = 0.0; }
    else {
        float blended = abs(top[id] - bottom[id]);
        float result = (blended * aA + bottom[id] * bA * (1.0 - aA)) / outAlpha;
        outC[id] = clamp(result, 0.0, 65535.0); outA[id] = outAlpha;
    }
}
";
    }

    #endregion
}
