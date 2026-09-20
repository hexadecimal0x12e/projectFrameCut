using projectFrameCut.AIContracts;

namespace projectFrameCut.Render.RenderAPIBase.Plugins;

/// <summary>Optional extension implemented by global plugins that provide AI backends.</summary>
public interface IAIProviderPlugin
{
    IReadOnlyDictionary<string, Func<IAIProvider>> AIProviderFactories { get; }
}
