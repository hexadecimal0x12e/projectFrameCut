using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Render.ClipsAndTracks.Text;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.RenderAPIBase.Text;
using projectFrameCut.Shared;
using System.Text.Json;
using System.Text.Json.Serialization;
using SubtitleDocument = projectFrameCut.Render.RenderAPIBase.ClipAndTrack.SubtitleData;

namespace projectFrameCut.Render.ClipsAndTracks;

public class SubtitleClip : TextClip, IClip
{
    public override ClipMode ClipType => ClipMode.SubtitleClip;
    public SubtitleData SubtitleData { get; set; } = new();

    private readonly object subtitleLock = new();
    private SubtitleData? preparedData;
    private SubtitleCue[] cues = [];
    private RichTextEntry[] entries = [];
    private SubtitleData? argumentData;
    private bool useArgumentStyle;

    public const string ArgumentPrefix = "SubtitleStyle.";
    private static readonly IReadOnlyDictionary<string, ClipArgumentFieldDescriptor> subtitleFields = CreateArgumentFields();

    [JsonIgnore]
    public override IReadOnlyDictionary<string, ClipArgumentFieldDescriptor> ArgumentFields => subtitleFields;

    private SubtitleData GetArgumentData() => argumentData ??= ExtraData?.TryGetValue(SubtitleDocument.MetadataKey, out var raw) == true
        ? SubtitleDocument.Restore(raw) : SubtitleData;

    private static IReadOnlyDictionary<string, ClipArgumentFieldDescriptor> CreateArgumentFields()
    {
        var fields = new Dictionary<string, ClipArgumentFieldDescriptor>(ClipArgumentHandler.DefaultFields);
        void Add(EffectArgumentFieldDescriptor d, Func<SubtitleData, object> read)
        {
            string id = ArgumentPrefix + d.Id;
            fields.Add(id, new()
            {
                Id = id, FieldType = d.FieldType, DefaultValue = d.DefaultValue,
                MinValue = d.MinValue, MaxValue = d.MaxValue, PresetOptions = d.PresetOptions, Remarks = d.Remarks,
                ReadValue = c => read(((SubtitleClip)c).GetArgumentData()),
            });
        }
        void Number(string id, float value, Func<SubtitleData, object> read, float? min = null, float? max = null) =>
            Add(TextStyleFieldHelper.NumericField(id, id, "", value, min, max), read);
        void Channel(string id, int value, Func<SubtitleData, object> read) =>
            Add(TextStyleFieldHelper.IntegerField(id, id, "16-bit color channel", value, 0, ushort.MaxValue), read);

        Add(TextStyleFieldHelper.StringField("FontName", "Font", "", "HarmonyOS Sans SC"), d => d.Style.FontName);
        Add(TextStyleFieldHelper.StringField("FontStyle", "Font style", "", new SubtitleData().Style.FontStyle), d => d.Style.FontStyle);
        Number("FontSize", 48, d => d.Style.FontSize, 1);
        Channel("FillR", ushort.MaxValue, d => d.Style.FillR);
        Channel("FillG", ushort.MaxValue, d => d.Style.FillG);
        Channel("FillB", ushort.MaxValue, d => d.Style.FillB);
        Number("FillA", 1, d => (float)d.Style.FillA, 0, 1);
        Channel("StrokeR", 0, d => d.Style.StrokeR);
        Channel("StrokeG", 0, d => d.Style.StrokeG);
        Channel("StrokeB", 0, d => d.Style.StrokeB);
        Number("StrokeA", 1, d => (float)d.Style.StrokeA, 0, 1);
        Number("StrokeThickness", 2, d => d.Style.StrokeThickness, 0);
        Number("X", 0, d => d.Style.X);
        Number("Y", 0, d => d.Style.Y);
        Number("Rotation", 0, d => d.Style.Rotation);
        Number("CharacterSpacing", 0, d => d.Style.CharacterSpacing);
        Number("WordSpacing", 0, d => d.Style.WordSpacing);
        Number("LineSpacing", new SubtitleData().Style.LineSpacing, d => d.Style.LineSpacing, 0);
        Number("BottomMargin", 60, d => d.BottomMargin, 0);
        Number("WrappingWidth", 0, d => d.Style.GetWrappingWidth() ?? 0f, 0);
        Add(TextStyleFieldHelper.EnumField("Alignment", "Alignment", "", TextAlignment.Center.ToString(),
            Enum.GetNames<TextAlignment>()), d => d.Style.Alignment.ToString());
        return fields;
    }

