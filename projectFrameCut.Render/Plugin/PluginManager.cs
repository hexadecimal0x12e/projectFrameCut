using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
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
                Logger.Log(PluginMetadata.GetWhatProvided(plugin));

#endif
            }
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

        public static string GetLocalizationItemInSpecificPlugin(this IPluginBase src, string key, string fallback)
        {
            var localizedString = src.ReadLocalizationItem(key, CurrentLocale);
            if (!string.IsNullOrEmpty(localizedString))
            {
                return localizedString;
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

        public static IClip CreateNewClip(string pluginID, string clipType, string id, string name)
        {
            if (PluginManager.LoadedPlugins.TryGetValue(pluginID, out var plugin))
            {
                if (plugin.ClipProvider.TryGetValue(clipType, out var creator))
                {
                    return creator(id, name);
                }
                else
                {
                    throw new ArgumentException($"Clip type not found: {clipType} in plugin {pluginID}");
                }
            }
            else
            {
                throw new ArgumentException($"Plugin not found: {pluginID}");
            }
        }

        public static ISoundTrack CreateSoundTrack(JsonElement source)
        {
            var type = source.GetProperty("FromPlugin").GetString();
            var name = source.GetProperty("Name").GetString();

            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("Invalid soundtrack data.");
            }

            if (PluginManager.LoadedPlugins.TryGetValue(type, out var plugin))
            {
                return plugin.SoundTrackCreator(source);
            }
            throw new ArgumentException($"Plugin not found: {type}");

        }

        public static ISoundTrack CreateNewSoundTrack(string pluginID, string soundTrackType, string id, string name)
        {
            if (PluginManager.LoadedPlugins.TryGetValue(pluginID, out var plugin))
            {
                if (plugin.SoundTrackProvider.TryGetValue(soundTrackType, out var creator))
                {
                    return creator(id, name);
                }
                else
                {
                    throw new ArgumentException($"SoundTrack type not found: {soundTrackType} in plugin {pluginID}");
                }
            }
            throw new ArgumentException($"Plugin not found: {pluginID}");

        }

        public static IEffect CreateEffect(EffectAndMixtureJSONStructure stru)
        {
            if (PluginManager.LoadedPlugins.TryGetValue(stru.FromPlugin, out var plugin))
            {
                var effect = plugin.EffectCreator(stru);
                effect.Index = stru.Index;
                effect.Enabled = stru.Enabled;
                try
                {
                    effect.Initialize();
                }
                catch (Exception ex)
                {
                    Log(ex, $"Init effect {effect.Name}", effect);
                    throw;
                }
                return effect;
            }
            else
            {
                throw new ArgumentException($"Plugin not found: {stru.FromPlugin}");
            }
        }
        public static IEffect CreateEffect(EffectAndMixtureJSONStructure stru, int relativeWidth, int relativeHeight)
        {
            // Only use the provided resolution as fallback when the effect doesn't have its own
            if (stru.RelativeWidth <= 0) stru.RelativeWidth = relativeWidth;
            if (stru.RelativeHeight <= 0) stru.RelativeHeight = relativeHeight;
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
            if(!File.Exists(filePath) && !filePath.StartsWith("#"))
            {
                throw new FileNotFoundException("The specified video file was not found.", filePath);
            }
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

        public static IAudioSource CreateAudioSource(string filePath)
        {
            if (!File.Exists(filePath) && !filePath.StartsWith("#"))
            {
                throw new FileNotFoundException("The specified video file was not found.", filePath);
            }
            foreach (var plugin in LoadedPlugins.Values)
            {
                try
                {
                    var source = plugin.AudioSourceCreator(filePath);
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
            throw new NotSupportedException($"No suitable audio source found for the given file '{filePath}'.");
        }

        public static IVideoWriter CreateVideoWriter(string filePath)
        {
            foreach (var plugin in LoadedPlugins.Values)
            {
                try
                {
                    foreach (var item in plugin.VideoWriterProvider)
                    {
                        var instance = item.Value(filePath);
                        if (instance.TryInitialize())
                        {
                            return instance;
                        }
                    }
                }
                catch
                {
                    // Ignore and try next plugin
                }
            }
            throw new NotSupportedException($"No suitable video writer found for the given file '{filePath}'.");
        }

        private static readonly ConcurrentDictionary<string, IComputer> ComputerCache = new();

        public static IComputer? CreateComputer(string? computerType, bool forceCreate = false)
        {
            if (computerType is null) return null;
            if (!forceCreate && ComputerCache.TryGetValue(computerType, out var cachedComputer))
                return cachedComputer;

            foreach (var plugin in LoadedPlugins.Values)
            {
                try
                {
                    var computer = plugin.ComputerCreator(computerType);
                    if (computer != null)
                    {
                        if (forceCreate)
                        {
                            // Caller explicitly requests a new instance (often for thread-safety).
                            return computer;
                        }

                        return ComputerCache.GetOrAdd(computerType, computer);
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
