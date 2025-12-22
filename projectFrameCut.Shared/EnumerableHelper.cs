using System;
using System.Collections.Generic;
using System.Text;

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

    }
}
