using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using projectFrameCut.Drawing.Vector.ImportExport;

namespace projectFrameCut.Render.Plugin
{
    public static class PluginManager
    {
        public const int CurrentPluginAPIVersion = IPluginBase.CurrentPluginAPIVersion;
        private static Dictionary<string, IPluginBase> loadedPlugins = new();
        public static IReadOnlyDictionary<string, IPluginBase> LoadedPlugins => loadedPlugins;
        public static bool Inited { get; private set; } = false;

        public static Func<string, string?>? ExtenedLocalizationGetter = null;
        public static string CurrentLocale = "en-US";

        public static void InitGlobalGetter()
        {
            if (GlobalPluginHelper.PluginGetter is null)
            {
                //LogDiagnostic($"Initing Global getter, {Environment.StackTrace}");
                GlobalPluginHelper.PluginGetter = (id) =>
                {
                    if (loadedPlugins.TryGetValue(id, out IPluginBase? value))
                    {
                        return value;
                    }
                    return null;
                };
                return;
            }
            throw new InvalidOperationException("GlobalPluginHelper has already been initialized.");
        }

        public static void Init(IEnumerable<IPluginBase> plugins)
        {
            if (Inited) throw new InvalidOperationException("PluginManager has already been initialized.");
            Inited = true;
            loadedPlugins.Clear();
            foreach (var plugin in plugins)
            {
                if (plugin.Properties.TryGetValue("IsInternalPlugin", out var value) && bool.TryParse(value, out var result) && result) plugin.OnLoaded(out _);
                loadedPlugins.Add(plugin.PluginID, plugin);
                Logger.Log($"Plugin {plugin.PluginID} loaded.");
            }


        }

        public static void Unload(string id)
        {
            if (loadedPlugins.TryGetValue(id, out IPluginBase? value))
            {
                try
                {
                    value.OnClosing();
                }
                catch { }
                loadedPlugins.Remove(id);
                Logger.Log($"Plugin {id} unloaded.");
            }
        }

        public static void ForceUnloadAll()
        {
            foreach (var item in loadedPlugins)
            {
                try
                {
                    item.Value.OnClosing();
                }
                catch { }
            }
            loadedPlugins.Clear();
            Inited = false;
        }

        public static void LoadFrom(IPluginBase pluginInstance)
        {
            ArgumentNullException.ThrowIfNull(pluginInstance, nameof(pluginInstance));
            try
            {
                if (pluginInstance.PluginAPIVersion == CurrentPluginAPIVersion)
                {
                    if (!loadedPlugins.TryAdd(pluginInstance.PluginID, pluginInstance))
                    {
                        loadedPlugins[pluginInstance.PluginID] = pluginInstance;
                    }
                }
                else
                {
                    throw new InvalidProgramException($"Plugin {pluginInstance.Name} has incompatible API version {pluginInstance.PluginAPIVersion}, expected {CurrentPluginAPIVersion}.");
                }
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "load plugins from assembly", "PluginManager");
            }

            Logger.Log($"Plugin {pluginInstance.PluginID} loaded.");

        }

