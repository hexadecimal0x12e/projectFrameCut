using Metal;
using Foundation;
using projectFrameCut.Render.HwAccelContracts;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

#if iDevices
using projectFrameCut;
using projectFrameCut.MetalAccelerater;
#endif

namespace projectFrameCut.MetalAccelerater
{
    public class MetalComputerHelper
    {
        private static IMTLDevice? _device;
        public static IMTLDevice Device => _device ??= MTLDevice.SystemDefault ?? throw new NotSupportedException("Metal is not supported on this device.");

        private static IMTLCommandQueue? _commandQueue;
        public static IMTLCommandQueue CommandQueue => _commandQueue ??= Device.CreateCommandQueue() ?? throw new InvalidOperationException("Could not create command queue.");

        public static void RegisterComputerBridge()
        {
            //todo: plugin
        }
    }

    public class OverlayComputer : IComputer
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

        public object[] Compute(object[] args)
        {
            InitializePipeline();

            // args: [A, B, aAlpha, bAlpha, outputBpp]
            var A = (float[])args[0];
            var B = (float[])args[1];
            var aAlpha = args.Length > 2 ? (args[2] as float[]) : null;
            var bAlpha = args.Length > 3 ? (args[3] as float[]) : null;
            var outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;

            if (aAlpha == null) aAlpha = Enumerable.Repeat(1f, A.Length).ToArray();
            if (bAlpha == null) bAlpha = Enumerable.Repeat(1f, A.Length).ToArray();

            int count = A.Length;
            int bufferSize = count * sizeof(float);

            var device = MetalComputerHelper.Device;
            var commandQueue = MetalComputerHelper.CommandQueue;
            var commandBuffer = commandQueue.CommandBuffer();
            if (commandBuffer == null) throw new Exception("Failed to create command buffer");

            var encoder = commandBuffer.ComputeCommandEncoder;
            if (encoder == null) throw new Exception("Failed to create compute encoder");

            var aBuffer = CreateBuffer(device, A);
            var bBuffer = CreateBuffer(device, B);
            var aAlphaBuffer = CreateBuffer(device, aAlpha);
            var bAlphaBuffer = CreateBuffer(device, bAlpha);
            var cAlphaBuffer = device.CreateBuffer((nuint)bufferSize, MTLResourceOptions.StorageModeShared) ?? throw new Exception("Failed to create buffer");
            var cBuffer = device.CreateBuffer((nuint)bufferSize, MTLResourceOptions.StorageModeShared) ?? throw new Exception("Failed to create buffer");

            // 1. Compute Alpha
            encoder.SetComputePipelineState(_alphaPipelineState!);
            encoder.SetBuffer(aAlphaBuffer, 0, 0);
            encoder.SetBuffer(bAlphaBuffer, 0, 1);
            encoder.SetBuffer(cAlphaBuffer, 0, 2);

            var threadGroupSize = new MTLSize(Math.Min(count, (int)_alphaPipelineState!.MaxTotalThreadsPerThreadgroup), 1, 1);
            var threadGroups = new MTLSize((count + threadGroupSize.Width - 1) / threadGroupSize.Width, 1, 1);
            
            encoder.DispatchThreadgroups(threadGroups, threadGroupSize);

            // 2. Compute Color
            encoder.SetComputePipelineState(_colorPipelineState!);
            encoder.SetBuffer(aAlphaBuffer, 0, 0);
            encoder.SetBuffer(aBuffer, 0, 1);
            encoder.SetBuffer(bAlphaBuffer, 0, 2);
            encoder.SetBuffer(bBuffer, 0, 3);
            encoder.SetBuffer(cBuffer, 0, 4);
            
            encoder.DispatchThreadgroups(threadGroups, threadGroupSize);

            encoder.EndEncoding();
            commandBuffer.Commit();
            commandBuffer.WaitUntilCompleted();

            float[] resultAlpha = new float[count];
            Marshal.Copy(cAlphaBuffer.Contents, resultAlpha, 0, count);

            if (outputBpp == 8)
            {
                float[] temp = new float[count];
                Marshal.Copy(cBuffer.Contents, temp, 0, count);
                var resultColor = new byte[count];
                for (int i = 0; i < count; i++)
                {
                    float v = temp[i] / 257.0f;
                    if (v < 0) v = 0;
                    if (v > 255) v = 255;
                    resultColor[i] = (byte)v;
                }
                return new object[] { resultColor, resultAlpha };
            }
            if (outputBpp == 16)
            {
                float[] temp = new float[count];
                Marshal.Copy(cBuffer.Contents, temp, 0, count);
                var resultColor = new ushort[count];
                for (int i = 0; i < count; i++)
                {
                    float v = temp[i];
                    if (v < 0) v = 0;
                    if (v > 65535) v = 65535;
                    resultColor[i] = (ushort)v;
                }
                return new object[] { resultColor, resultAlpha };
            }

