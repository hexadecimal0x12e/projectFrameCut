using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.Render.AndroidOpenGL.Platforms.Android
{
    public class OpenGLAccelerator : IAcceleratedComputer
    {
        public float[] Compute(MyAcceleratorType type, int numOfArgs, float[] A, float[] B, float[] C, float[] D, float[] E, float[] F, object? extend = null)
        {
            throw new NotImplementedException();
        }
    }
    
    
}
