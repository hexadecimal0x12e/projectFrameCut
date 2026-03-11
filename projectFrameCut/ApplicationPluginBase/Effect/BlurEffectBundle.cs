using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Text.Json;
using projectFrameCut.ApplicationAPIBase.Helpers;

namespace projectFrameCut.ApplicationPluginBase.Effect
{
    public class BlurEffectBundle : IEffectBundle
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string Name { get; set; } = "Blur";

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>
        {
            {"Sigma", 0f }
        };

        public List<string> ParametersNeeded => new List<string>
        {
            "Sigma"
        };

        public Dictionary<string, string> ParametersType => new Dictionary<string, string>
        {
            {"Sigma", "float" }
        };

        public string TypeName => "Blur";

        public bool IsNormalEffect => true;

        public bool IsContinuousEffect => false;

        public bool IsBindableEffect => false;

        public Guid BindedInputId { get; set; } = IEffectBundle.InputAnchorGUID;

        public Guid BindedOutputId { get; set; } = IEffectBundle.OutputAnchorGUID;

        public bool IsMultiInput => false;
        public string InputAnchorDisplayName => string.Empty;
        public string[]? InputAnchorsDisplayName => null;
        public string OutputAnchorDisplayName => string.Empty;
        public List<Guid>? BindedInputIds { get; set; }

        public bool Enabled { get; set; } = true;

        public int StartPoint { get; set; }
        public int EndPoint { get; set; }

        public IEffectFactory[] Create()
        {
            var factory = new BlurEffectFactory();
            this.ConfigureFactory(factory); 
            return new IEffectFactory[] { factory };
        }

        public PropertyPanelBuilder CreateUI()
        {
            var ppb = new PropertyPanelBuilder();

            // Default value handling
            float currentSigma = 0;
            if (Parameters != null && Parameters.TryGetValue("Sigma", out var val))
            {
                 if (val is JsonElement je)
                 {
                    if (je.ValueKind == JsonValueKind.Number)
                        currentSigma = je.GetSingle();
                    else if(float.TryParse(je.ToString(), out var parsed))
                        currentSigma = parsed;
                 }
                 else
                 {
                    try { currentSigma = Convert.ToSingle(val); } catch { }
                 }
            }

            ppb.AddEntry("Sigma", PluginManager.GetLocalizationItem("Property_Sigma", "Sigma (Blur Radius)"), currentSigma.ToString(), "0");

            return ppb;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            if (Parameters == null) Parameters = new Dictionary<string, object>();
            
            if (args.Id == "Sigma")
            {
                if (float.TryParse(args.Value as string, out var val))
                {
                    Parameters["Sigma"] = val;
                }
            }
            return Parameters;
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = PluginManager.GetLocalizationItem("DisplayName_Effect_Blur", "Blur"),
                Description = PluginManager.GetLocalizationItem("Description_Effect_Blur", "Apply Gaussian blur to the image."),
                Thumbnail = ImageHelper.LoadFromAsset("icon_effect_blur") // Assuming an icon exists or will be added, or fallback
            };
        }
    }
}
