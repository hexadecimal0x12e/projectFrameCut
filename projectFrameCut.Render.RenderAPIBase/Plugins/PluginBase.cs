using projectFrameCut.Render.RenderAPIBase;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.RenderAPIBase.Plugins
{
    public interface IPluginBase
    {
        /// <summary>
        /// The unique identifier of the plugin. Must equal to the full name of the main class implementing IPluginBase.
        /// </summary>
        public string PluginID { get; }
        /// <summary>
        /// The supported API version of the plugin.
        /// </summary>
        public int PluginAPIVersion { get; }
        /// <summary>
        /// The plugin's name.
        /// </summary>
        public string Name { get; }
        public string Author { get; }
        public string Description { get; }
        public Version Version { get; }
        public string AuthorUrl { get; }
        /// <summary>
        /// The publish page of the plugin. Used for update checking.
        /// </summary>
        public string? PublishingUrl { get; }

        [JsonIgnore]
        public Dictionary<string, Func<IEffect>> EffectProvider { get; }
        [JsonIgnore]
        public Dictionary<string, Func<IMixture>> MixtureProvider { get; }
        [JsonIgnore]
        public Dictionary<string, Func<IComputer>> ComputerProvider { get; }
        [JsonIgnore]
        public Dictionary<string, Func<string,string,IClip>> ClipProvider { get; }
        [JsonIgnore]
        public Dictionary<string, Func<string, IVideoSource>> VideoSourceProvider { get; }
        public IClip ClipCreator(JsonElement element);
        public IEffect EffectCreator(EffectAndMixtureJSONStructure stru);
        public IVideoSource VideoSourceCreator(string filePath)
        {
            var prefered = VideoSourceProvider.Values.Where((k) => k(null).PreferredExtension.Contains(Path.GetExtension(filePath)));
            if (prefered.Any())
            {
                return prefered.First()(filePath);
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

        public virtual void OnProjectLoad()
        {

        }

        public virtual void OnProjectSave()
        {

        }

        public virtual void OnProjectClose()
        {

        }
    }


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
        /// The plugin's name.
        /// </summary>
        public string Name { get; set; }
        public string Author { get; set; }
        public string Description { get; set; }
        public Version Version { get; set; }
        public string AuthorUrl { get; set; }
        /// <summary>
        /// The publish page of the plugin. Used for update checking.
        /// </summary>
        public string? PublishingUrl { get; set; }

        public string PluginKey { get; set; }
        public string PluginHash { get; set; }
    }

}
