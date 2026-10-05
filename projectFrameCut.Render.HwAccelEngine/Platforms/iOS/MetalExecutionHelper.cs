#if IOS || MACCATALYST
using Metal;
using Foundation;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.HwAccelEngine.Platforms.iOS;

namespace projectFrameCut.Render.HwAccelEngine.Platforms.iOS
{
    public class MetalExecutionHelper
    {
        private static readonly Lazy<IMTLDevice> device = new(() => MTLDevice.SystemDefault ?? throw new NotSupportedException("Metal is not supported on this device."));
        public static IMTLDevice Device => device.Value;

        private static readonly Lazy<IMTLCommandQueue> commandQueue = new(() => Device.CreateCommandQueue() ?? throw new InvalidOperationException("Could not create command queue."));
        public static IMTLCommandQueue CommandQueue => commandQueue.Value;

        public static (IMTLCommandBuffer commandBuffer, IMTLComputeCommandEncoder encoder) CreateCommandEncoder()
        {
            var cb = CommandQueue.CommandBuffer() ?? throw new Exception("Failed to create command buffer");
            var enc = cb.ComputeCommandEncoder;
            if (enc is null)
            {
                cb.Dispose();
                throw new InvalidOperationException("Failed to create compute encoder.");
            }

            return (cb, enc);
        }

        public static unsafe IMTLBuffer CreateBuffer(float[] data)
        {
            fixed (float* ptr = data)
                return Device.CreateBuffer((IntPtr)ptr, (nuint)(data.Length * sizeof(float)), MTLResourceOptions.StorageModeShared) ?? throw new Exception("Failed to create buffer");
        }

        public static IMTLBuffer AllocBuffer(int floatCount) => Device.CreateBuffer((nuint)(floatCount * sizeof(float)), MTLResourceOptions.StorageModeShared) ?? throw new Exception("Failed to allocate buffer");
        public static void CheckCommand(IMTLCommandBuffer cb)
        {
            if (cb.Status == MTLCommandBufferStatus.Error)
                throw new InvalidOperationException($"Metal effect submission failed: {cb.Error?.LocalizedDescription}");
        }

        public static (MTLSize threadGroupSize, MTLSize threadGroups) ComputeDispatchSizes(int count, int maxThreadsPerGroup)
        {
            var tgs = new MTLSize(Math.Min(count, maxThreadsPerGroup), 1, 1);
            var tg = new MTLSize(count, 1, 1);
            return (tgs, tg);
        }
    }
}
#endif
