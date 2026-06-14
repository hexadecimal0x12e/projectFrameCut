using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;

namespace projectFrameCut.ApplicationPluginBase.Effect
{
    public class ZoominEffectBundle : IEffectBundle
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string Name { get; set; } = "ZoomIn";

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>
        {
            { "TargetX", 960 },
            { "TargetY", 540 },
        };

        public List<string> ParametersNeeded => ZoomInContinuousEffectFactory.s_ParametersNeeded;

        public Dictionary<string, string> ParametersType => ZoomInContinuousEffectFactory.s_ParametersType;

        public string TypeName => "ZoomIn";

        public bool IsNormalEffect => false;

        public bool IsContinuousEffect => true;

        public bool IsBindableEffect => false;

        public EffectType TypeOfEffect => EffectType.ContinuousEffect;

        public EffectTarget Target => EffectTarget.Video;

        public Guid BindedInputId { get; set; } = IEffectBundle.InputAnchorGUID;
        public Guid BindedOutputId { get; set; } = IEffectBundle.OutputAnchorGUID;
        public List<Guid>? BindedInputIds { get; set; }
        public bool IsMultiInput => false;
        public bool Enabled { get; set; } = true;

        public string InputAnchorDisplayName => string.Empty;
        public string[]? InputAnchorsDisplayName => null;
        public string OutputAnchorDisplayName => string.Empty;

        public int StartPoint { get; set; }
        public int EndPoint { get; set; }

        public IEffectFactory[] Create()
        {
            var factory = new ZoomInContinuousEffectFactory();
            this.ConfigureFactory(factory);
            return [factory];
        }

        public PropertyPanelBuilder CreateUI()
        {
            int targetX = Math.Max(1, EffectBundleUiHelper.GetInt(Parameters, "TargetX", 960));
            int targetY = Math.Max(1, EffectBundleUiHelper.GetInt(Parameters, "TargetY", 540));

            var panel = new PropertyPanelBuilder();
            panel.AddPositionTupleInputBox("zoomIn", new SingleLineLabel(EffectBundleUiHelper.L("_TargetPosition", "Target")), PositionTupleMode.XY, (targetX, targetY, 0, 0));
            return panel;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            switch (args.Id)
            {
                case "zoomIn_X":
                    if (EffectBundleUiHelper.TrySetInt(Parameters, "TargetX", args.Value))
                        Parameters["TargetX"] = Math.Max(1, EffectBundleUiHelper.GetInt(Parameters, "TargetX", 960));
                    break;
                case "zoomIn_Y":
                    if (EffectBundleUiHelper.TrySetInt(Parameters, "TargetY", args.Value))
                        Parameters["TargetY"] = Math.Max(1, EffectBundleUiHelper.GetInt(Parameters, "TargetY", 540));
                    break;
            }

            return Parameters;
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = EffectBundleUiHelper.L("DisplayName_Effect_ZoomIn", "Zoom In"),
                Description = EffectBundleUiHelper.L("Description_Effect_ZoomIn", "Zoom in from the source frame size to the target size over time."),
                Thumbnail = ImageHelper.LoadFromAsset("icon_add")
            };
        }
    }
}
