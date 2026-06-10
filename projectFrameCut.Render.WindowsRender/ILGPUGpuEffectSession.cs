using ILGPU;
using ILGPU.Runtime;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.WindowsRender
{
    internal class ILGPUGpuEffectSession : IGpuEffectSession
    {
        private readonly Accelerator _accelerator;
        private MemoryBuffer1D<float, Stride1D.Dense> _curBufR;
        private MemoryBuffer1D<float, Stride1D.Dense> _curBufG;
        private MemoryBuffer1D<float, Stride1D.Dense> _curBufB;
        private MemoryBuffer1D<float, Stride1D.Dense> _curBufA;
        private MemoryBuffer1D<float, Stride1D.Dense> _altBufR;
        private MemoryBuffer1D<float, Stride1D.Dense> _altBufG;
        private MemoryBuffer1D<float, Stride1D.Dense> _altBufB;
        private MemoryBuffer1D<float, Stride1D.Dense> _altBufA;
        private readonly bool _sync;

        public int Width { get; }
        public int Height { get; }
        public bool HasAlpha { get; }

        public MemoryBuffer1D<float, Stride1D.Dense> CurBufR => _curBufR;
        public MemoryBuffer1D<float, Stride1D.Dense> CurBufG => _curBufG;
        public MemoryBuffer1D<float, Stride1D.Dense> CurBufB => _curBufB;
        public MemoryBuffer1D<float, Stride1D.Dense> CurBufA => _curBufA;
        public MemoryBuffer1D<float, Stride1D.Dense> AltBufR => _altBufR;
        public MemoryBuffer1D<float, Stride1D.Dense> AltBufG => _altBufG;
        public MemoryBuffer1D<float, Stride1D.Dense> AltBufB => _altBufB;
        public MemoryBuffer1D<float, Stride1D.Dense> AltBufA => _altBufA;
        public Accelerator Accelerator => _accelerator;

        public ILGPUGpuEffectSession(Accelerator accelerator, float[] r, float[] g, float[] b, float[] a, int width, int height, bool sync = false)
        {
            _accelerator = accelerator;
            Width = width;
            Height = height;
            HasAlpha = a is not null;
            _sync = sync;

            int length = r.Length;

            _curBufR = accelerator.Allocate1D(r);
            _curBufG = accelerator.Allocate1D(g);
            _curBufB = accelerator.Allocate1D(b);
            _curBufA = accelerator.Allocate1D(a ?? throw new ArgumentException("Alpha channel is required.", nameof(a)));

            _altBufR = accelerator.Allocate1D<float>(length);
            _altBufG = accelerator.Allocate1D<float>(length);
            _altBufB = accelerator.Allocate1D<float>(length);
            _altBufA = accelerator.Allocate1D<float>(length);
        }

        public void SwapBuffers()
        {
            (_curBufR, _altBufR) = (_altBufR, _curBufR);
            (_curBufG, _altBufG) = (_altBufG, _curBufG);
            (_curBufB, _altBufB) = (_altBufB, _curBufB);
            (_curBufA, _altBufA) = (_altBufA, _curBufA);
        }

        public (float[] r, float[] g, float[] b, float[] a) Download()
        {
            if (_sync)
            {
                _accelerator.Synchronize();
            }

            return (
                _curBufR.GetAsArray1D(),
                _curBufG.GetAsArray1D(),
                _curBufB.GetAsArray1D(),
                _curBufA.GetAsArray1D()
            );
        }

        public void Dispose()
        {
            _curBufR.Dispose();
            _curBufG.Dispose();
            _curBufB.Dispose();
            _curBufA.Dispose();
            _altBufR.Dispose();
            _altBufG.Dispose();
            _altBufB.Dispose();
            _altBufA.Dispose();
        }
    }
}
