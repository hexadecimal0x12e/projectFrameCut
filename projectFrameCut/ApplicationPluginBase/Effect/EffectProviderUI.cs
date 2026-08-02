using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;

namespace projectFrameCut.ApplicationPluginBase.Effect
{
    /// <summary>
    /// The App-layer UI wrapper of a Render-side <see cref="IEffectProvider"/>.
    /// It implements ONLY <see cref="IEffectProviderUIProvider"/> (plus <see cref="IBindingHostHolder"/>);
    /// it does NOT implement <see cref="IEffectProvider"/>. The wrapped provider is the exact instance being
    /// edited in the node editor, so its <see cref="IEffectProvider.Parameters"/> / <see cref="IEffectProvider.Fields"/>
    /// are read and written by the property UI.
    /// </summary>
    public class EffectProviderUI : IEffectProviderUIProvider, IBindingHostHolder
    {
        public EffectProviderUI(IEffectProvider inner)
        {
            Inner = inner;
        }

        /// <summary>
        /// The underlying Render-side provider instance.
        /// </summary>
        public IEffectProvider Inner { get; }

        /// <summary>
        /// The binding host injected by the node editor, forwarded to the metadata-driven UI builder
        /// so each field can offer a bind action.
        /// </summary>
        public IEffectBindingHost? BindingHost { get; set; }

        /// <summary>
        /// Metadata driven property UI built from the provider's fields.
        /// </summary>
        public virtual PropertyPanelBuilder CreateUI()
        {
            var panel = new PropertyPanelBuilder();
            EffectProviderUIHelper.BuildUI(Inner, panel, BindingHost);
            return panel;
        }

        /// <summary>
        /// Handles a property panel change by writing the typed value back into the provider's parameters.
        /// </summary>
        public virtual (Dictionary<string, object>? newParams, Dictionary<string, IEffectArgumentField>? newFields) HandlePropertyPanelChange(IEffectProvider source, PropertyPanelPropertyChangedEventArgs args)
        {
            return EffectProviderUIHelper.HandleChange(Inner, args);
        }
    }
}
