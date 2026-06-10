using System.Collections.Generic;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    public interface ISessionComputer
    {
        bool SupportsBatching { get; }
        IGpuEffectSession CreateSession(float[] r, float[] g, float[] b, float[] a, int width, int height);
        void ExecuteOnSession(IGpuEffectSession session, IReadOnlyDictionary<string, object> parameters);
    }
}
