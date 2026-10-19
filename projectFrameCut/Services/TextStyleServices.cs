using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.ApplicationPluginBase.Text;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Text;
using projectFrameCut.Render.Text;
using projectFrameCut.Shared;

namespace projectFrameCut.Services;

public static class TextStyleServices
{
    public static Dictionary<string, Func<ITextClipStyleProvider>> GetAvailableTextStyleProviders() =>
        PluginManager.LoadedPlugins.Values.SelectMany(p => p.TextClipStyleProvider)
            .Where(p => p.Key != "OldTextClipEntry").ToDictionary();

    public static ITextClipStyleProvider? RestoreTextStyleProvider(string fromPlugin, string typeName, IEnumerable<KeyValuePair<string, string>>? parameters)
    {
        var provider = TextClipStyles.Create(fromPlugin, typeName);
        if (provider is not null && parameters is not null) provider.RestoreParameters(parameters);
        return provider;
    }

    public static ITextClipStyleUIProvider GetUIProvider(ITextClipStyleProvider source)
    {
        if (PluginManager.LoadedPlugins.GetValueOrDefault(source.FromPlugin) is IApplicationPluginBase plugin)
        {
            try
            {
                if (plugin.TextClipStyleUIProvider.TryGetValue(source.TypeName, out var factory)) return factory(source);
                if (plugin.GetDefaultTextClipStyleUIProvider(source) is { } ui) return ui;
            }
            catch (Exception ex) { Logger.Log(ex, $"Create text style UI for '{source.TypeName}'", typeof(TextStyleServices)); }
        }
        return new TextClipStyleUIProvider();
    }
}
