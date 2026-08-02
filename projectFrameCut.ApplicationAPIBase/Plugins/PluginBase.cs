using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
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
using projectFrameCut.ApplicationAPIBase.DynamicPreviewProvider;
using projectFrameCut.ApplicationAPIBase.Project;
using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.ApplicationAPIBase.VectorComponentHandler;


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
        /// Get the current Application-level plugin API version.
        /// </summary>
        public static int CurrentAppLevelPluginAPIVersion => 6;

        /// <summary>
        /// The root of app's data.
        /// </summary>
        public static string AppDataRoot
        {
            get { if (!Directory.Exists(field)) return FileSystem.AppDataDirectory; return field; }
            set { if (!string.IsNullOrWhiteSpace(field)) throw new InvalidOperationException("The AppDataRoot could only be set once."); else if (!Directory.Exists(value)) throw new DirectoryNotFoundException(); else field = value; }
        } = "";

        /// <summary>
        /// Get the version of the Application-level plugin.
        /// </summary>
        public int AppLevelPluginAPIVersion { get; }

        /// <summary>
        /// Gets a dictionary that maps effect type names to their UI provider creation functions.
        /// The function receives the current <see cref="IEffectProvider"/> instance being edited and returns
        /// the property-panel UI wrapper for it. Aggregated the same way as <see cref="IPluginBase.EffectProviderProvider"/>.
        /// </summary>
        /// <remarks>
        /// <see cref="IEffectProvider"/> itself is a Render-side, UI-free contract; the mapping to a UI provider
        /// (with custom property panels, keyframing, color pickers, etc.) lives here at the App layer.
        /// </remarks>
        public Dictionary<string, Func<IEffectProvider, IEffectProviderUIProvider>> EffectProviderUIProvider { get; }

        /// <summary>
        /// Gets a dictionary that maps text style provider names to their corresponding provider creation functions.
        /// </summary>
        public Dictionary<string, Func<ITextClipStyleProvider>> TextClipStyleProvider { get; }

        /// <summary>
        /// Gets a dictionary that maps vector component type names to their corresponding handler creation functions.
        /// </summary>
        public Dictionary<string, Func<IVectorComponentHandler>> VectorComponentHandlerProvider { get; }

        /// <summary>
        /// Get a helper for dynamic preview generation. The key of the dictionary is the type name of the clip or effect that the provider can generate preview for. The value is the provider itself.
        /// </summary>
        public Dictionary<string, IClipDynamicPreviewProvider> ClipDynamicPreviewProvider { get; }
        /// <summary>
        /// Get a helper for dynamic preview generation for effects. The key of the dictionary is the type name of the effect that the provider can generate preview for. The value is the provider itself.
        /// </summary>
        public Dictionary<string, IEffectDynamicPreviewProvider> EffectDynamicPreviewProvider { get; }


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

        /// <summary>
        /// Inject custom controls to the editor UI.
        /// </summary>
        /// <remarks>
        /// This method will be called after Post-Init stage (The time when DraftPage is created in HomePage and PostInit() is called.)
        /// </remarks>
        /// <param name="draftPage"></param>
        public virtual void InjectUI(IDraftPage draftPage)
        {

        }

        /// <summary>
        /// Inject custom menu-bar items to the 'Extension' menu.
        /// </summary>
        /// <remarks>
        /// This method will be called after Post-Init stage (The time when DraftPage is created in HomePage and PostInit() is called.)
        /// This method will parse the page's <see cref="MultiWindowView"/>, you can use it for displaying a new page. To get the whole page, you may use the <paramref name="MainMWV"/>'s <see cref="IElement.Parent"/> and cast it to <see cref="Page"/>.
        /// </remarks>
        /// <param name="MainMWV">
        /// The <see cref="MultiWindowView"/> of the showing page.
        /// </param>
        /// <return>The items.</return>
        public virtual List<MenuFlyoutItem> GetMenuItems(IDraftPage Page)
        {
            return [];
        }

        public static string GetWhatProvided(IApplicationPluginBase pluginBase)
        {
            // re-use the base IPluginBase metadata dump
            var baseInfo = PluginMetadata.GetWhatProvided(pluginBase);
            // strip the trailing newline so we can append our own sections
            baseInfo = baseInfo.TrimEnd('\r', '\n');

            StringBuilder sb = new(baseInfo);
            sb.AppendLine();
            sb.AppendLine();

            // ----- Effect Providers -----
            if (pluginBase.EffectProviderProvider.Any())
            {
                sb.AppendLine("EffectProvider:");
                foreach (var item in pluginBase.EffectProviderProvider)
                {
                    sb.AppendLine($"- {item.Key}");
                }
            }

            // ----- Text Style Providers -----
            if (pluginBase.TextClipStyleProvider.Any())
            {
                sb.AppendLine("TextClipStyleProvider:");
                foreach (var item in pluginBase.TextClipStyleProvider)
                {
                    sb.AppendLine($"- {item.Key}");
                }
            }

            if (pluginBase.VectorComponentHandlerProvider.Any())
            {
                sb.AppendLine("VectorComponentHandler:");
                foreach (var item in pluginBase.VectorComponentHandlerProvider)
                {
                    sb.AppendLine($"- {item.Key}");
                }
            }

            // ----- Clip Dynamic Previews -----
            if (pluginBase.ClipDynamicPreviewProvider.Any())
            {
                sb.AppendLine("ClipDynamicPreviewProvider:");
                foreach (var item in pluginBase.ClipDynamicPreviewProvider)
                {
                    sb.AppendLine($"- {item.Key}: {item.Value.GetType().Name}");
                }
            }

            // ----- Effect Dynamic Previews -----
            if (pluginBase.EffectDynamicPreviewProvider.Any())
            {
                sb.AppendLine("EffectDynamicPreviewProvider:");
                foreach (var item in pluginBase.EffectDynamicPreviewProvider)
                {
                    sb.AppendLine($"- {item.Key}: {item.Value.GetType().Name}");
                }
            }

            // ----- Setting Page -----
            var dummy = pluginBase;
            var settingPage = pluginBase.SettingPageProvider(ref dummy);
            sb.AppendLine(settingPage is not null
                ? "SettingPage: Yes"
                : "SettingPage: None");

            return sb.ToString();
        }
    }



}
