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
        /// Create the Effect property UI for the given source.
        /// </summary>
        /// <remarks>
        /// To maintenance a uniform UI style, you'll need to use <see cref="PropertyPanelBuilder"/>.
        /// </remarks>
        public PropertyPanelBuilder CreateUI(IEffectProvider source);

        /// <summary>
        /// Handle the change of the Effect property UI created via <see cref="CreateUI"/>.
        /// </summary>
        /// <param name="source">The effect provider that triggered the change.</param>
        /// <param name="args">The input arguments for the property panel change event.</param>
        /// <returns>The updated parameters or fields after handling the property panel change. Keep newParams to null means no changes to the parameters, and keep newFields to null means no changes to the fields.</returns>
        public (Dictionary<string, object>? newParams, Dictionary<string, IEffectArgumentField>? newFields) HandlePropertyPanelChange(IEffectProvider source,  PropertyPanelPropertyChangedEventArgs args);

        /// <summary>
        /// Returns the static display configuration for the given provider, including thumbnail sources
        /// and localization keys. The default implementation uses key conventions and the thumbnail
        /// mapping table.
        /// </summary>
        public EffectProviderDisplayItem GetDisplayItem(IEffectProvider source) => EffectProviderDisplayDefaults.BuildDefault(source);

        /// <summary>
        /// Returns the localized effect name for the given provider and BCP-47 locale tag.
        /// </summary>
        public string GetLocalizedEffectName(IEffectProvider source, string locate)
        {
            var item = GetDisplayItem(source);
            return EffectProviderDisplayDefaults.ResolveLocalized(item.LocalizedNameKey, source.TypeName, locate);
        }

        /// <summary>
        /// Returns the localized effect description for the given provider and BCP-47 locale tag.
        /// </summary>
        public string GetLocalizedEffectDescription(IEffectProvider source, string locate)
        {
            var item = GetDisplayItem(source);
            return EffectProviderDisplayDefaults.ResolveLocalized(item.LocalizedDescriptionKey, "", locate);
        }

        /// <summary>
        /// Returns the localized field name and description for the given provider, field ID,
        /// and BCP-47 locale tag. Uses key conventions when no explicit mapping is provided.
        /// </summary>
        public (string name, string description) GetLocalizedFieldInfo(IEffectProvider source, string fieldId, string locate)
        {
            var item = GetDisplayItem(source);
            string? nameKey = null;
            string? descKey = null;

            if (item.Fields is not null && item.Fields.TryGetValue(fieldId, out var fieldItem))
            {
                nameKey = fieldItem.LocalizedNameKey;
                descKey = fieldItem.LocalizedDescriptionKey;
            }

            nameKey ??= $"_{fieldId}";
            descKey ??= $"Description_Field_{source.TypeName}_{fieldId}";

            var name = EffectProviderDisplayDefaults.ResolveLocalized(nameKey, fieldId, locate);
            var desc = EffectProviderDisplayDefaults.ResolveLocalized(descKey, "", locate);
            return (name, desc);
        }
    }

    /// <summary>
    /// Marks a UI provider that can hold an <see cref="IEffectBindingHost"/>.
    /// The binding host is injected by the node editor before the property UI is built so each field
    /// can offer a bind action. Implemented by <c>EffectProviderUI</c> and its subclasses.
    /// </summary>
    public interface IBindingHostHolder
    {
        /// <summary>
        /// The UI binding host used to configure dynamic field bindings, injected by the node editor
        /// before the property UI is built. Null when the UI is built outside the node editor.
        /// </summary>
        public IEffectBindingHost? BindingHost { get; set; }
    }
}