    public override IClip HandleArguments(IReadOnlyDictionary<string, object> arguments)
    {
        var copy = (SubtitleClip)base.HandleArguments(arguments.Where(p => !p.Key.StartsWith(ArgumentPrefix, StringComparison.Ordinal)).ToDictionary());
        var values = arguments.Where(p => p.Key.StartsWith(ArgumentPrefix, StringComparison.Ordinal)).ToArray();
        if (values.Length == 0) return copy;

        // Frame overrides share cues but keep their style and layout data independent of the source.
        var source = GetArgumentData();
        var data = new SubtitleData
        {
            Cues = source.Cues,
            Style = source.Style with { ExtraData = new(source.Style.ExtraData) },
            BottomMargin = source.BottomMargin,
        };
        foreach (var (id, raw) in values)
        {
            if (!subtitleFields.TryGetValue(id, out var descriptor)) throw new ArgumentException($"Unknown subtitle argument '{id}'.");
            if (!TextStyleFieldHelper.TryNormalizeValue(descriptor, raw, out var value, out var error))
                throw new ArgumentException($"Invalid subtitle argument '{id}': {error}");
            switch (id[ArgumentPrefix.Length..])
            {
                case "FontName": data.Style.FontName = (string)value; break;
                case "FontStyle": data.Style.FontStyle = (string)value; break;
                case "FontSize": data.Style.FontSize = Convert.ToSingle(value); break;
                case "FillR": data.Style.FillR = Convert.ToUInt16(value); break;
                case "FillG": data.Style.FillG = Convert.ToUInt16(value); break;
                case "FillB": data.Style.FillB = Convert.ToUInt16(value); break;
                case "FillA": data.Style.FillA = (Half)Convert.ToSingle(value); break;
                case "StrokeR": data.Style.StrokeR = Convert.ToUInt16(value); break;
                case "StrokeG": data.Style.StrokeG = Convert.ToUInt16(value); break;
                case "StrokeB": data.Style.StrokeB = Convert.ToUInt16(value); break;
                case "StrokeA": data.Style.StrokeA = (Half)Convert.ToSingle(value); break;
                case "StrokeThickness": data.Style.StrokeThickness = Convert.ToSingle(value); break;
                case "X": data.Style.X = Convert.ToSingle(value); break;
                case "Y": data.Style.Y = Convert.ToSingle(value); break;
                case "Rotation": data.Style.Rotation = Convert.ToSingle(value); break;
                case "CharacterSpacing": data.Style.CharacterSpacing = Convert.ToSingle(value); break;
                case "WordSpacing": data.Style.WordSpacing = Convert.ToSingle(value); break;
                case "LineSpacing": data.Style.LineSpacing = Convert.ToSingle(value); break;
                case "BottomMargin": data.BottomMargin = Convert.ToSingle(value); break;
                case "WrappingWidth":
                    data.Style.SetWrappingWidth(Convert.ToSingle(value) > 0 ? Convert.ToSingle(value) : null);
                    break;
                case "Alignment": data.Style.Alignment = Enum.Parse<TextAlignment>((string)value); break;
            }
        }
        copy.SubtitleData = data;
        copy.argumentData = data;
        lock (subtitleLock)
        {
            if (ReferenceEquals(preparedData, source))
            {
                copy.cues = cues;
                copy.entries = entries;
                copy.preparedData = data;
            }
            else
            {
                copy.preparedData = null;
                copy.Prepare();
            }
        }
        copy.useArgumentStyle = true;
        return copy;
    }

