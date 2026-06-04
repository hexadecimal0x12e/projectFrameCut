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
    public class MovementEffectBundle : IEffectBundle
    {
        private static readonly List<string> s_ParametersNeeded =
        [
            "StartX",
            "StartY",
            "EndX",
            "EndY",
            "Duration"
        ];

        private static readonly Dictionary<string, string> s_ParametersType = new Dictionary<string, string>
        {
            { "StartX", "int" },
            { "StartY", "int" },
            { "EndX", "int" },
            { "EndY", "int" },
            { "Duration", "int" }
        };

        public string TypeName => "Movement";

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public bool IsNormalEffect => false;

        public bool IsContinuousEffect => true;

        public bool IsBindableEffect => true;

        public EffectType TypeOfEffect => EffectType.ContinuousEffect;

        public EffectTarget Target => EffectTarget.Video;

        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "Movement";

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

        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>
        {
            { "StartX", 0 },
            { "StartY", 0 },
            { "EndX", 200 },
            { "EndY", 0 },
            { "Duration", 1000 },
        };

        public List<string> ParametersNeeded => s_ParametersNeeded;

        public Dictionary<string, string> ParametersType => s_ParametersType;

        public IEffectFactory[] Create()
        {
            string movementProducerId = Guid.NewGuid().ToString();
            var producerFactory = new StraightLineMovementValueProducerFactory
            {
                ID = movementProducerId
            };

            var pointPlacerFactory = new PointPlacerFactory
            {
                ID = this.Id.ToString(),
                BindedInputID = movementProducerId
            };

            return [producerFactory, pointPlacerFactory];
        }

        public PropertyPanelBuilder CreateUI()
        {
            int startX = EffectBundleUiHelper.GetInt(Parameters, "StartX", 0);
            int startY = EffectBundleUiHelper.GetInt(Parameters, "StartY", 0);
            int endX = EffectBundleUiHelper.GetInt(Parameters, "EndX", 200);
            int endY = EffectBundleUiHelper.GetInt(Parameters, "EndY", 0);
            int duration = Math.Max(100, EffectBundleUiHelper.GetInt(Parameters, "Duration", 1000));

            var panel = new PropertyPanelBuilder();
            panel.AddPositionTupleInputBox("start", new SingleLineLabel(EffectBundleUiHelper.L("_MovementStart", "Start")), PositionTupleMode.XY, (startX, startY, 0, 0));
            panel.AddPositionTupleInputBox("end", new SingleLineLabel(EffectBundleUiHelper.L("_MovementEnd", "End")), PositionTupleMode.XY, (endX, endY, 0, 0));
            panel.AddSlider(
                "Duration",
                EffectBundleUiHelper.L("Effect_Movement_Duration", "Duration"),
                100,
                20000,
                duration,
                null,
                SliderUpdateEventCallMode.OnMouseUp);
            return panel;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            if (!ParametersType.ContainsKey(args.Id))
            {
                // check compound IDs from tuple inputs
                switch (args.Id)
                {
                    case "start_X":
                        EffectBundleUiHelper.TrySetInt(Parameters, "StartX", args.Value);
                        return Parameters;
                    case "start_Y":
                        EffectBundleUiHelper.TrySetInt(Parameters, "StartY", args.Value);
                        return Parameters;
                    case "end_X":
                        EffectBundleUiHelper.TrySetInt(Parameters, "EndX", args.Value);
                        return Parameters;
                    case "end_Y":
                        EffectBundleUiHelper.TrySetInt(Parameters, "EndY", args.Value);
                        return Parameters;
                }
                return Parameters;
            }

            if (EffectBundleUiHelper.TrySetInt(Parameters, args.Id, args.Value))
            {
                if (args.Id == "Duration")
                {
                    Parameters["Duration"] = Math.Max(100, EffectBundleUiHelper.GetInt(Parameters, "Duration", 1000));
                }
            }

            return Parameters;
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = EffectBundleUiHelper.L("DisplayName_Effect_Movement", "Movement"),
                Description = EffectBundleUiHelper.L("Description_Effect_Movement", "Move an element from the start point to the end point."),
                Thumbnail = ImageHelper.LoadFromAsset("icon_add"),
                VideoThumbnail = null
            };
        }
    }
}
