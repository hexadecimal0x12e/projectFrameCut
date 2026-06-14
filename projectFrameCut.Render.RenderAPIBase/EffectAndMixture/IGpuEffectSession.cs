using System;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    public interface IGpuEffectSession : IDisposable
    {
        int Width { get; }
        int Height { get; }
        bool HasAlpha { get; }
        (float[] r, float[] g, float[] b, float[] a) Download();
    }
}
