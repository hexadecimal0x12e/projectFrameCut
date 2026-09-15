using ProtoBuf;

namespace projectFrameCut.Render.Contracts;

[ProtoContract]
public sealed class RegisterExternalVideoSourcesRequest
{
    [ProtoMember(1)] public List<ExternalVideoSourceDescriptor> Sources { get; set; } = [];
}

[ProtoContract]
public sealed class ExternalVideoSourceCatalog
{
    [ProtoMember(1)] public List<ExternalVideoSourceDescriptor> Sources { get; set; } = [];
}

[ProtoContract]
public sealed class ExternalVideoSourceDescriptor
{
    [ProtoMember(1)] public Guid ClientId { get; set; }
    [ProtoMember(2)] public string SourceId { get; set; } = string.Empty;
    [ProtoMember(3)] public string Name { get; set; } = string.Empty;
    [ProtoMember(4)] public string DecoderName { get; set; } = string.Empty;
    [ProtoMember(5)] public List<string> PreferredExtensions { get; set; } = [];
    [ProtoMember(6)] public long TotalFrames { get; set; }
    [ProtoMember(7)] public double Fps { get; set; }
    [ProtoMember(8)] public int Width { get; set; }
    [ProtoMember(9)] public int Height { get; set; }
    [ProtoMember(10)] public int ResultBitsPerPixel { get; set; }
    [ProtoMember(11)] public bool HasKnownResultBitsPerPixel { get; set; }
    [ProtoMember(12)] public bool SupportsHdr { get; set; }
    [ProtoMember(13)] public bool SupportsAlpha { get; set; }
    [ProtoMember(14)] public string ClientName { get; set; } = string.Empty;
    [ProtoMember(15)] public Dictionary<string, string> Metadata { get; set; } = [];
}

[ProtoContract]
public sealed class ExternalVideoSourceReference
{
    [ProtoMember(1)] public Guid ClientId { get; set; }
    [ProtoMember(2)] public string SourceId { get; set; } = string.Empty;
    [ProtoMember(3)] public string DecoderName { get; set; } = string.Empty;
    [ProtoMember(4)] public Dictionary<string, string> Metadata { get; set; } = [];
    [ProtoMember(5)] public ExternalVideoSourceDescriptor Descriptor { get; set; } = new();

    public string Encode()
    {
        var value = Convert.ToBase64String(RenderRpcSerializer.Serialize(this));
        return value.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static ExternalVideoSourceReference Decode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 262144) throw new InvalidDataException("External video source reference is too large.");
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        return RenderRpcSerializer.Deserialize<ExternalVideoSourceReference>(Convert.FromBase64String(base64));
    }
}

[ProtoContract]
public sealed class ExternalVideoSourceCreateRequest
{
    [ProtoMember(1)] public ExternalVideoSourceReference Source { get; set; } = new();
}

[ProtoContract]
public sealed class ExternalVideoSourceInstance
{
    [ProtoMember(1)] public Guid InstanceId { get; set; }
    [ProtoMember(2)] public ExternalVideoSourceDescriptor Descriptor { get; set; } = new();
}

[ProtoContract]
public sealed class ExternalVideoSourceStateRequest
{
    [ProtoMember(1)] public Guid InstanceId { get; set; }
    [ProtoMember(2)] public uint Index { get; set; }
    [ProtoMember(3)] public bool EnableLock { get; set; }
    [ProtoMember(4)] public bool StrictMode { get; set; }
}

[ProtoContract]
public sealed class ExternalVideoSourceReadRequest
{
    [ProtoMember(1)] public ExternalVideoSourceStateRequest State { get; set; } = new();
    [ProtoMember(2)] public uint TargetFrame { get; set; }
    [ProtoMember(3)] public int SourceX { get; set; }
    [ProtoMember(4)] public int SourceY { get; set; }
    [ProtoMember(5)] public int SourceWidth { get; set; }
    [ProtoMember(6)] public int SourceHeight { get; set; }
    [ProtoMember(7)] public int TargetWidth { get; set; }
    [ProtoMember(8)] public int TargetHeight { get; set; }
    [ProtoMember(9)] public bool UseRegion { get; set; }
    [ProtoMember(10)] public bool RequestHdr { get; set; }
    [ProtoMember(11)] public bool HasAlpha { get; set; }
}

[ProtoContract]
public sealed class ExternalVideoFrame
{
    [ProtoMember(1)] public int Width { get; set; }
    [ProtoMember(2)] public int Height { get; set; }
    [ProtoMember(3)] public int BitsPerChannel { get; set; }
    [ProtoMember(4)] public byte[] Red { get; set; } = [];
    [ProtoMember(5)] public byte[] Green { get; set; } = [];
    [ProtoMember(6)] public byte[] Blue { get; set; } = [];
    [ProtoMember(7)] public byte[] Alpha { get; set; } = [];
    [ProtoMember(8)] public byte[] Brightness { get; set; } = [];
    [ProtoMember(9)] public float MaximumBrightness { get; set; }

    public void Validate()
    {
        if (Width <= 0 || Height <= 0 || Width > 65536 || Height > 65536)
            throw new InvalidDataException("External video frame dimensions are invalid.");
        int bytes = BitsPerChannel switch { 8 => 1, 16 => 2, _ => throw new InvalidDataException("External video frame bit depth must be 8 or 16.") };
        long pixels = checked((long)Width * Height);
        long colorLength = checked(pixels * bytes);
        long floatLength = checked(pixels * sizeof(float));
        if (Red.LongLength != colorLength || Green.LongLength != colorLength || Blue.LongLength != colorLength
            || Alpha.LongLength != 0 && Alpha.LongLength != floatLength
            || Brightness.LongLength != 0 && Brightness.LongLength != floatLength)
            throw new InvalidDataException("External video frame plane lengths are invalid.");
    }
}
