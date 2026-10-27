using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Shared.Text;
using System.Globalization;
using System.Text.Json;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.RenderAPIBase.ClipAndTrack;

public record SubtitleCue
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public long StartMilliseconds { get; set; }
    public long EndMilliseconds { get; set; }
    public string Markdown { get; set; } = "";
    public int SourceLine { get; init; }
}

public class SubtitleData
{
    public const string MetadataKey = "SubtitleData";
    public List<SubtitleCue> Cues { get; set; } = [];
    public TextEntry Style { get; set; } = new()
    {
        Text = "", FontName = "HarmonyOS Sans SC", FontSize = 48,
        FillR = ushort.MaxValue, FillG = ushort.MaxValue, FillB = ushort.MaxValue,
        StrokeA = Half.One, StrokeThickness = 2, Alignment = TextAlignment.Center
    };
    public float BottomMargin { get; set; } = 60;

    public static SubtitleData Restore(object value) => value switch
    {
        SubtitleData data => data,
        JsonElement json => json.Deserialize<SubtitleData>() ?? throw new InvalidDataException("Subtitle data is empty."),
        string json => JsonSerializer.Deserialize<SubtitleData>(json) ?? throw new InvalidDataException("Subtitle data is empty."),
        _ => throw new InvalidDataException("Invalid subtitle data.")
    };

    public SubtitleData Clone() => JsonSerializer.Deserialize<SubtitleData>(JsonSerializer.Serialize(this))!;

    public void Validate()
    {
        if (Cues is null || Style is null) throw new InvalidDataException("Subtitle data is incomplete.");
        if (!float.IsFinite(Style.FontSize) || Style.FontSize <= 0 ||
            !float.IsFinite(Style.StrokeThickness) || Style.StrokeThickness < 0 ||
            !float.IsFinite(Style.LineSpacing) || Style.LineSpacing < 0 ||
            !float.IsFinite(Style.X) || !float.IsFinite(Style.Y) ||
            !float.IsFinite(BottomMargin) || BottomMargin < 0)
            throw new InvalidDataException("Invalid subtitle style.");
        if (Style.GetWrappingWidth() is float width && (!float.IsFinite(width) || width <= 0))
            throw new InvalidDataException("Invalid subtitle wrapping width.");
        SubtitleCue? previous = null;
        foreach (var cue in Cues.OrderBy(c => c.StartMilliseconds).ThenBy(c => c.EndMilliseconds))
        {
            if (cue.StartMilliseconds < 0 || cue.EndMilliseconds <= cue.StartMilliseconds)
                throw new InvalidDataException($"Invalid subtitle time: {Describe(cue)}.");
            if (previous is not null && cue.StartMilliseconds < previous.EndMilliseconds)
                throw new InvalidDataException($"Overlapping subtitles: {Describe(previous)} / {Describe(cue)}.");
            previous = cue;
        }
    }

    private static string Describe(SubtitleCue cue) => $"{FormatTime(cue.StartMilliseconds)}–{FormatTime(cue.EndMilliseconds)} ({(cue.SourceLine > 0 ? $"line {cue.SourceLine}, " : "")}{cue.Markdown})";

    public static uint FrameAt(long milliseconds, uint fps)
    {
        if (milliseconds < 0 || fps == 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        return checked((uint)decimal.Ceiling((decimal)milliseconds * fps / 1000));
    }

    public static string FormatTime(long milliseconds) =>
        $"{milliseconds / 3600000:00}:{milliseconds / 60000 % 60:00}:{milliseconds / 1000 % 60:00}.{milliseconds % 1000:000}";

    public static long ParseTime(string text)
    {
        text = text.Trim().Replace(',', '.');
        var parts = text.Split(':');
        if (parts.Length is < 1 or > 3) throw new FormatException($"Invalid subtitle time: {text}");
        decimal seconds = decimal.Parse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        if (seconds < 0 || (parts.Length > 1 && seconds >= 60)) throw new FormatException($"Invalid subtitle time: {text}");
        if (parts.Length > 1)
        {
            int minutes = int.Parse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture);
            if (parts.Length == 3 && minutes >= 60) throw new FormatException($"Invalid subtitle time: {text}");
            seconds += minutes * 60m;
        }
        if (parts.Length == 3) seconds += int.Parse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture) * 3600m;
        return checked((long)decimal.Round(seconds * 1000, 0, MidpointRounding.AwayFromZero));
    }

    public RichTextEntry BuildEntry(SubtitleCue cue)
    {
        var entry = new RichTextEntry
        {
            Text = "", FontName = Style.FontName, FontStyle = Style.FontStyle,
            FontSize = Style.FontSize, FallbackFonts = Style.FallbackFonts,
            FillR = Style.FillR, FillG = Style.FillG, FillB = Style.FillB, FillA = Style.FillA,
            StrokeR = Style.StrokeR, StrokeG = Style.StrokeG, StrokeB = Style.StrokeB,
            StrokeA = Style.StrokeA, StrokeThickness = Style.StrokeThickness,
            X = Style.X, Y = Style.Y, Rotation = Style.Rotation, LayerIndex = Style.LayerIndex,
            Alignment = Style.Alignment, Decoration = Style.Decoration,
            CharacterSpacing = Style.CharacterSpacing, WordSpacing = Style.WordSpacing,
            LineSpacing = Style.LineSpacing, FlowDirection = Style.FlowDirection,
            VariationAxes = new(Style.VariationAxes), ExtraData = new(Style.ExtraData)
        };
        foreach (var run in MarkdownText.Parse(cue.Markdown ?? ""))
        {
            var flags = run.Style;
            string fontStyle = Style.FontStyle;
            bool bold = flags.HasFlag(MarkdownTextStyle.Bold) || fontStyle.Contains("Bold", StringComparison.OrdinalIgnoreCase);
            bool italic = flags.HasFlag(MarkdownTextStyle.Italic) || fontStyle.Contains("Italic", StringComparison.OrdinalIgnoreCase);
            entry.StyledRanges.Add(new()
            {
                Start = entry.Text.Length, Length = run.Text.Length,
                Style = new()
                {
                    FontName = flags.HasFlag(MarkdownTextStyle.Code) ? "Source Code Pro" : null,
                    FontStyle = bold ? italic ? "BoldItalic" : "Bold" : italic ? "Italic" : fontStyle,
                    Decoration = Style.Decoration |
                        (flags.HasFlag(MarkdownTextStyle.Underline) ? TextDecoration.Underline : 0) |
                        (flags.HasFlag(MarkdownTextStyle.Strikethrough) ? TextDecoration.Strikethrough : 0)
                }
            });
            entry.Text += run.Text;
        }
        return entry;
    }
}
