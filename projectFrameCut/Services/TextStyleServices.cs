using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.Render.Plugin;
using System;
using System.Collections.Generic;
using System.Linq;

namespace projectFrameCut.Services
{
    public static class TextStyleServices
    {
        public static Dictionary<string, Func<ITextClipStyleProvider>> GetAvailableTextStyleProviders()
        {
            if (!PluginManager.Inited) return [];
            return PluginManager.LoadedPlugins.Values
                .OfType<IApplicationPluginBase>()
                .SelectMany(c => c.TextClipStyleProvider)
                .ToDictionary(k => k.Key, v => v.Value);
        }

        public static ITextClipStyleProvider? RestoreTextStyleProvider(string fromPlugin, string typeName, Dictionary<string, string>? parameters)
        {
            if (!PluginManager.Inited) return null;
            if (!PluginManager.LoadedPlugins.TryGetValue(fromPlugin, out var plugin)) return null;
            if (plugin is not IApplicationPluginBase appBase) return null;
            if (!appBase.TextClipStyleProvider.TryGetValue(typeName, out var factory)) return null;

            var provider = factory();
            if (parameters != null)
            {
                provider.Parameters = new Dictionary<string, string>(parameters);
            }
            return provider;
        }
    }
}
