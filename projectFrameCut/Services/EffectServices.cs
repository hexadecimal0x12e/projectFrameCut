using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationPluginBase.Effect;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;


namespace projectFrameCut.Services
{
    public static class EffectServices
    {
        public static IEffect ReCreateEffect(IEffect effect, Dictionary<string, object>? parameters = null, bool? enabled = null, int? index = null, DraftPage? page = null)
        {
            var newEffect = effect.WithParameters(parameters ?? effect.Parameters);

            newEffect.Enabled = enabled ?? effect.Enabled;
            newEffect.Index = index ?? effect.Index;

            newEffect.Name = effect.Name;

            newEffect.RelativeWidth = page?.ProjectInfo?.RelativeWidth ?? 1920;
            newEffect.RelativeHeight = page?.ProjectInfo?.RelativeHeight ?? 1080;

            // Preserve IBindableArgumentEffect properties
            if (effect is IBindableArgumentEffect oldBindable && newEffect is IBindableArgumentEffect newBindable)
            {
                newBindable.Id = oldBindable.Id;
                newBindable.BindedArgumentProviderID = oldBindable.BindedArgumentProviderID;
            }

            // Preserve BindedEffectGroupID
            newEffect.BindedEffectProvidingSystemID = effect.BindedEffectProvidingSystemID;

            return newEffect;
        }

        public static Dictionary<string, string> GetLocalizedEffectNames(string splitter = " ")
        {
            string GetEffectDisplayName(KeyValuePair<string, Func<IEffectProvider>> e)
            {
                var instance = e.Value();
                var type = instance.TypeOfEffect.ToString();
                if (instance.FromPlugin == InternalPluginBase.InternalPluginBaseID || SettingsManager.IsBoolSettingTrue("edit_AlwaysShowEffectsSource"))
                {
                    var dispName = PluginManager.GetLocalizationItem("DisplayName_Effect_" + e.Key, e.Key);
                    return $"{dispName}{splitter}({type})";
                }
                else
                {
                    var plg = PluginManager.LoadedPlugins.TryGetValue(instance.FromPlugin, out var value) ? value : null;
                    var dispName = plg?.GetLocalizationItemInSpecificPlugin("_PluginBase_Name_", plg.Name) ?? e.Key;
                    return $"{plg.GetLocalizationItemInSpecificPlugin("DisplayName_Effect_" + e.Key, e.Key)}{splitter}({PPLocalizedResources.Effect_FromPlugin(type, dispName)})";
                }

            }

            return EffectHelper.EffectsProviderEnum.ToDictionary(c => c.Key, GetEffectDisplayName);
        }

        public static Dictionary<string, Func<IEffectProvider>> GetAvailableEffectProviders()
        {
            if (!PluginManager.Inited) return [];
            return PluginManager.LoadedPlugins.Values.OfType<IApplicationPluginBase>().SelectMany(c => c.EffectProviderProvider).ToDictionary(k => k.Key, v => v.Value);
        }

        /// <summary>
        /// Get the App-layer UI provider for the given Render-side provider. The UI provider is resolved from
        /// the <see cref="IApplicationPluginBase.EffectProviderUIProvider"/> registrations (custom UI for color pickers,
        /// keyframing, position tuples, ...); when no plugin registers the effect type, a generic metadata-driven
        /// <see cref="EffectProviderUI"/> is returned.
        /// </summary>
        public static IEffectProviderUIProvider GetUIProvider(IEffectProvider provider)
        {
            var factory = PluginManager.LoadedPlugins.Values.OfType<IApplicationPluginBase>()
                .SelectMany(c => c.EffectProviderUIProvider)
                .FirstOrDefault(kv => kv.Key == provider.TypeName).Value;
            return factory is not null ? factory(provider) : new EffectProviderUI(provider);
        }

        public static Dictionary<string, string> GetLocalizedEffectProviderNames(string splitter = " ", bool haveSubFix = true)
        {
            string GetEffectDisplayName(KeyValuePair<string, Func<IEffectProvider>> e)
            {
                var instance = e.Value();
                var type = instance.TypeOfEffect switch
                {
                    Shared.EffectType.ContinuousEffect => PPLocalizedResources.Effect_ContinuousEffect,
                    Shared.EffectType.AudioContinuousEffect => PPLocalizedResources.Effect_ContinuousEffect,
                    Shared.EffectType.BindableEffect => PPLocalizedResources.Effect_BindableArgsEffect,
                    Shared.EffectType.AudioBindableEffect => PPLocalizedResources.Effect_BindableArgsEffect,
                    Shared.EffectType.TextEffect => PPLocalizedResources.Effect_TextEffect,
                    Shared.EffectType.ContinuousTextEffect => PPLocalizedResources.Effect_ContinuousTextEffect,
                    _ => PPLocalizedResources.Effect_GeneralEffect,
                };
                if (!haveSubFix)
                {
                    return PluginManager.GetLocalizationItem("DisplayName_Effect_" + e.Key, e.Key);   
                }
                else if (instance.FromPlugin == InternalPluginBase.InternalPluginBaseID || SettingsManager.IsBoolSettingTrue("edit_AlwaysShowEffectsSource"))
                {
                    var dispName = PluginManager.GetLocalizationItem("DisplayName_Effect_" + e.Key, e.Key);
                    return $"{dispName}{splitter}({type})";
                }
                else
                {
                    var plg = PluginManager.LoadedPlugins.TryGetValue(instance.FromPlugin, out var value) ? value : null;
                    var dispName = plg?.GetLocalizationItemInSpecificPlugin("_PluginBase_Name_", plg.Name) ?? e.Key;
                    return $"{plg.GetLocalizationItemInSpecificPlugin("DisplayName_Effect_" + e.Key, e.Key)}{splitter}({PPLocalizedResources.Effect_FromPlugin(type, dispName)})";
                }

            }

            return GetAvailableEffectProviders().ToDictionary(c => c.Key, GetEffectDisplayName);
        }
    }
}
