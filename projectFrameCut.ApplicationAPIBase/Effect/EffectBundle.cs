using projectFrameCut.ApplicationAPIBase.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.ApplicationAPIBase.Effect
{
    public interface IEffectBundle
    {
        /// <summary>
        /// The display name for the EffectGroup in neutral language.
        /// </summary>
        public string DefaultNeutralLanguageDisplayName { get; }
        /// <summary>
        /// The id of the EffectGroup.
        /// </summary>
        /// <remarks>
        /// DO NOT set this property manually. It will be set when the effect group is created.
        /// </remarks>
        public string Id { get; set; }

        /// <summary>
        /// Get or set the display name for the EffectGroup.
        /// </summary>
        public string DisplayName { get; set; }

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
        public PropertyPanelBuilder CreateUI(ref IEffectBundle group);

        /// <summary>
        /// Check and modify the effects created by Create() if needed.
        /// </summary>
        /// <param name="source"></param>
        /// <returns></returns>
        public IEffect[] Maintenance(IEffect[] source);
    }
}
