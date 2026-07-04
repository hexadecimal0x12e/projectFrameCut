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
        public const int CurrentPluginAPIVersion = 6;

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
        /// Create an blank IEffect instance from the given id.
        /// </summary>
        /// <remarks>
        /// Although we have <see cref="IEffectFactory"/>, but you STILL need to register ONE implementation of IEffect here for each effect type.
        /// </remarks>
        public Dictionary<string, Func<IEffect>> EffectProvider { get; }

        /// <summary>
        /// Create an <see cref="IEffect"/> instance via <see cref="IEffectFactory"/>.
        /// Key is effect type name (e.g. "Resize").
        /// </summary>
        /// <remarks>
        /// This is also for the factory of <see cref="IColorAdjustEffect"/>, <see cref="IClipPositionProvider"/>, <see cref="IContinuousClipPositionProvider"/> and <see cref="ISpeedVarianceProvider"/>.
        /// </remarks>
        public Dictionary<string, IEffectFactory> EffectFactoryProvider { get; }
        /// <summary>
        /// Create an blank IEffect instance from the given id.
        /// </summary>
        /// <remarks>
        /// <b>DO NOT register</b> <see cref="IContinuousTextEffect"/> here. Register it in <see cref="EffectProvider"/>
        /// </remarks>
        public Dictionary<string, Func<IEffect>> ContinuousEffectProvider { get; }

        /// <summary>
        /// Create a continuous <see cref="IEffect"/> instance via <see cref="IEffectFactory"/>.
        /// </summary>
        /// <remarks>
        /// <b>DO NOT register</b> <see cref="IContinuousTextEffect"/> here. Register it in <see cref="EffectFactoryProvider"/>
        /// </remarks>
        public Dictionary<string, IEffectFactory> ContinuousEffectFactoryProvider { get; }

        /// <summary>
        /// Create an blank IEffect instance from the given id.
        /// </summary>
        public Dictionary<string, Func<IEffect>> BindableArgumentEffectProvider { get; }

        /// <summary>
        /// Create a variable-argument <see cref="IEffect"/> instance via <see cref="IEffectFactory"/>.
        /// </summary>
        public Dictionary<string, IEffectFactory> BindableArgumentEffectFactoryProvider { get; }

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
        public Dictionary<string, Func<string, IVideoSource>> VideoSourceProvider { get; }

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
        /// We provide you a default implement, which can cover 99% of purpose when you make factories correctly, so you probably don't need to override this method unless you have some special needs that cannot be achieved by factories, or you want to support some special effect types that are not covered by current implementation.
        /// </remarks>
        /// <param name="stru">the source structure</param>
        /// <returns>the effect</returns>
        /// <exception cref="NotSupportedException"></exception>
        public virtual IEffect EffectCreator(EffectAndMixtureJSONStructure stru, EffectImplementType implementType = EffectImplementType.NotSpecified)
        {
#pragma warning disable CS0618 // we need to handle fallback for deprecated implement types for compatibility, but we don't want to have Obsolete warning in the main logic.
            if (implementType == EffectImplementType.ImageSharp_Deprecated) implementType = EffectImplementType.IPicture;
            if (stru.ImplementType == EffectImplementType.ImageSharp_Deprecated) stru.ImplementType = EffectImplementType.IPicture;
#pragma warning restore CS0618 
            static IEffect ApplyCommonProperties(IEffect effect, EffectAndMixtureJSONStructure s)
            {
                effect.Name = s.Name;
                effect.BindedEffectGroupID = s.BindedEffectGroupID;
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

            if (stru.IsContinuousEffect)
            {
                if (ContinuousEffectFactoryProvider.TryGetValue(stru.TypeName, out var cFactory))
                {
                    if (implementType != EffectImplementType.NotSpecified && cFactory.SupportsImplementTypes.Contains(implementType))
                    {
                        return ApplyCommonProperties(cFactory.Build(implementType, ConvertParams(stru.Parameters, cFactory.ParametersType)), stru);
                    }
                    return ApplyCommonProperties(cFactory.BuildContinuousWithDefaultType(ConvertParams(stru.Parameters, cFactory.ParametersType)), stru);
                }
            }
            else if (stru.IsVariableArgumentEffect)
            {
                if (BindableArgumentEffectFactoryProvider.TryGetValue(stru.TypeName, out var vFactory))
                {
                    if (vFactory is IBindableEffectFactory bef)
                    {
                        if (implementType != EffectImplementType.NotSpecified && bef.SupportsImplementTypes.Contains(implementType))
                        {
                            return ApplyCommonProperties(bef.Build(implementType, stru.Id, stru.BindedInputID, stru.BindedInputIDs, ConvertParams(stru.Parameters, vFactory.ParametersType)), stru);
                        }
                        return ApplyCommonProperties(bef.BuildWithDefaultType(stru.Id, stru.BindedInputID, stru.BindedInputIDs, ConvertParams(stru.Parameters, vFactory.ParametersType)), stru);
                    }
                    else
                    {
                        throw new InvalidDataException($"{stru.Name} is marked as a variable argument effect but does not implement IBindableEffectFactory.");
                    }
                }
            }
            else
            {
                if (EffectFactoryProvider.TryGetValue(stru.TypeName, out var factory))
                {
                    if (implementType != EffectImplementType.NotSpecified && factory.SupportsImplementTypes.Contains(implementType))
                    {
                        return ApplyCommonProperties(factory.Build(implementType, ConvertParams(stru.Parameters, factory.ParametersType)), stru);
                    }
                    return ApplyCommonProperties(factory.BuildWithDefaultType(ConvertParams(stru.Parameters, factory.ParametersType)), stru);
                }
            }

            // Compatibility fallback: the serialized flags may be stale or inferred from interfaces.
            // Resolve by TypeName across all factory registries before failing.
            if (EffectFactoryProvider.TryGetValue(stru.TypeName, out var fallbackFactory))
            {
                if (implementType != EffectImplementType.NotSpecified && fallbackFactory.SupportsImplementTypes.Contains(implementType))
                {
                    return ApplyCommonProperties(fallbackFactory.Build(implementType, ConvertParams(stru.Parameters, fallbackFactory.ParametersType)), stru);
                }

                return ApplyCommonProperties(fallbackFactory.BuildWithDefaultType(ConvertParams(stru.Parameters, fallbackFactory.ParametersType)), stru);
            }

            if (ContinuousEffectFactoryProvider.TryGetValue(stru.TypeName, out var fallbackContinuousFactory))
            {
                if (implementType != EffectImplementType.NotSpecified && fallbackContinuousFactory.SupportsImplementTypes.Contains(implementType))
                {
                    return ApplyCommonProperties(fallbackContinuousFactory.Build(implementType, ConvertParams(stru.Parameters, fallbackContinuousFactory.ParametersType)), stru);
                }

                return ApplyCommonProperties(fallbackContinuousFactory.BuildContinuousWithDefaultType(ConvertParams(stru.Parameters, fallbackContinuousFactory.ParametersType)), stru);
            }

            if (BindableArgumentEffectFactoryProvider.TryGetValue(stru.TypeName, out var fallbackBindableFactory))
            {
                if (fallbackBindableFactory is IBindableEffectFactory fallbackBindable)
                {
                    if (implementType != EffectImplementType.NotSpecified && fallbackBindable.SupportsImplementTypes.Contains(implementType))
                    {
                        return ApplyCommonProperties(fallbackBindable.Build(implementType, stru.Id, stru.BindedInputID, stru.BindedInputIDs, ConvertParams(stru.Parameters, fallbackBindableFactory.ParametersType)), stru);
                    }

                    return ApplyCommonProperties(fallbackBindable.BuildWithDefaultType(stru.Id, stru.BindedInputID, stru.BindedInputIDs, ConvertParams(stru.Parameters, fallbackBindableFactory.ParametersType)), stru);
                }
            }

            throw new NotSupportedException($"No suitable effect found for the given type '{stru.TypeName}'.");
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
            var prefered = VideoSourceProvider.Values.Where((k) => k(null!).PreferredExtension.Contains(Path.GetExtension(filePath)));
            if (prefered.Any())
            {
                return prefered.First()(null!).CreateNew(filePath);
            }
            else
            {
                foreach (var provider in VideoSourceProvider.Values)
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
                return value(filePath);
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

        /// <summary>
        /// Represents the messaging queue provided by the host application.
        /// </summary>
        [Obsolete("Use GlobalPluginHelper.MessagingService instead. This property is no longer been assigned while initialization and it will be removed in next Plugin API version.", false)]
        public virtual IMessagingService MessagingQueue { get => null; set { } }
    }

#pragma warning disable CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。

    public class PluginMetadata
    {
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
            var effectTypes = pluginBase.EffectFactoryProvider.Keys.Concat(pluginBase.EffectProvider.Keys).Distinct();
            if (effectTypes.Any())
            {
                providedContent.AppendLine("Effect:");
                foreach (var key in effectTypes)
                {
                    providedContent.AppendLine($"- {key}");
                }
            }

            var continuousEffectTypes = pluginBase.ContinuousEffectFactoryProvider.Keys.Concat(pluginBase.ContinuousEffectProvider.Keys).Distinct();
            if (continuousEffectTypes.Any())
            {
                providedContent.AppendLine("ContinuousEffect:");
                foreach (var key in continuousEffectTypes)
                {
                    providedContent.AppendLine($"- {key}");
                }
            }

            var variableArgumentEffectTypes = pluginBase.BindableArgumentEffectFactoryProvider.Keys.Concat(pluginBase.BindableArgumentEffectProvider.Keys).Distinct();
            if (variableArgumentEffectTypes.Any())
            {
                providedContent.AppendLine("VariableArgumentEffect:");
                foreach (var key in variableArgumentEffectTypes)
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
