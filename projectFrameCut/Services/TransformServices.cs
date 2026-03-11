using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using System;
using System.Collections.Generic;
using ITransform = projectFrameCut.Render.RenderAPIBase.ClipAndTrack.ITransform;

namespace projectFrameCut.Services
{
    /// <summary>
    /// Helper service for discovering ITransform factories from all loaded plugins.
    /// </summary>
    public static class TransformServices
    {
        /// <summary>
        /// Collects all available transform factories from all loaded plugins.
        /// </summary>
        /// <returns>
        /// Dictionary where key = transform type name (e.g. "Crossfade") and
        /// value = factory that takes (prevClipId, nextClipId) and returns an <see cref="ITransform"/>.
        /// </returns>
        public static Dictionary<string, Func<Guid, Guid, ITransform>> GetAvailableTransforms()
        {
            var result = new Dictionary<string, Func<Guid, Guid, ITransform>>();
            if (!PluginManager.Inited) return result;

            foreach (var plugin in PluginManager.LoadedPlugins.Values)
            {
                if (plugin is IPluginBase pluginBase)
                {
                    try
                    {
                        var provider = pluginBase.TransformProvider;
                        if (provider is null) continue;

                        foreach (var kvp in provider)
                        {
                            if (!result.ContainsKey(kvp.Key))
                                result.Add(kvp.Key, kvp.Value);
                        }
                    }
                    catch (NotImplementedException)
                    {
                        // Plugin does not support transforms – skip silently.
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Returns display names for all available transforms, keyed by type name.
        /// Falls back to the type name itself when no localized display name is registered.
        /// </summary>
        public static Dictionary<string, string> GetLocalizedTransformNames()
        {
            var transforms = GetAvailableTransforms();
            var result = new Dictionary<string, string>();
            foreach (var kvp in transforms)
            {
                var dispName = PluginManager.GetLocalizationItem("DisplayName_Transform_" + kvp.Key, kvp.Key);
                result[kvp.Key] = dispName;
            }
            return result;
        }
    }
}
