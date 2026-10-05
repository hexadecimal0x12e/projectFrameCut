using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.RPCProtocol;
using projectFrameCut.Render.PluginIsolation;

namespace projectFrameCut.Render.EncodeAndDecode;

public class ProjectExternalVideoSource : RemoteRpcVideoSource
{
    public new const string DecoderTypeName = nameof(ProjectExternalVideoSource);
    private readonly ProjectExternalSourceHost host;
    private ProjectExternalSourceClient? client;
    protected ProjectExternalVideoSource(ExternalVideoSourceReference source, ProjectExternalSourceHost host) : base(source, false) { this.host = host; }
    public override string TypeName => DecoderTypeName;
    public new static bool IsPath(string? path) => path?.StartsWith($"#{DecoderTypeName}:", StringComparison.Ordinal) == true;
    public static string CreatePath(Guid importId, ExternalVideoSourceDescriptor descriptor)
    {
        var copy = RenderRpcSerializer.Clone(descriptor);
        copy.ClientId = importId;
        return $"#{DecoderTypeName}:{new ExternalVideoSourceReference { ClientId = importId, SourceId = descriptor.SourceId,
            DecoderName = descriptor.DecoderName, Metadata = new(descriptor.Metadata), Descriptor = copy }.Encode()}";
    }
    private static ExternalVideoSourceReference Parse(string path)
    {
        if (!IsPath(path)) throw new ArgumentException("Invalid project external video source path.", nameof(path));
        var source = ExternalVideoSourceReference.Decode(path[($"#{DecoderTypeName}:").Length..]);
        if (source.ClientId == Guid.Empty || string.IsNullOrWhiteSpace(source.SourceId) || string.IsNullOrWhiteSpace(source.DecoderName))
            throw new InvalidDataException("Invalid project external video source reference.");
        return source;
    }
    public new static IVideoSource Open(string path) => Open(path, ProjectExternalSourceRuntime.Current);
    public static IVideoSource Open(string path, ProjectExternalSourceHost host)
    {
        var source = Parse(path);
        return source.Descriptor.SupportsHdr ? new ProjectExternalHdrVideoSource(source, host) : new ProjectExternalVideoSource(source, host);
    }
    public new static bool TryGetDescriptor(string path, out ExternalVideoSourceDescriptor descriptor)
    {
        descriptor = null!;
        try { descriptor = Parse(path).Descriptor; return true; }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or FormatException) { return false; }
    }
    public override IVideoSource CreateNew(string newSource) => Open(newSource, host);
    public override IPicture GetFrame(uint frame) => Read(new() { TargetFrame = frame, HasAlpha = true });
    public override IPicture GetFrame(uint frame, int x, int y, int width, int height, int targetWidth, int targetHeight) => Read(new()
    {
        TargetFrame = frame, SourceX = x, SourceY = y, SourceWidth = width, SourceHeight = height,
        TargetWidth = targetWidth, TargetHeight = targetHeight, UseRegion = true, HasAlpha = true,
    });
    protected override ValueTask<ExternalVideoSourceInstance> CreateInstanceAsync(ExternalVideoSourceReference source)
    {
        client = host.GetClient(source.ClientId);
        return client.CreateAsync(new() { Source = source });
    }
    protected override ValueTask<ExternalVideoSourceInstance> InitializeInstanceAsync(Guid clientId, ExternalVideoSourceStateRequest state) => client!.InitializeAsync(state);
    protected override ValueTask<ExternalVideoFrame> ReadInstanceAsync(Guid clientId, ExternalVideoSourceReadRequest request) => client!.ReadFrameAsync(request);
    protected override ValueTask ReleaseInstanceAsync(Guid clientId, ExternalVideoSourceStateRequest state) => client!.ReleaseAsync(state);
}

public sealed class ProjectExternalHdrVideoSource(ExternalVideoSourceReference source, ProjectExternalSourceHost host) : ProjectExternalVideoSource(source, host), IHDRVideoSource
{
    public HDRPicture16bpp GetHDRFrame(uint frame, bool hasAlpha = false) => ToHdr(ReadFrame(new() { TargetFrame = frame, RequestHdr = true, HasAlpha = hasAlpha }));
    public HDRPicture16bpp GetHDRFrame(uint frame, int x, int y, int width, int height, int targetWidth, int targetHeight, bool hasAlpha = false) =>
        ToHdr(ReadFrame(new() { TargetFrame = frame, SourceX = x, SourceY = y, SourceWidth = width, SourceHeight = height,
            TargetWidth = targetWidth, TargetHeight = targetHeight, UseRegion = true, RequestHdr = true, HasAlpha = hasAlpha }));
}
