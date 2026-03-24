using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.ApplicationPluginBase.Effect
{
    public class JitterEffectBundle : IEffectBundle
    {
        public string TypeName => "Jitter";

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public bool IsNormalEffect => false;

        public bool IsContinuousEffect => true;

        public bool IsBindableEffect => false;

        public EffectType TypeOfEffect => EffectType.ContinuousEffect;

        public EffectTarget Target => EffectTarget.Video;

        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; }
        
        public Guid BindedInputId { get; set; } = IEffectBundle.InputAnchorGUID;
        public Guid BindedOutputId { get; set; } = IEffectBundle.OutputAnchorGUID;
        public List<Guid>? BindedInputIds { get; set; }
        public bool IsMultiInput => false;

        public string InputAnchorDisplayName => string.Empty;
        public string[]? InputAnchorsDisplayName => null;
        public string OutputAnchorDisplayName => string.Empty;

        public int StartPoint { get; set; }
        public int EndPoint { get; set; }

        public Dictionary<string, object> Parameters { get; set; }

        public List<string> ParametersNeeded => JitterEffect.s_ParametersNeeded;

        public Dictionary<string, string> ParametersType => JitterEffect.s_ParametersType;

        public IEffectFactory[] Create()
        {
            var factory = new JitterContinuousEffectFactory();
            this.ConfigureFactory(factory);
            return [factory];
        }

        public PropertyPanelBuilder CreateUI()
        {
            var ppb = new PropertyPanelBuilder();
            ppb.AddEntry("MaxOffsetX", PluginManager.GetLocalizationItem("Effect_Jitter_MaxOffsetX", "Max X Offset"), (Parameters.TryGetValue("MaxOffsetX", out var mx) ? mx.ToString() : "10") ?? "10", "10");
            ppb.AddEntry("MaxOffsetY", PluginManager.GetLocalizationItem("Effect_Jitter_MaxOffsetY", "Max Y Offset"), (Parameters.TryGetValue("MaxOffsetY", out var my) ? my.ToString() : "10") ?? "10", "10");
            string[] options = [
                                    PluginManager.GetLocalizationItem("_Effect_Jitter_Both", "Both directions"),
                        PluginManager.GetLocalizationItem("_Effect_Jitter_XOnly", "X direction"),
                        PluginManager.GetLocalizationItem("_Effect_Jitter_YOnly", "Y direction")
                                ];
            string val = Parameters.TryGetValue("Direction", out var d) ? d as string ?? JitterEffect.Direction_Both : JitterEffect.Direction_Both;
            string defaultVal = val switch
            {
                JitterEffect.Direction_XOnly => options[1],
                JitterEffect.Direction_YOnly => options[2],
                _ => options[0]
            };
            ppb.AddPicker("Direction", PluginManager.GetLocalizationItem("Direction", "Direction"), options, defaultVal);
            ppb.AddEntry("Seed", PluginManager.GetLocalizationItem("Effect_Jitter_Seed", "Random Seed"), (Parameters.TryGetValue("Seed", out var s) ? s.ToString() : "0") ?? "0", "0");
            return ppb;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            if (args.Id == "Direction")
            {
                string[] options = [
                    PluginManager.GetLocalizationItem("_Effect_Jitter_Both", "Both directions"),
                    PluginManager.GetLocalizationItem("_Effect_Jitter_XOnly", "X direction"),
                    PluginManager.GetLocalizationItem("_Effect_Jitter_YOnly", "Y direction")
                ];
                if (args.Value?.ToString() == options[1])
                    Parameters["Direction"] = JitterEffect.Direction_XOnly;
                else if (args.Value?.ToString() == options[2])
                    Parameters["Direction"] = JitterEffect.Direction_YOnly;
                else
                    Parameters["Direction"] = JitterEffect.Direction_Both;
            }
            else
            {
                Parameters[args.Id] = int.TryParse(args.Value as string, out var result) ? result : throw new InvalidDataException();
            }
            return Parameters;
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources.DisplayName_Effect_Jitter,
                Description = "jitter",

            };
        }

        public bool Enabled { get; set; }

    }
}
