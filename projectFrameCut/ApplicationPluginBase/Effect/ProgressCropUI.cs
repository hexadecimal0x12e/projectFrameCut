using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.InteractableEditor;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.ApplicationPluginBase.Effect;

public class ProgressCropUI(IEffectProvider inner) : KeyFramedEffectUI(inner)
{
    public override PropertyPanelBuilder CreateStepUI(IKeyFrameStep step)
    {
        var panel = base.CreateStepUI(step);
        AddStepEditor(panel, () =>
        {
            var editor = new ClipCropConfiguratorView
            {
                StartX = Convert.ToInt32(step.Fields["StartX"].GetGetter()()),
                StartY = Convert.ToInt32(step.Fields["StartY"].GetGetter()()),
                CropWidth = Convert.ToInt32(step.Fields["Width"].GetGetter()()),
                CropHeight = Convert.ToInt32(step.Fields["Height"].GetGetter()()),
                Angle = Convert.ToSingle(step.Fields["Angle"].GetGetter()()),
                RelativeWidth = 1920, RelativeHeight = 1080,
            };
            return (editor, () => step.SetValues(new Dictionary<string, object>
            {
                ["StartX"] = editor.StartX, ["StartY"] = editor.StartY,
                ["Width"] = Math.Max(1, editor.CropWidth), ["Height"] = Math.Max(1, editor.CropHeight), ["Angle"] = editor.Angle,
            }));
        });
        return panel;
    }
}
