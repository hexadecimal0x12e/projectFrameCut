using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;

namespace projectFrameCut.ApplicationAPIBase.Effect
{
    public abstract class MockBindableEffectBundleBase<TFactory, TEffect> : IEffectBundle
        where TFactory : IBindableEffectFactory, new()
        where TEffect : class // Just for TypeName
    {
        public string TypeName => typeof(TEffect).Name;
        public string FromPlugin => "projectFrameCut.Render.Plugins.InternalPluginBase";
        public bool IsNormalEffect => false;
        public bool IsContinuousEffect => false; // Simplifying
        public bool IsBindableEffect => true;
        public bool Enabled { get; set; }


        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = typeof(TEffect).Name;
        public Dictionary<string, object> Parameters { get; set; } = new();

        public Guid BindedInputId { get; set; } = IEffectBundle.InputAnchorGUID;
        public Guid BindedOutputId { get; set; } = IEffectBundle.OutputAnchorGUID;
        public List<Guid>? BindedInputIds { get; set; }
        public bool IsMultiInput => false;

        public string InputAnchorDisplayName => !TypeName.Contains("Many") ? "Input anchor" : "";
        public string[]? InputAnchorsDisplayName => TypeName.Contains("Many") ? ["Input anchor 1","Input anchor 2"] :null;
        public string OutputAnchorDisplayName => "Output anchor";

        public int StartPoint { get; set; }
        public int EndPoint { get; set; }

        public List<string> ParametersNeeded => new();
        public Dictionary<string, string> ParametersType => new();

        public IEffectFactory[] Create()
        {
            var factory = new TFactory();
            this.ConfigureFactory(factory);
            return [factory];
        }

        public PropertyPanelBuilder CreateUI()
        {
            return new PropertyPanelBuilder();
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = this.Name,
                Description = "Mock Bindable Effect: " + TypeName,
                Thumbnail = null,
                VideoThumbnail = null
            };
        }
    }

    public class MockValueProviderBundle : MockBindableEffectBundleBase<MockValueProviderFactory, MockValueProvider> { }
    public class MockOneToOneProcessorBundle : MockBindableEffectBundleBase<MockOneToOneProcessorFactory, MockOneToOneProcessor> { }
    public class MockManyToOneProcessorBundle : MockBindableEffectBundleBase<MockManyToOneProcessorFactory, MockManyToOneProcessor> { }
    public class MockOneInputResultGeneratorBundle : MockBindableEffectBundleBase<MockOneInputResultGeneratorFactory, MockOneInputResultGenerator> { }
    public class MockManyInputResultGeneratorBundle : MockBindableEffectBundleBase<MockManyInputResultGeneratorFactory, MockManyInputResultGenerator> { }
}
