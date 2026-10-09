using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using System.Globalization;
using System.Text.Json;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

public sealed class EffectExecutionContext
{
    public object? Input { get; init; }
    public IReadOnlyDictionary<string, object?> Parameters { get; init; } = new Dictionary<string, object?>();
    public IClip? Clip { get; init; }
    public uint FrameIndex { get; init; }
    public float Progress { get; init; }
    public float ClipProgress { get; init; }
    public int TargetWidth { get; init; }
    public int TargetHeight { get; init; }
    public int RelativeWidth { get; init; }
    public int RelativeHeight { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

public static class EffectFieldTypes
{
    public static bool IsPicture(this EffectArgumentFieldType type) =>
        type.HasFlag(EffectArgumentFieldType.IPicture) || type.HasFlag(EffectArgumentFieldType.FrameAsParameterFlow);

    public static EffectArgumentFieldType BaseType(this EffectArgumentFieldType type) =>
        type.IsPicture() ? EffectArgumentFieldType.IPicture : type & (EffectArgumentFieldType)0xFFFF;

    public static bool AreCompatible(EffectArgumentFieldType source, EffectArgumentFieldType target)
    {
        source = source.BaseType();
        target = target.BaseType();
        if (source == target || source == EffectArgumentFieldType.Unknown || target == EffectArgumentFieldType.Unknown) return true;
        if (IsNumeric(source) && IsNumeric(target)) return true;
        return source == EffectArgumentFieldType.SizeAndPosition && target is EffectArgumentFieldType.Size or EffectArgumentFieldType.Position
            || target == EffectArgumentFieldType.SizeAndPosition && source is EffectArgumentFieldType.Size or EffectArgumentFieldType.Position;
    }

    private static bool IsNumeric(EffectArgumentFieldType type) => type is EffectArgumentFieldType.Integer
        or EffectArgumentFieldType.UnsignedInteger or EffectArgumentFieldType.Long or EffectArgumentFieldType.UnsignedLong or EffectArgumentFieldType.Numeric;

    public static object? DefaultValue(IEffectArgumentField field) => field.FieldType.BaseType() switch
    {
        EffectArgumentFieldType.IPicture => null,
        EffectArgumentFieldType.Integer => EffectParamConvert.TryConvertToInt(field.DefaultValue, out var i) ? i : 0,
        EffectArgumentFieldType.UnsignedInteger => uint.TryParse(field.DefaultValue, out var u) ? u : 0u,
        EffectArgumentFieldType.Long => long.TryParse(field.DefaultValue, out var l) ? l : 0L,
        EffectArgumentFieldType.UnsignedLong => ulong.TryParse(field.DefaultValue, out var ul) ? ul : 0UL,
        EffectArgumentFieldType.Numeric => EffectParamConvert.TryConvertToFloat(field.DefaultValue, out var f) ? f : 0f,
        EffectArgumentFieldType.Boolean => EffectParamConvert.TryConvertToBool(field.DefaultValue, out var b) && b,
        _ => field.DefaultValue,
    };

    public static bool Accepts(EffectArgumentFieldType type, object? value)
    {
        if (value is null) return true;
        return type.BaseType() switch
        {
            EffectArgumentFieldType.Unknown => true,
            EffectArgumentFieldType.IPicture => value is IPicture,
            EffectArgumentFieldType.Integer or EffectArgumentFieldType.UnsignedInteger or EffectArgumentFieldType.Long
                or EffectArgumentFieldType.UnsignedLong or EffectArgumentFieldType.Numeric => value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal,
            EffectArgumentFieldType.Boolean => value is bool,
            EffectArgumentFieldType.String => value is string,
            EffectArgumentFieldType.Size or EffectArgumentFieldType.Position or EffectArgumentFieldType.SizeAndPosition => value is projectFrameCut.Shared.ClipPositionTuple,
            EffectArgumentFieldType.Color => value is PictureExtensions.Pixel<byte> or PictureExtensions.Pixel<ushort>,
            _ => value is not IPicture,
        };
    }

    public static object? ConvertInput(EffectArgumentFieldType type, object? value)
    {
        if (value is JsonElement json) value = json.ValueKind switch
        {
            JsonValueKind.String => json.GetString(),
            JsonValueKind.Number when json.TryGetInt64(out var i) => i,
            JsonValueKind.Number when json.TryGetUInt64(out var u) => u,
            JsonValueKind.Number => json.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => value,
        };
        if (value is null) return null;
        var target = type.BaseType() switch
        {
            EffectArgumentFieldType.Integer => typeof(int),
            EffectArgumentFieldType.UnsignedInteger => typeof(uint),
            EffectArgumentFieldType.Long => typeof(long),
            EffectArgumentFieldType.UnsignedLong => typeof(ulong),
            EffectArgumentFieldType.Numeric => typeof(float),
            EffectArgumentFieldType.Boolean => typeof(bool),
            _ => null,
        };
        return target is null ? value : Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }
}
