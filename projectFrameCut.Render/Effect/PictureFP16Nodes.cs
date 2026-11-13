using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

public abstract class CreateFP16PictureProvider : EffectProviderBase
{
    public override IReadOnlyList<EffectStage> SupportedStages => EffectStages.Values;
    public override bool IsDeterministic => true;
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override EffectType TypeOfEffect => EffectType.NormalEffect;
    public override EffectTarget Target => EffectTarget.Pipeline | EffectTarget.ValueProvider;

    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => [];
    protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() => new Dictionary<string, EffectArgumentFieldDescriptor>
    {
        ["Width"] = Field("Width", EffectArgumentFieldType.Integer | EffectArgumentFieldType.Mandatory, "1920"),
        ["Height"] = Field("Height", EffectArgumentFieldType.Integer | EffectArgumentFieldType.Mandatory, "1080"),
    };

    protected override EffectArgumentFieldDescriptor DefineOutField() => Field(OutputAnchorKey, EffectArgumentFieldType.IPicture, "");

    internal static (int Width, int Height) GetSize(EffectExecutionContext context)
    {
        int width = Convert.ToInt32(context.Parameters["Width"]);
        int height = Convert.ToInt32(context.Parameters["Height"]);
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Picture dimensions must be positive.");
        _ = checked(width * height);
        return (width, height);
    }
}

public sealed class CreatePictureFP16Provider : CreateFP16PictureProvider
{
    public override string TypeName => "CreatePictureFP16";
    public CreatePictureFP16Provider() => Name = "Create PictureFP16";
    public override object? ResolveDataFormat(string fieldId, bool isInput, EffectFieldTypeContext context) =>
        isInput ? base.ResolveDataFormat(fieldId, true, context) : PictureDataFormat.CpuFP16;
}

public sealed class CreateHDRPictureFP16Provider : CreateFP16PictureProvider
{
    public override string TypeName => "CreateHDRPictureFP16";
    public CreateHDRPictureFP16Provider() => Name = "Create HDRPictureFP16";
    public override object? ResolveDataFormat(string fieldId, bool isInput, EffectFieldTypeContext context) =>
        isInput ? base.ResolveDataFormat(fieldId, true, context) : PictureDataFormat.CpuHdrFP16;
}

public sealed class CreateFP16PictureEffect(string typeName, bool hdr) : IEffect
{
    public bool IsDeterministic => true;
    public string TypeName => typeName;
    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public EffectType TypeOfEffect => EffectType.NormalEffect;
    public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = typeName;
    public bool Enabled { get; set; } = true;
    public int Index { get; set; }
    public int RelativeWidth { get; set; }
    public int RelativeHeight { get; set; }
    public bool IsReorderable => false;
    public string? BindedEffectProvidingSystemID { get; set; }
    public Dictionary<string, object> Parameters { get; set; } = new();
    public IEffect WithParameters(Dictionary<string, object> parameters) => new CreateFP16PictureEffect(typeName, hdr) { Parameters = parameters };

    public object Compute(EffectExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var (width, height) = CreateFP16PictureProvider.GetSize(context);
        return hdr ? new HDRPictureFP16(width, height) : new PictureFP16(width, height);
    }
}

public sealed class ReadPictureFP16PixelProvider : EffectProviderBase, IMultipleOutputEffectProvider
{
    private static readonly string[] Channels = ["R", "G", "B", "A", "Brightness"];
    public override IReadOnlyList<EffectStage> SupportedStages => EffectStages.Values;
    public override bool IsDeterministic => true;
    public ReadPictureFP16PixelProvider() => Name = "Read PictureFP16 Pixel";
    public override string TypeName => "ReadPictureFP16Pixel";
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override EffectType TypeOfEffect => EffectType.NormalEffect;
    public override EffectTarget Target => EffectTarget.Pipeline | EffectTarget.ValueProvider;
    public IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> OutFields => Channels.ToDictionary(
        channel => channel, channel => Field(channel, EffectArgumentFieldType.Numeric, channel is "A" or "Brightness" ? "1" : "0"));
    EffectArgumentFieldDescriptor IEffectProvider.OutField => throw new NotSupportedException("Select a channel output.");
    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => [];
    protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() => new Dictionary<string, EffectArgumentFieldDescriptor>
    {
        ["Picture"] = Field("Picture", EffectArgumentFieldType.IPicture | EffectArgumentFieldType.Mandatory, ""),
        ["X"] = Field("X", EffectArgumentFieldType.Integer | EffectArgumentFieldType.Mandatory, "0"),
        ["Y"] = Field("Y", EffectArgumentFieldType.Integer | EffectArgumentFieldType.Mandatory, "0"),
    };
}

public sealed class ReadPictureFP16PixelEffect : IMultipleOutputEffect
{
    public bool IsDeterministic => true;
    public string TypeName => "ReadPictureFP16Pixel";
    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public EffectType TypeOfEffect => EffectType.NormalEffect;
    public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Read PictureFP16 Pixel";
    public bool Enabled { get; set; } = true;
    public int Index { get; set; }
    public int RelativeWidth { get; set; }
    public int RelativeHeight { get; set; }
    public bool IsReorderable => false;
    public string? BindedEffectProvidingSystemID { get; set; }
    public Dictionary<string, object> Parameters { get; set; } = new();
    public IEffect WithParameters(Dictionary<string, object> parameters) => new ReadPictureFP16PixelEffect { Parameters = parameters };

