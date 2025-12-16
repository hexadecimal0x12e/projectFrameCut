using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.Render.Plugin
{
    public static class PluginManager
    {
        public const int CurrentPluginAPIVersion = 1;
        private static Dictionary<string, IPluginBase> loadedPlugins = new();
        public static IReadOnlyDictionary<string, IPluginBase> LoadedPlugins => loadedPlugins;
        private static bool Inited = false;

        public static Func<string, string?>? ExtenedLocalizationGetter = null;
        public static string CurrentLocale = "en-US";

        public static void Init(IEnumerable<IPluginBase> plugins)
        {
            if (Inited) throw new InvalidOperationException("PluginManager has already been initialized.");
            Inited = true;
            loadedPlugins.Clear();
            foreach (var plugin in plugins)
            {
                loadedPlugins.Add(plugin.PluginID, plugin);
                Logger.Log($"Plugin {plugin.PluginID} loaded.");
#if DEBUG
                Logger.Log(GetWhatProvided(plugin));

#endif
            }
        }

        public static string GetWhatProvided(IPluginBase pluginBase)
        {
            StringBuilder providedContent = new($"{pluginBase.Name} ({pluginBase.PluginID}) provide these:\r\n");
            providedContent.AppendLine("Clips:");
            foreach (var item in pluginBase.ClipProvider)
            {
                providedContent.AppendLine($"- {item.Key}");
            }
            providedContent.AppendLine("Effect:");
            foreach (var item in pluginBase.EffectProvider)
            {
                providedContent.AppendLine($"- {item.Key}");
            }
            providedContent.AppendLine("Mixture:");
            foreach (var item in pluginBase.MixtureProvider)
            {
                providedContent.AppendLine($"- {item.Key}");
            }
            providedContent.AppendLine("Computer:");
            foreach (var item in pluginBase.ComputerProvider)
            {
                providedContent.AppendLine($"- {item.Key}");
            }
            providedContent.AppendLine("VideoSource:");
            foreach (var item in pluginBase.VideoSourceProvider)
            {
                providedContent.AppendLine($"- {item.Key}");
            }
            return providedContent.ToString();
        }

        public static void LoadFrom(IPluginBase pluginInstance)
        {
#if !DEBUG
            throw new InvalidOperationException("Loading plugins at runtime is not supported in release builds.");
#endif
            ArgumentNullException.ThrowIfNull(pluginInstance, nameof(pluginInstance));
            try
            {
                if (pluginInstance.PluginAPIVersion == CurrentPluginAPIVersion)
                {
                    loadedPlugins.Add(pluginInstance.PluginID, pluginInstance);
                }
                else
                {
                    Logger.Log($"Plugin {pluginInstance.Name} has incompatible API version {pluginInstance.PluginAPIVersion}, expected {CurrentPluginAPIVersion}.", "warning");
                }
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "load plugins from assembly", "PluginManager");
            }

            Logger.Log($"Plugin {pluginInstance.PluginID} loaded.");

        }

        public static string GetLocalizationItem(string key, string fallback)
        {
            foreach (var plugin in LoadedPlugins.Values)
            {
                var localizedString = plugin.ReadLocalizationItem(key, CurrentLocale);
                if (!string.IsNullOrEmpty(localizedString))
                {
                    return localizedString;
                }
            }
            if (ExtenedLocalizationGetter != null)
            {
                var str = ExtenedLocalizationGetter(key);
                return str ?? fallback;
            }
            return fallback;
        }

        public static IClip CreateClip(JsonElement source)
        {
            var type = source.GetProperty("FromPlugin").GetString();
            var name = source.GetProperty("Name").GetString();

            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("Invalid clip data.");
            }

            if (PluginManager.LoadedPlugins.TryGetValue(type, out var plugin))
            {
                return plugin.ClipCreator(source);
            }
            else
            {
                throw new ArgumentException($"Plugin not found: {type}");
            }

        }

        public static IEffect CreateEffect(EffectAndMixtureJSONStructure stru)
        {
            if (PluginManager.LoadedPlugins.TryGetValue(stru.FromPlugin, out var plugin))
            {
                return plugin.EffectCreator(stru);
            }
            else
            {
                throw new ArgumentException($"Plugin not found: {stru.FromPlugin}");
            }
        }

        public static IVideoSource CreateVideoSource(string filePath)
        {
            foreach (var plugin in LoadedPlugins.Values)
            {
                try
                {
                    var source = plugin.VideoSourceCreator(filePath);
                    if (source != null)
                    {
                        return source;
                    }
                }
                catch
                {
                    // Ignore and try next plugin
                }
            }
            throw new NotSupportedException($"No suitable video source found for the given file '{filePath}'.");
        }

        private static Dictionary<string, IComputer> ComputerCache = new();

        public static IComputer CreateComputer(string computerType, bool forceCreate = false)
        {
            if(!forceCreate && ComputerCache.TryGetValue(computerType, out var cachedComputer))
            {
                return cachedComputer;
            }
            foreach (var plugin in LoadedPlugins.Values)
            {
                try
                {
                    var computer = plugin.ComputerCreator(computerType);
                    if (computer != null)
                    {
                        ComputerCache[computerType] = computer;
                        return computer;
                    }
                }
                catch
                {
                    // Ignore and try next plugin
                }
            }
            throw new NotSupportedException($"No suitable computer found for the given type '{computerType}'.");
        }

    }
}
