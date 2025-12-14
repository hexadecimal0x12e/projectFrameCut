using Metal;
using Foundation;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace projectFrameCut.iOS.Render
{
    public class MetalComputerHelper
    {
        private static IMTLDevice? _device;
        public static IMTLDevice Device => _device ??= MTLDevice.SystemDefault ?? throw new NotSupportedException("Metal is not supported on this device.");

        private static IMTLCommandQueue? _commandQueue;
        public static IMTLCommandQueue CommandQueue => _commandQueue ??= Device.CreateCommandQueue() ?? throw new InvalidOperationException("Could not create command queue.");

        public static void RegisterComputerBridge()
        {
            AcceleratedComputerBridge.RequireAComputer = new((name) =>
            {
                switch (name)
                {
                    case "Overlay":
                        return new OverlayComputer();
                    case "RemoveColor":
                        return new RemoveColorComputer();
                    default:
                        Log($"Computer {name} not found.", "Error");
                        return null;

                }
            });
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

            // args: [A, B, aAlpha, bAlpha]
            var A = (float[])args[0];
            var B = (float[])args[1];
            var aAlpha = (float[])args[2];
            var bAlpha = (float[])args[3];

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

            float[] resultColor = new float[count];
            Marshal.Copy(cBuffer.Contents, resultColor, 0, count);

            float[] resultAlpha = new float[count];
            Marshal.Copy(cAlphaBuffer.Contents, resultAlpha, 0, count);

            return new object[] { resultColor, resultAlpha };
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
}
