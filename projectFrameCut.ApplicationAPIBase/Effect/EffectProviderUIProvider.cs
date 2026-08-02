using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.ApplicationAPIBase.Effect
{
    public interface IEffectProviderUIProvider
    {
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
        /// <param name="source">The effect provider that triggered the change.</param>
        /// <param name="args">The input arguments for the property panel change event.</param>
        /// <returns>The updated parameters or fields after handling the property panel change. Keep newParams to null means no changes to the parameters, and keep newFields to null means no changes to the fields.</returns>
        public (Dictionary<string, object>? newParams, Dictionary<string, IEffectArgumentField>? newFields) HandlePropertyPanelChange(IEffectProvider source,  PropertyPanelPropertyChangedEventArgs args);
    }
}
