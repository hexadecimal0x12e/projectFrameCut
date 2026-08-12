using projectFrameCut.Drawing.Vector.ImportExport;
using System;
using System.Collections.Generic;
using System.Linq;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    /// <summary>
    /// A system that let fields drive the parameters dynamically or statically, and rendering priority of the effect.
    /// </summary>
    /// <remarks>
    /// The IEffectProvider can replace every <see cref="IBindableArgumentEffect"/> and <see cref="EffectFactory"/>,
    /// with <see cref="IValueProviderEffect"/> allows dynamic value binding.
    /// </remarks>
    public interface IEffectProvider
    {
        /// <summary>
        /// The ID for input anchor.
        /// </summary>
        public static readonly Guid InputAnchorGUID = new("00000000-0000-0000-0000-000000000000");
        /// <summary>
        /// The ID for output anchor.
        /// </summary>
        public static readonly Guid OutputAnchorGUID = new("ffffffff-ffff-ffff-ffff-ffffffffffff");
        /// <summary>
        /// The Id for any unconnected anchor.
        /// </summary>
        public static readonly Guid NoConnectionGUID = new("00001234-5678-90ab-cdef-012345678900");
        /// <summary>
        /// The reserved parameter key that tells a provider to build the continuous variant of an effect.
        /// Consumed by the Crop provider to select between the normal crop and the progress cropper.
        /// </summary>
        public const string IsContinuousEffectParameterKey = "__IsContinuous__";

        /// <summary>
        /// The TypeName of the EffectGroup.
        /// </summary>
        /// <remarks>
        /// it SHOULD equals to <see cref="IEffect.TypeName"/> and so on.
        /// </remarks>
        public string TypeName { get; }

        /// <summary>
        /// Indicate which plugin this effect comes from, which is used to determine which plugin to use when creating the effect.
        /// </summary>
        public string FromPlugin { get; }

        /// <summary>
        /// Get the type of the effect, which is used to determine how to process this effect.
        /// </summary>
        public EffectType TypeOfEffect { get; }

        /// <summary>
        /// Get the target of the effect, which is used to determine where this effect can be applied.
        /// </summary>
        public EffectTarget Target { get; }

        /// <summary>
        /// Determine whether this effect is enabled.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// The id of the EffectGroup.
        /// </summary>
        /// <remarks>
        /// DO NOT set this property manually. It will be set when the effect group is created.
        /// </remarks>
        public Guid Id { get; set; }

        /// <summary>
        /// Get or set the name for the EffectGroup.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// All input fields of the effect provider, which is used to determine what kind of input data the effect can accept.
        /// The key is the field id, and the value is the field descriptor.
        /// </summary>
        /// <remarks>
        /// None of the input fields except the <c>__Input__</c> field can be act as a picture source. 
        /// The <c>__Input__</c> field is the primary input for the effect, and it is used to determine the input picture for the effect.
        /// </remarks>
        public IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> InFields { get; }

        /// <summary>
        /// The output field's descriptor of the effect provider.
        /// </summary>
        public EffectArgumentFieldDescriptor OutField { get; }

        /// <summary>
        /// Persisted binding configuration. <c>__Input__</c> stores the single picture source,
        /// <c>__Output__</c> stores the final-output marker, and ordinary field ids store value sources.
        /// Values are strings so both Guid-based sources and <c>builtin://</c> sources are representable.
        /// </summary>
        /// <remarks>
        /// For output anchor the key should be <c>__Output__</c>.
        /// </remarks>
        public Dictionary<string, string> AnchorsBindingState { get; set; }

        /// <summary>
        /// The connected effect providers for the anchors in the effect group. 
        /// The key is the anchor id, and the value is the connected effect provider.
        /// </summary>
        public Dictionary<string, IEffectArgumentField> Fields { get; set; }

        /// <summary>
        /// Metadata dictionary for the effect provider. Carries extra information that is not part of
        /// the effect parameters, such as <c>ImplementType</c>, <c>__IsContinuous__</c>, and
        /// <c>__DraftEffectBindingView_InteractiveEditorX/Y__</c>.
        /// </summary>
        public Dictionary<string, object> MetaData { get; set; }

        /// <summary>
        /// Build the final effect(s) instance from the effect provider.
        /// </summary>
        /// <returns>
        /// The built effect instance(s). The result is an array of <see cref="IEffect"/> instances, which can be used to apply the effect(s) to the target.
        /// <para/>
        /// return multiple effects in the order of the effect stack, the first effect will be rendered first, and the last effect will be rendered last.
        /// </returns>
        /// <remarks>
        /// <b>DO NOT</b> set the output effect's <see cref="IEffect.Id"/>, <see cref="IEffect.BindedEffectProvidingSystemID"/> and <see cref="IEffect.Index"/> property manually.
        /// It will be set when the effect is created.
        /// <para/>
        /// Throw a <see cref="NotSupportedException"/> if the effect does not yield IPicture output (e.g. <see cref="IValueProviderEffect"/>)
        /// and Providers that can create a ordinary effect (e.g. <see cref="INormalEffect"/>) override this member.
        /// </remarks>
        public IEffect[] Build();

        /// <summary>
        /// Restore the <see cref="IEffect"/> instance from the <paramref name="parameters"/> param dictionary with a specific implementation type.
        /// </summary>
        /// <param name="implementType">the desired implementation type.</param>
        /// <param name="parameters">the normalized parameters.</param>
        /// <returns>the single built effect.</returns>
        /// <remarks>
        /// Not recommended to use this method directly. Use <see cref="Build"/> instead to allow dynamic binding.
        /// Let it throw a <see cref="NotSupportedException"/> to indicate that the provider does not support stateless factory. 
        /// <para/>
        /// This allows legacy providers which do not implement the stateless factory capability still compile. Providers that
        /// support it (e.g. <c>EffectProviderBase</c>) override this member.
        /// </remarks>
        public IEffect RestoreInstance(EffectImplementType implementType, Dictionary<string, object>? parameters = null);

        /// <summary>
        /// Get the supported implement types of this effect.
        /// </summary>
        public virtual EffectImplementType[] SupportsImplementTypes => [];

        /// <summary>
        /// Gets the default effect implementation type supported by this instance.
        /// </summary>
        /// <remarks>
        /// The default implementation type is determined by the first entry in the
        /// <see cref="SupportsImplementTypes"/> array. If no implementation types are supported, the value is
        /// <see cref="EffectImplementType.NotSpecified"/>.
        /// </remarks>
        public virtual EffectImplementType DefaultImplementType => SupportsImplementTypes.Length > 0 ? SupportsImplementTypes[0] : EffectImplementType.NotSpecified;

        /// <summary>
        /// Build an effect with the default implementation type (stateless factory).
        /// </summary>
        /// <param name="parameters">the normalized parameters.</param>
        /// <returns>the single built effect.</returns>
        public virtual IEffect RestoreInstanceWithDefaultType(Dictionary<string, object>? parameters = null) => RestoreInstance(DefaultImplementType, parameters);

        /// <summary>
        /// Indicates which parameters are needed for this effect.
        /// </summary>
        /// <remarks>
        /// Default implementation derives the list from the non-<see cref="EffectArgumentFieldType.IPicture"/> settable fields.
        /// </remarks>
        public virtual List<string> ParametersNeeded => Fields
            .Where(c => !c.Value.FieldType.HasFlag(EffectArgumentFieldType.IPicture))
            .Select(c => c.Key)
            .ToList();

        /// <summary>
        /// Indicates the type of each parameter.
        /// </summary>
        /// <remarks>
        /// Default implementation derives the type map from the non-<see cref="EffectArgumentFieldType.IPicture"/> settable fields.
        /// </remarks>
        public virtual Dictionary<string, string> ParametersType => Fields
            .Where(c => !c.Value.FieldType.HasFlag(EffectArgumentFieldType.IPicture))
            .ToDictionary(c => c.Key, c => EffectProviderContractMapping.FieldTypeToParamType(c.Value.FieldType));
    }

    /// <summary>
    /// Contract helpers over <see cref="IEffectProvider"/> for the stateless factory members.
    /// </summary>
    public static class EffectProviderContractMapping
    {
        /// <summary>
        /// Maps an <see cref="EffectArgumentFieldType"/> to the parameter type name understood by
        /// <see cref="EffectArgsHelper.ConvertElementDictToObjectDict(Dictionary{string, object}, Dictionary{string, string}, IEffectArgsEnumHandler)"/>.
        /// </summary>
        public static string FieldTypeToParamType(EffectArgumentFieldType fieldType)
        {
            return (fieldType & (EffectArgumentFieldType)0x3FF) switch
            {
                EffectArgumentFieldType.Integer => EffectArgsHelper.ArgTypeInt32,
                EffectArgumentFieldType.UnsignedInteger => EffectArgsHelper.ArgTypeUInt16,
                EffectArgumentFieldType.Numeric => EffectArgsHelper.ArgTypeFloat,
                EffectArgumentFieldType.Boolean => EffectArgsHelper.ArgTypeBool,
                _ => EffectArgsHelper.ArgTypeString,
            };
        }
    }

    /// <summary>
    /// Binding configuration helpers over <see cref="IEffectProvider.AnchorsBindingState"/>.
    /// The dictionary is the persisted source of truth. <see cref="IEffectProvider.Fields"/> is a
    /// materialized runtime projection and must not be used to discover stored bindings.
    /// </summary>
    public static class EffectProviderAnchorExtensions
    {
        /// <summary>
        /// The anchor key of the primary input in <see cref="IEffectProvider.AnchorsBindingState"/>.
        /// </summary>
        public const string InputKey = "__Input__";
        /// <summary>
        /// The anchor key of the output in <see cref="IEffectProvider.AnchorsBindingState"/>.
        /// </summary>
        public const string OutputKey = "__Output__";

        /// <summary>
        /// Returns whether the provider declares <c>__Input__</c> as its only picture input.
        /// Non-picture descriptors do not participate in the picture graph.
        /// </summary>
        public static bool HasMainPictureInput(this IEffectProvider provider)
        {
            return provider.InFields.TryGetValue(InputKey, out var main)
                && main.FieldType.HasFlag(EffectArgumentFieldType.IPicture)
                && !provider.InFields.Any(field => field.Key != InputKey
                    && field.Value.FieldType.HasFlag(EffectArgumentFieldType.IPicture));
        }

        /// <summary>
        /// Reads the configured source of the single picture input.
        /// </summary>
        public static string GetMainInputSource(this IEffectProvider provider)
        {
            return provider.AnchorsBindingState is { } state && state.TryGetValue(InputKey, out var id)
                ? id
                : IEffectProvider.NoConnectionGUID.ToString();
        }

        /// <summary>
        /// Configures the source of the single picture input.
        /// </summary>
        public static void SetMainInputSource(this IEffectProvider provider, string sourceId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
            if (!provider.HasMainPictureInput())
                throw new InvalidOperationException($"Provider '{provider.TypeName}' does not declare '__Input__' as its only picture input.");
            var state = new Dictionary<string, string>(provider.AnchorsBindingState ?? []);
            state[InputKey] = sourceId;
            provider.AnchorsBindingState = state;
        }

        public static void SetMainInputSource(this IEffectProvider provider, Guid sourceId) => provider.SetMainInputSource(sourceId.ToString());

        public static void DisconnectMainInput(this IEffectProvider provider)
        {
            var state = new Dictionary<string, string>(provider.AnchorsBindingState ?? []);
            state[InputKey] = IEffectProvider.NoConnectionGUID.ToString();
            provider.AnchorsBindingState = state;
        }

        /// <summary>
        /// Returns true when this provider is the sole configured source of the final picture output.
        /// </summary>
        public static bool IsFinalOutputSource(this IEffectProvider provider)
        {
            return provider.AnchorsBindingState is { } state
                && state.TryGetValue(OutputKey, out var id)
                && id == IEffectProvider.OutputAnchorGUID.ToString();
        }

        /// <summary>
        /// Sets or clears the final-output marker. Collection-level callers are responsible for
        /// clearing the marker on other providers before setting it here.
        /// </summary>
        public static void SetFinalOutputSource(this IEffectProvider provider, bool isFinalOutput)
        {
            var state = new Dictionary<string, string>(provider.AnchorsBindingState ?? []);
            state[OutputKey] = (isFinalOutput ? IEffectProvider.OutputAnchorGUID : IEffectProvider.NoConnectionGUID).ToString();
            provider.AnchorsBindingState = state;
        }

        /// <summary>
        /// Reads a stored value-field binding without consulting the materialized Fields collection.
        /// </summary>
        public static bool TryGetFieldBinding(this IEffectProvider provider, string fieldId, out string sourceId)
        {
            sourceId = string.Empty;
            return fieldId != InputKey
                && fieldId != OutputKey
                && provider.AnchorsBindingState is { } state
                && state.TryGetValue(fieldId, out sourceId)
                && !string.IsNullOrWhiteSpace(sourceId);
        }

        /// <summary>
        /// Stores a value-field binding on the provider that owns the target field.
        /// </summary>
        public static void SetFieldBinding(this IEffectProvider provider, string fieldId, string sourceId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fieldId);
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
            if (fieldId == InputKey || fieldId == OutputKey)
                throw new ArgumentException($"'{fieldId}' is a reserved picture binding key.", nameof(fieldId));
            if (!provider.Fields.ContainsKey(fieldId))
                throw new ArgumentException($"Provider '{provider.TypeName}' does not own field '{fieldId}'.", nameof(fieldId));

            var state = new Dictionary<string, string>(provider.AnchorsBindingState ?? []);
            state[fieldId] = sourceId;
            provider.AnchorsBindingState = state;
        }

        /// <summary>
        /// Removes a stored value-field binding. The static fallback remains in Fields and is restored
        /// by the next BindingHelper materialization pass.
        /// </summary>
        public static void ClearFieldBinding(this IEffectProvider provider, string fieldId)
        {
            var state = new Dictionary<string, string>(provider.AnchorsBindingState ?? []);
            state.Remove(fieldId);
            provider.AnchorsBindingState = state;
        }

        /// <summary>
        /// Enumerates only stored value-field bindings, excluding the two reserved picture keys.
        /// </summary>
        public static IEnumerable<KeyValuePair<string, string>> EnumerateFieldBindings(this IEffectProvider provider)
        {
            return (provider.AnchorsBindingState ?? [])
                .Where(kv => kv.Key != InputKey && kv.Key != OutputKey);
        }
    }


}
