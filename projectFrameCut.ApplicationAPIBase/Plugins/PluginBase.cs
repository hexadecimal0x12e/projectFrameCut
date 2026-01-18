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
    /// The base interface for all application-level plugins.
    /// </summary>
    /// <remarks>
    /// Application plugins is an MAUI Class library, so that it can create UI.
    /// </remarks>
    public interface IApplicationPluginBase : IPluginBase
    {
        public sealed new bool IsApplicationPlugin => true;

        public Dictionary<string,Func<IEffectBundle>> EffectBundleProvider { get; }

        /// <summary>
        /// Create the setting page for the plugin.
        /// return null if no setting page is provided.
        /// </summary>
        public View? SettingPageProvider(ref IApplicationPluginBase instance);


    }




   
    

}
