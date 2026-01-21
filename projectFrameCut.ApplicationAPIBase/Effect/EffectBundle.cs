using projectFrameCut.ApplicationAPIBase.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.ApplicationAPIBase.Effect
{
    public interface IEffectBundle
    {
        public string TypeName { get; }

        /// <summary>
        /// The id of the EffectGroup.
        /// </summary>
        /// <remarks>
        /// DO NOT set this property manually. It will be set when the effect group is created.
        /// </remarks>
        public string Id { get; set; }

        /// <summary>
        /// Get or set the name for the EffectGroup.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The arguments of the EffectGroup.
        /// </summary>
        public Dictionary<string, object> Parameters { get; set; }
        /// <summary>
        /// Indicates which parameters are needed for this effect.
        /// </summary>
        public List<string> ParametersNeeded { get; }
        /// <summary>
        /// Indicates the type of each parameter.
        /// </summary>
        public Dictionary<string, string> ParametersType { get; }

        /// <summary>
        /// Create the specified effects.
        /// </summary>
        /// <returns></returns>
        public IEffect[] Create();

        /// <summary>
        /// Create the Effect property UI.
        /// </summary>
        /// <remarks>
        /// To maintenance a uniform UI style, you'll need to use <see cref="PropertyPanelBuilder"/>.
        /// </remarks>
        public PropertyPanelBuilder CreateUI();

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args);

        /// <summary>
        /// Check and modify the effects created by Create() if needed.
        /// </summary>
        /// <param name="source"></param>
        /// <returns></returns>
        public IEffect[] Maintenance(IEffect[] source);

        public EffectBundleItem GetEffectBundleItem(string? locate = null);
    }

    public class EffectBundleItem
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public ImageSource Thumbnail { get; set; }
    }

    public class EffectBundleData
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string BundleTypeName { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new();
        public bool Enabled { get; set; } = true;
        public string Name { get; set; }
    }
}