    uint? IClip.TryGetRelativeFrameIndex(uint targetFrame, uint? onFail)
    {
        // 全项目模式下，字幕时间直接对应项目时间。
        if (ExtendToWholeDraft) return targetFrame;

        long offset = (long)targetFrame - StartFrame;
        uint duration = ((IClip)this).GetEffectiveDuration();
        if (offset == duration) return null;
        if (offset < 0 || offset >= duration) return onFail;

        ulong frame = (ulong)RelativeStartFrame
            + ClipTiming.SourceOffset((uint)offset, Duration, SpeedVarianceProviderInstance, duration);
        return frame <= uint.MaxValue ? (uint)frame : onFail;
    }

    public override void ReInit(IPicture.PicturePixelMode targetPPB)
    {
        lock (subtitleLock)
        {
            if (ExtraData?.TryGetValue(SubtitleDocument.MetadataKey, out var raw) == true)
                SubtitleData = SubtitleDocument.Restore(raw);
            argumentData = SubtitleData;
            useArgumentStyle = false;
            preparedData = null;
            Prepare();
            Log($"SubtitleClip {Name} ({Id}): prepared {cues.Length} cues, ExtendToWholeDraft={ExtendToWholeDraft}.");
        }
        base.ReInit(targetPPB);
    }

    private void Prepare()
    {
        if (ReferenceEquals(preparedData, SubtitleData)) return;
        SubtitleData.Validate();
        cues = SubtitleData.Cues.OrderBy(c => c.StartMilliseconds).ToArray();
        entries = cues.Select(SubtitleData.BuildEntry).ToArray();
        preparedData = SubtitleData;
    }

    protected override IReadOnlyList<TextEntry> GetRawEntries(uint frameIndex)
    {
        lock (subtitleLock)
        {
            Prepare();
            double fps = ResolveFrameRate();
            decimal milliseconds = frameIndex * 1000m / (decimal)fps;
            int left = 0, right = cues.Length - 1, index = -1;
            while (left <= right)
            {
                int middle = left + (right - left) / 2;
                if (cues[middle].StartMilliseconds <= milliseconds)
                {
                    index = middle;
                    left = middle + 1;
                }
                else right = middle - 1;
            }
            if (index < 0 || milliseconds >= cues[index].EndMilliseconds) return [];
            float width = TargetWidth > 0 ? TargetWidth : 1920;
            var entry = useArgumentStyle ? SubtitleData.BuildEntry(cues[index])
                : entries[index] with { ExtraData = new(entries[index].ExtraData) };
            entry.X += entry.Alignment switch
            {
                TextAlignment.Center => width / 2,
                TextAlignment.Right => width * .95f,
                _ => width * .05f
            };
            entry.Y -= SubtitleData.BottomMargin;
            entry.SetLayoutMode(TextClipLayoutMode.FixedWidth);
            entry.SetVerticalAlignment(ClipVerticalAlignment.Bottom);
            if (entry.GetWrappingWidth() is not > 0) entry.SetWrappingWidth(width * .9f);
            return [entry];
        }
    }

    private double ResolveFrameRate()
    {
        if (ExtraData?.TryGetValue(ClipDraftDTO.ProjectFrameRateMetaKey, out var raw) == true)
        {
            double fps = raw is JsonElement json ? json.GetDouble() : Convert.ToDouble(raw);
            if (double.IsFinite(fps) && fps > 0) return fps;
        }
        if (FrameTime > 0 && float.IsFinite(FrameTime)) return Math.Round(1d / FrameTime, 6);
        throw new InvalidDataException("Subtitle clip requires a valid project frame rate.");
    }
}
