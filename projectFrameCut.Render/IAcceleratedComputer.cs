using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.Render
{
    public class AcceleratedComputer
    {
        public Func<float, float, float, float, float, float, float>? ManagedSource { get; set; }
        public IAcceleratedComputer? ILGpuSource { get; set; }
        public IAcceleratedComputer? OpenGLSource { get; set; }
        public IAcceleratedComputer? MetalSource { get; set; }

        public float[] Compute(MyAcceleratorType type, int n, float[] A, float[] B, float[] C, float[] D, float[] E, float[] F)
        {
            if (type == MyAcceleratorType.Auto)
            {
                // Auto-detect logic can be implemented here
                type = MyAcceleratorType.CPU; // Defaulting to CPU for this example
            }
            switch (type)
            {
                case MyAcceleratorType.CPU:
                    return DoCPUCalculation(A, B, C, D, E, F);
                case MyAcceleratorType.CUDA:
                case MyAcceleratorType.OpenCL:
                    return ILGpuSource?.Compute(type, n, A, B, C, D, E, F, ManagedSource) ?? throw new InvalidOperationException("ILGpuSource is not set.");
                case MyAcceleratorType.OpenGL:
                    return OpenGLSource?.Compute(type, n, A, B, C, D, E, F) ?? throw new InvalidOperationException("OpenGLSource is not set.");
                case MyAcceleratorType.Metal:
                    return MetalSource?.Compute(type, n, A, B, C, D, E, F) ?? throw new InvalidOperationException("MetalSource is not set.");
                default:
                    throw new NotSupportedException($"The accelerator type {type} is not supported.");

            }
        }



        private float[] DoCPUCalculation(float[] A, float[] B, float[] C, float[] D, float[] E, float[] F)
        {
            if (ManagedSource == null) throw new InvalidOperationException("ManagedSource is not set.");
            float[] result = new float[A.Length];
            for (int i = 0; i < A.Length; i++)
            {
                result[i] = ManagedSource.Invoke(A[i], B[i], C[i], D[i], E[i], F[i]);
            }
            return result;
        }
    }

    public interface IAcceleratedComputer
    {
        public float[] Compute(MyAcceleratorType type, int numOfArgs, float[] A, float[] B, float[] C, float[] D, float[] E, float[] F, object? extend = null);
    }


}
