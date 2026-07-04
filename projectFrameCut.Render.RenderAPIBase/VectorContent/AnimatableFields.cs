using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.RenderAPIBase.VectorContent
{
    /// <summary>
    /// A class representing a field of a vector component that can be animated over time, with properties for its identifier, display name, description, and value range.
    /// </summary>
    public class AnimatableField
    {
        /// <summary>
        /// A unique identifier for this animatable field, used for serialization and referencing in animation tracks.
        /// </summary>
        public string Id { get; init; }
        /// <summary>
        /// A user-friendly name for this animatable field, suitable for display in UI elements.
        /// </summary>
        public string DisplayName { get; init; }
        /// <summary>
        /// A description of this animatable field, providing context and guidance for users on how it affects the component's behavior or appearance.
        /// </summary>
        public string Description { get; init; }
        /// <summary>
        /// The current value of this animatable field, which can be modified by animation tracks or user input.
        /// </summary>
        public float MinimumValue { get; init; }
        /// <summary>
        /// The maximum value of this animatable field, which can be modified by animation tracks or user input.
        /// </summary>
        public float MaximumValue { get; init; }

        /// <summary>
        /// Get the localized name and description for this animatable field based on the provided locale identifier.
        /// </summary>
        /// <param name="localeId">the locale identifier in BCP-47 format.</param>
        /// <returns>A tuple containing the localized name and description.</returns>
        public virtual (string Name, string Description) GetLocalizedDescription(string localeId)
        {
            return (DisplayName, Description);
        }
    }
}
