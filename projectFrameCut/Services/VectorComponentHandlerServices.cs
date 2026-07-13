using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.VectorComponentHandler;
using projectFrameCut.Render.Plugin;

namespace projectFrameCut.Services;

public static class VectorComponentHandlerServices
{
    public static Dictionary<string, Func<IVectorComponentHandler>> GetAvailableHandlers()
    {
        if (!PluginManager.Inited)
        {
            return [];
        }

        return PluginManager.LoadedPlugins.Values
            .OfType<IApplicationPluginBase>()
            .SelectMany(p => p.VectorComponentHandlerProvider)
            .GroupBy(e => e.Key)
            .ToDictionary(g => g.Key, g => g.First().Value);
    }
}

