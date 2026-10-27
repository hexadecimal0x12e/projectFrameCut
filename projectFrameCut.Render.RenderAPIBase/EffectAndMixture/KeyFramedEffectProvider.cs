using System.Globalization;
using System.Text.Json;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

public interface IKeyFramedEffectProvider : IEffectProvider
{
    /// <summary>Detached editing snapshots, ordered by progress. Commit changes with UpdateStep.</summary>
    IReadOnlyList<IKeyFrameStep> Steps { get; }
    IKeyFrameStep CreateStep(double progress);
    void AddStep(IKeyFrameStep step);
    void UpdateStep(int index, IKeyFrameStep step);
    void RemoveStep(int index);
}

public interface IKeyFrameStep
{
    /// <summary>The value of the static Progress field, between zero and one.</summary>
    double Progress { get; set; }
    Dictionary<string, IEffectArgumentField> Fields { get; set; }
}

public sealed class KeyFrameStep : IKeyFrameStep
{
    public Dictionary<string, IEffectArgumentField> Fields { get; set; } = new();

    public double Progress
    {
        get => Convert.ToDouble(Fields[nameof(Progress)].GetGetter()(), CultureInfo.InvariantCulture);
        set => this.SetValues(new Dictionary<string, object> { [nameof(Progress)] = value });
    }

    public KeyFrameStep(double progress)
    {
        Fields[nameof(Progress)] = Field(nameof(Progress), EffectArgumentFieldType.Numeric, progress, "0", "1");
        Progress = progress;
    }

    public static StaticEffectArgumentField Field(string id, EffectArgumentFieldType type, object value,
        string? min = null, string? max = null, string[]? options = null) => new(value, type
            | EffectArgumentFieldType.CannotBeDynamic
            | (min is null ? 0 : EffectArgumentFieldType.HasMinValue)
            | (max is null ? 0 : EffectArgumentFieldType.HasMaxValue))
        {
            Id = id, DefaultValue = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
            MinValue = min ?? "", MaxValue = max ?? "", PresetOptions = options,
        };
}

public static class KeyFrameStepExtensions
{
    public static void SetValues(this IKeyFrameStep step, IReadOnlyDictionary<string, object> values)
    {
        var fields = new Dictionary<string, IEffectArgumentField>(step.Fields);
        foreach (var (id, raw) in values)
        {
            if (!fields.TryGetValue(id, out var field)) throw new ArgumentException($"Unknown keyframe field '{id}'.");
            if (field.IsDynamic || field.IsDynamicAtRenderTime || field.FieldType.HasFlag(EffectArgumentFieldType.CanNotBeStatic)
                || raw is Delegate or IEffectArgumentField)
                throw new ArgumentException($"Keyframe field '{id}' cannot be bound.");
            object value = EffectParamConvert.Normalize(raw) ?? throw new ArgumentException($"Keyframe field '{id}' cannot be null.");
            value = field.FieldType.BaseType() switch
            {
                EffectArgumentFieldType.Integer when EffectParamConvert.TryConvertToInt(value, out var i) => i,
                EffectArgumentFieldType.UnsignedInteger => Convert.ToUInt32(value, CultureInfo.InvariantCulture),
                EffectArgumentFieldType.Numeric when field.GetGetter()() is double => Convert.ToDouble(value, CultureInfo.InvariantCulture),
                EffectArgumentFieldType.Numeric when EffectParamConvert.TryConvertToFloat(value, out var n) => n,
                EffectArgumentFieldType.Long => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                EffectArgumentFieldType.UnsignedLong => Convert.ToUInt64(value, CultureInfo.InvariantCulture),
                EffectArgumentFieldType.Boolean when EffectParamConvert.TryConvertToBool(value, out var b) => b,
                EffectArgumentFieldType.String when value is string => value,
                EffectArgumentFieldType.AudioSample => throw new ArgumentException($"Keyframe field '{id}' cannot contain audio samples."),
                EffectArgumentFieldType.Any or EffectArgumentFieldType.Color or EffectArgumentFieldType.Size
                    or EffectArgumentFieldType.Position or EffectArgumentFieldType.SizeAndPosition => value,
                _ => throw new ArgumentException($"Invalid value for keyframe field '{id}'."),
            };
            if (value is double d && !double.IsFinite(d) || value is float f && !float.IsFinite(f))
                throw new ArgumentException($"Keyframe field '{id}' must be finite.");
            if (field.FieldType.BaseType() is EffectArgumentFieldType.Integer or EffectArgumentFieldType.UnsignedInteger
                or EffectArgumentFieldType.Long or EffectArgumentFieldType.UnsignedLong or EffectArgumentFieldType.Numeric)
            {
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (field.FieldType.HasFlag(EffectArgumentFieldType.HasMinValue)
                    && double.TryParse(field.MinValue, CultureInfo.InvariantCulture, out double min) && number < min
                    || field.FieldType.HasFlag(EffectArgumentFieldType.HasMaxValue)
                    && double.TryParse(field.MaxValue, CultureInfo.InvariantCulture, out double max) && number > max)
                    throw new ArgumentOutOfRangeException(id);
            }
            if (field.PresetOptions is { Length: > 0 } && !field.PresetOptions.Contains(value.ToString()))
                throw new ArgumentException($"Invalid option for keyframe field '{id}'.");
            fields[id] = new StaticEffectArgumentField(value, field.FieldType | EffectArgumentFieldType.CannotBeDynamic)
            {
                Id = id, DataFormat = field.DataFormat, DefaultValue = field.DefaultValue, MinValue = field.MinValue, MaxValue = field.MaxValue,
                PresetOptions = field.PresetOptions, Remarks = field.Remarks,
            };
        }
        step.Fields = fields;
    }