        public static void UnloadPlugin(string id)
        {
            if (loadedPlugins.TryGetValue(id, out IPluginBase? value))
            {
                value.OnClosing();
                loadedPlugins.Remove(id);
                Logger.Log($"Plugin {id} unloaded.");
            }
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
            var localizedString = src?.ReadLocalizationItem(key, CurrentLocale);
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
                var clip = plugin.ClipCreator(source);
                clip.ExtraData = source.Deserialize<ClipDraftDTO>()?.MetaData ?? new();
                if (clip is IVectorContentClip vectorClip)
                {
                    vectorClip.ClipAntiAliasMode = source.TryGetProperty("VectorAntiAliasMode", out var aaModeProp) && aaModeProp.ValueKind == JsonValueKind.String
                        ? Enum.TryParse<AntiAliasMode>(aaModeProp.GetString(), out var parsedMode) && Enum.IsDefined<AntiAliasMode>(parsedMode) ? parsedMode : null
                        : null;
                }
                return clip;
            }
            else
            {
                throw new ArgumentException($"Plugin not found: {type}");
            }
        }

        public static ITransform CreateTransform(JsonElement source)
        {
            var plug = source.GetProperty("FromPlugin").GetString();
            var type = source.GetProperty("TypeName").GetString();
            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(plug))
            {
                throw new ArgumentException("Invalid transform data.");
            }

            if (PluginManager.LoadedPlugins.TryGetValue(plug, out var plugin))
            {
                return plugin.TransformCreator(source);
            }
            throw new ArgumentException($"Plugin not found: {type}");
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

        public static IEffect CreateEffect(EffectAndMixtureJSONStructure stru, EffectImplementType type = EffectImplementType.NotSpecified)
        {
            IEffect effect = null!;
            if (PluginManager.LoadedPlugins.TryGetValue(stru.FromPlugin, out var plugin))
            {
                try
                {
                    effect = plugin.EffectCreator(stru, type);
                }
                catch
                {
                    try
                    {
                        effect = plugin.EffectCreator(stru, EffectImplementType.NotSpecified);
                    }
                    catch (Exception ex)
                    {
                        Log(ex, $"Create effect {stru.Name}/{stru.TypeName}", effect);
                        throw;
                    }
                }
                try
                {
                    effect.Index = stru.Index;
                    effect.Enabled = stru.Enabled;
                    effect.BindedEffectGroupID = stru.BindedEffectGroupID;
                    effect.Initialize();
                }
                catch (Exception ex)
                {
                    Log(ex, $"Init effect {effect?.Name}/{stru.TypeName}", effect);
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
                var effect = plugin.EffectCreator(stru);
                effect.Index = stru.Index;
                effect.Enabled = stru.Enabled;
                effect.BindedEffectGroupID = stru.BindedEffectGroupID;
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

        public static IVideoSource CreateVideoSource(string filePath, IPicture.PicturePixelMode? PreferredTargetPPB = null)
        {
            if (filePath.StartsWith("#"))
            {
                var part = filePath.Substring(1).Split(',', 2);
                var decoder = part[0];
                var supportedPlugin = LoadedPlugins.Values.FirstOrDefault(p => p.VideoSourceProvider.ContainsKey(decoder));
                if (supportedPlugin is null) throw new NotSupportedException($"The specificed video decoder '{decoder}' was not found for the file '{filePath}'.");
                return supportedPlugin.VideoSourceProvider[decoder](null!).CreateNew(part[1]);

            }
            else if (!File.Exists(filePath) && !filePath.StartsWith("#"))
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
                catch (Exception ex)
                {
                    if (Debugger.IsAttached) Log(ex, $"Init decoder for {filePath}");
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

        public static IVideoWriter CreateVideoWriter(string codec)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(codec);
            var codecCandidates = GetVideoWriterCodecCandidates(codec);

            foreach (var plugin in LoadedPlugins.Values)
            {
                try
                {
                    foreach (var item in plugin.VideoWriterProvider)
                    {
                        var keyMatch = codecCandidates.FirstOrDefault(c => string.Equals(item.Key, c, StringComparison.OrdinalIgnoreCase));
                        if (keyMatch is not null)
                        {
                            var writer = item.Value(keyMatch);
                            TryAssignWriterCodec(writer, keyMatch);
                            return writer;
                        }
                    }
                }
                catch
                {
                    // Ignore and try next plugin
                }
            }

            // Fallback: probe each writer implementation by asking whether it supports the codec.
            // IMPORTANT: dispose non-selected instances to avoid leaking unmanaged resources.
            foreach (var candidateCodec in codecCandidates)
            {
                foreach (var plugin in LoadedPlugins.Values)
                {
                    foreach (var item in plugin.VideoWriterProvider)
                    {
                        IVideoWriter? instance = null;
                        var selected = false;
                        try
                        {
                            instance = item.Value(candidateCodec);
                            if (instance.SupportCodec(candidateCodec))
                            {
                                TryAssignWriterCodec(instance, candidateCodec);
                                selected = true;
                                return instance;
                            }
                        }
                        catch
                        {
                            // Ignore and try next plugin/writer
                        }
                        finally
                        {
                            if (!selected && instance is not null)
                            {
                                try { instance.Dispose(); } catch { }
                            }
                        }
                    }
                }
            }

            throw new NotSupportedException($"No suitable video writer found for the codec '{codec}'. Tried candidates: {string.Join(", ", codecCandidates)}.");
        }

        private static void TryAssignWriterCodec(IVideoWriter writer, string codecName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(writer.CodecName) || !writer.SupportCodec(writer.CodecName))
                {
                    writer.CodecName = codecName;
                }
            }
            catch
            {
                // Keep writer as-is when plugin doesn't expose codec assignment.
            }
        }

        private static List<string> GetVideoWriterCodecCandidates(string codec)
        {
            var requestedCodec = codec.Trim();
            var candidates = new List<string>();

            void AddCandidate(string name)
            {
                if (!string.IsNullOrWhiteSpace(name) && !candidates.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(name);
                }
            }

            AddCandidate(requestedCodec);

            switch (requestedCodec.ToLowerInvariant())
            {
                case "h264":
                case "avc":
                case "avc1":
                case "x264":
                case "libx264":
                    AddCandidate("libx264");
                    AddCandidate("h264_nvenc");
                    AddCandidate("h264_qsv");
                    AddCandidate("h264_amf");
                    AddCandidate("h264_videotoolbox");
                    AddCandidate("h264");
                    break;

                case "h265":
                case "hevc":
                case "h265/hevc":
                case "x265":
                case "libx265":
                    AddCandidate("libx265");
                    AddCandidate("hevc_nvenc");
                    AddCandidate("hevc_qsv");
                    AddCandidate("hevc_amf");
                    AddCandidate("hevc_videotoolbox");
                    AddCandidate("hevc");
                    break;

                case "av1":
                    AddCandidate("libaom-av1");
                    AddCandidate("svtav1");
                    AddCandidate("rav1e");
                    AddCandidate("av1_nvenc");
                    AddCandidate("av1_qsv");
                    AddCandidate("av1_amf");
                    AddCandidate("av1");
                    break;
            }

            try
            {
                var availableVideoEncoders = FFmpegHelper.CodecUtils
                    .GetCodecsByType(FFmpeg.AutoGen.AVMediaType.AVMEDIA_TYPE_VIDEO, true)
                    .Select(c => c.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var supportedCandidates = candidates
                    .Where(c => availableVideoEncoders.Contains(c))
                    .ToList();

                if (supportedCandidates.Count > 0)
                {
                    return supportedCandidates;
                }
            }
            catch
            {
                // Fallback to static candidate list when ffmpeg probing isn't available.
            }

            return candidates;
        }

        private static readonly ConcurrentDictionary<string, IComputer> ComputerCache = new();

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static IComputer? CreateComputer(string? computerType, bool forceCreate = false)
        {
            if (computerType is null) return null;
            if (!forceCreate && ComputerCache.TryGetValue(computerType, out var cachedComputer))
                return cachedComputer;

            return GetComputerInternal(computerType, forceCreate);
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        private static IComputer GetComputerInternal(string computerType, bool forceCreate)
        {
            foreach (var plugin in LoadedPlugins.Values)
            {
                try
                {
                    if (plugin.ComputerProvider.TryGetValue(computerType, out var creator))
                    {
                        var computer = creator();
                        if (computer != null)
                        {
                            if (forceCreate)
                            {
                                return computer;
                            }
                            return ComputerCache.GetOrAdd(computerType, computer);
                        }
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