            float[] resultColorF = new float[count];
            Marshal.Copy(cBuffer.Contents, resultColorF, 0, count);
            return new object[] { resultColorF, resultAlpha };
        }

        private IMTLBuffer CreateBuffer(IMTLDevice device, float[] data)
        {
            unsafe
            {
                fixed (float* ptr = data)
                {
                    return device.CreateBuffer((IntPtr)ptr, (nuint)(data.Length * sizeof(float)), MTLResourceOptions.StorageModeShared) ?? throw new Exception("Failed to create buffer");
                }
            }
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

    public class ApproximateOverlayComputer : IComputer
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

        public object[] Compute(object[] args)
        {
            InitializePipeline();

            // args: [A, B, aAlpha, bAlpha, outputBpp]
            var A = (float[])args[0];
            var B = (float[])args[1];
            var aAlpha = args.Length > 2 ? (args[2] as float[]) : null;
            var bAlpha = args.Length > 3 ? (args[3] as float[]) : null;
            var outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;

            if (aAlpha == null) aAlpha = Enumerable.Repeat(1f, A.Length).ToArray();
            if (bAlpha == null) bAlpha = Enumerable.Repeat(1f, A.Length).ToArray();

            int count = A.Length;
            int bufferSize = count * sizeof(float);

            var device = MetalComputerHelper.Device;
            var commandQueue = MetalComputerHelper.CommandQueue;
            var commandBuffer = commandQueue.CommandBuffer();
            if (commandBuffer == null) throw new Exception("Failed to create command buffer");

            var encoder = commandBuffer.ComputeCommandEncoder;
            if (encoder == null) throw new Exception("Failed to create compute encoder");

            var aBuffer = CreateBuffer(device, A);
            var bBuffer = CreateBuffer(device, B);
            var aAlphaBuffer = CreateBuffer(device, aAlpha);
            var bAlphaBuffer = CreateBuffer(device, bAlpha);
            var cAlphaBuffer = device.CreateBuffer((nuint)bufferSize, MTLResourceOptions.StorageModeShared) ?? throw new Exception("Failed to create buffer");
            var cBuffer = device.CreateBuffer((nuint)bufferSize, MTLResourceOptions.StorageModeShared) ?? throw new Exception("Failed to create buffer");

            // 1. Compute Alpha
            encoder.SetComputePipelineState(_alphaPipelineState!);
            encoder.SetBuffer(aAlphaBuffer, 0, 0);
            encoder.SetBuffer(bAlphaBuffer, 0, 1);
            encoder.SetBuffer(cAlphaBuffer, 0, 2);

            var threadGroupSize = new MTLSize(Math.Min(count, (int)_alphaPipelineState!.MaxTotalThreadsPerThreadgroup), 1, 1);
            var threadGroups = new MTLSize((count + threadGroupSize.Width - 1) / threadGroupSize.Width, 1, 1);
            
            encoder.DispatchThreadgroups(threadGroups, threadGroupSize);

            // 2. Compute Color
            encoder.SetComputePipelineState(_colorPipelineState!);
            encoder.SetBuffer(aAlphaBuffer, 0, 0);
            encoder.SetBuffer(aBuffer, 0, 1);
            encoder.SetBuffer(bAlphaBuffer, 0, 2);
            encoder.SetBuffer(bBuffer, 0, 3);
            encoder.SetBuffer(cBuffer, 0, 4);
            
            encoder.DispatchThreadgroups(threadGroups, threadGroupSize);

            encoder.EndEncoding();
            commandBuffer.Commit();
            commandBuffer.WaitUntilCompleted();

            float[] resultAlpha = new float[count];
            Marshal.Copy(cAlphaBuffer.Contents, resultAlpha, 0, count);

            if (outputBpp == 8)
            {
                float[] temp = new float[count];
                Marshal.Copy(cBuffer.Contents, temp, 0, count);
                var resultColor = new byte[count];
                for (int i = 0; i < count; i++)
                {
                    float v = temp[i] / 257.0f;
                    if (v < 0) v = 0;
                    if (v > 255) v = 255;
                    resultColor[i] = (byte)v;
                }
                return new object[] { resultColor, resultAlpha };
            }
            if (outputBpp == 16)
            {
                float[] temp = new float[count];
                Marshal.Copy(cBuffer.Contents, temp, 0, count);
                var resultColor = new ushort[count];
                for (int i = 0; i < count; i++)
                {
                    float v = temp[i];
                    if (v < 0) v = 0;
                    if (v > 65535) v = 65535;
                    resultColor[i] = (ushort)v;
                }
                return new object[] { resultColor, resultAlpha };
            }

            float[] resultColorF = new float[count];
            Marshal.Copy(cBuffer.Contents, resultColorF, 0, count);
            return new object[] { resultColorF, resultAlpha };
        }

        private IMTLBuffer CreateBuffer(IMTLDevice device, float[] data)
        {
            unsafe
            {
                fixed (float* ptr = data)
                {
                    return device.CreateBuffer((IntPtr)ptr, (nuint)(data.Length * sizeof(float)), MTLResourceOptions.StorageModeShared) ?? throw new Exception("Failed to create buffer");
                }
            }
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

    public class RemoveColorComputer : IComputer
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

        public object[] Compute(object[] args)
        {
            InitializePipeline();

            // args: [aR, aG, aB, sourceA, [toRemoveR], [toRemoveG], [toRemoveB], [range]]
            var aR = (float[])args[0];
            var aG = (float[])args[1];
            var aB = (float[])args[2];
            var sourceA = (float[])args[3];

            var toRemoveR = (uint)((float[])args[4])[0];
            var toRemoveG = (uint)((float[])args[5])[0];
            var toRemoveB = (uint)((float[])args[6])[0];
            var range = (uint)((float[])args[7])[0];

            uint lowR = toRemoveR > range ? toRemoveR - range : 0;
            uint highR = toRemoveR + range;
            uint lowG = toRemoveG > range ? toRemoveG - range : 0;
            uint highG = toRemoveG + range;
            uint lowB = toRemoveB > range ? toRemoveB - range : 0;
            uint highB = toRemoveB + range;

            if (highR > 65535) highR = 65535;
            if (highG > 65535) highG = 65535;
            if (highB > 65535) highB = 65535;

            int count = aR.Length;
            int bufferSize = count * sizeof(float);

            var device = MetalComputerHelper.Device;
            var commandQueue = MetalComputerHelper.CommandQueue;
            var commandBuffer = commandQueue.CommandBuffer();
            if (commandBuffer == null) throw new Exception("Failed to create command buffer");

            var encoder = commandBuffer.ComputeCommandEncoder;
            if (encoder == null) throw new Exception("Failed to create compute encoder");

            var rBuffer = CreateBuffer(device, aR);
            var gBuffer = CreateBuffer(device, aG);
            var bBuffer = CreateBuffer(device, aB);
            var aBuffer = CreateBuffer(device, sourceA);
            var outABuffer = device.CreateBuffer((nuint)bufferSize, MTLResourceOptions.StorageModeShared) ?? throw new Exception("Failed to create buffer");

            encoder.SetComputePipelineState(_pipelineState!);
            encoder.SetBuffer(rBuffer, 0, 0);
            encoder.SetBuffer(gBuffer, 0, 1);
            encoder.SetBuffer(bBuffer, 0, 2);
            encoder.SetBuffer(aBuffer, 0, 3);
            encoder.SetBuffer(outABuffer, 0, 4);
            
            unsafe {
                encoder.SetBytes((IntPtr)(&lowR), (nuint)sizeof(uint), 5);
                encoder.SetBytes((IntPtr)(&highR), (nuint)sizeof(uint), 6);
                encoder.SetBytes((IntPtr)(&lowG), (nuint)sizeof(uint), 7);
                encoder.SetBytes((IntPtr)(&highG), (nuint)sizeof(uint), 8);
                encoder.SetBytes((IntPtr)(&lowB), (nuint)sizeof(uint), 9);
                encoder.SetBytes((IntPtr)(&highB), (nuint)sizeof(uint), 10);
            }

            var threadGroupSize = new MTLSize(Math.Min(count, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup), 1, 1);
            var threadGroups = new MTLSize((count + threadGroupSize.Width - 1) / threadGroupSize.Width, 1, 1);
            
            encoder.DispatchThreadgroups(threadGroups, threadGroupSize);

            encoder.EndEncoding();
            commandBuffer.Commit();
            commandBuffer.WaitUntilCompleted();

            float[] result = new float[count];
            Marshal.Copy(outABuffer.Contents, result, 0, count);

            return new object[] { result };
        }

        private IMTLBuffer CreateBuffer(IMTLDevice device, float[] data)
        {
            unsafe
            {
                fixed (float* ptr = data)
                {
                    return device.CreateBuffer((IntPtr)ptr, (nuint)(data.Length * sizeof(float)), MTLResourceOptions.StorageModeShared) ?? throw new Exception("Failed to create buffer");
                }
            }
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

    public class OpacityComputer : IComputer
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

        public object[] Compute(object[] args)
        {
            InitializePipeline();
            var rIn = (float[])args[0]; var gIn = (float[])args[1]; var bIn = (float[])args[2]; var aIn = (float[])args[3];
            float opacity = Convert.ToSingle(args[4]);
            int count = rIn.Length, bufSize = count * sizeof(float);

            var device = MetalComputerHelper.Device; var cq = MetalComputerHelper.CommandQueue;
            var cb = cq.CommandBuffer() ?? throw new Exception("Command buffer failed");
            var encoder = cb.ComputeCommandEncoder ?? throw new Exception("Encoder failed");
            var rBuf = CreateBuffer(device, rIn); var gBuf = CreateBuffer(device, gIn); var bBuf = CreateBuffer(device, bIn); var aBuf = CreateBuffer(device, aIn);
            var rOBuf = device.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared) ?? throw new Exception();
            var gOBuf = device.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared) ?? throw new Exception();
            var bOBuf = device.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared) ?? throw new Exception();
            var aOBuf = device.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared) ?? throw new Exception();

