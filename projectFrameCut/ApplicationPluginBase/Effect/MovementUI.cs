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

        public override PropertyPanelBuilder CreateUI(IEffectProvider _)
        {
            int startX = EffectProviderHelper.GetFieldInt(Inner.Fields, "StartX", 0);
            int startY = EffectProviderHelper.GetFieldInt(Inner.Fields, "StartY", 0);
            int endX = EffectProviderHelper.GetFieldInt(Inner.Fields, "EndX", 200);
            int endY = EffectProviderHelper.GetFieldInt(Inner.Fields, "EndY", 0);
            int duration = Math.Max(100, EffectProviderHelper.GetFieldInt(Inner.Fields, "Duration", 1000));

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
                    EffectProviderHelper.SetFieldValue(Inner.Fields, "StartX", DynamicParam.ToInt32(args.Value), EffectArgumentFieldType.Integer);
                    return (null, Inner.Fields);
                case "start_Y":
                    EffectProviderHelper.SetFieldValue(Inner.Fields, "StartY", DynamicParam.ToInt32(args.Value), EffectArgumentFieldType.Integer);
                    return (null, Inner.Fields);
                case "end_X":
                    EffectProviderHelper.SetFieldValue(Inner.Fields, "EndX", DynamicParam.ToInt32(args.Value), EffectArgumentFieldType.Integer);
                    return (null, Inner.Fields);
                case "end_Y":
                    EffectProviderHelper.SetFieldValue(Inner.Fields, "EndY", DynamicParam.ToInt32(args.Value), EffectArgumentFieldType.Integer);
                    return (null, Inner.Fields);
            }

            if (Inner.Fields.ContainsKey(args.Id))
            {
                EffectProviderHelper.SetFieldValue(Inner.Fields, args.Id, DynamicParam.ToInt32(args.Value), EffectArgumentFieldType.Integer);
                if (args.Id == "Duration")
                {
                    var duration = Math.Max(100, EffectProviderHelper.GetFieldInt(Inner.Fields, "Duration", 1000));
                    EffectProviderHelper.SetFieldValue(Inner.Fields, "Duration", duration, EffectArgumentFieldType.Integer);
                }
            }

            return (null, Inner.Fields);
        }
    }
}
