using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
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
            newEffect.BindedEffectGroupID = effect.BindedEffectGroupID;

            return newEffect;
        }

        public static Dictionary<string, string> GetLocalizedEffectNames(string splitter = " ")
        {
            string GetEffectDisplayName(KeyValuePair<string, Func<IEffect>> e)
            {
                var instance = e.Value();
                var type = instance switch
                {
                    var t when t is IContinuousEffect => PPLocalizedResources.Effect_ContinuousEffect,
                    var t when t is IBindableArgumentEffect => PPLocalizedResources.Effect_BindableArgsEffect,
                    _ => PPLocalizedResources.Effect_GeneralEffect,
                };
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

            return EffectHelper.EffectsEnum.ToDictionary(c => c.Key, GetEffectDisplayName);
        }

        public static Dictionary<string, Func<IEffectBundle>> GetAvailableEffectBundles()
        {
            var bundles = new Dictionary<string, Func<IEffectBundle>>();
            if (!PluginManager.Inited) return bundles;

            foreach (var plugin in PluginManager.LoadedPlugins.Values)
            {
                if (plugin is IApplicationPluginBase appPlugin)
                {
                    foreach (var kvp in appPlugin.EffectBundleProvider)
                    {
                        if (!bundles.ContainsKey(kvp.Key))
                        {
                            bundles.Add(kvp.Key, kvp.Value);
                        }
                    }
                }
            }
            return bundles;
        }

        public static Dictionary<string, string> GetLocalizedEffectBundleNames(string splitter = " ", bool haveSubFix = true)
        {
            string GetEffectDisplayName(KeyValuePair<string, Func<IEffectBundle>> e)
            {
                var instance = e.Value();
                var type = instance.TypeOfEffect switch
                {
                    Shared.EffectType.ContinuousEffect => PPLocalizedResources.Effect_ContinuousEffect,
                    Shared.EffectType.AudioContinuousEffect => PPLocalizedResources.Effect_ContinuousEffect,
                    Shared.EffectType.BindableEffect => PPLocalizedResources.Effect_BindableArgsEffect,
                    Shared.EffectType.AudioBindableEffect => PPLocalizedResources.Effect_BindableArgsEffect,
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

            return GetAvailableEffectBundles().ToDictionary(c => c.Key, GetEffectDisplayName);
        }
    }
}