    public static void ValidateStatic(this IKeyFrameStep step)
    {
        if (!double.IsFinite(step.Progress) || step.Progress < 0 || step.Progress > 1)
            throw new ArgumentOutOfRangeException(nameof(step.Progress));
        foreach (var field in step.Fields.Values)
            if (field.IsDynamic || field.IsDynamicAtRenderTime || !field.FieldType.HasFlag(EffectArgumentFieldType.CannotBeDynamic)
                || field.FieldType.HasFlag(EffectArgumentFieldType.CanNotBeStatic))
                throw new ArgumentException($"Keyframe field '{field.Id}' must be static.");
    }
}

/// <summary>Maps editable step snapshots to an existing JSON list parameter.</summary>
public abstract class KeyFramedEffectProviderBase<T> : EffectProviderBase, IKeyFramedEffectProvider
{
    protected abstract string StepListFieldId { get; }
    protected abstract IKeyFrameStep ReadStep(T item);
    protected abstract T WriteStep(IKeyFrameStep step);
    public abstract IKeyFrameStep CreateStep(double progress);

    public IReadOnlyList<IKeyFrameStep> Steps
    {
        get
        {
            object raw = Fields[StepListFieldId].GetGetter()();
            IEnumerable<T> list;
            if (raw is IEnumerable<T> items) list = items;
            else
            {
                string? json = raw is JsonElement element
                    ? element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText()
                    : raw?.ToString();
                list = JsonSerializer.Deserialize<List<T>>(string.IsNullOrWhiteSpace(json) ? "[]" : json) ?? [];
            }
            return list.Select(ReadStep).OrderBy(s => s.Progress).ToArray();
        }
    }

    public void AddStep(IKeyFrameStep step)
    {
        var list = Steps.ToList();
        list.Add(ValidateStep(step));
        SaveSteps(list);
    }

    public void UpdateStep(int index, IKeyFrameStep step)
    {
        var list = Steps.ToList();
        list[index] = ValidateStep(step);
        SaveSteps(list);
    }

    public void RemoveStep(int index)
    {
        var list = Steps.ToList();
        list.RemoveAt(index);
        SaveSteps(list);
    }

    private IKeyFrameStep ValidateStep(IKeyFrameStep step)
    {
        step.ValidateStatic();
        var result = CreateStep(step.Progress);
        result.SetValues(step.Fields.ToDictionary(p => p.Key, p => p.Value.GetGetter()()));
        return result;
    }

    private void SaveSteps(IEnumerable<IKeyFrameStep> steps) =>
        SetField(StepListFieldId, JsonSerializer.Serialize(steps.OrderBy(s => s.Progress).Select(WriteStep).ToArray()));
}
