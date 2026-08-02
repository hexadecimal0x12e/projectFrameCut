using projectFrameCut.ApplicationAPIBase.Effect;
using System;

namespace projectFrameCut.ApplicationAPIBase.Effect
{
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
