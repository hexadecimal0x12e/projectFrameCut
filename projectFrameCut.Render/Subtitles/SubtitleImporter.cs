using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Shared.Text;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace projectFrameCut.Render.Subtitles;

public record SubtitleImportResult(SubtitleData Data, IReadOnlyList<string> Warnings);

public static class SubtitleImporter
{
    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".srt" or ".vtt" or ".ass" or ".ssa";

    public static SubtitleImportResult Read(string path, Encoding? encoding = null)
    {
        using var reader = new StreamReader(path, encoding ?? new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        return Parse(reader.ReadToEnd(), Path.GetExtension(path));
    }

    public static async Task<SubtitleImportResult> ReadAsync(string path, Encoding? encoding = null)
    {
        using var reader = new StreamReader(path, encoding ?? new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        return Parse(await reader.ReadToEndAsync().ConfigureAwait(false), Path.GetExtension(path));
    }

    public static SubtitleImportResult Parse(string text, string format)
    {
        var data = new SubtitleData();
        var warnings = new HashSet<string>();
        var lines = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        switch (format.TrimStart('.').ToLowerInvariant())
        {
            case "srt": ReadTimed(lines, false, data, warnings); break;
            case "vtt": ReadTimed(lines, true, data, warnings); break;
            case "ass":
            case "ssa": ReadAss(lines, data, warnings); break;
            default: throw new NotSupportedException($"Unsupported subtitle format: {format}");
        }
        if (data.Cues.Count == 0) throw new InvalidDataException("No text subtitles were found.");
        data.Validate();
        data.Cues = data.Cues.OrderBy(c => c.StartMilliseconds).ToList();
        return new(data, warnings.ToArray());
    }

    private static void ReadTimed(string[] lines, bool vtt, SubtitleData data, HashSet<string> warnings)
    {
        int i = 0;
        if (vtt)
        {
            if (lines.Length == 0 || !Regex.IsMatch(lines[0], @"^WEBVTT(?:\s|$)"))
                throw new InvalidDataException("Missing WEBVTT header (line 1).");
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i])) i++;
        }
        while (i < lines.Length)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) { i++; continue; }
            int start = i;
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i])) i++;
            var block = lines[start..i];
            if (vtt && (Regex.IsMatch(block[0], @"^NOTE(?:\s|$)") || block[0] is "STYLE" or "REGION"))
            {
                if (block[0] != "NOTE" && !block[0].StartsWith("NOTE ")) warnings.Add("WebVTT styles and regions use the clip's uniform style.");
                continue;
            }
            int timing = block[0].Contains("-->") ? 0 : 1;
            if (timing >= block.Length) throw new InvalidDataException($"Missing subtitle timing (line {start + 1}).");
            var match = Regex.Match(block[timing], @"^\s*(\d+(?::\d+){1,2}[.,]\d+)\s*-->\s*(\d+(?::\d+){1,2}[.,]\d+)(.*)$");
            if (!match.Success) throw new InvalidDataException($"Invalid subtitle timing (line {start + timing + 1}).");
            if (!string.IsNullOrWhiteSpace(match.Groups[3].Value)) warnings.Add("Subtitle positioning uses the clip's uniform style.");
            var cue = CreateCue(match.Groups[1].Value, match.Groups[2].Value, start + timing + 1);
            cue.Markdown = ReadHtml(string.Join('\n', block.Skip(timing + 1)), warnings);
            data.Cues.Add(cue);
        }
    }

    private static SubtitleCue CreateCue(string start, string end, int line)
    {
        try
        {
            return new() { StartMilliseconds = SubtitleData.ParseTime(start), EndMilliseconds = SubtitleData.ParseTime(end), SourceLine = line };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new InvalidDataException($"Invalid subtitle timing (line {line}).", ex);
        }
    }

    private static string ReadHtml(string text, HashSet<string> warnings)
    {
        var runs = new List<MarkdownTextRun>();
        MarkdownTextStyle style = 0;
        var depths = new Dictionary<MarkdownTextStyle, int>();
        int last = 0;
        foreach (Match tag in Regex.Matches(text, @"<[^>]*>"))
        {
            AddRun(runs, WebUtility.HtmlDecode(text[last..tag.Index]), style);
            string value = tag.Value.Trim('<', '>').Trim();
            bool closing = value.StartsWith('/');
            string name = value.TrimStart('/').Split([' ', '.', '\t'], 2)[0].ToLowerInvariant();
            MarkdownTextStyle flag = name switch
            {
                "b" or "strong" => MarkdownTextStyle.Bold,
                "i" or "em" => MarkdownTextStyle.Italic,
                "u" => MarkdownTextStyle.Underline,
                "s" or "del" => MarkdownTextStyle.Strikethrough,
                _ => 0
            };
            if (flag != 0)
            {
                depths[flag] = Math.Max(0, depths.GetValueOrDefault(flag) + (closing ? -1 : 1));
                style = depths[flag] > 0 ? style | flag : style & ~flag;
            }
            else if (name is "br" or "br/") AddRun(runs, "\n", style);
            else if (name is not ("v" or "lang")) warnings.Add("Some subtitle tags were converted to plain text or uniform styling.");
            last = tag.Index + tag.Length;
        }
        AddRun(runs, WebUtility.HtmlDecode(text[last..]), style);
        return ToMarkdown(runs);
    }

    private static void ReadAss(string[] lines, SubtitleData data, HashSet<string> warnings)
    {
        string section = "";
        string[] format = [], styleFormat = [];
        var styles = new Dictionary<string, MarkdownTextStyle>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line.ToLowerInvariant(); continue; }
            if (section is "[v4 styles]" or "[v4+ styles]")
            {
                if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase)) styleFormat = Columns(line[7..]);
                if (line.StartsWith("Style:", StringComparison.OrdinalIgnoreCase) && styleFormat.Length > 0)
                {
                    var fields = line[6..].Split(',', styleFormat.Length);
                    if (fields.Length != styleFormat.Length) throw new InvalidDataException($"Invalid ASS style (line {i + 1}).");
                    string name = Field(styleFormat, fields, "name");
                    MarkdownTextStyle flags = 0;
                    foreach (var (key, flag) in AssFlags)
                        if (int.TryParse(Field(styleFormat, fields, key), out int enabled) && enabled != 0) flags |= flag;
                    styles[name] = flags;
                }
                continue;
            }
            if (section != "[events]") continue;
            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase)) { format = Columns(line[7..]); continue; }
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;
            if (format.Length == 0 || format[^1] != "text") throw new InvalidDataException($"Invalid ASS event format (line {i + 1}).");
            var values = line[9..].TrimStart().Split(',', format.Length);
            if (values.Length != format.Length) throw new InvalidDataException($"Invalid ASS event (line {i + 1}).");
            var cue = CreateCue(Field(format, values, "start"), Field(format, values, "end"), i + 1);
            var initial = styles.GetValueOrDefault(Field(format, values, "style"));
            cue.Markdown = ReadAssText(Field(format, values, "text"), initial, styles, warnings);
            if (!string.IsNullOrWhiteSpace(cue.Markdown)) data.Cues.Add(cue);
        }
    }

    private static readonly (string Name, MarkdownTextStyle Flag)[] AssFlags =
    [
        ("bold", MarkdownTextStyle.Bold), ("italic", MarkdownTextStyle.Italic),
        ("underline", MarkdownTextStyle.Underline), ("strikeout", MarkdownTextStyle.Strikethrough)
    ];
    private static string[] Columns(string text) => text.Split(',').Select(c => c.Trim().ToLowerInvariant()).ToArray();
    private static string Field(string[] format, string[] values, string name)
    {
        int index = Array.IndexOf(format, name);
        return index < 0 ? "" : name == "text" ? values[index] : values[index].Trim();
    }

    private static string ReadAssText(string text, MarkdownTextStyle initial, Dictionary<string, MarkdownTextStyle> styles, HashSet<string> warnings)
    {
        var runs = new List<MarkdownTextRun>();
        var current = initial;
        bool drawing = false;
        int last = 0;
        void AddText(string value)
        {
            if (!drawing) AddRun(runs, value.Replace(@"\N", "\n").Replace(@"\n", "\n").Replace(@"\h", "\u00A0"), current);
        }
        foreach (Match tag in Regex.Matches(text, @"\{([^}]*)\}"))
        {
            AddText(text[last..tag.Index]);
            string commands = Regex.Replace(tag.Groups[1].Value, @"\\t\([^)]*\)", "");
            foreach (Match command in Regex.Matches(commands, @"\\(b|i|u|s|p)(-?\d+)(?!\d)|\\r([^\\]*)"))
            {
                if (command.Groups[3].Success)
                    current = command.Groups[3].Value.Length == 0 ? initial : styles.GetValueOrDefault(command.Groups[3].Value, initial);
                else if (command.Groups[1].Value == "p")
                    drawing = int.Parse(command.Groups[2].Value) != 0;
                else
                {
                    var flag = command.Groups[1].Value switch
                    {
                        "b" => MarkdownTextStyle.Bold, "i" => MarkdownTextStyle.Italic,
                        "u" => MarkdownTextStyle.Underline, _ => MarkdownTextStyle.Strikethrough
                    };
                    current = int.Parse(command.Groups[2].Value) != 0 ? current | flag : current & ~flag;
                }
            }
            string remaining = Regex.Replace(commands, @"\\(?:b|i|u|s|p)-?\d+|\\r[^\\]*", "");
            if (drawing || remaining.Length > 0 || commands != tag.Groups[1].Value)
                warnings.Add("ASS positioning, colors, animations, karaoke and drawing are not retained; the clip uses uniform styling.");
            last = tag.Index + tag.Length;
        }
        AddText(text[last..]);
        return ToMarkdown(runs);
    }

    private static void AddRun(List<MarkdownTextRun> runs, string text, MarkdownTextStyle style)
    {
        if (text.Length == 0) return;
        if (runs.Count > 0 && runs[^1].Style == style) runs[^1] = runs[^1] with { Text = runs[^1].Text + text };
        else runs.Add(new(text, style));
    }

    private static string ToMarkdown(List<MarkdownTextRun> runs)
    {
        var result = new StringBuilder();
        foreach (var run in runs)
        {
            bool bold = run.Style.HasFlag(MarkdownTextStyle.Bold), italic = run.Style.HasFlag(MarkdownTextStyle.Italic);
            string font = bold ? italic ? "***" : "**" : italic ? "*" : "";
            string underline = run.Style.HasFlag(MarkdownTextStyle.Underline) ? "++" : "";
            string strike = run.Style.HasFlag(MarkdownTextStyle.Strikethrough) ? "~~" : "";
            result.Append(font).Append(underline).Append(strike).Append(MarkdownText.Escape(run.Text)).Append(strike).Append(underline).Append(font);
        }
        return result.ToString();
    }
}
