using projectFrameCut.Drawing.Effect;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RPCProtocol;
using System.Collections.Concurrent;

namespace projectFrameCut.Render.Sources;

public abstract class MaskSource
{
    public abstract string TypeName { get; }
    public abstract IReadOnlyList<EffectArgumentFieldDescriptor> Fields { get; }
    /// <summary>The caller retains ownership of the backdrop, including when it is returned unchanged.</summary>
    public abstract IPicture Process(IPicture backdrop, IReadOnlyDictionary<string, object> arguments,
        float scale, CancellationToken token);

    public virtual object ReadArgument(string id, object? value)
    {
        var field = Fields.Single(f => f.Id == id);
        value = EffectParamConvert.Normalize(value ?? field.DefaultValue);
        var type = field.FieldType & (EffectArgumentFieldType)0x3FF;
        if (type == EffectArgumentFieldType.Boolean)
        {
            if (!EffectParamConvert.TryConvertToBool(value, out bool b))
                throw new ArgumentException($"Invalid mask argument {TypeName}/{id}: {value}.");
            return b;
        }
        if (type == EffectArgumentFieldType.Integer)
        {
            if (!EffectParamConvert.TryConvertToInt(value, out int n))
                throw new ArgumentException($"Invalid mask argument {TypeName}/{id}: {value}.");
            return Math.Clamp(n, int.TryParse(field.MinValue, out int min) ? min : int.MinValue,
                int.TryParse(field.MaxValue, out int max) ? max : int.MaxValue);
        }
        if (type == EffectArgumentFieldType.Numeric)
        {
            if (!EffectParamConvert.TryConvertToFloat(value, out float n) || !float.IsFinite(n))
                throw new ArgumentException($"Invalid mask argument {TypeName}/{id}: {value}.");
            return Math.Clamp(n,
                float.TryParse(field.MinValue, System.Globalization.CultureInfo.InvariantCulture, out float min) ? min : float.MinValue,
                float.TryParse(field.MaxValue, System.Globalization.CultureInfo.InvariantCulture, out float max) ? max : float.MaxValue);
        }
        return value ?? field.DefaultValue;
    }
}

public static class MaskSources
{
    private static readonly ConcurrentDictionary<string, Func<MaskSource>> factories = new(StringComparer.Ordinal);

    static MaskSources()
    {
        Register("Blur", () => new BlurMaskSource());
        Register("Mosaic", () => new MosaicMaskSource());
        Register("Passthrough", () => new PassthroughMaskSource());
    }

    public static void Register(string typeName, Func<MaskSource> create)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentNullException.ThrowIfNull(create);
        factories[typeName] = create;
        Log($"Registered mask source {typeName}.");
    }

    public static MaskSource Create(string typeName)
    {
        if (factories.TryGetValue(typeName, out var create)) return create();
        if (RemoteRpcMaskSource.IsPath(typeName)) return RemoteRpcMaskSource.Open(typeName);
        if (ProjectExternalMaskSource.IsPath(typeName)) return ProjectExternalMaskSource.Open(typeName);
        throw new NotSupportedException($"Unknown mask source '{typeName}'.");
    }

    public static IEnumerable<MaskSource> GetAvailable() => factories.OrderBy(p => p.Key).Select(p => p.Value())
        .Concat(ExternalMaskSourceRegistry.List().Sources.Select(s => new RemoteRpcMaskSource(s)))
        .Concat(ProjectExternalSourceRuntime.Current.GetMaskSources().Select(s => new ProjectExternalMaskSource(s.ClientId, s)));
}

public sealed class PassthroughMaskSource : MaskSource
{
    public override string TypeName => "Passthrough";
    public override IReadOnlyList<EffectArgumentFieldDescriptor> Fields { get; } = [];

    public override IPicture Process(IPicture backdrop, IReadOnlyDictionary<string, object> arguments,
        float scale, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return backdrop;
    }
}

public sealed class BlurMaskSource : MaskSource
{
    public override string TypeName => "Blur";
    public override IReadOnlyList<EffectArgumentFieldDescriptor> Fields { get; } =
    [
        new() { Id = "Sigma", FieldType = EffectArgumentFieldType.Numeric, DefaultValue = "4", MinValue = "0", MaxValue = "128" }
    ];

    public override IPicture Process(IPicture backdrop, IReadOnlyDictionary<string, object> arguments,
        float scale, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return BlurEffect.Process(backdrop, (float)ReadArgument("Sigma", arguments.GetValueOrDefault("Sigma")) * scale);
    }
}

public sealed class MosaicMaskSource : MaskSource
{
    public override string TypeName => "Mosaic";
    public override IReadOnlyList<EffectArgumentFieldDescriptor> Fields { get; } =
    [
        new() { Id = "BlockSize", FieldType = EffectArgumentFieldType.Integer, DefaultValue = "16", MinValue = "1", MaxValue = "512" }
    ];

    public override IPicture Process(IPicture backdrop, IReadOnlyDictionary<string, object> arguments,
        float scale, CancellationToken token)
    {
        int size = Math.Max(1, (int)Math.Round((int)ReadArgument("BlockSize", arguments.GetValueOrDefault("BlockSize")) * scale));
        var result = DynamicEffectGraph.CopyPicture(backdrop);
        try
        {
            for (int y = 0; y < backdrop.Height; y += size)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < backdrop.Width; x += size)
                {
                    int right = Math.Min(x + size, backdrop.Width), bottom = Math.Min(y + size, backdrop.Height);
                    if (result is IPicture<byte> p8) FillBlock(p8, x, y, right, bottom);
                    else if (result is IPicture<ushort> p16) FillBlock(p16, x, y, right, bottom);
                    else throw new NotSupportedException($"Unsupported mask picture {result.GetType().Name}.");
                }
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static void FillBlock<T>(IPicture<T> p, int x, int y, int right, int bottom) where T : System.Numerics.INumber<T>
    {
        double r = 0, g = 0, b = 0, a = 0, brightness = 0;
        int count = (right - x) * (bottom - y);
        var hdr = p as IHDRPicture<ushort>;
        for (int yy = y; yy < bottom; yy++)
        {
            for (int xx = x; xx < right; xx++)
            {
                int i = yy * p.Width + xx;
                r += double.CreateChecked(p.r[i]);
                g += double.CreateChecked(p.g[i]);
                b += double.CreateChecked(p.b[i]);
                a += p.HasAlphaChannel && p.a is not null ? p.a[i] : 1;
                if (hdr is not null) brightness += hdr.Brightness[i];
            }
        }
        for (int yy = y; yy < bottom; yy++)
        {
            for (int xx = x; xx < right; xx++)
            {
                int i = yy * p.Width + xx;
                p.r[i] = T.CreateChecked(Math.Round(r / count));
                p.g[i] = T.CreateChecked(Math.Round(g / count));
                p.b[i] = T.CreateChecked(Math.Round(b / count));
                if (p.HasAlphaChannel && p.a is not null) p.a[i] = (float)(a / count);
                if (hdr is not null) hdr.Brightness[i] = (float)(brightness / count);
            }
        }
    }
}
