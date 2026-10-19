using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.ApplicationAPIBase.Effect;

public interface IKeyFramedEffectUIProvider : IEffectProviderUIProvider
{
    PropertyPanelBuilder CreateStepUI(IKeyFrameStep step);
    bool HandleStepUIChange(IKeyFrameStep step, PropertyPanelPropertyChangedEventArgs args);
}
