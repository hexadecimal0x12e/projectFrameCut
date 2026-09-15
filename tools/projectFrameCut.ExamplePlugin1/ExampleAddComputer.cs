using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace SomePublisher;

public sealed class ExampleAddComputer : IComputer
{
    public string FromPlugin => ExamplePluginConstants.PluginId;
    public string SupportedEffectOrMixture => "ExampleInvert";

    public object[] Compute(object[] args)
    {
        if (args.Length == 0) return [0d];
        return [args.Sum(value => Convert.ToDouble(value))];
    }
}
