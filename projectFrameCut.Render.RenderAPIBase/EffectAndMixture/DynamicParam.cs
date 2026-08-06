using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    /// <summary>
    /// Static helper for bindable dynamic effect parameters.
    /// A bindable parameter is stored in the provider's field storage under a reserved key
    /// <c>__Binding_{fieldId}</c>, whose value is the source id of the binding
    /// (a value-provider bundle Guid, or a built-in source like <c>builtin://frame</c>).
    /// </summary>
    public static class DynamicParam
    {
        /// <summary>
        /// The reserved key prefix used to store a field's binding source in the provider's field storage.
        /// The full key is <c>__Binding_{fieldId}</c> and the value is the source id.
        /// </summary>
        public const string BindingPrefix = "__Binding_";

        /// <summary>
        /// Resolve the current value of a parameter from <see cref="IEffect.Parameters"/>.
        /// When <paramref name="param"/> is a <see cref="Func{T}"/> (dynamic binding), it is invoked and the result
        /// is converted to <typeparamref name="T"/>; when it is a static value, it is converted directly.
        /// When <paramref name="param"/> is null, <paramref name="staticValue"/> is returned.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static T Resolve<T>(object? param, T staticValue)
        {
            if (param is null) return staticValue;
            object? v;
            switch (param)
            {
                case Func<object?> func:
                    try
                    {
                        v = func();
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(ex, $"resolve dynamic parameter value");
                        throw;
                    }
                    break;
                case Lazy<object?> lazyObj:
                    v = lazyObj.Value;
                    break;
                default:
                    v = param;
                    break;
            }
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

        /// <summary>
        /// Convert a parameter value to <see cref="int"/> using <see cref="EffectParamConvert"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static int ToInt32(object? raw)
        {
            return EffectParamConvert.TryConvertToInt(raw, out var v) ? v : 0;
        }

        /// <summary>
        /// Convert a parameter value to <see cref="float"/> using <see cref="EffectParamConvert"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static float ToFloat(object? raw)
        {
            return EffectParamConvert.TryConvertToFloat(raw, out var v) ? v : 0f;
        }

        /// <summary>
        /// Convert a parameter value to <see cref="ushort"/> using <see cref="EffectParamConvert"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static ushort ToUShort(object? raw)
        {
            return EffectParamConvert.TryConvertToUShort(raw, out var v) ? v : (ushort)0;
        }

        /// <summary>
        /// Convert a parameter value to <see cref="bool"/> using <see cref="EffectParamConvert"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static bool ToBool(object? raw)
        {
            return EffectParamConvert.TryConvertToBool(raw, out var v) && v;
        }

        /// <summary>
        /// Convert a parameter value to <see cref="string"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static string ToStringValue(object? raw)
        {
            return raw?.ToString() ?? string.Empty;
        }

        /// <summary>
        /// Whether a raw value is a <see cref="Func{T}"/> / <see cref="Lazy{T}"/> dynamic value that cannot be
        /// converted by the effect factories and must be kept out of the static parameter dictionary.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static bool IsDynamicValue(object? raw)
        {
            if (raw is null) return false;
            if (raw is Func<object?> || raw is Func<object>) return true;
            var rawType = raw.GetType();
            if (rawType.IsGenericType)
            {
                var def = rawType.GetGenericTypeDefinition();
                if (def == typeof(Func<>) || def == typeof(Lazy<>)) return true;
            }
            return false;
        }
    }
}
