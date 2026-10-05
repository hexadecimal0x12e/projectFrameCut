#if WINDOWS || LINUX || WINNETCORE
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.OpenCL;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Device = ILGPU.Runtime.Device;
using projectFrameCut.Render.WindowsRender;

namespace projectFrameCut.Render.WindowsRender
{
    public static class ILGPUExecutionHelper
    {
        public static Lock locker = new();
        public static bool? SyncOverride;
        public static Device? PickOneAccel(string accelType, int acceleratorId, List<Device> devices)
        {
            Device? pick = null;
            if (acceleratorId >= 0)
            {
                if (acceleratorId >= devices.Count)
                {
                    Logger.Log($"ERROR: Accelerator {acceleratorId} is not exist.", "error");
                    return null;
                }

                pick = devices[acceleratorId];
            }
            else if (accelType == "cuda")
                pick = devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.Cuda);
            else if (accelType == "opencl")
                pick = devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.OpenCL && (d.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || d.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase))) //优先用独显
 ?? devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.OpenCL);
            else if (accelType == "cpu")
                pick = devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.CPU);
            else if (accelType == "auto")
                pick = devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.Cuda) ?? devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.OpenCL && (d.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || d.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase))) ?? devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.OpenCL) ?? devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.CPU);
            else
            {
                Logger.Log($"ERROR: acceleratorType {accelType} is not supported.");
            }

            return pick;
        }

        internal static Accelerator SelectAccelerator(Accelerator[] accelerators, ref int index)
        {
            if (accelerators.Length == 0)
            {
                Logger.Log("[HwAccelEngine] No ILGPU accelerator is available.", "error");
                throw new NotSupportedException("No ILGPU accelerator is available.");
            }

            return accelerators[(int)((uint)Interlocked.Increment(ref index) % (uint)accelerators.Length)];
        }
    }
}
#endif
