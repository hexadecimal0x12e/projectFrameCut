using System.Text;

namespace projectFrameCut.Shared.Text;

[Flags]
public enum MarkdownTextStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Strikethrough = 8,
    Code = 16
}

public record MarkdownTextRun(string Text, MarkdownTextStyle Style);

public static class MarkdownText
{
    private record Delimiter(int Start, int Length, char Marker, MarkdownTextStyle Style, bool Opening);

    public static List<MarkdownTextRun> Parse(string text)
    {
        var runs = new List<MarkdownTextRun>();
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var delimiters = FindDelimiters(text);
        var active = new List<MarkdownTextStyle>();
        MarkdownTextStyle style = 0;
        var buffer = new StringBuilder();
        void Flush()
        {
            if (buffer.Length == 0) return;
            string value = buffer.ToString();
            if (runs.Count > 0 && runs[^1].Style == style)
                runs[^1] = runs[^1] with { Text = runs[^1].Text + value };
            else
                runs.Add(new(value, style));
            buffer.Clear();
        }

        for (int i = 0; i < text.Length;)
        {
            if (text[i] == '\\' && i + 1 < text.Length && "\\*_~+`".Contains(text[i + 1]))
            {
                buffer.Append(text[i + 1]);
                i += 2;
                continue;
            }
            if (text[i] == '`')
            {
                int count = 1;
                while (i + count < text.Length && text[i + count] == '`') count++;
                string marker = new('`', count);
                int end = FindClosing(text, marker, i + count);
                if (end >= 0)
                {
                    Flush();
                    runs.Add(new(text[(i + count)..end], style | MarkdownTextStyle.Code));
                    i = end + count;
                    continue;
                }
                buffer.Append(marker);
                i += count;
                continue;
            }
            if (delimiters.TryGetValue(i, out var delimiter))
            {
                Flush();
                if (delimiter.Opening) active.Add(delimiter.Style);
                else active.RemoveAt(active.Count - 1);
                style = active.Aggregate(MarkdownTextStyle.None, (s, flag) => s | flag);
                i += delimiter.Length;
            }
            else buffer.Append(text[i++]);
        }
        Flush();
        return runs;
    }

    private static Dictionary<int, Delimiter> FindDelimiters(string text)
    {
        var result = new Dictionary<int, Delimiter>();
        var stack = new List<Delimiter>();
        for (int i = 0; i < text.Length;)
        {
            char marker = text[i];
            if (marker == '\\') { i += Math.Min(2, text.Length - i); continue; }
            int count = 1;
            while (i + count < text.Length && text[i + count] == marker) count++;
            if (marker == '`')
            {
                int end = FindClosing(text, new('`', count), i + count);
                i = end < 0 ? i + count : end + count;
                continue;
            }
            if (marker is not ('*' or '_' or '~' or '+') ||
                marker is '~' or '+' && count < 2 ||
                marker == '_' && i > 0 && i + count < text.Length && IsAsciiWord(text[i - 1]) && IsAsciiWord(text[i + count]))
            {
                i += count;
                continue;
            }
            bool canOpen = i + count < text.Length && !char.IsWhiteSpace(text[i + count]);
            bool canClose = i > 0 && (!char.IsWhiteSpace(text[i - 1]) || !canOpen ||
                stack.Count > 0 && stack[^1].Marker == marker && (stack[^1].Length == count || count > 3));
            int remaining = count;
            while (remaining > 0)
            {
                if (canClose && stack.Count > 0 && stack[^1].Marker == marker && stack[^1].Length <= remaining)
                {
                    var opener = stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    result[opener.Start] = opener;
                    result[i] = opener with { Start = i, Opening = false };
                    i += opener.Length;
                    remaining -= opener.Length;
                    continue;
                }
                int length = marker is '*' or '_' ? Math.Min(3, remaining) : 2;
                if (!canOpen || remaining < length) { i += remaining; break; }
                var flag = marker switch
                {
                    '~' => MarkdownTextStyle.Strikethrough,
                    '+' => MarkdownTextStyle.Underline,
                    _ => length == 3 ? MarkdownTextStyle.Bold | MarkdownTextStyle.Italic : length == 2 ? MarkdownTextStyle.Bold : MarkdownTextStyle.Italic
                };
                stack.Add(new(i, length, marker, flag, true));
                i += length;
                remaining -= length;
                canClose = false;
            }
        }
        return result;
    }

    private static int FindClosing(string text, string marker, int start)
    {
        for (int i = text.IndexOf(marker, start, StringComparison.Ordinal); i >= 0; i = text.IndexOf(marker, i + marker.Length, StringComparison.Ordinal))
        {
            if ((i == 0 || text[i - 1] != '`') && (i + marker.Length == text.Length || text[i + marker.Length] != '`')) return i;
        }
        return -1;
    }

    private static bool IsAsciiWord(char c) => c < 128 && (char.IsLetterOrDigit(c) || c == '_');

    public static string Escape(string text)
    {
        var result = new StringBuilder();
        foreach (char c in text)
        {
            if ("\\*_~+`".Contains(c)) result.Append('\\');
            result.Append(c);
        }
        return result.ToString();
    }
}
