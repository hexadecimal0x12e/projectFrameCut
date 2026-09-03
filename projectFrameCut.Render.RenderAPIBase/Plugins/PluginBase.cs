using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.RenderAPIBase.Plugins
{
    /// <summary>
    /// The base interface for all plugins.
    /// </summary>
    public interface IPluginBase
    {
        /// <summary>
        /// Get the current plugin API version.
        /// </summary>
        public const int CurrentPluginAPIVersion = 8;

        /// <summary>
        /// The unique identifier of the plugin. Must equal to the full name of the main class implementing IPluginBase.
        /// </summary>
        public string PluginID { get; }
        /// <summary>
        /// The supported API version of the plugin.
        /// </summary>
        public int PluginAPIVersion { get; }
        /// <summary>
        /// Get the minor version of plugin. Default to 0.
        /// </summary>
        public virtual int PluginAPIMinorVersion => 0;

        /// <summary>
        /// The plugin's name.
        /// </summary>
        /// <remarks>
        /// for this field, it's localized key is '_PluginBase_Name_'.
        /// </remarks>
        public string Name { get; }
        /// <summary>
        /// Plugin's author.
        /// </summary>
        /// <remarks>
        /// for this field, it's localized key is '_PluginBase_Author_'.
        /// </remarks>
        public string Author { get; }
        /// <summary>
        /// Description of the plugin.
        /// </summary>
        /// <remarks>
        /// for this field, it's localized key is '_PluginBase_Description_'.
        /// </remarks>
        public string Description { get; }
        /// <summary>
        /// The version of the plugin.
        /// </summary>
        public Version Version { get; }
        /// <summary>
        /// Author or project's homepage.
        /// </summary>
        public string AuthorUrl { get; }
        /// <summary>
        /// The publish page of the plugin. Used for update checking.
        /// </summary>
        public string? PublishingUrl { get; }

        /// <summary>
        /// Get the properties of the plugin.
        /// </summary>
        /// <remarks>
        /// Default implementation returns an empty dictionary, override it to provide actual properties.
        /// </remarks>
        public virtual IReadOnlyDictionary<string, string> Properties => blankProperties;

        private static Dictionary<string, string> blankProperties = new();

        /// <summary>
        /// Represents the localization strings provided by the plugin.
        /// </summary>
        /// <remarks>
        /// For each key, it represents the locate code (like 'en-US'), and it's values represents the mapping of the localization strings.
        /// The first key of <see cref="LocalizationProvider"/> is the default localization.
        /// </remarks>
        public Dictionary<string, Dictionary<string, string>> LocalizationProvider { get; }

        /// <summary>
        /// Create an <see cref="IEffectProvider"/> instance for the given effect type name (e.g. "Resize").
        /// The provider owns both the property metadata (fields/parameters) and the effect factory capability
        /// (<see cref="IEffectProvider.RestoreInstance(EffectImplementType, Dictionary{string, object})"/>).
        /// </summary>
        public Dictionary<string, Func<IEffectProvider>> EffectProviderProvider { get; }

        /// <summary>
        /// Create an ISoundTrack instance from the given file path and JSON data.
        /// </summary>
        /// <remarks>
        /// The argument for value is Id of the sound track, and the second argument is the name of the sound track.
        /// </remarks>
        public Dictionary<string, Func<string, string, ISoundTrack>> SoundTrackProvider { get; }

        /// <summary>
        /// Create an IClip instance from the given file path and JSON data.
        /// </summary>
        /// <remarks>
        /// The argument for value is Id of the previous clip, and the second argument is Id of the next clip
        /// </remarks>
        public Dictionary<string, Func<Guid, Guid, ITransform>> TransformProvider { get; }

        /// <summary>
        /// Create an IComputer instance from the given JSON structure.
        /// </summary>
        public Dictionary<string, Func<IComputer>> ComputerProvider { get; }

        /// <summary>
        /// Create an IVideoSource instance from the given file path.
        /// </summary>
        /// <remarks>
        /// When the argument is null or empty when creating a IVideoSource, the provider should return an instance that can be used to check for preferred extensions.
        /// </remarks>
        public Dictionary<string, IVideoSource> VideoSourceProvider { get; }

        /// <summary>
        /// Create an IAudioSource instance from the given file path.
        /// </summary>
        /// <remarks>
        /// When the argument is null or empty when creating a IAudioSource, the provider should return an instance that can be used to check for preferred extensions.
        /// </remarks>
        public Dictionary<string, Func<string, IAudioSource>> AudioSourceProvider { get; }

        /// <summary>
        /// Create an IVideoWriter instance from the given file path.
        /// </summary>
        /// <remarks>
        /// The key of each item is NOT used in detect the video writer type from file extension. They can be 100% meaningless and doesn't need to be the same as the file extension. 
        /// The value is a function that takes a file path and returns an IVideoWriter instance.
        /// Implement <see cref="IVideoWriter.SupportCodec(string)"/> to allow render engine to check for preferred extensions.
        /// </remarks>
        public Dictionary<string, Func<string, IVideoWriter>> VideoWriterProvider { get; }


        /// <summary>
        /// Get or set the configuration of the plugin.
        /// </summary>
        /// <remarks>
        /// The default implementation is the default settings value of this plugin.
        /// </remarks>
        public Dictionary<string, string> Configuration { get; set; }

        /// <summary>
        /// Represents the display strings for each configuration key.
        /// </summary>
        /// <remarks>
        /// Same as <see cref="LocalizationProvider"/>,
        /// each key represents the locate code (like 'en-US'), and it's values represents the mapping of the setting strings. 
        /// For each locate's mapping, the key is the setting key, and the value is the display name.
        /// </remarks>
        public Dictionary<string, Dictionary<string, string>> ConfigurationDisplayString { get; }

        /// <summary>
        /// Read a localization item from the provider.
        /// </summary>
        /// <remarks>
        /// If you don't override this method, the default implementation will first try to find the localization item from the given locate.
        /// </remarks>
        /// <param name="key"></param>
        /// <param name="locate"></param>
        /// <returns>string if key exists; null if key not exist.</returns>
        public virtual string? ReadLocalizationItem(string key, string locate)
        {
            if (LocalizationProvider.TryGetValue(locate, out var pair))
            {
                if (pair.TryGetValue(key, out var result)) return result;
            }
            else
            {
                if (!LocalizationProvider.Any()) return null;
                if (LocalizationProvider.First().Value.TryGetValue(key, out var result)) return result;
            }
            return null;
        }

        /// <summary>
        /// Obtains an instance of IClip from the given JSON element. Let this method throw an <see cref="NotImplementedException"/> (default behavior in API V7+) to indicate that this plugin does not provide any clip.
        /// </summary>
        /// <param name="element">the source element</param>
        /// <returns>the clip</returns>
        /// <exception cref="NotImplementedException">indicates that this plugin does not provide any clip.</exception>
        public virtual IClip ClipCreator(JsonElement element)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Obtains an instance of ISoundTrack from the given JSON element. Let this method throw an <see cref="NotImplementedException"/> (default behavior in API V7+) to indicate that this plugin does not provide any soundtrack.
        /// </summary>
        /// <param name="element">the source element</param>
        /// <returns>the soundtrack</returns>
        /// <exception cref="NotImplementedException">indicates that this plugin does not provide any clip.</exception>
        public virtual ISoundTrack SoundTrackCreator(JsonElement element)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Obtains an instance of ITransform from the given JSON element. Let this method throw an <see cref="NotImplementedException"/> to indicate that this plugin does not provide any transform.
        /// </summary>
        /// <param name="element">the source element</param>
        /// <returns>the transform</returns>
        /// <exception cref="NotImplementedException">indicates that this plugin does not provide any transform.</exception>
        public virtual ITransform TransformCreator(JsonElement element)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Obtains an instance of IVectorComponent from the given JSON element. Let this method throw an <see cref="NotImplementedException"/> to indicate that this plugin does not provide any vector component.
        /// </summary>
        /// <param name="element">the source element</param>
        /// <returns>the vector component</returns>
        /// <exception cref="NotImplementedException">indicates that this plugin does not provide any vector component.</exception>
        public virtual IVectorComponent VectComponentCreator(JsonElement element)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Creates an effect instance from the given JSON structure.
        /// </summary>
        /// <remarks>
        /// We provide you a default implement, which can cover 99% of purpose when you make providers correctly,
        /// so you probably don't need to override this method unless you have some special needs that cannot be
        /// achieved by providers, or you want to support some special effect types that are not covered by current implementation.
        /// </remarks>
        /// <param name="stru">the source structure</param>
        /// <returns>the effect</returns>
        /// <exception cref="NotSupportedException"></exception>
        public virtual IEffect EffectCreator(EffectAndMixtureJSONStructure stru, EffectImplementType implementType = EffectImplementType.NotSpecified)
        {
#pragma warning disable CS0618 // we need to handle fallback for deprecated implement types for compatibility, but we don't want to have Obsolete warning in the main logic.
            if (implementType == EffectImplementType.ImageSharp_Deprecated) implementType = EffectImplementType.IPicture;
            if (stru.ImplementType == EffectImplementType.ImageSharp_Deprecated) stru.ImplementType = EffectImplementType.IPicture;
            static IEffect ApplyCommonProperties(IEffect effect, EffectAndMixtureJSONStructure s)
            {
                effect.Name = s.Name;
                effect.BindedEffectProvidingSystemID = s.BindedEffectGroupID;
                if (effect.TypeOfEffect != EffectType.SpeedVarianceProvider)
                {
                    effect.RelativeWidth = s.RelativeWidth;
                    effect.RelativeHeight = s.RelativeHeight;
                    effect.Enabled = s.Enabled;
                }

                // Restore IBindableArgumentEffect properties
                if (effect is IBindableArgumentEffect bindableEffect)
                {
                    if (!string.IsNullOrEmpty(s.Id))
                    {
                        bindableEffect.Id = s.Id;
                    }
                    if (!string.IsNullOrEmpty(s.BindedInputID))
                    {
                        bindableEffect.BindedArgumentProviderID = s.BindedInputID;
                    }
                }

                return effect;
            }

            static Dictionary<string, object> ConvertParams(Dictionary<string, object>? source, Dictionary<string, string> parameterTypes)
            {
                source ??= new Dictionary<string, object>();
                return EffectArgsHelper.ConvertElementDictToObjectDict(source, parameterTypes);
            }

            if (!EffectProviderProvider.TryGetValue(stru.TypeName, out var creator))
            {
                throw new KeyNotFoundException($"No suitable effect provider found for the given type '{stru.TypeName}'. If you are trying to use a custom effect, make sure it is properly registered.");
            }

            var provider = creator();
            var parameters = ConvertParams(stru.Parameters, provider.ParametersType);
            // The continuous-Crop branch (and any future dual-mode provider) is driven by this reserved key.
            if (stru.IsContinuousEffect)
            {
                provider.MetaData[IEffectProvider.IsContinuousEffectParameterKey] = true;
            }

            var effect = (implementType != EffectImplementType.NotSpecified && provider.SupportsImplementTypes.Contains(implementType))
                ? provider.RestoreInstance(implementType, parameters)
                : provider.RestoreInstanceWithDefaultType(parameters);
            return ApplyCommonProperties(effect, stru);
#pragma warning restore CS0618
        }


        /// <summary>
        /// Create a VideoSource instance from the file.
        /// This method will first try to find a preferred video source by file extension,
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns></returns>
        /// <exception cref="NotSupportedException"></exception>
        public virtual IVideoSource VideoSourceCreator(string filePath)
        {
            var prefered = VideoSourceProvider.Values.Where((k) => k.PreferredExtension.Contains(Path.GetExtension(filePath)));
            if (prefered.Any())
            {
                return prefered.First().CreateNew(filePath);
            }
            else
            {
                foreach (var provider in VideoSourceProvider.Values)
                {
                    var instance = provider.CreateNew(filePath);
                    if (instance.TryInitialize())
                    {
                        return instance;
                    }
                    else
                    {
                        instance.Dispose();
                    }
                }
            }
            throw new NotSupportedException($"No suitable video source found for the given file '{filePath}'.");
        }
        /// <summary>
        /// Create a AudioSource instance from the file.
        /// This method will first try to find a preferred audio source by file extension,
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns></returns>
        /// <exception cref="NotSupportedException"></exception>
        public virtual IAudioSource AudioSourceCreator(string filePath)
        {
            var prefered = AudioSourceProvider.Values.Where((k) => k(null!).PreferredExtension.Contains(Path.GetExtension(filePath)));
            if (prefered.Any())
            {
                return prefered.First()(null!).CreateNew(filePath);
            }
            else
            {
                foreach (var provider in AudioSourceProvider.Values)
                {
                    var instance = provider(filePath);
                    if (instance.TryInitialize())
                    {
                        return instance;
                    }
                    else
                    {
                        instance.Dispose();
                    }
                }
            }
            throw new NotSupportedException($"No suitable audio source found for the given file '{filePath}'.");
        }

        /// <summary>
        /// Create a VideoSource instance using the given decoder.
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns></returns>
        /// <exception cref="NotSupportedException"></exception>
        public virtual IVideoSource VideoSourceCreator(string filePath, string decoderName)
        {
            if (VideoSourceProvider.TryGetValue(decoderName, out var value))
            {
                return value.CreateNew(filePath);
            }
            throw new NotSupportedException($"Video source '{decoderName}' not found.");
        }
        /// <summary>
        /// Create a AudioSource instance using the given decoder.
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns></returns>
        /// <exception cref="NotSupportedException"></exception>
        public virtual IAudioSource AudioSourceCreator(string filePath, string decoderName)
        {
            if (AudioSourceProvider.TryGetValue(decoderName, out var value))
            {
                return value(filePath);
            }
            throw new NotSupportedException($"Audio source '{decoderName}' not found.");
        }

        /// <summary>
        /// Create a mixture instance from the given JSON structure.
        /// </summary>
        /// <param name="computerType"></param>
        /// <returns></returns>
        /// <exception cref="NotSupportedException"></exception>
        public IComputer ComputerCreator(string computerType)
        {
            if (ComputerProvider.TryGetValue(computerType, out var creator))
            {
                return creator();
            }
            else
            {
                throw new NotSupportedException($"No suitable computer found for the given type '{computerType}'.");
            }
        }

        /// <summary>
        /// Invoked when the plugin is loaded. Return true if loaded successfully, false otherwise.
        /// </summary>
        /// <param name="FailedReason">The reason for failure if loading was unsuccessful. Will be displayed in UI</param>
        /// <returns>whether the plugin was loaded successfully</returns>
        public virtual bool OnLoaded(out string FailedReason)
        {
            FailedReason = string.Empty;
            return true;
        }

        /// <summary>
        /// Called when a project is loaded.
        /// </summary>
        /// <param name="project">The loaded project</param>
        /// <returns>If return a non-null value this will replace the project with the returned value</returns>
        public virtual ProjectJSONStructure? OnProjectLoad(ProjectJSONStructure project)
        {
            return null;
        }
        /// <summary>
        /// Called when a project is saved.
        /// </summary>
        /// <param name="project">The loaded project</param>
        /// <returns>If return a non-null value this will replace the project with the returned value</returns>
        public virtual ProjectJSONStructure? OnProjectSave(ProjectJSONStructure project)
        {
            return null;
        }
        /// <summary>
        /// Called when a project is unloaded, or program exited normally.
        /// </summary>
        /// <param name="project">The loaded project</param>
        /// <returns>If return a non-null value this will replace the project with the returned value</returns>
        public virtual ProjectJSONStructure? OnProjectClose(ProjectJSONStructure project)
        {
            return null;
        }

        /// <summary>
        /// Called when the application is closing, or this plugin is being unloaded.
        /// </summary>
        public virtual void OnClosing()
        {

        }
    }

#pragma warning disable CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。

    public class PluginMetadata
    {
        /// <summary>
        /// The package format version. External plugins must use version 2 or later.
        /// </summary>
        public int PackageFormatVersion { get; set; }

        /// <summary>
        /// SHA-256 fingerprint of the publisher CA certificate.
        /// </summary>
        public string PublisherId { get; set; } = string.Empty;

        /// <summary>
        /// SHA-256 fingerprint of the end-entity certificate that signed this package.
        /// </summary>
        public string SigningCertificateFingerprint { get; set; } = string.Empty;

        /// <summary>
        /// The unique identifier of the plugin. Must equal to the full name of the main class implementing IPluginBase.
        /// </summary>
        public string PluginID { get; set; }
        /// <summary>
        /// The supported API version of the plugin.
        /// </summary>
        public int PluginAPIVersion { get; set; }
        /// <summary>
        /// Get the minor version of plugin. Default to 0.
        /// </summary>
        public int PluginAPIMinorVersion { get; set; } = 0;
        /// <summary>
        /// The plugin's name.
        /// </summary>
        public string Name { get; set; }
        /// <summary>
        /// Plugin's author.
        /// </summary>
        public string Author { get; set; }
        /// <summary>
        /// Description of the plugin.
        /// </summary>
        public string Description { get; set; }
        /// <summary>
        /// The version of the plugin.
        /// </summary>
        public Version Version { get; set; }
        /// <summary>
        /// Author or project's homepage.
        /// </summary>
        public string AuthorUrl { get; set; }
        /// <summary>
        /// The publish page of the plugin. Used for update checking.
        /// </summary>
        public string? PublishingUrl { get; set; }

        /// <summary>
        /// The encrypt key for a .NET Assembly-based plugin.
        /// For more information, see the bundler's documentation.
        /// </summary>
        public string PluginKey { get; set; }

        /// <summary>
        /// Hash of the plugin's source or assembly file.
        /// </summary>
        public string PluginHash { get; set; }

        public static string GetWhatProvided(IPluginBase pluginBase)
        {
            StringBuilder providedContent = new($"{pluginBase.Name} ({pluginBase.PluginID}) provide these:\r\n");

            // ----- Content Sources -----
            if (pluginBase.VideoSourceProvider.Any())
            {
                providedContent.AppendLine("VideoSource:");
                foreach (var item in pluginBase.VideoSourceProvider)
                {
                    providedContent.AppendLine($"- {item.Key}");
                }
            }
            if (pluginBase.AudioSourceProvider.Any())
            {
                providedContent.AppendLine("AudioSource:");
                foreach (var item in pluginBase.AudioSourceProvider)
                {
                    providedContent.AppendLine($"- {item.Key}");
                }
            }
            if (pluginBase.SoundTrackProvider.Any())
            {
                providedContent.AppendLine("SoundTrack:");
                foreach (var item in pluginBase.SoundTrackProvider)
                {
                    providedContent.AppendLine($"- {item.Key}");
                }
            }

            // ----- Transforms -----
            if (pluginBase.TransformProvider.Any())
            {
                providedContent.AppendLine("Transform:");
                foreach (var item in pluginBase.TransformProvider)
                {
                    providedContent.AppendLine($"- {item.Key}");
                }
            }

            // ----- Effects -----
            if (pluginBase.EffectProviderProvider.Any())
            {
                providedContent.AppendLine("Effect:");
                foreach (var key in pluginBase.EffectProviderProvider.Keys)
                {
                    providedContent.AppendLine($"- {key}");
                }
            }

            // ----- Computers / Mixtures -----
            if (pluginBase.ComputerProvider.Any())
            {
                providedContent.AppendLine("Computer:");
                foreach (var item in pluginBase.ComputerProvider)
                {
                    providedContent.AppendLine($"- {item.Key}");
                }
            }

            // ----- Output / Export -----
            if (pluginBase.VideoWriterProvider.Any())
            {
                providedContent.AppendLine("VideoWriter:");
                foreach (var item in pluginBase.VideoWriterProvider)
                {
                    providedContent.AppendLine($"- {item.Key}");
                }
            }

            // ----- Localization -----
            if (pluginBase.LocalizationProvider.Any())
            {
                providedContent.AppendLine("Localization:");
                foreach (var locale in pluginBase.LocalizationProvider.Keys)
                {
                    var count = pluginBase.LocalizationProvider[locale]?.Count ?? 0;
                    providedContent.AppendLine($"- {locale} ({count} entries)");
                }
            }

            // ----- Configuration -----
            if (pluginBase.Configuration.Any())
            {
                providedContent.AppendLine("Configuration Keys:");
                foreach (var kv in pluginBase.Configuration)
                {
                    providedContent.AppendLine($"- {kv.Key} = {kv.Value}");
                }
            }

            // ----- Custom Properties -----
            if (pluginBase.Properties.Any())
            {
                providedContent.AppendLine("Properties:");
                foreach (var kv in pluginBase.Properties)
                {
                    providedContent.AppendLine($"- {kv.Key} = {kv.Value}");
                }
            }

            return providedContent.ToString();
        }
    }
#pragma warning restore CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。

}
