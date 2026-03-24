using Microsoft.Maui.Handlers;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace projectFrameCut.ApplicationPluginBase.Effect
{
    public class RemoveColorEffectBundle : IEffectBundle
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string Name { get; set; } = string.Empty;

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();

        public List<string> ParametersNeeded => new List<string>
        {
            "R",
            "G",
            "B",
            "A",
            "Tolerance",
        };

        public Dictionary<string, string> ParametersType => new Dictionary<string, string>
        {
            {"R", "ushort" },
            {"G", "ushort" },
            {"B", "ushort" },
            {"A", "ushort" },
            {"Tolerance", "ushort" },
        };

        public string TypeName => "RemoveColor";

        public bool IsNormalEffect => true;

        public bool IsContinuousEffect => false;

        public bool IsBindableEffect => false;

        public EffectType TypeOfEffect => EffectType.NormalEffect;

        public EffectTarget Target => EffectTarget.Video;

        public Guid BindedInputId { get; set; } = IEffectBundle.InputAnchorGUID;
        public Guid BindedOutputId { get; set; } = IEffectBundle.OutputAnchorGUID;
        public List<Guid>? BindedInputIds { get; set; }

        public bool IsMultiInput => false;
        public string InputAnchorDisplayName => string.Empty;
        public string[]? InputAnchorsDisplayName => null;
        public string OutputAnchorDisplayName => string.Empty;
        public bool Enabled { get; set; }

        public int StartPoint { get; set; }
        public int EndPoint { get; set; }

        public IEffectFactory[] Create()
        {
            var factory = new RemoveColorEffectFactory();
            this.ConfigureFactory(factory);
            return new IEffectFactory[] { factory };
        }

        public PropertyPanelBuilder CreateUI()
        {
            var ppb = new PropertyPanelBuilder();

            foreach (var paramName in ParametersNeeded)
            {
                if (!ParametersType.TryGetValue(paramName, out var paramType)) continue;

                var currentVal = Parameters != null && Parameters.ContainsKey(paramName) ? Parameters[paramName] : null;
                if (currentVal is JsonElement je)
                {
                    if (je.ValueKind == JsonValueKind.Number)
                        currentVal = je.ToString(); // Simply convert to string for display
                    else if (je.ValueKind == JsonValueKind.String)
                        currentVal = je.GetString();
                    else
                        currentVal = je.ToString();
                }

                string valStr = currentVal?.ToString() ?? "0";
                
                // For ushort values, we use a simple entry. 
                // In a more advanced UI, we might use a color picker if R/G/B combine to a color, 
                // but checking the interface, these are individual fields.
                ppb.AddEntry(paramName, PluginManager.GetLocalizationItem($"_{paramName}", paramName), valStr, "");
            }
            return ppb;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            if (Parameters == null) Parameters = new Dictionary<string, object>();
            
            if (ushort.TryParse(args.Value as string, out var val))
            {
                Parameters[args.Id] = val;
            }
            return Parameters;
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = PluginManager.GetLocalizationItem("DisplayName_Effect_RemoveColor", "Remove Color"),
                Description = PluginManager.GetLocalizationItem("Description_Effect_RemoveColor", "Remove a specific color from the image based on tolerance."),
                Thumbnail = ImageHelper.LoadFromAsset("icon_effect_remove_color")
            };
        }
    }
}
