using System.Collections.Generic;
using System.Threading.Tasks;

namespace projectFrameCut.ApplicationAPIBase.Effect
{
    /// <summary>
    /// A source that a dynamic field can be bound to. Its <see cref="Id"/> is what is stored
    /// under the <c>__Binding_{fieldId}</c> parameter key.
    /// </summary>
    public record ValueBindingSource(string Id, string DisplayName, string? OutputAnchorName = null);

    /// <summary>
    /// Host for configuring field bindings from the UI.
    /// Implemented by the node editor (<c>DraftEffectBindingView</c>) and injected into the effect
    /// provider before its property UI is built, so each field can offer a "bind" action.
    /// </summary>
    /// <remarks>
    /// When no host is injected (e.g. the property UI is built by scripts / MCP), fields degrade to
    /// static editors without a bind button.
    /// </remarks>
    public interface IEffectBindingHost
    {
        /// <summary>
        /// List the currently available binding sources: value providers in the current clip
        /// plus built-in time-driven sources.
        /// </summary>
        IReadOnlyList<ValueBindingSource> GetBindingSources();

        /// <summary>
        /// Resolve the display name of a binding source id, or null when unknown.
        /// </summary>
        string? GetSourceDisplayName(string sourceId);

        /// <summary>
        /// Create a new value-provider effect of the given provider type and bind the current field to it,
        /// returning the new source id (the created bundle's Guid), or null on failure.
        /// </summary>
        string? AddValueProvider(string providerTypeName);

        /// <summary>
        /// Apply a binding to a field of the current provider.
        /// </summary>
        void ApplyBinding(string fieldId, string sourceId);

        /// <summary>
        /// Remove the binding of a field of the current provider.
        /// </summary>
        void Unbind(string fieldId);

        /// <summary>
        /// Show the binding editor (a picker of available sources) for a field of the current provider,
        /// and apply the chosen binding (or unbind) when the user confirms.
        /// </summary>
        Task EditBinding(string fieldId);
    }
}
