using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.InteractableEditor;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.ApplicationPluginBase.Effect;

public class ProgressPlacerUI(IEffectProvider inner) : KeyFramedEffectUI(inner)
{
    public override PropertyPanelBuilder CreateStepUI(IKeyFrameStep step)
    {
        var panel = base.CreateStepUI(step);
        AddStepEditor(panel, () =>
        {
            var editor = new ClipPlaceConfiguratorView
            {
                TargetX = Convert.ToInt32(step.Fields["TargetX"].GetGetter()()),
                TargetY = Convert.ToInt32(step.Fields["TargetY"].GetGetter()()),
                TargetWidth = Convert.ToInt32(step.Fields["TargetWidth"].GetGetter()()),
                TargetHeight = Convert.ToInt32(step.Fields["TargetHeight"].GetGetter()()),
                Rotation = Convert.ToSingle(step.Fields["Rotation"].GetGetter()()),
                RelativeWidth = 1920, RelativeHeight = 1080,
            };
            return (editor, () =>
            {
                var position = editor.BuildPositionTuple();
                step.SetValues(new Dictionary<string, object>
                {
                    ["TargetX"] = position.TargetX, ["TargetY"] = position.TargetY,
                    ["TargetWidth"] = Math.Max(1, position.TargetWidth), ["TargetHeight"] = Math.Max(1, position.TargetHeight),
                    ["Rotation"] = position.Rotation,
                });
            });
        });
        return panel;
    }
}
