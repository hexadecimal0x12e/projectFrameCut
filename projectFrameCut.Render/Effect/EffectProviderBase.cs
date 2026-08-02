using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace projectFrameCut.Render.Effect
{
    /// <summary>
    /// The shared base class of the Render-side effect providers.
    /// It implements <see cref="IEffectProvider"/> (core contract) WITHOUT any UI capability.
    /// The property-panel UI is provided separately by the App-layer <c>EffectProviderUI</c> wrappers.
    /// </summary>
    /// <remarks>
    /// The instance state is driven by <see cref="Parameters"/> / <see cref="Fields"/> / <see cref="AnchorsBindingState"/>.
    /// Subclasses implement <see cref="BuildEffects"/> to create the final effect(s); the stateless factory
    /// members (<see cref="Build(EffectImplementType, Dictionary{string, object})"/> /
    /// <see cref="BuildWithDefaultType"/>) and the stateful <see cref="Build()"/> all route into it.
    /// </remarks>
    public abstract class EffectProviderBase : IEffectProvider
    {
        /// <summary>
        /// The parameter key used to pass the desired <see cref="EffectImplementType"/> into <see cref="Build()"/>.
        /// It is consumed and immediately removed by <see cref="ResolveImplementType"/> to avoid leaking into serialized parameters.
        /// </summary>
        public const string ImplementTypeParameterKey = "ImplementType";
        /// <summary>
        /// The key of the primary input anchor in <see cref="AnchorsBindingState"/>.
        /// </summary>
        public const string PrimaryInputAnchorKey = "__Input__";
        /// <summary>
        /// The key of the output anchor in <see cref="AnchorsBindingState"/>.
        /// </summary>
        public const string OutputAnchorKey = "__Output__";
        /// <summary>
        /// The reserved parameter key that tells a provider to build the continuous variant of an effect.
        /// Consumed by <see cref="CropEffectProvider"/> to select between the normal crop and the progress cropper.
        /// </summary>
        public const string IsContinuousEffectParameterKey = IEffectProvider.IsContinuousEffectParameterKey;

        protected EffectProviderBase()
        {
            Parameters = new Dictionary<string, object>();
        }

        #region Identity (IEffectProvider)

        public abstract string TypeName { get; }

        public virtual string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public abstract EffectType TypeOfEffect { get; }

        public abstract EffectTarget Target { get; }

        public virtual bool Enabled { get; set; } = true;

        public Guid Id { get; set; } = Guid.NewGuid();

        public virtual string Name { get; set; } = "New Effect";

        #endregion

        #region Field metadata (subclasses implement)

        /// <summary>
        /// Define the settable argument fields of the effect.
        /// Each field maps to a parameter key in <see cref="Parameters"/>.
        /// </summary>
        protected abstract IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields();

        /// <summary>
        /// Define the input fields (anchors) of the effect. Defaults to a single IPicture input.
        /// </summary>
        protected virtual IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields()
        {
            return new Dictionary<string, EffectArgumentFieldDescriptor>
            {
                { PrimaryInputAnchorKey, DefaultField(PrimaryInputAnchorKey) }
            };
        }

        /// <summary>
        /// Define the output field (anchor) of the effect. Defaults to an IPicture output.
        /// </summary>
        protected virtual EffectArgumentFieldDescriptor DefineOutField()
        {
            return DefaultField(OutputAnchorKey);
        }

        private EffectArgumentFieldDescriptor DefaultField(string id)
        {
            return new EffectArgumentFieldDescriptor
            {
                Id = id,
                TypeName = "IPicture",
                FromPlugin = FromPlugin,
                FieldType = EffectArgumentFieldType.IPicture,
                DefaultValue = string.Empty,
            };
        }

        /// <summary>
        /// A compact helper for subclasses to declare a settable argument field.
        /// </summary>
        protected EffectArgumentFieldDescriptor Field(
            string id,
            EffectArgumentFieldType fieldType,
            string defaultValue,
            string? min = null,
            string? max = null,
            string[]? presetOptions = null,
            string? remarks = null,
            string? typeName = null)
        {
            var ft = fieldType;
            if (min is not null) ft |= EffectArgumentFieldType.HasMinValue;
            if (max is not null) ft |= EffectArgumentFieldType.HasMaxValue;

            return new EffectArgumentFieldDescriptor
            {
                Id = id,
                TypeName = typeName ?? EffectProviderContractMapping.FieldTypeToParamType(fieldType),
                FromPlugin = FromPlugin,
                FieldType = ft,
                DefaultValue = defaultValue,
                MinValue = min ?? "",
                MaxValue = max ?? "",
                PresetOptions = presetOptions,
                Remarks = remarks,
            };
        }

        #endregion

        #region IEffectProvider

        public IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> InFields => DefineInFields();

        public EffectArgumentFieldDescriptor OutField => DefineOutField();

        /// <summary>
        /// The single source of truth of the anchor bindings.
        /// Only the reserved <c>__Input__</c> / <c>__Output__</c> keys are written back on set.
        /// </summary>
        public Dictionary<string, Guid> AnchorsBindingState
        {
            get => new()
            {
                { PrimaryInputAnchorKey, _inputId },
                { OutputAnchorKey, _outputId }
            };
            set
            {
                if (value is null) return;
                if (value.TryGetValue(PrimaryInputAnchorKey, out var inputId)) _inputId = inputId;
                if (value.TryGetValue(OutputAnchorKey, out var outputId)) _outputId = outputId;
            }
        }

        /// <summary>
        /// The current settable fields, materialized from <see cref="DefineFields"/> and <see cref="Parameters"/>.
        /// A field bound to a value provider (via the <c>__Binding_{id}</c> reserved parameter key) is returned
        /// as a <see cref="DynamicEffectParamField"/>; otherwise a <see cref="StaticEffectArgumentField"/> is returned.
        /// The setter merges the given field values back into <see cref="Parameters"/>.
        /// </summary>
        public Dictionary<string, IEffectArgumentField> Fields
        {
            get
            {
                var result = new Dictionary<string, IEffectArgumentField>();
                foreach (var desc in DefineFields())
                {
                    object value = Parameters.TryGetValue(desc.Id, out var raw) && raw is not null
                        ? raw
                        : ParseDefault(desc);
                    if (DynamicParam.IsBound(Parameters, desc.Id))
                    {
                        result[desc.Id] = new DynamicEffectParamField
                        {
                            Id = desc.Id,
                            FieldType = desc.FieldType,
                            BoundProviderId = DynamicParam.GetBoundSource(Parameters, desc.Id),
                            StaticFallbackValue = value,
                            DefaultValue = desc.DefaultValue,
                            MinValue = desc.MinValue,
                            MaxValue = desc.MaxValue,
                            PresetOptions = desc.PresetOptions,
                            Remarks = desc.Remarks,
                        };
                    }
                    else
                    {
                        result[desc.Id] = new StaticEffectArgumentField
                        {
                            Id = desc.Id,
                            FieldType = desc.FieldType,
                            Value = value,
                            DefaultValue = desc.DefaultValue,
                            MinValue = desc.MinValue,
                            MaxValue = desc.MaxValue,
                            PresetOptions = desc.PresetOptions,
                            Remarks = desc.Remarks,
                        };
                    }
                }
                return result;
            }
            set
            {
                if (value is null) return;
                foreach (var kvp in value)
                {
                    if (kvp.Value is null) continue;
                    Parameters[kvp.Key] = kvp.Value is StaticEffectArgumentField sf
                        ? sf.Value
                        : kvp.Value.GetGetter().Value;
                }
            }
        }

        public Dictionary<string, object> Parameters { get; set; }

        #endregion

        #region Anchor synchronization

        private Guid _inputId = IEffectProvider.InputAnchorGUID;
        private Guid _outputId = IEffectProvider.OutputAnchorGUID;

        #endregion

        #region Stateless factory members (IEffectProvider)

        /// <summary>
        /// The supported implementation types, resolved by <see cref="SupportedImplementTypes"/>.
        /// </summary>
        public EffectImplementType[] SupportsImplementTypes => SupportedImplementTypes();

        /// <summary>
        /// The default implementation type, derived from the first supported type.
        /// </summary>
        public EffectImplementType DefaultImplementType => SupportsImplementTypes.Length > 0 ? SupportsImplementTypes[0] : EffectImplementType.NotSpecified;

        /// <summary>
        /// The supported implementation types of this provider. Defaults to <see cref="EffectImplementType.NotSpecified"/>.
        /// </summary>
        protected virtual EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.NotSpecified];

        /// <summary>
        /// Build the specified effect implementation type (stateless factory, single effect).
        /// </summary>
        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            var effects = BuildEffects(implementType, NormalizedParameters(parameters));
            if (effects is null || effects.Length == 0)
            {
                throw new InvalidOperationException($"EffectProvider '{TypeName}' returned no effects from BuildEffects.");
            }
            return effects[0];
        }

        /// <summary>
        /// Build an effect with the default implementation type (stateless factory).
        /// </summary>
        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            return Build(DefaultImplementType, parameters);
        }

        /// <summary>
        /// Create a fresh effect instance of this provider's default implementation type.
        /// This is the "blank effect" entry point used by <see cref="EffectProvider"/>.
        /// </summary>
        public virtual IEffect CreateNewEffect() => BuildWithDefaultType(null);

        /// <summary>
        /// A factory delegate that creates a new effect instance via <see cref="CreateNewEffect"/>.
        /// Registered in <see cref="IEffectProvider"/> dictionaries that need a blank effect creator.
        /// </summary>
        public Func<IEffect> EffectProvider => CreateNewEffect;

        #endregion

        #region Helpers

        /// <summary>
        /// The reserved parameter key that stores the binding source of a field: <c>__Binding_{fieldId}</c>.
        /// </summary>
        public static string BoundParameterKey(string fieldId) => DynamicParam.BindingPrefix + fieldId;

        #endregion

        #region Build pipeline

        /// <summary>
        /// Create the final effect(s) from the normalized parameters and the resolved implementation type.
        /// This is the single place a provider implements its effect-creation logic (inlined from the legacy factories).
        /// </summary>
        /// <param name="implementType">the resolved implementation type.</param>
        /// <param name="parameters">the normalized parameters (reserved keys stripped, values typed).</param>
        protected abstract IEffect[] BuildEffects(EffectImplementType implementType, Dictionary<string, object> parameters);

        /// <summary>
        /// Stateful build: resolves the <see cref="ImplementTypeParameterKey"/> from the current
        /// <see cref="Parameters"/> and creates the effect(s). Returns the effect stack in render order.
        /// </summary>
        public IEffect[] Build()
        {
            var imp = ResolveImplementType(SupportedImplementTypes(), DefaultImplementType);
            var p = NormalizedParameters();
            return BuildEffects(imp, p);
        }

        /// <summary>
        /// Reads and removes the <see cref="ImplementTypeParameterKey"/> from <see cref="Parameters"/>.
        /// </summary>
        protected EffectImplementType ResolveImplementType(IReadOnlyList<EffectImplementType> supported, EffectImplementType defaultType = EffectImplementType.NotSpecified)
        {
            EffectImplementType requested = defaultType;
            if (Parameters.TryGetValue(ImplementTypeParameterKey, out var raw) && raw is not null)
            {
                if (raw is EffectImplementType e) requested = e;
                else if (raw is int i && Enum.IsDefined(typeof(EffectImplementType), i)) requested = (EffectImplementType)i;
                else if (raw is string s && Enum.TryParse<EffectImplementType>(s, out var parsed)) requested = parsed;
                else if (raw is JsonElement je && je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var ji) && Enum.IsDefined(typeof(EffectImplementType), ji)) requested = (EffectImplementType)ji;
            }
            Parameters.Remove(ImplementTypeParameterKey);
            if (requested != EffectImplementType.NotSpecified && !supported.Contains(requested))
            {
                return defaultType;
            }
            return requested;
        }

        /// <summary>
        /// Filters out the <c>__DraftEffectBindingView*</c> keys and strips the reserved <c>__Binding_*</c>
        /// keys and raw <see cref="Func{T}"/>/<see cref="Lazy{T}"/> dynamic values, then converts
        /// <see cref="Parameters"/> to an object dictionary typed by <see cref="BuildParametersType"/>.
        /// The stripped entries are only consumed by the dynamic-parameter machinery
        /// (<see cref="DynamicParam.BuildProviders"/>) and must never leak into effect factories.
        /// </summary>
        protected Dictionary<string, object> NormalizedParameters()
        {
            return NormalizedParameters(Parameters);
        }

        /// <summary>
        /// Same as <see cref="NormalizedParameters()"/> but against an explicit source dictionary,
        /// used by the stateless factory members.
        /// </summary>
        protected Dictionary<string, object> NormalizedParameters(Dictionary<string, object>? source)
        {
            var filtered = (source ?? Parameters)
                .Where(c => !c.Key.StartsWith("__DraftEffectBindingView"))
                .ToDictionary(c => c.Key, c => c.Value);
            return EffectArgsHelper.ConvertElementDictToObjectDict(DynamicParam.StripBindings(filtered), BuildParametersType());
        }

        /// <summary>
        /// Maps the declared argument fields to their parameter type names, keyed by field id.
        /// Used by <see cref="NormalizedParameters"/> to type the serialized parameter values.
        /// </summary>
        protected virtual Dictionary<string, string> BuildParametersType()
        {
            var result = new Dictionary<string, string>();
            foreach (var desc in DefineFields())
            {
                if (desc.FieldType.HasFlag(EffectArgumentFieldType.IPicture)) continue;
                result[desc.Id] = EffectProviderContractMapping.FieldTypeToParamType(desc.FieldType);
            }
            // Reserved key consumed by the continuous-Crop branch; registered so normalization tolerates it.
            result[IsContinuousEffectParameterKey] = EffectArgsHelper.ArgTypeBool;
            return result;
        }

        private static object ParseDefault(EffectArgumentFieldDescriptor desc)
        {
            var baseType = desc.FieldType & (EffectArgumentFieldType)0x3FF;
            return baseType switch
            {
                EffectArgumentFieldType.Boolean => EffectParamConvert.TryConvertToBool(desc.DefaultValue, out var b) ? b : false,
                EffectArgumentFieldType.UnsignedInteger => EffectParamConvert.TryConvertToUShort(desc.DefaultValue, out var us) ? us : (ushort)0,
                EffectArgumentFieldType.Integer => EffectParamConvert.TryConvertToInt(desc.DefaultValue, out var i) ? i : 0,
                EffectArgumentFieldType.Numeric => EffectParamConvert.TryConvertToFloat(desc.DefaultValue, out var f) ? f : 0f,
                _ => desc.DefaultValue,
            };
        }

        #endregion
    }
}
