using projectFrameCut.Render.Contracts;
using projectFrameCut.Shared;
using System.Text.Json;

namespace projectFrameCut.Render.PluginIsolation;

public static class IsolationValueConverter
{
    public static IsolationValue FromObject(object? value)
    {
        if (value is JsonElement json) value = FromJson(json);
        return value switch
        {
            null => new(),
            string x => new() { Kind = IsolationValueKind.String, StringValue = x },
            bool x => new() { Kind = IsolationValueKind.Boolean, BooleanValue = x },
            byte x => new() { Kind = IsolationValueKind.UInt32, UnsignedValue = x },
            ushort x => new() { Kind = IsolationValueKind.UInt32, UnsignedValue = x },
            uint x => new() { Kind = IsolationValueKind.UInt32, UnsignedValue = x },
            ulong x => new() { Kind = IsolationValueKind.UInt64, UnsignedValue = x },
            sbyte x => new() { Kind = IsolationValueKind.Int32, SignedValue = x },
            short x => new() { Kind = IsolationValueKind.Int32, SignedValue = x },
            int x => new() { Kind = IsolationValueKind.Int32, SignedValue = x },
            long x => new() { Kind = IsolationValueKind.Int64, SignedValue = x },
            float x => new() { Kind = IsolationValueKind.Single, NumberValue = x },
            double x => new() { Kind = IsolationValueKind.Double, NumberValue = x },
            Enum x => new() { Kind = IsolationValueKind.Int32, SignedValue = Convert.ToInt64(x), StringValue = x.GetType().AssemblyQualifiedName ?? string.Empty },
            ClipPositionTuple x => new() { Kind = IsolationValueKind.Position, X = x.TargetX, Y = x.TargetY, Width = x.TargetWidth, Height = x.TargetHeight, BooleanValue = x.IsDelta },
            projectFrameCut.Drawing.Base.PictureExtensions.Pixel<byte> x => new() { Kind = IsolationValueKind.Color, StringValue = "byte", X = x.r, Y = x.g, Width = x.b, Alpha = x.a, HasAlpha = true },
            projectFrameCut.Drawing.Base.PictureExtensions.Pixel<ushort> x => new() { Kind = IsolationValueKind.Color, StringValue = "ushort", X = x.r, Y = x.g, Width = x.b, Alpha = x.a, HasAlpha = true },
            _ => throw new NotSupportedException($"Isolation value type '{value.GetType().FullName}' is not supported."),
        };
    }

    public static object? ToObject(IsolationValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Kind switch
        {
            IsolationValueKind.Null => null,
            IsolationValueKind.String => value.StringValue,
            IsolationValueKind.Boolean => value.BooleanValue,
            IsolationValueKind.Int32 => RestoreEnum(value) ?? checked((int)value.SignedValue),
            IsolationValueKind.UInt32 => checked((uint)value.UnsignedValue),
            IsolationValueKind.Int64 => value.SignedValue,
            IsolationValueKind.UInt64 => value.UnsignedValue,
            IsolationValueKind.Single => (float)value.NumberValue,
            IsolationValueKind.Double => value.NumberValue,
            IsolationValueKind.Position => new ClipPositionTuple(checked((int)value.X), checked((int)value.Y), checked((int)value.Width), checked((int)value.Height), value.BooleanValue),
            IsolationValueKind.Color when value.StringValue == "byte" => new projectFrameCut.Drawing.Base.PictureExtensions.Pixel<byte> { r = checked((byte)value.X), g = checked((byte)value.Y), b = checked((byte)value.Width), a = value.Alpha },
            IsolationValueKind.Color => new projectFrameCut.Drawing.Base.PictureExtensions.Pixel<ushort> { r = checked((ushort)value.X), g = checked((ushort)value.Y), b = checked((ushort)value.Width), a = value.Alpha },
            _ => throw new NotSupportedException($"Isolation value kind '{value.Kind}' is not supported."),
        };
    }

    private static object? RestoreEnum(IsolationValue value)
    {
        if (string.IsNullOrWhiteSpace(value.StringValue)) return null;
        var type = Type.GetType(value.StringValue, throwOnError: false);
        return type?.IsEnum == true ? Enum.ToObject(type, value.SignedValue) : null;
    }

    private static object? FromJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        _ => throw new NotSupportedException($"JSON isolation value kind '{value.ValueKind}' is not supported."),
    };
}
