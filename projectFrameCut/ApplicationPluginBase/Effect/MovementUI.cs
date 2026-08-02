using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;

namespace projectFrameCut.ApplicationPluginBase.Effect
{
    /// <summary>
    /// Custom property UI of the Movement effect: start/end position tuples plus a duration slider.
    /// </summary>
    public class MovementUI : EffectProviderUI
    {
        public MovementUI(IEffectProvider inner) : base(inner)
        {
        }

        public override PropertyPanelBuilder CreateUI()
        {
            int startX = EffectProviderHelper.GetInt(Inner.Parameters, "StartX", 0);
            int startY = EffectProviderHelper.GetInt(Inner.Parameters, "StartY", 0);
            int endX = EffectProviderHelper.GetInt(Inner.Parameters, "EndX", 200);
            int endY = EffectProviderHelper.GetInt(Inner.Parameters, "EndY", 0);
            int duration = Math.Max(100, EffectProviderHelper.GetInt(Inner.Parameters, "Duration", 1000));

            var panel = new PropertyPanelBuilder();
            panel.AddPositionTupleInputBox("start", new SingleLineLabel(EffectProviderHelper.L("_MovementStart", "Start")), PositionTupleMode.XY, (startX, startY, 0, 0));
            panel.AddPositionTupleInputBox("end", new SingleLineLabel(EffectProviderHelper.L("_MovementEnd", "End")), PositionTupleMode.XY, (endX, endY, 0, 0));
            panel.AddSlider(
                "Duration",
                EffectProviderHelper.L("Effect_Movement_Duration", "Duration"),
                100,
                20000,
                duration,
                null,
                SliderUpdateEventCallMode.OnMouseUp);
            return panel;
        }

        public override (Dictionary<string, object>? newParams, Dictionary<string, IEffectArgumentField>? newFields) HandlePropertyPanelChange(IEffectProvider source, PropertyPanelPropertyChangedEventArgs args)
        {
            switch (args.Id)
            {
                case "start_X":
                    EffectProviderHelper.TrySetInt(Inner.Parameters, "StartX", args.Value);
                    return (Inner.Parameters, Inner.Fields);
                case "start_Y":
                    EffectProviderHelper.TrySetInt(Inner.Parameters, "StartY", args.Value);
                    return (Inner.Parameters, Inner.Fields);
                case "end_X":
                    EffectProviderHelper.TrySetInt(Inner.Parameters, "EndX", args.Value);
                    return (Inner.Parameters, Inner.Fields);
                case "end_Y":
                    EffectProviderHelper.TrySetInt(Inner.Parameters, "EndY", args.Value);
                    return (Inner.Parameters, Inner.Fields);
            }

            if (Inner.Fields.ContainsKey(args.Id) && EffectProviderHelper.TrySetInt(Inner.Parameters, args.Id, args.Value))
            {
                if (args.Id == "Duration")
                {
                    Inner.Parameters["Duration"] = Math.Max(100, EffectProviderHelper.GetInt(Inner.Parameters, "Duration", 1000));
                }
            }

            return (Inner.Parameters, Inner.Fields);
        }
    }
}
