using projectFrameCut.Shared;

namespace projectFrameCut.Render.Metal.Platforms.iOS
{
    // All the code in this file is only included on iOS.
    public class MetalAccelerater : IAcceleratedComputer
    {
        public float[] Compute(MyAcceleratorType type, int numOfArgs, float[] A, float[] B, float[] C, float[] D, float[] E, float[] F, object? extend = null)
        {
            throw new NotImplementedException();
        }
    }
}
