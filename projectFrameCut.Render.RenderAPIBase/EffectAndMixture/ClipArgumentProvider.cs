using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

public sealed class ClipArgumentProvider : IEffectProvider
{
    public IReadOnlyList<Stage> SupportedStages => [Stage.ClipArguments];
    public Stage Stage
    {
        get => Stage.ClipArguments;
        set { if (value != Stage.ClipArguments) throw new ArgumentOutOfRangeException(nameof(value)); }
    }
    public bool IsDeterministic => true;

    public const string ProviderTypeName = "ClipArguments";
    private readonly Dictionary<string, object> staticValues = new();
    private readonly Dictionary<string, IValueProviderEffect> runtimeFields = new();
    private IReadOnlyDictionary<string, ClipArgumentFieldDescriptor>? descriptors;
    private Func<string, object>? readValue;

    public string TypeName => ProviderTypeName;
    public string FromPlugin => "projectFrameCut.Render.Plugins.InternalPluginBase";
    public EffectType TypeOfEffect => EffectType.NotSpecified;
    public EffectTarget Target => EffectTarget.InternalUse | EffectTarget.IsNotVisibleInEffectEditor | EffectTarget.IsNotVisibleInNewEffectSelector;
    public bool Enabled { get; set; } = true;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Clip 参数";
    public Dictionary<string, string> AnchorsBindingState { get; set; } = new();
    public Dictionary<string, object> MetaData { get; set; } = new();
    public IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> InFields => descriptors?.ToDictionary(p => p.Key, p => (EffectArgumentFieldDescriptor)p.Value) ?? new();
    public EffectArgumentFieldDescriptor OutField => new();
    public IReadOnlyDictionary<string, object> StaticValues => staticValues;

    public void Attach(IClip clip)
    {
        var fields = clip.ArgumentFields;
        Attach(fields, key => fields[key].ReadValue(clip));
    }

    public void Attach(IReadOnlyDictionary<string, ClipArgumentFieldDescriptor> fields, Func<string, object> read)
    {
        if (fields.Any(p => p.Key is EffectProviderAnchorExtensions.InputKey or EffectProviderAnchorExtensions.OutputKey
            || p.Value.FieldType.HasFlag(EffectArgumentFieldType.IPicture)))
            throw new ArgumentException("Clip arguments cannot declare picture anchors.", nameof(fields));
        descriptors = new Dictionary<string, ClipArgumentFieldDescriptor>(fields)
        {
            [ClipArgumentHandler.ShouldRenderKey] = ClipArgumentHandler.DefaultFields[ClipArgumentHandler.ShouldRenderKey],
        };
        readValue = key => key == ClipArgumentHandler.ShouldRenderKey ? true : read(key);
        ClearRuntimeFields();
    }

    public void RestoreStaticValues(IReadOnlyDictionary<string, object>? values)
    {
        staticValues.Clear();
        if (values is not null)
            foreach (var (key, value) in values)
                staticValues[key] = EffectParamConvert.Normalize(value) ?? new object();
    }

    public void ClearRuntimeFields()
    {
        foreach (var field in runtimeFields.Values.OfType<IDisposable>().Distinct()) field.Dispose();
        runtimeFields.Clear();
    }

    public Dictionary<string, IEffectArgumentField> Fields
    {
        get
        {
            var keys = descriptors?.Keys ?? staticValues.Keys.Union(this.EnumerateFieldBindings().Select(p => p.Key));
            return keys.Union([ClipArgumentHandler.ShouldRenderKey]).ToDictionary(key => key, CreateField);
        }
        set
        {
            foreach (var (key, f) in value)
            {
                if (f is IValueProviderEffect inline)
                {
                    if (runtimeFields.TryGetValue(key, out var old) && !ReferenceEquals(old, inline) && old is IDisposable disposable)
                        disposable.Dispose();
                    runtimeFields[key] = inline;
                    continue;
                }
                if (f is DynamicEffectParamField or ClipArgumentField { UsesClipValue: true }) continue;
                if (f is StaticEffectArgumentField sf)
                {
                    if (runtimeFields.Remove(key, out var previous) && previous is IDisposable disposableValue) disposableValue.Dispose();
                    staticValues[key] = sf.Value;
                    this.ClearFieldBinding(key);
                }
            }
        }
    }

