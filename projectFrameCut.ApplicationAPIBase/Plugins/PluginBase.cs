using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.ApplicationAPIBase.Plugins
{
    /// <summary>
    /// The base interface for all application-level plugins. Application-level plugins is an MAUI Class library, so that it can create UI.
    /// </summary>
    /// <remarks>
    /// A good way to make an application plugin is to create a normal .NET class library and implement all interface from <see cref="IPluginBase"/>, then create a new .NET MAUI class library project that reference the previous class library, and implement <see cref="IApplicationPluginBase"/> interface, and create other things like setting page in the MAUI class library.
    /// </remarks>
    public interface IApplicationPluginBase : IPluginBase
    {
        /// <summary>
        /// Gets a dictionary that maps effect names to their corresponding effect bundle creation functions.
        /// </summary>
        public Dictionary<string, Func<IEffectBundle>> EffectBundleProvider { get; }

        /// <summary>
        /// Create the setting page for the plugin.
        /// return null if no setting page is provided.
        /// </summary>
        public View? SettingPageProvider(ref IApplicationPluginBase instance);

        /// <summary>
        /// Override this method to do some custom action after this plugin loaded in application level.
        /// </summary>
        /// <remarks>
        /// If you override both <see cref="IPluginBase.OnLoaded(out string)"/> and <see cref="OnApplicationPluginLoaded"/>, Application will call <see cref="IPluginBase.OnLoaded(out string)"/> first then call <see cref="OnApplicationPluginLoaded"/> if previous one is succeed.
        /// </remarks>
        public virtual void OnApplicationPluginLoaded()
        {

        }
    }







}
