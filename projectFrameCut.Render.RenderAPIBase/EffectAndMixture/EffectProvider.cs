using projectFrameCut.Drawing.Vector.ImportExport;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
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
        /// it SHOULD equals to <see cref="IEffect.TypeName"/>, <see cref="IEffectFactory.TypeName"/> and so on.
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
        public IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> InFields { get; }

        /// <summary>
        /// The output field's type of the effect provider.
        /// </summary>
        public EffectArgumentFieldDescriptor OutField { get; }

        /// <summary>
        /// The connected effect providers for the anchors in the effect group. 
        /// The key is the anchor id, and the value is the connected effect provider.
        /// </summary>
        /// <remarks>
        /// For output anchor the key should be <c>__Output__</c>.
        /// </remarks>
        public Dictionary<string, Guid> AnchorsBindingState { get; set; }

        /// <summary>
        /// The connected effect providers for the anchors in the effect group. 
        /// The key is the anchor id, and the value is the connected effect provider.
        /// </summary>
        public Dictionary<string, IEffectArgumentField> Fields { get; set; }

        /// <summary>
        /// The parameters of the effect provider, which is used to determine the settings of the effect.
        /// </summary>
        public Dictionary<string, object> Parameters { get; set; }

        /// <summary>
        /// Build the final effect(s) instance from the effect provider.
        /// </summary>
        /// <returns>
        /// The built effect instance(s). The result is an array of <see cref="IEffect"/> instances, which can be used to apply the effect(s) to the target.
        /// <para/>
        /// return multiple effects in the order of the effect stack, the first effect will be rendered first, and the last effect will be rendered last.
        /// </returns>
        /// <remarks>
        /// <b>DO NOT</b> set the output effect's <see cref="IEffect.Id"/>, <see cref="IEffect.BindedEffectGroupID"/> and <see cref="IEffect.Index"/> property manually.
        /// It will be set when the effect is created.
        /// </remarks>
        public IEffect[] Build();

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
        /// Build the specified effect implementation type (stateless factory).
        /// </summary>
        /// <param name="implementType">the desired implementation type.</param>
        /// <param name="parameters">the normalized parameters.</param>
        /// <returns>the single built effect.</returns>
        /// <remarks>
        /// The default implementation is conservative: it throws <see cref="NotSupportedException"/> so that
        /// providers which do not implement the stateless factory capability still compile. Providers that
        /// support it (e.g. <c>EffectProviderBase</c>) override this member.
        /// </remarks>
        public virtual IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            throw new NotSupportedException($"EffectProvider '{TypeName}' does not support stateless Build(implementType, parameters). Override Build(EffectImplementType, Dictionary<string, object>) to support it.");
        }

        /// <summary>
        /// Build an effect with the default implementation type (stateless factory).
        /// </summary>
        /// <param name="parameters">the normalized parameters.</param>
        /// <returns>the single built effect.</returns>
        public virtual IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null) => Build(DefaultImplementType, parameters);
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

    public record EffectArgumentFieldDescriptor : IEffectArgumentField
    {
        public string Id { get; set; } = string.Empty;
        public string TypeName { get; set; } = string.Empty;
        public string FromPlugin { get; set; } = string.Empty;
        public bool IsDynamic { get; set; } = false;
        public EffectArgumentFieldType FieldType { get; set; } = EffectArgumentFieldType.Unknown;
        public string DefaultValue { get; set; } = string.Empty;
        public string MinValue { get; set; } = string.Empty;
        public string MaxValue { get; set; } = string.Empty;
        public string[]? PresetOptions { get; set; }
        public string? Remarks { get; set; }

        public Lazy<object> GetGetter()
        {
            return new Lazy<object>(() => DefaultValue);
        }
    }

    /// <summary>
    /// Anchor helpers over <see cref="IEffectProvider.AnchorsBindingState"/>.
    /// They read/write the reserved <c>__Input__</c> / <c>__Output__</c> keys so the core stack
    /// can work with anchors uniformly without depending on the legacy <see cref="IEffectBundle"/> model.
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
        /// Reads the primary input anchor. Falls back to <see cref="IEffectProvider.InputAnchorGUID"/> when unbound.
        /// </summary>
        public static Guid GetInputAnchor(this IEffectProvider provider)
        {
            return provider.AnchorsBindingState is { } state && state.TryGetValue(InputKey, out var id)
                ? id
                : IEffectProvider.InputAnchorGUID;
        }

        /// <summary>
        /// Writes the primary input anchor.
        /// </summary>
        public static void SetInputAnchor(this IEffectProvider provider, Guid id)
        {
            provider.AnchorsBindingState ??= new Dictionary<string, Guid>();
            provider.AnchorsBindingState[InputKey] = id;
        }

        /// <summary>
        /// Reads the output anchor. Falls back to <see cref="IEffectProvider.OutputAnchorGUID"/> when unbound.
        /// </summary>
        public static Guid GetOutputAnchor(this IEffectProvider provider)
        {
            return provider.AnchorsBindingState is { } state && state.TryGetValue(OutputKey, out var id)
                ? id
                : IEffectProvider.OutputAnchorGUID;
        }

        /// <summary>
        /// Writes the output anchor.
        /// </summary>
        public static void SetOutputAnchor(this IEffectProvider provider, Guid id)
        {
            provider.AnchorsBindingState ??= new Dictionary<string, Guid>();
            provider.AnchorsBindingState[OutputKey] = id;
        }

        /// <summary>
        /// Returns every input anchor value currently bound in <see cref="IEffectProvider.AnchorsBindingState"/>.
        /// Excludes the output anchor key. Single-input providers yield a single-element list.
        /// </summary>
        public static List<Guid> GetInputAnchors(this IEffectProvider provider)
        {
            if (provider.AnchorsBindingState is not { } state) return new List<Guid>();
            return state.Where(kv => kv.Key != OutputKey).Select(kv => kv.Value).ToList();
        }

        /// <summary>
        /// Replaces the input anchors in <see cref="IEffectProvider.AnchorsBindingState"/>.
        /// The first element is stored under the primary <c>__Input__</c> key.
        /// </summary>
        public static void SetInputAnchors(this IEffectProvider provider, IEnumerable<Guid> ids)
        {
            provider.AnchorsBindingState ??= new Dictionary<string, Guid>();
            var list = ids?.ToList() ?? new List<Guid>();
            if (list.Count > 0)
            {
                provider.AnchorsBindingState[InputKey] = list[0];
            }
            else
            {
                provider.AnchorsBindingState[InputKey] = IEffectProvider.InputAnchorGUID;
            }
        }

        /// <summary>
        /// True when the provider declares more than one input anchor (in <see cref="IEffectProvider.InFields"/>).
        /// </summary>
        public static bool HasMultiInputAnchors(this IEffectProvider provider)
        {
            return provider.InFields is { Count: > 1 };
        }

        /// <summary>
        /// The input anchor names declared in <see cref="IEffectProvider.InFields"/>, or null for single-input providers.
        /// </summary>
        public static string[]? GetInputAnchorNames(this IEffectProvider provider)
        {
            return provider.InFields is { Count: > 1 } ? provider.InFields.Keys.ToArray() : null;
        }
    }


}