    private IEffectArgumentField CreateField(string key)
    {
        var desc = key == ClipArgumentHandler.ShouldRenderKey
            ? ClipArgumentHandler.DefaultFields[key] : descriptors?.GetValueOrDefault(key);
        object value = staticValues.TryGetValue(key, out var v) ? v : readValue?.Invoke(key) ?? desc?.DefaultValue ?? "";
        if (this.TryGetFieldBinding(key, out var source))
            return new DynamicEffectParamField
            {
                Id = key,
                FieldType = desc?.FieldType ?? EffectArgumentFieldType.Unknown,
                BoundProviderId = source,
                StaticFallbackValue = value,
                DefaultValue = desc?.DefaultValue ?? "",
                MinValue = desc?.MinValue ?? "",
                MaxValue = desc?.MaxValue ?? "",
                PresetOptions = desc?.PresetOptions,
                Remarks = desc?.Remarks,
            };
        return new ClipArgumentField(value, desc?.FieldType ?? EffectArgumentFieldType.Unknown)
        {
            Id = key,
            UsesClipValue = !staticValues.ContainsKey(key),
            DefaultValue = desc?.DefaultValue ?? "",
            MinValue = desc?.MinValue ?? "",
            MaxValue = desc?.MaxValue ?? "",
            PresetOptions = desc?.PresetOptions,
            Remarks = desc?.Remarks,
        };
    }

    public bool EvaluateShouldRender()
    {
        if (!Enabled || !this.TryGetFieldBinding(ClipArgumentHandler.ShouldRenderKey, out var source)
            || source == IEffectProvider.NoConnectionGUID.ToString()) return true;
        object? value = runtimeFields.TryGetValue(ClipArgumentHandler.ShouldRenderKey, out var inline)
            ? inline.GetGetter()() : CreateField(ClipArgumentHandler.ShouldRenderKey).GetGetter()();
        if (EffectParamConvert.TryConvertToBool(value, out bool b)) return b;
        return !EffectParamConvert.TryConvertToBool(staticValues.GetValueOrDefault(ClipArgumentHandler.ShouldRenderKey) ?? true, out b) || b;
    }

    public Dictionary<string, object> Evaluate(IClip clip)
    {
        var values = new Dictionary<string, object>();
        var fields = clip.ArgumentFields;
        foreach (var (key, field) in Fields)
        {
            if (key == ClipArgumentHandler.ShouldRenderKey) continue;
            if (!fields.TryGetValue(key, out var descriptor)) throw new ArgumentException($"Unknown clip argument '{key}'.");
            object fallback = staticValues.GetValueOrDefault(key) ?? descriptor.ReadValue(clip);
            object? value = runtimeFields.TryGetValue(key, out var inline) ? inline.GetGetter()() : field.GetGetter()();
            values[key] = (field.FieldType & (EffectArgumentFieldType)0xFFFF) switch
            {
                EffectArgumentFieldType.Integer => EffectParamConvert.TryConvertToInt(value, out int i) ? i : fallback,
                EffectArgumentFieldType.Numeric => EffectParamConvert.TryConvertToFloat(value, out float f) ? f : fallback,
                EffectArgumentFieldType.Boolean => EffectParamConvert.TryConvertToBool(value, out bool b) ? b : fallback,
                _ => value ?? fallback,
            };
        }
        return values;
    }

    public IEffect[] Build() => [];
    public IEffect RestoreInstance(EffectImplementType implementType, Dictionary<string, object>? parameters = null) =>
        throw new NotSupportedException("Clip arguments do not build rendering effects.");
}
