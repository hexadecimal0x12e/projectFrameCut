using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Diagnostics;

namespace projectFrameCut.Shared
{
    public static class EnumerableHelper
    {
        /// <summary>
        /// Get whether the array have any value.
        /// </summary>
        public static bool ArrayAny<T>(this T[]? input)
        {
            if (input is null) return false;
            return input.Length > 0;
        }
        public static bool ListAny<T>(this List<T>? input)
        {
            if (input is null) return false;
            return input.Count > 0;
        }

        [DebuggerNonUserCode()]
        public static TKey? ReverseLookup<TKey, TValue>(this IDictionary<TKey, TValue> dict, TValue value, TKey? DefaultValue = default)
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

    }
}
