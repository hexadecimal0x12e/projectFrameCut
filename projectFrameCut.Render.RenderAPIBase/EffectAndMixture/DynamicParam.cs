using System;
using System.Collections.Generic;
using System.Globalization;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    /// <summary>
    /// Static helper for bindable dynamic effect parameters.
    /// A bindable parameter is stored in <see cref="IEffectProvider.Parameters"/> under a reserved key
    /// <c>__Binding_{fieldId}</c>, whose value is the source id of the binding
    /// (a value-provider bundle Guid, or a built-in source like <c>builtin://frame</c>).
    /// </summary>
    public static class DynamicParam
    {
        /// <summary>
        /// The reserved key prefix used to store a field's binding source in <see cref="IEffectProvider.Parameters"/>.
        /// The full key is <c>__Binding_{fieldId}</c> and the value is the source id.
        /// </summary>
        public const string BindingPrefix = "__Binding_";

        /// <summary>
        /// Build the per-frame value getters for an effect from its provider's parameters.
        /// Each <c>__Binding_{fieldId}</c> entry becomes a getter that resolves the current value
        /// from <see cref="ValueProviderFrameContext"/> at render time.
        /// Raw <see cref="Func{T}"/> / <see cref="Lazy{T}"/> values stored directly in the parameters
        /// are also recognized and wrapped as getters.
        /// </summary>
        /// <returns>A dictionary keyed by field id, or null when no parameter is bound.</returns>
        public static IReadOnlyDictionary<string, Func<object?>>? BuildProviders(Dictionary<string, object> parameters)
        {
            if (parameters is null) return null;
            Dictionary<string, Func<object?>>? providers = null;
            foreach (var kvp in parameters)
            {
                if (kvp.Key.StartsWith(BindingPrefix, StringComparison.Ordinal))
                {
                    var sourceId = kvp.Value as string ?? kvp.Value?.ToString();
                    if (string.IsNullOrWhiteSpace(sourceId)) continue;
                    var fieldId = kvp.Key.Substring(BindingPrefix.Length);
                    var captured = sourceId;
                    (providers ??= new Dictionary<string, Func<object?>>())[fieldId] = () => ValueProviderFrameContext.Get(captured!);
                    continue;
                }

                if (TryWrapGetter(kvp.Value, out var getter))
                {
                    (providers ??= new Dictionary<string, Func<object?>>())[kvp.Key] = getter;
                }
            }
            return providers;
        }

        /// <summary>
        /// Wrap a raw <see cref="Func{T}"/> / <see cref="Lazy{T}"/> value as an object getter.
        /// </summary>
        private static bool TryWrapGetter(object? raw, out Func<object?> getter)
        {
            getter = null!;
            if (raw is null) return false;
            var rawType = raw.GetType();
            if (rawType.IsGenericType)
            {
                var def = rawType.GetGenericTypeDefinition();
                if (def == typeof(Func<>))
                {
                    getter = () => rawType.GetMethod("Invoke")!.Invoke(raw, null);
                    return true;
                }
                if (def == typeof(Lazy<>))
                {
                    getter = () => rawType.GetProperty("Value")!.GetValue(raw);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Resolve the current value of a parameter inside an effect's <c>Render</c>.
        /// When a dynamic provider exists for <paramref name="key"/>, its value (with type conversion)
        /// is returned; otherwise the static value is used. This is a no-op when no provider is mounted,
        /// so unbinded effects behave exactly as before.
        /// </summary>
        public static T Resolve<T>(IReadOnlyDictionary<string, Func<object?>>? providers, string key, T staticValue)
        {
            if (providers is not null && providers.TryGetValue(key, out var g))
            {
                var v = g();
                if (v is null) return staticValue;
                if (v is T t) return t;
                try
                {
                    return (T)Convert.ChangeType(v, typeof(T), CultureInfo.InvariantCulture);
                }
                catch
                {
                    return staticValue;
                }
            }
            return staticValue;
        }

        /// <summary>
        /// Whether the given field of a provider is currently bound to a dynamic source.
        /// </summary>
        public static bool IsBound(Dictionary<string, object> parameters, string fieldId)
        {
            return parameters is not null && parameters.ContainsKey(BindingPrefix + fieldId);
        }

        /// <summary>
        /// Get the binding source id of a provider field, or null when not bound.
        /// </summary>
        public static string? GetBoundSource(Dictionary<string, object> parameters, string fieldId)
        {
            if (parameters is null || !parameters.TryGetValue(BindingPrefix + fieldId, out var raw)) return null;
            return raw as string ?? raw?.ToString();
        }

        /// <summary>
        /// Whether a raw value is a <see cref="Func{T}"/> / <see cref="Lazy{T}"/> dynamic value that cannot be
        /// converted by the effect factories and must be kept out of the static parameter dictionary.
        /// </summary>
        public static bool IsDynamicValue(object? raw)
        {
            if (raw is null) return false;
            var rawType = raw.GetType();
            if (rawType.IsGenericType)
            {
                var def = rawType.GetGenericTypeDefinition();
                if (def == typeof(Func<>) || def == typeof(Lazy<>)) return true;
            }
            return false;
        }

        /// <summary>
        /// Remove all reserved <c>__Binding_</c> keys and all raw <see cref="Func{T}"/> / <see cref="Lazy{T}"/>
        /// dynamic values from the parameters.
        /// Must be called before passing parameters into effect factories that reject unknown keys (e.g. Crop);
        /// the dynamic getters are mounted separately by <see cref="BuildProviders"/>.
        /// </summary>
        public static Dictionary<string, object> StripBindings(Dictionary<string, object> parameters)
        {
            if (parameters is null) return parameters;
            bool needsStrip = false;
            foreach (var kvp in parameters)
            {
                if (kvp.Key.StartsWith(BindingPrefix, StringComparison.Ordinal) || IsDynamicValue(kvp.Value))
                {
                    needsStrip = true;
                    break;
                }
            }
            if (!needsStrip) return parameters;

            var result = new Dictionary<string, object>(parameters.Count);
            foreach (var kvp in parameters)
            {
                if (kvp.Key.StartsWith(BindingPrefix, StringComparison.Ordinal) || IsDynamicValue(kvp.Value))
                    continue;
                result[kvp.Key] = kvp.Value;
            }
            return result;
        }
    }
}
