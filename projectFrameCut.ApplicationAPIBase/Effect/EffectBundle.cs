using CommunityToolkit.Maui.Views;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.ApplicationAPIBase.Effect
{
    public interface IEffectBundle
    {
        /// <summary>
        /// The TypeName of the EffectGroup.
        /// </summary>
        /// <remarks>
        /// it SHOULD equals to <see cref="IEffect.TypeName"/>, <see cref="IEffectFactory.TypeName"/> and so on.
        /// </remarks>
        public string TypeName { get; }

        public string FromPlugin { get; }

        public bool IsNormalEffect { get; }
        public bool IsContinuousEffect { get; }
        public bool IsBindableEffect { get; }

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
        /// The index of this EffectGroup's Index. 
        /// </summary>
        /// <remarks>
        /// DO NOT set this property manually. It will be set by the user interface when the effect group is added to the effect stack.
        /// </remarks>
        public int Index { get; set; }

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
        /// <returns>The final effect(s).</returns>
        public IEffectFactory[] Create();

        /// <summary>
        /// Create the Effect property UI.
        /// </summary>
        /// <remarks>
        /// To maintenance a uniform UI style, you'll need to use <see cref="PropertyPanelBuilder"/>.
        /// </remarks>
        public PropertyPanelBuilder CreateUI();

        /// <summary>
        /// Handle the change of the Effect property UI created via <see cref="CreateUI"/>.
        /// </summary>
        /// <remarks>
        /// Default implementation will simply update the parameter with the new value. Override this method if you need custom handling.
        /// </remarks>
        /// <param name="args">The input arguments for the property panel change event.</param>
        /// <returns>The updated parameters after handling the property panel change.</returns>
        public virtual Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            Parameters[args.Id] = args.Value;
            return Parameters;
        }

        /// <summary>
        /// Get the display information of this effect bundle.
        /// </summary>
        /// <param name="locate"></param>
        /// <returns></returns>
        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null);

    }

    public class EffectBundleDisplayItem
    {
        public required string Name { get; set; }
        public required string Description { get; set; }
        public ImageSource? Thumbnail { get; set; }
        public MediaSource? VideoThumbnail { get; set; }
    }

    public class EffectBundleData
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string BundleTypeName { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new();
        public bool Enabled { get; set; } = true;
        public string Name { get; set; }
        public int Index { get; set; }
    }
}
