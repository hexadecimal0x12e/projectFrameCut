using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut;

public partial class DraftPage
{
    private static object GuiEffectSteps(Guid clipId, IKeyFramedEffectProvider provider, int? index = null)
    {
        var steps = provider.Steps;
        if (index is { } i && (i < 0 || i >= steps.Count)) throw new ArgumentOutOfRangeException(nameof(index));
        return new
        {
            ClipId = clipId, ProviderId = provider.Id,
            DefaultFields = provider.CreateStep(0).Fields.Values.Select(GuiField).ToArray(),
            Steps = steps.Select((step, i) => new
            {
                StepIndex = i, step.Progress, Fields = step.Fields.Values.Select(GuiField).ToArray(),
            }).Where(s => index is null || s.StepIndex == index).ToArray(),
        };
    }
}
