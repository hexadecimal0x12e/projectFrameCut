using System;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    /// <summary>
    /// A dynamic effect argument field that is bound to a value-provider source.
    /// Its current value is resolved from <see cref="ValueProviderFrameContext"/> at render time,
    /// falling back to <see cref="StaticFallbackValue"/> when the source has no value for the frame.
    /// </summary>
    /// <remarks>
    /// This is the official replacement of the legacy <see cref="IBindableArgumentEffect"/> binding
    /// mechanism, referenced by the <see cref="ObsoleteAttribute"/> messages on the old interfaces.
    /// </remarks>
    public class DynamicEffectParamField : IEffectArgumentField
    {
        public string Id { get; set; } = "Dynamic value";

        public string TypeName => "DynamicEffectParamField";

        public string FromPlugin => "projectFrameCut.Render.Plugins.InternalPluginBase";

        public bool IsDynamic => true;

        public required EffectArgumentFieldType FieldType { get; set; }

        /// <summary>
        /// The binding source id: a value-provider bundle Guid, or a built-in source
        /// like <c>builtin://frame</c> / <c>builtin://progress</c>.
        /// </summary>
        public string? BoundProviderId { get; set; }

        /// <summary>
        /// The static value to fall back to when the bound source has no value for the current frame.
        /// </summary>
        public object? StaticFallbackValue { get; set; }

        public string DefaultValue { get; set; } = string.Empty;
        public string MinValue { get; set; } = string.Empty;
        public string MaxValue { get; set; } = string.Empty;
        public string[]? PresetOptions { get; set; }
        public string? Remarks { get; set; }

        public DynamicEffectParamField() { }

        public DynamicEffectParamField(string id, EffectArgumentFieldType fieldType, string? boundProviderId, object? staticFallbackValue)
        {
            Id = id;
            FieldType = fieldType;
            BoundProviderId = boundProviderId;
            StaticFallbackValue = staticFallbackValue;
        }

        public Lazy<object> GetGetter()
        {
            var boundId = BoundProviderId ?? string.Empty;
            var fallback = StaticFallbackValue;
            return new Lazy<object>(() => ValueProviderFrameContext.Get(boundId) ?? fallback ?? new object());
        }
    }
}
