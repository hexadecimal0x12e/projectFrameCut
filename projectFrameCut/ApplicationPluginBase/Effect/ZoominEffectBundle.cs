using Microsoft.Maui.Handlers;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.PropertyPanelBuilders;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.ApplicationPluginBase.Effect
{
    public class ZoominEffectBundle : IEffectBundle
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public Dictionary<string, object> Parameters { get; set; }

        public List<string> ParametersNeeded => Render.Effect.ZoomInContinuousEffectFactory.s_ParametersNeeded;

        public Dictionary<string, string> ParametersType => Render.Effect.ZoomInContinuousEffectFactory.s_ParametersType;

        public string TypeName => "ZoomIn";

        public bool IsNormalEffect => false;

        public bool IsContinuousEffect => true;

        public bool IsBindableEffect => false;

        public int Index { get; set; }

        public IEffectFactory[] Create()
        {
            return [new Render.Effect.ZoomInContinuousEffectFactory()];
        }

        public PropertyPanelBuilder CreateUI()
        {
            var ppb = new PropertyPanelBuilder();

            foreach (var paramName in ParametersNeeded)
            {
                if (!ParametersType.TryGetValue(paramName, out var paramType)) continue;

                var currentVal = Parameters.ContainsKey(paramName) ? Parameters[paramName] : null;
                if (currentVal is JsonElement je)
                {
                    if (je.ValueKind == JsonValueKind.True || je.ValueKind == JsonValueKind.False)
                        currentVal = je.GetBoolean();
                    else if (je.ValueKind == JsonValueKind.String)
                        currentVal = je.GetString();
                    else
                        currentVal = je.ToString();
                }

                if (paramType == "bool")
                {
                    bool val = false;
                    if (currentVal is bool b) val = b;
                    else if (bool.TryParse(currentVal?.ToString(), out var bParsed)) val = bParsed;
                    ppb.AddCheckbox(paramName, PluginManager.GetLocalizationItem($"_{paramName}", paramName), val);
                }
                else
                {
                    string valStr = currentVal?.ToString() ?? "";
                    ppb.AddEntry(paramName, PluginManager.GetLocalizationItem($"_{paramName}", paramName), valStr, "");
                }
            }
            return ppb;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            Parameters[args.Id] = int.Parse(args.Value as string);
            return Parameters;
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources.DisplayName_Effect_ZoomIn,
                Description = "111",
                Thumbnail = ImageHelper.LoadFromAsset("icon_add")
            };
        }


    }
}
