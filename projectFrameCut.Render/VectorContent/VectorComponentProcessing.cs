using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.VectorContent;

namespace projectFrameCut.Render.VectorContent;

public static class VectorComponentProcessing
{
    public static VectorCanvasElement[] Compute(IVectorComponent component, IEnumerable<IVectorComponentEffect> effects, uint frame, float progress)
    {
        using var context = ValueProviderFrameContext.PushFrame(frame, progress);
        IVectorComponent? temporary = null;
        try
        {
            var source = component;
            foreach (var effect in effects)
            {
                if (temporary is null) temporary = source = VectorComponentSerializer.Clone(component);
                var output = effect.Process(source, progress) ?? throw new InvalidDataException($"Vector effect {effect.TypeName} returned no component.");
                if (!ReferenceEquals(source, output) && source is IDisposable disposable) disposable.Dispose();
                temporary = source = output;
            }
            return source.ComputeAll().ToArray();
        }
        finally { if (temporary is IDisposable disposable) disposable.Dispose(); }
    }
}