    public IReadOnlyDictionary<string, object?> ComputeOutputs(EffectExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (context.Parameters.GetValueOrDefault("Picture") is not IPicture<Half> picture)
            throw new ArgumentException("Picture must be a PictureFP16 or HDRPictureFP16.");
        int index = PixelIndex(picture, context.Parameters);
        var alpha = picture.HasAlphaChannel && picture.a is { } a ? a[index] : Half.One;
        var brightness = picture is IHDRPicture<Half> hdr ? hdr.Brightness[index]
            : PictureFP16.Unit((float)picture.r[index] * .2627f + (float)picture.g[index] * .678f + (float)picture.b[index] * .0593f);
        return new Dictionary<string, object?>
        {
            ["R"] = (float)picture.r[index], ["G"] = (float)picture.g[index], ["B"] = (float)picture.b[index],
            ["A"] = (float)alpha, ["Brightness"] = (float)brightness,
        };
    }

    internal static int PixelIndex(IPicture picture, IReadOnlyDictionary<string, object> parameters)
    {
        int x = Convert.ToInt32(parameters["X"]), y = Convert.ToInt32(parameters["Y"]);
        if ((uint)x >= (uint)picture.Width || (uint)y >= (uint)picture.Height)
            throw new ArgumentOutOfRangeException(nameof(x), $"Pixel coordinate ({x}, {y}) is outside {picture.Width}x{picture.Height}.");
        return checked(y * picture.Width + x);
    }
}

public sealed class WritePictureFP16PixelProvider : EffectProviderBase
{
    public override IReadOnlyList<EffectStage> SupportedStages => EffectStages.Values;
    public override bool IsDeterministic => true;
    public WritePictureFP16PixelProvider() => Name = "Write PictureFP16 Pixel";
    public override string TypeName => "WritePictureFP16Pixel";
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override EffectType TypeOfEffect => EffectType.NormalEffect;
    public override EffectTarget Target => EffectTarget.Pipeline | EffectTarget.ValueProvider;
    public override object? ResolveDataFormat(string fieldId, bool isInput, EffectFieldTypeContext context)
    {
        if (isInput) return base.ResolveDataFormat(fieldId, true, context);
        return base.ResolveDataFormat("Picture", true, context);
    }
    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => [];
    protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() => new Dictionary<string, EffectArgumentFieldDescriptor>
    {
        ["Picture"] = Field("Picture", EffectArgumentFieldType.IPicture | EffectArgumentFieldType.Mandatory, ""),
        ["X"] = Field("X", EffectArgumentFieldType.Integer | EffectArgumentFieldType.Mandatory, "0"),
        ["Y"] = Field("Y", EffectArgumentFieldType.Integer | EffectArgumentFieldType.Mandatory, "0"),
        ["R"] = Field("R", EffectArgumentFieldType.Numeric | EffectArgumentFieldType.Mandatory, "0"),
        ["G"] = Field("G", EffectArgumentFieldType.Numeric | EffectArgumentFieldType.Mandatory, "0"),
        ["B"] = Field("B", EffectArgumentFieldType.Numeric | EffectArgumentFieldType.Mandatory, "0"),
        ["A"] = Field("A", EffectArgumentFieldType.Numeric | EffectArgumentFieldType.Mandatory, "1"),
        ["Brightness"] = Field("Brightness", EffectArgumentFieldType.Numeric | EffectArgumentFieldType.Mandatory, "1"),
    };
}

public sealed class WritePictureFP16PixelEffect : IEffect
{
    public bool IsDeterministic => true;
    public string TypeName => "WritePictureFP16Pixel";
    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public EffectType TypeOfEffect => EffectType.NormalEffect;
    public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Write PictureFP16 Pixel";
    public bool Enabled { get; set; } = true;
    public int Index { get; set; }
    public int RelativeWidth { get; set; }
    public int RelativeHeight { get; set; }
    public bool IsReorderable => false;
    public string? BindedEffectProvidingSystemID { get; set; }
    public Dictionary<string, object> Parameters { get; set; } = new();
    public IEffect WithParameters(Dictionary<string, object> parameters) => new WritePictureFP16PixelEffect { Parameters = parameters };

    public object Compute(EffectExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (context.Parameters.GetValueOrDefault("Picture") is not IPicture<Half> source)
            throw new ArgumentException("Picture must be a PictureFP16 or HDRPictureFP16.");
        int index = ReadPictureFP16PixelEffect.PixelIndex(source, context.Parameters);
        PictureFP16 output = source is IHDRPicture<Half> ? new HDRPictureFP16(source) : new PictureFP16(source);
        output.SetAlpha(true);
        using (var edit = output.Edit())
        {
            edit.Red[index] = PictureFP16.Unit(Convert.ToSingle(context.Parameters["R"]));
            edit.Green[index] = PictureFP16.Unit(Convert.ToSingle(context.Parameters["G"]));
            edit.Blue[index] = PictureFP16.Unit(Convert.ToSingle(context.Parameters["B"]));
            edit.Alpha[index] = PictureFP16.Unit(Convert.ToSingle(context.Parameters["A"]));
            if (output is HDRPictureFP16) edit.Brightness[index] = PictureFP16.Unit(Convert.ToSingle(context.Parameters["Brightness"]));
        }
        return output;
    }
}
