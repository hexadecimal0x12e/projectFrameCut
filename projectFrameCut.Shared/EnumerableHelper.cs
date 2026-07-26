using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace projectFrameCut.Shared
{
    public static class EnumerableHelper
    {
        /// <summary>
        /// Get whether the array have any value.
        /// </summary>
        [DebuggerNonUserCode()]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ArrayAny<T>([NotNullWhen(true)] this T[]? input)
        {
            if (input is null) return false;
            return input.Length > 0;
        }

        /// <summary>
        /// Get whether the list have any value.
        /// </summary>
        [DebuggerNonUserCode()]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ListAny<T>([NotNullWhen(true)] this List<T>? input)
        {
            if (input is null) return false;
            return input.Count > 0;
        }

        /// <summary>
        /// Reverse looking up a dictionary.
        /// </summary>
        /// <remarks>
        /// This method will return the first matching key found, whenever there are multiple keys with the same value.
        /// </remarks>
        [DebuggerNonUserCode()]
        public static TKey ReverseLookup<TKey, TValue>(this IDictionary<TKey, TValue> dict, TValue value, TKey DefaultValue) where TKey : notnull
        {
            foreach (var kv in dict)
            {
                if (EqualityComparer<TValue>.Default.Equals(kv.Value, value))
                {
                    return kv.Key;
                }
            }
            return DefaultValue;

        }

        /// <summary>
        /// Reverse looking up a dictionary.
        /// </summary>
        /// <remarks>
        /// This method will return the first matching key found, whenever there are multiple keys with the same value.
        /// </remarks>
        [DebuggerNonUserCode()]
        public static TKey ReverseLookup<TKey, TValue>(this IDictionary<TKey, TValue> dict, TValue value) where TKey : notnull
        {
            foreach (var kv in dict)
            {
                if (EqualityComparer<TValue>.Default.Equals(kv.Value, value))
                {
                    return kv.Key;
                }
            }
            throw new KeyNotFoundException($"No item with value {value} found.");
        }

        /// <summary>
        /// Remove the values in <paramref name="input"/> where equals to any element in <paramref name="ToRemove"/> .
        /// </summary>
        public static IEnumerable<T> RemoveRange<T>(this IEnumerable<T> input, IEnumerable<T> ToRemove)
        {
            return input.Where(c => !ToRemove.Contains(c));
        }
        /// <summary>
        /// Remove the values in <paramref name="input"/> where equals to any element in <paramref name="ToRemove"/> .
        /// </summary>
        public static IEnumerable<T> RemoveRange<T>(this IEnumerable<T> input, IEnumerable<T> ToRemove, IEqualityComparer<T> comparer)
        {
            return input.Where(c => !ToRemove.Contains(c, comparer));
        }

        public static IEnumerable<T> PickRandom<T>(this IEnumerable<T> input, int count, Random? rand = null)
        {
            rand ??= new Random();
            return input.OrderBy(x => rand.Next()).Take(count);
        }

        /// <summary>
        /// Compute a dictionary based on the conditions and values provided in the input dictionary.
        /// </summary>
        /// <typeparam name="TKey">The type of the keys in the dictionary.</typeparam>
        /// <typeparam name="TValue">The type of the values in the dictionary.</typeparam>
        /// <param name="conditions">Source dictionary containing the conditions and values.</param>
        /// <returns>A new dictionary with the filtered results.</returns>
        [DebuggerNonUserCode()]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Dictionary<TKey, TValue> ComputeCondition<TKey, TValue>(this IDictionary<TKey, (Func<bool>, TValue)> conditions) where TKey : notnull
        {
            var result = new Dictionary<TKey, TValue>();
            foreach (var (key, (condition, value)) in conditions)
            {
                if (condition())
                {
                    result[key] = value;
                }
            }
            return result;
        }
    }
}
