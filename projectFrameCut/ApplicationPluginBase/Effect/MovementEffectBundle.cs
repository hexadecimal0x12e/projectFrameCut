using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Services;
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

        private static readonly Dictionary<string, EffectBundleSettableFields> s_settableFields = new()
        {
            { "StartX", EffectBundleHelper.IntField("StartX", "Start X", "Movement start X position", 0) },
            { "StartY", EffectBundleHelper.IntField("StartY", "Start Y", "Movement start Y position", 0) },
            { "EndX", EffectBundleHelper.IntField("EndX", "End X", "Movement end X position", 200) },
            { "EndY", EffectBundleHelper.IntField("EndY", "End Y", "Movement end Y position", 0) },
            { "Duration", EffectBundleHelper.IntField("Duration", "Duration", "Movement duration in milliseconds", 1000, 100) }
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
        public Dictionary<string, EffectBundleSettableFields> SettableFields => s_settableFields;

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
            int startX = EffectBundleHelper.GetInt(Parameters, "StartX", 0);
            int startY = EffectBundleHelper.GetInt(Parameters, "StartY", 0);
            int endX = EffectBundleHelper.GetInt(Parameters, "EndX", 200);
            int endY = EffectBundleHelper.GetInt(Parameters, "EndY", 0);
            int duration = Math.Max(100, EffectBundleHelper.GetInt(Parameters, "Duration", 1000));

            var panel = new PropertyPanelBuilder();
            panel.AddPositionTupleInputBox("start", new SingleLineLabel(EffectBundleHelper.L("_MovementStart", "Start")), PositionTupleMode.XY, (startX, startY, 0, 0));
            panel.AddPositionTupleInputBox("end", new SingleLineLabel(EffectBundleHelper.L("_MovementEnd", "End")), PositionTupleMode.XY, (endX, endY, 0, 0));
            panel.AddSlider(
                "Duration",
                EffectBundleHelper.L("Effect_Movement_Duration", "Duration"),
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
                        EffectBundleHelper.TrySetInt(Parameters, "StartX", args.Value);
                        return Parameters;
                    case "start_Y":
                        EffectBundleHelper.TrySetInt(Parameters, "StartY", args.Value);
                        return Parameters;
                    case "end_X":
                        EffectBundleHelper.TrySetInt(Parameters, "EndX", args.Value);
                        return Parameters;
                    case "end_Y":
                        EffectBundleHelper.TrySetInt(Parameters, "EndY", args.Value);
                        return Parameters;
                }
                return Parameters;
            }

            if (EffectBundleHelper.TrySetInt(Parameters, args.Id, args.Value))
            {
                if (args.Id == "Duration")
                {
                    Parameters["Duration"] = Math.Max(100, EffectBundleHelper.GetInt(Parameters, "Duration", 1000));
                }
            }

            return Parameters;
        }

        public bool HandleSettableFieldsChange(EffectBundleSettableFields field, object value, out string feedback)
        {
            return EffectBundleHelper.HandleSettableFieldChange(Parameters, field, value, out feedback);
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = EffectBundleHelper.L("DisplayName_Effect_Movement", "Movement"),
                Description = EffectBundleHelper.L("Description_Effect_Movement", "Move an element from the start point to the end point."),
                Thumbnail = ImageSource.FromFile(FileSystemService.GetAppPackageFileSync("EffectSample/movement.png")),
                VideoThumbnail = null
            };
        }
    }
}