            encoder.SetComputePipelineState(_pipelineState!);
            encoder.SetBuffer(rBuf, 0, 0); encoder.SetBuffer(gBuf, 0, 1);
            encoder.SetBuffer(bBuf, 0, 2); encoder.SetBuffer(aBuf, 0, 3);
            encoder.SetBytes((IntPtr)(&opacity), (nuint)sizeof(float), 4);
            encoder.SetBuffer(rOBuf, 0, 5); encoder.SetBuffer(gOBuf, 0, 6);
            encoder.SetBuffer(bOBuf, 0, 7); encoder.SetBuffer(aOBuf, 0, 8);
            var tgs = new MTLSize(Math.Min(count, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup), 1, 1);
            var tg = new MTLSize((count + tgs.Width - 1) / tgs.Width, 1, 1);
            encoder.DispatchThreadgroups(tg, tgs);
            encoder.EndEncoding(); cb.Commit(); cb.WaitUntilCompleted();

            var rO = new float[count]; var gO = new float[count]; var bO = new float[count]; var aO = new float[count];
            Marshal.Copy(rOBuf.Contents, rO, 0, count); Marshal.Copy(gOBuf.Contents, gO, 0, count);
            Marshal.Copy(bOBuf.Contents, bO, 0, count); Marshal.Copy(aOBuf.Contents, aO, 0, count);
            return [rO, gO, bO, aO];
        }

        private unsafe IMTLBuffer CreateBuffer(IMTLDevice device, float[] data) { fixed (float* p = data) { return device.CreateBuffer((IntPtr)p, (nuint)(data.Length * sizeof(float)), MTLResourceOptions.StorageModeShared) ?? throw new Exception(); } }

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

    public class VignetteComputer : IComputer
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

        public object[] Compute(object[] args)
        {
            InitializePipeline();
            var rIn = (float[])args[0]; var gIn = (float[])args[1]; var bIn = (float[])args[2]; var aIn = (float[])args[3];
            int w = Convert.ToInt32(args[4]), h = Convert.ToInt32(args[5]);
            float strength = Convert.ToSingle(args[6]), radius = Convert.ToSingle(args[7]);
            int count = rIn.Length, bufSize = count * sizeof(float);
            var device = MetalComputerHelper.Device; var cq = MetalComputerHelper.CommandQueue;
            var cb = cq.CommandBuffer()!; var encoder = cb.ComputeCommandEncoder!;

            var rBuf = CreateBuffer(device, rIn); var gBuf = CreateBuffer(device, gIn); var bBuf = CreateBuffer(device, bIn); var aBuf = CreateBuffer(device, aIn);
            var rOBuf = device.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
            var gOBuf = device.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
            var bOBuf = device.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
            var aOBuf = device.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared)!;

            encoder.SetComputePipelineState(_pipelineState!);
            encoder.SetBuffer(rBuf, 0, 0); encoder.SetBuffer(gBuf, 0, 1); encoder.SetBuffer(bBuf, 0, 2); encoder.SetBuffer(aBuf, 0, 3);
            encoder.SetBytes((IntPtr)(&w), (nuint)sizeof(int), 4); encoder.SetBytes((IntPtr)(&h), (nuint)sizeof(int), 5);
            encoder.SetBytes((IntPtr)(&strength), (nuint)sizeof(float), 6); encoder.SetBytes((IntPtr)(&radius), (nuint)sizeof(float), 7);
            encoder.SetBuffer(rOBuf, 0, 8); encoder.SetBuffer(gOBuf, 0, 9); encoder.SetBuffer(bOBuf, 0, 10); encoder.SetBuffer(aOBuf, 0, 11);
            var tgs = new MTLSize(Math.Min(count, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup), 1, 1);
            encoder.DispatchThreadgroups(new MTLSize((count + tgs.Width - 1) / tgs.Width, 1, 1), tgs);
            encoder.EndEncoding(); cb.Commit(); cb.WaitUntilCompleted();

            var rO = new float[count]; Marshal.Copy(rOBuf.Contents, rO, 0, count);
            var gO = new float[count]; Marshal.Copy(gOBuf.Contents, gO, 0, count);
            var bO = new float[count]; Marshal.Copy(bOBuf.Contents, bO, 0, count);
            var aO = new float[count]; Marshal.Copy(aOBuf.Contents, aO, 0, count);
            return [rO, gO, bO, aO];
        }

        private unsafe IMTLBuffer CreateBuffer(IMTLDevice device, float[] data) { fixed (float* p = data) { return device.CreateBuffer((IntPtr)p, (nuint)(data.Length * sizeof(float)), MTLResourceOptions.StorageModeShared)!; } }

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

    public class FlipComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.iOS.MetalAccelerater.MetalComputers";
        public string SupportedEffectOrMixture => "Flip";
        private IMTLComputePipelineState? _pipelineState;
        private void InitializePipeline() { if (_pipelineState != null) return; var d = MetalComputerHelper.Device; var l = d.CreateLibrary(ShaderSrc, new MTLCompileOptions(), out NSError e); if (l == null) throw new Exception(e?.LocalizedDescription); _pipelineState = d.CreateComputePipelineState(l.CreateFunction("flip_compute"), out e); if (_pipelineState == null) throw new Exception(e?.LocalizedDescription); }

        public object[] Compute(object[] args)
        {
            InitializePipeline();
            var rIn = (float[])args[0]; var gIn = (float[])args[1]; var bIn = (float[])args[2]; var aIn = (float[])args[3];
            int w = Convert.ToInt32(args[4]), h = Convert.ToInt32(args[5]), horiz = Convert.ToBoolean(args[6]) ? 1 : 0, vert = Convert.ToBoolean(args[7]) ? 1 : 0;
            int count = rIn.Length, bufSize = count * sizeof(float);
            var d = MetalComputerHelper.Device; var cq = MetalComputerHelper.CommandQueue; var cb = cq.CommandBuffer()!; var enc = cb.ComputeCommandEncoder!;
            unsafe {
                fixed (float* pr = rIn) fixed (float* pg = gIn) fixed (float* pb = bIn) fixed (float* pa = aIn) {
                    var rB = d.CreateBuffer((IntPtr)pr, (nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var gB = d.CreateBuffer((IntPtr)pg, (nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var bB = d.CreateBuffer((IntPtr)pb, (nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var aB = d.CreateBuffer((IntPtr)pa, (nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var rOB = d.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var gOB = d.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var bOB = d.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var aOB = d.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    enc.SetComputePipelineState(_pipelineState!);
                    enc.SetBuffer(rB, 0, 0); enc.SetBuffer(gB, 0, 1); enc.SetBuffer(bB, 0, 2); enc.SetBuffer(aB, 0, 3);
                    enc.SetBytes((IntPtr)(&w), (nuint)sizeof(int), 4); enc.SetBytes((IntPtr)(&h), (nuint)sizeof(int), 5);
                    enc.SetBytes((IntPtr)(&horiz), (nuint)sizeof(int), 6); enc.SetBytes((IntPtr)(&vert), (nuint)sizeof(int), 7);
                    enc.SetBuffer(rOB, 0, 8); enc.SetBuffer(gOB, 0, 9); enc.SetBuffer(bOB, 0, 10); enc.SetBuffer(aOB, 0, 11);
                    var ts = new MTLSize(Math.Min(count, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup), 1, 1);
                    enc.DispatchThreadgroups(new MTLSize((count + ts.Width - 1) / ts.Width, 1, 1), ts);
                    enc.EndEncoding(); cb.Commit(); cb.WaitUntilCompleted();
                    var rO = new float[count]; Marshal.Copy(rOB.Contents, rO, 0, count);
                    var gO = new float[count]; Marshal.Copy(gOB.Contents, gO, 0, count);
                    var bO = new float[count]; Marshal.Copy(bOB.Contents, bO, 0, count);
                    var aO = new float[count]; Marshal.Copy(aOB.Contents, aO, 0, count);
                    return [rO, gO, bO, aO];
                }
            }
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

    public class SharpenComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.iOS.MetalAccelerater.MetalComputers";
        public string SupportedEffectOrMixture => "Sharpen";
        private IMTLComputePipelineState? _pipelineState;
        private void InitializePipeline() { if (_pipelineState != null) return; var d = MetalComputerHelper.Device; var l = d.CreateLibrary(ShaderSrc, new MTLCompileOptions(), out NSError e); if (l == null) throw new Exception(e?.LocalizedDescription); _pipelineState = d.CreateComputePipelineState(l.CreateFunction("sharpen_compute"), out e); if (_pipelineState == null) throw new Exception(e?.LocalizedDescription); }

        public object[] Compute(object[] args)
        {
            InitializePipeline();
            var rIn = (float[])args[0]; var gIn = (float[])args[1]; var bIn = (float[])args[2]; var aIn = (float[])args[3];
            int w = Convert.ToInt32(args[4]); float amount = Convert.ToSingle(args[5]);
            int count = rIn.Length, bufSize = count * sizeof(float);
            var d = MetalComputerHelper.Device; var cq = MetalComputerHelper.CommandQueue; var cb = cq.CommandBuffer()!; var enc = cb.ComputeCommandEncoder!;
            unsafe {
                fixed (float* pr = rIn) fixed (float* pg = gIn) fixed (float* pb = bIn) fixed (float* pa = aIn) {
                    var rB = d.CreateBuffer((IntPtr)pr, (nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var gB = d.CreateBuffer((IntPtr)pg, (nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var bB = d.CreateBuffer((IntPtr)pb, (nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var aB = d.CreateBuffer((IntPtr)pa, (nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var rOB = d.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var gOB = d.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var bOB = d.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var aOB = d.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    enc.SetComputePipelineState(_pipelineState!);
                    enc.SetBuffer(rB, 0, 0); enc.SetBuffer(gB, 0, 1); enc.SetBuffer(bB, 0, 2); enc.SetBuffer(aB, 0, 3);
                    enc.SetBytes((IntPtr)(&w), (nuint)sizeof(int), 4); enc.SetBytes((IntPtr)(&amount), (nuint)sizeof(float), 5);
                    enc.SetBuffer(rOB, 0, 6); enc.SetBuffer(gOB, 0, 7); enc.SetBuffer(bOB, 0, 8); enc.SetBuffer(aOB, 0, 9);
                    var ts = new MTLSize(Math.Min(count, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup), 1, 1);
                    enc.DispatchThreadgroups(new MTLSize((count + ts.Width - 1) / ts.Width, 1, 1), ts);
                    enc.EndEncoding(); cb.Commit(); cb.WaitUntilCompleted();
                    var rO = new float[count]; Marshal.Copy(rOB.Contents, rO, 0, count);
                    var gO = new float[count]; Marshal.Copy(gOB.Contents, gO, 0, count);
                    var bO = new float[count]; Marshal.Copy(bOB.Contents, bO, 0, count);
                    var aO = new float[count]; Marshal.Copy(aOB.Contents, aO, 0, count);
                    return [rO, gO, bO, aO];
                }
            }
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

    public class RotationComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.iOS.MetalAccelerater.MetalComputers";
        public string SupportedEffectOrMixture => "Rotation";
        private IMTLComputePipelineState? _pipelineState;
        private void InitializePipeline() { if (_pipelineState != null) return; var d = MetalComputerHelper.Device; var l = d.CreateLibrary(ShaderSrc, new MTLCompileOptions(), out NSError e); if (l == null) throw new Exception(e?.LocalizedDescription); _pipelineState = d.CreateComputePipelineState(l.CreateFunction("rotation_compute"), out e); if (_pipelineState == null) throw new Exception(e?.LocalizedDescription); }

        public object[] Compute(object[] args)
        {
            InitializePipeline();
            var rIn = (float[])args[0]; var gIn = (float[])args[1]; var bIn = (float[])args[2]; var aIn = (float[])args[3];
            int srcW = Convert.ToInt32(args[4]), srcH = Convert.ToInt32(args[5]), outW = Convert.ToInt32(args[6]), outH = Convert.ToInt32(args[7]);
            float angle = Convert.ToSingle(args[8]);
            int srcLen = srcW * srcH, dstLen = outW * outH;
            var d = MetalComputerHelper.Device; var cq = MetalComputerHelper.CommandQueue; var cb = cq.CommandBuffer()!; var enc = cb.ComputeCommandEncoder!;
            unsafe {
                fixed (float* pr = rIn) fixed (float* pg = gIn) fixed (float* pb = bIn) fixed (float* pa = aIn) {
                    var rB = d.CreateBuffer((IntPtr)pr, (nuint)(srcLen * sizeof(float)), MTLResourceOptions.StorageModeShared)!;
                    var gB = d.CreateBuffer((IntPtr)pg, (nuint)(srcLen * sizeof(float)), MTLResourceOptions.StorageModeShared)!;
                    var bB = d.CreateBuffer((IntPtr)pb, (nuint)(srcLen * sizeof(float)), MTLResourceOptions.StorageModeShared)!;
                    var aB = d.CreateBuffer((IntPtr)pa, (nuint)(srcLen * sizeof(float)), MTLResourceOptions.StorageModeShared)!;
                    var rOB = d.CreateBuffer((nuint)(dstLen * sizeof(float)), MTLResourceOptions.StorageModeShared)!;
                    var gOB = d.CreateBuffer((nuint)(dstLen * sizeof(float)), MTLResourceOptions.StorageModeShared)!;
                    var bOB = d.CreateBuffer((nuint)(dstLen * sizeof(float)), MTLResourceOptions.StorageModeShared)!;
                    var aOB = d.CreateBuffer((nuint)(dstLen * sizeof(float)), MTLResourceOptions.StorageModeShared)!;
                    enc.SetComputePipelineState(_pipelineState!);
                    enc.SetBuffer(rB, 0, 0); enc.SetBuffer(gB, 0, 1); enc.SetBuffer(bB, 0, 2); enc.SetBuffer(aB, 0, 3);
                    enc.SetBytes((IntPtr)(&srcW), (nuint)sizeof(int), 4); enc.SetBytes((IntPtr)(&srcH), (nuint)sizeof(int), 5);
                    enc.SetBytes((IntPtr)(&outW), (nuint)sizeof(int), 6); enc.SetBytes((IntPtr)(&outH), (nuint)sizeof(int), 7);
                    enc.SetBytes((IntPtr)(&angle), (nuint)sizeof(float), 8);
                    enc.SetBuffer(rOB, 0, 9); enc.SetBuffer(gOB, 0, 10); enc.SetBuffer(bOB, 0, 11); enc.SetBuffer(aOB, 0, 12);
                    var ts = new MTLSize(Math.Min(dstLen, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup), 1, 1);
                    enc.DispatchThreadgroups(new MTLSize((dstLen + ts.Width - 1) / ts.Width, 1, 1), ts);
                    enc.EndEncoding(); cb.Commit(); cb.WaitUntilCompleted();
                    var rO = new float[dstLen]; Marshal.Copy(rOB.Contents, rO, 0, dstLen);
                    var gO = new float[dstLen]; Marshal.Copy(gOB.Contents, gO, 0, dstLen);
                    var bO = new float[dstLen]; Marshal.Copy(bOB.Contents, bO, 0, dstLen);
                    var aO = new float[dstLen]; Marshal.Copy(aOB.Contents, aO, 0, dstLen);
                    return [rO, gO, bO, aO];
                }
            }
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

    public class BlurComputer : IComputer
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

        public object[] Compute(object[] args)
        {
            InitializePipelines();
            var rIn = (float[])args[0]; var gIn = (float[])args[1]; var bIn = (float[])args[2]; var aIn = (float[])args[3];
            int w = Convert.ToInt32(args[4]), count = rIn.Length, h = count / w;
            int radius = Math.Max(1, (int)MathF.Ceiling(Convert.ToSingle(args[5])));
            int bufSize = count * sizeof(float);
            var d = MetalComputerHelper.Device; var cq = MetalComputerHelper.CommandQueue; var cb = cq.CommandBuffer()!; var enc = cb.ComputeCommandEncoder!;

            unsafe {
                fixed (float* pr = rIn) fixed (float* pg = gIn) fixed (float* pb = bIn) fixed (float* pa = aIn) {
                    var rB = d.CreateBuffer((IntPtr)pr, (nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var gB = d.CreateBuffer((IntPtr)pg, (nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var bB = d.CreateBuffer((IntPtr)pb, (nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var aB = d.CreateBuffer((IntPtr)pa, (nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var rT = d.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var gT = d.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var bT = d.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var aT = d.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared)!;

                    // Horizontal pass
                    enc.SetComputePipelineState(_pipelineStateH!);
                    enc.SetBuffer(rB, 0, 0); enc.SetBuffer(gB, 0, 1); enc.SetBuffer(bB, 0, 2); enc.SetBuffer(aB, 0, 3);
                    enc.SetBytes((IntPtr)(&w), (nuint)sizeof(int), 4); enc.SetBytes((IntPtr)(&radius), (nuint)sizeof(int), 5);
                    enc.SetBuffer(rT, 0, 6); enc.SetBuffer(gT, 0, 7); enc.SetBuffer(bT, 0, 8); enc.SetBuffer(aT, 0, 9);
                    var ts = new MTLSize(Math.Min(count, (int)_pipelineStateH!.MaxTotalThreadsPerThreadgroup), 1, 1);
                    enc.DispatchThreadgroups(new MTLSize((count + ts.Width - 1) / ts.Width, 1, 1), ts);

                    // Vertical pass
                    enc.SetComputePipelineState(_pipelineStateV!);
                    enc.SetBuffer(rT, 0, 0); enc.SetBuffer(gT, 0, 1); enc.SetBuffer(bT, 0, 2); enc.SetBuffer(aT, 0, 3);
                    enc.SetBytes((IntPtr)(&w), (nuint)sizeof(int), 4); enc.SetBytes((IntPtr)(&h), (nuint)sizeof(int), 5);
                    enc.SetBytes((IntPtr)(&radius), (nuint)sizeof(int), 6);
                    var rOB = d.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var gOB = d.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var bOB = d.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var aOB = d.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    enc.SetBuffer(rOB, 0, 7); enc.SetBuffer(gOB, 0, 8); enc.SetBuffer(bOB, 0, 9); enc.SetBuffer(aOB, 0, 10);
                    enc.DispatchThreadgroups(new MTLSize((count + ts.Width - 1) / ts.Width, 1, 1), ts);
                    enc.EndEncoding(); cb.Commit(); cb.WaitUntilCompleted();

                    var rO = new float[count]; Marshal.Copy(rOB.Contents, rO, 0, count);
                    var gO = new float[count]; Marshal.Copy(gOB.Contents, gO, 0, count);
                    var bO = new float[count]; Marshal.Copy(bOB.Contents, bO, 0, count);
                    var aO = new float[count]; Marshal.Copy(aOB.Contents, aO, 0, count);
                    return [rO, gO, bO, aO];
                }
            }
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

    public class ColorAdjustmentComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.iOS.MetalAccelerater.MetalComputers";
        public string SupportedEffectOrMixture => "ColorAdjustment";
        private IMTLComputePipelineState? _pipelineState;
        private void InitializePipeline() { if (_pipelineState != null) return; var d = MetalComputerHelper.Device; var l = d.CreateLibrary(ShaderSrc, new MTLCompileOptions(), out NSError e); if (l == null) throw new Exception(e?.LocalizedDescription); _pipelineState = d.CreateComputePipelineState(l.CreateFunction("coloradjust_compute"), out e); if (_pipelineState == null) throw new Exception(e?.LocalizedDescription); }

        public object[] Compute(object[] args)
        {
            InitializePipeline();
            var rIn = (float[])args[0]; var gIn = (float[])args[1]; var bIn = (float[])args[2]; var aIn = (float[])args[3];
            float brightness = Convert.ToSingle(args[4]), contrast = Convert.ToSingle(args[5]);
            float saturation = Convert.ToSingle(args[6]), hue = Convert.ToSingle(args[7]);
            float gamma = Convert.ToSingle(args[8]), vibrance = Convert.ToSingle(args[9]);
            float temperature = Convert.ToSingle(args[10]), invertF = Convert.ToSingle(args[11]);
            float grayscale = Convert.ToSingle(args[12]), opacity = Convert.ToSingle(args[13]);
            float maxV = Convert.ToSingle(args[14]);
            int count = rIn.Length, bufSize = count * sizeof(float);
            var d = MetalComputerHelper.Device; var cq = MetalComputerHelper.CommandQueue; var cb = cq.CommandBuffer()!; var enc = cb.ComputeCommandEncoder!;
            unsafe {
                fixed (float* pr = rIn) fixed (float* pg = gIn) fixed (float* pb = bIn) fixed (float* pa = aIn) {
                    var rB = d.CreateBuffer((IntPtr)pr, (nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var gB = d.CreateBuffer((IntPtr)pg, (nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var bB = d.CreateBuffer((IntPtr)pb, (nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var aB = d.CreateBuffer((IntPtr)pa, (nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var rOB = d.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var gOB = d.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var bOB = d.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    var aOB = d.CreateBuffer((nuint)bufSize, MTLResourceOptions.StorageModeShared)!;
                    enc.SetComputePipelineState(_pipelineState!);
                    enc.SetBuffer(rB, 0, 0); enc.SetBuffer(gB, 0, 1); enc.SetBuffer(bB, 0, 2); enc.SetBuffer(aB, 0, 3);
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
                    enc.SetBuffer(rOB, 0, 15); enc.SetBuffer(gOB, 0, 16); enc.SetBuffer(bOB, 0, 17); enc.SetBuffer(aOB, 0, 18);
                    var ts = new MTLSize(Math.Min(count, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup), 1, 1);
                    enc.DispatchThreadgroups(new MTLSize((count + ts.Width - 1) / ts.Width, 1, 1), ts);
                    enc.EndEncoding(); cb.Commit(); cb.WaitUntilCompleted();
                    var rO = new float[count]; Marshal.Copy(rOB.Contents, rO, 0, count);
                    var gO = new float[count]; Marshal.Copy(gOB.Contents, gO, 0, count);
                    var bO = new float[count]; Marshal.Copy(bOB.Contents, bO, 0, count);
                    var aO = new float[count]; Marshal.Copy(aOB.Contents, aO, 0, count);
                    return [rO, gO, bO, aO];
                }
            }
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
}
