using System;
using System.Collections.Generic;
using System.Linq;

namespace projectFrameCut.Shared
{
    public static class RangeParser
    {
        public static IReadOnlyList<Range> ParseToRanges(string? input, bool inclusiveEnd = true)
        {
            var ranges = new List<Range>();
            if (string.IsNullOrWhiteSpace(input))
                return ranges;

            string s = Normalize(input);
            foreach (var token in s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.IsNullOrWhiteSpace(token))
                    continue;

                if (token.Contains('-'))
                {
                    var parts = token.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length < 2)
                        continue;

                    if (!int.TryParse(parts.First(), out var start) || !int.TryParse(parts.Last(), out var end))
                        continue;

                    if (start > end)
                        (start, end) = (end, start);

                    if (start < 0)
                        throw new ArgumentOutOfRangeException(nameof(input), "Start of the range must bigger than 0.");

                    long endExclusiveLong = inclusiveEnd ? end + 1L : end;
                    if (endExclusiveLong > int.MaxValue)
                        throw new ArgumentOutOfRangeException(nameof(input), "End of range is too big.");

                    int endExclusive = (int)endExclusiveLong;

                    ranges.Add(new Range(new Index(start, fromEnd: false), new Index(endExclusive, fromEnd: false)));
                }
                else
                {
                    if (!int.TryParse(token, out var value))
                        continue;

                    if (value < 0)
                        throw new ArgumentOutOfRangeException(nameof(input), "Range number must positive");

                    long endExclusiveLong = value + 1L;
                    if (endExclusiveLong > int.MaxValue)
                        throw new ArgumentOutOfRangeException(nameof(input), "Position is too big.");

                    ranges.Add(new Range(new Index(value, fromEnd: false), new Index((int)endExclusiveLong, fromEnd: false)));
                }
            }

            return ranges;
        }

        public static uint[] ParseToSequence(string? input)
        {
            var set = new SortedSet<uint>();
            if (string.IsNullOrWhiteSpace(input))
                return Array.Empty<uint>();

            string s = Normalize(input);
            foreach (var token in s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.IsNullOrWhiteSpace(token))
                    continue;

                if (token.Contains('-'))
                {
                    var parts = token.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length < 2)
                        continue;

                    if (!long.TryParse(parts.First(), out var startL) || !long.TryParse(parts.Last(), out var endL))
                        continue;

                    if (startL > endL)
                        (startL, endL) = (endL, startL);

                    if (startL < 0)
                        startL = 0;

                    for (long v = startL; v < endL; v++)
                    {
                        if (v > uint.MaxValue) break;
                        set.Add((uint)v);
                    }
                }
                else
                {
                    if (!long.TryParse(token, out var v) || v < 0 || v > uint.MaxValue)
                        continue;

                    set.Add((uint)v);
                }
            }

            return set.ToArray();
        }

        private static string Normalize(string input)
        {
            return input
                .Replace('，', ',')
                .Replace('；', ';')
                .Replace('–', '-')  
                .Replace('—', '-')  
                .Replace('－', '-')   
                .Replace('~', '-')   
                .Trim();
        }
    }
}