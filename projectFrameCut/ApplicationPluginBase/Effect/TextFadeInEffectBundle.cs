using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Services;
using projectFrameCut.Shared;

namespace projectFrameCut.ApplicationPluginBase.Effect
{
    public class TextFadeInEffectBundle : IEffectBundle
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "TextFadeIn";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public Dictionary<string, object> Parameters { get; set; } = new();
        public List<string> ParametersNeeded => new();
        public Dictionary<string, string> ParametersType => new();

        public string TypeName => "TextFadeIn";
        public bool IsNormalEffect => false;
        public bool IsContinuousEffect => true;
        public bool IsBindableEffect => false;
        public EffectType TypeOfEffect => EffectType.ContinuousTextEffect;
        public EffectTarget Target => EffectTarget.Text;

        public Guid BindedInputId { get; set; } = IEffectBundle.InputAnchorGUID;
        public Guid BindedOutputId { get; set; } = IEffectBundle.OutputAnchorGUID;
        public List<Guid>? BindedInputIds { get; set; }
        public bool IsMultiInput => false;
        public string InputAnchorDisplayName => string.Empty;
        public string[]? InputAnchorsDisplayName => null;
        public string OutputAnchorDisplayName => string.Empty;

        public bool Enabled { get; set; } = true;
        public int StartPoint { get; set; }
        public int EndPoint { get; set; }

        public IEffectFactory[] Create()
        {
            var factory = new TextFadeInContinuousEffectFactory();
            this.ConfigureFactory(factory);
            return [factory];
        }

        public PropertyPanelBuilder CreateUI()
        {
            var panel = new PropertyPanelBuilder();
            panel.AddText(
                EffectBundleUiHelper.L("_TextFadeIn_Desc",
                    "Fades the text from transparent to fully visible over the specified duration."));
            return panel;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            return Parameters;
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = EffectBundleUiHelper.L("DisplayName_Effect_TextFadeIn", "Text Fade In"),
                Description = EffectBundleUiHelper.L("Description_Effect_TextFadeIn", "Fades in text from transparent to fully visible over time."),
                Thumbnail = ImageSource.FromFile(FileSystemService.GetAppPackageFileSync("EffectSample/textFadeIn.png"))
            };
        }
    }
}
