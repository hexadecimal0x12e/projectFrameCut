using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.RPCProtocol;

namespace projectFrameCut.Render.EncodeAndDecode;

public class RemoteRpcVideoSource : IVideoSource
{
    public bool IsDeterministic => _descriptor?.IsDeterministic == true;

    public const string DecoderTypeName = nameof(RemoteRpcVideoSource);
    private readonly ExternalVideoSourceReference? _source;
    private ExternalVideoSourceDescriptor? _descriptor;
    private Guid _instanceId;

    public RemoteRpcVideoSource() { }

    public RemoteRpcVideoSource(string source) : this(ParsePath(source)) { }

    protected RemoteRpcVideoSource(ExternalVideoSourceReference source, bool useRpcCatalog = true)
    {
        _source = source;
        _descriptor = (useRpcCatalog ? ExternalVideoSourceRegistry.Find(source) : null) ?? source.Descriptor;
    }

    public virtual string TypeName => DecoderTypeName;
    public int? ResultBitPerPixel => IPicture.PicturePixelMode.Float16Picture;
    public bool AllowCachingResult => _descriptor?.AllowCachingResult ?? false;
    public string[] PreferredExtension => _descriptor?.PreferredExtensions.ToArray() ?? [];
    public uint Index { get; set; }
    public long TotalFrames => _descriptor?.TotalFrames ?? -1;
    public double Fps => _descriptor?.Fps ?? 0;
    public int Width => _descriptor?.Width ?? 0;
    public int Height => _descriptor?.Height ?? 0;
    public bool Disposed { get; private set; }
    public bool EnableLock { get; set; } = true;
    public bool StrictMode { get; set; } = true;

    public static string CreatePath(ExternalVideoSourceDescriptor source) => $"#{DecoderTypeName}:{new ExternalVideoSourceReference
    {
        ClientId = source.ClientId,
        SourceId = source.SourceId,
        DecoderName = source.DecoderName,
        Metadata = new(source.Metadata),
        Descriptor = RenderRpcSerializer.Clone(source),
    }.Encode()}";

    public static bool IsPath(string? path) => path?.StartsWith($"#{DecoderTypeName}:", StringComparison.Ordinal) == true;
    public static bool IsExternalPath(string? path) => IsPath(path) || ProjectExternalVideoSource.IsPath(path);

    public static bool TryGetDescriptor(string path, out ExternalVideoSourceDescriptor descriptor)
    {
        descriptor = null!;
        try
        {
            var source = ParsePath(path);
            descriptor = ExternalVideoSourceRegistry.Find(source) ?? source.Descriptor;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or FormatException)
        {
            return false;
        }
    }

    public static IVideoSource Open(string path)
    {
        var source = ParsePath(path);
        var descriptor = ExternalVideoSourceRegistry.Find(source) ?? source.Descriptor;
        return descriptor.SupportsHdr ? new RemoteRpcHdrVideoSource(source) : new RemoteRpcVideoSource(source);
    }

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (_source is null) return;
        if (_instanceId != Guid.Empty) return;
        try
        {
            var created = CreateInstanceAsync(_source).AsTask().GetAwaiter().GetResult();
            if (created.InstanceId == Guid.Empty) throw new InvalidDataException("External RPC video source returned an empty instance ID.");
            _instanceId = created.InstanceId;
            var initialized = InitializeInstanceAsync(_source.ClientId, State()).AsTask().GetAwaiter().GetResult();
            if (initialized.InstanceId != _instanceId) throw new InvalidDataException("External RPC video source initialization returned a different instance ID.");
            _descriptor = initialized.Descriptor;
            Log($"Initialized external RPC video source {_source.ClientId}/{_source.SourceId}, AllowCachingResult={AllowCachingResult}.");
        }
        catch
        {
            if (_instanceId != Guid.Empty)
                try { ReleaseInstanceAsync(_source.ClientId, State()).AsTask().GetAwaiter().GetResult(); } catch { }
            _instanceId = Guid.Empty;
            throw;
        }
    }

    public virtual IVideoSource CreateNew(string newSource) => Open(newSource);

    public IVideoSource FromStream(Stream source, long length, bool leaveOpen = false) =>
        throw new NotSupportedException("External RPC video sources cannot be created from streams.");

    public virtual IPicture GetFrame(uint targetFrame) => Read(new()
    {
        State = State(),
        TargetFrame = targetFrame,
    });

    public virtual IPicture GetFrame(uint targetFrame, int sourceX, int sourceY, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight) => Read(new()
    {
        State = State(),
        TargetFrame = targetFrame,
        SourceX = sourceX,
        SourceY = sourceY,
        SourceWidth = sourceWidth,
        SourceHeight = sourceHeight,
        TargetWidth = targetWidth,
        TargetHeight = targetHeight,
        UseRegion = true,
    });

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        if (_source is not null && _instanceId != Guid.Empty)
        {
            try { ReleaseInstanceAsync(_source.ClientId, State()).AsTask().GetAwaiter().GetResult(); }
            catch (Exception ex) { Log(ex, "Release external RPC video source", this); }
        }
        _instanceId = Guid.Empty;
    }

    protected IPicture Read(ExternalVideoSourceReadRequest request)
    {
        var frame = ReadFrame(request);
        return frame.Brightness.Length > 0 ? ToHdr(frame) : ToPicture(frame);
    }

    protected ExternalVideoFrame ReadFrame(ExternalVideoSourceReadRequest request)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        EnsureInitialized();
        request.State = State();
        try
        {
            var frame = ReadInstanceAsync(_source!.ClientId, request).AsTask().GetAwaiter().GetResult();
            frame.Validate();
            Index = request.TargetFrame == uint.MaxValue ? request.TargetFrame : request.TargetFrame + 1;
            return frame;
        }
        catch (IOException)
        {
            _instanceId = Guid.Empty;
            Initialize();
            request.State = State();
            var frame = ReadInstanceAsync(_source!.ClientId, request).AsTask().GetAwaiter().GetResult();
            frame.Validate();
            Index = request.TargetFrame == uint.MaxValue ? request.TargetFrame : request.TargetFrame + 1;
            return frame;
        }
    }

    private void EnsureInitialized()
    {
        if (_instanceId == Guid.Empty) Initialize();
    }

    protected virtual ValueTask<ExternalVideoSourceInstance> CreateInstanceAsync(ExternalVideoSourceReference source) => ExternalVideoSourceRegistry.CreateAsync(source, CancellationToken.None);
    protected virtual ValueTask<ExternalVideoSourceInstance> InitializeInstanceAsync(Guid clientId, ExternalVideoSourceStateRequest state) => ExternalVideoSourceRegistry.InitializeAsync(clientId, state, CancellationToken.None);
    protected virtual ValueTask<ExternalVideoFrame> ReadInstanceAsync(Guid clientId, ExternalVideoSourceReadRequest request) => ExternalVideoSourceRegistry.ReadAsync(clientId, request, CancellationToken.None);
    protected virtual ValueTask ReleaseInstanceAsync(Guid clientId, ExternalVideoSourceStateRequest state) => ExternalVideoSourceRegistry.ReleaseAsync(clientId, state);

    protected ExternalVideoSourceStateRequest State() => new()
    {
        InstanceId = _instanceId,
        Index = Index,
        EnableLock = EnableLock,
        StrictMode = StrictMode,
    };

    private static ExternalVideoSourceReference ParsePath(string path)
    {
        if (!IsPath(path)) throw new ArgumentException("Invalid external RPC video source path.", nameof(path));
        var source = ExternalVideoSourceReference.Decode(path[($"#{DecoderTypeName}:").Length..]);
        if (source.ClientId == Guid.Empty || string.IsNullOrWhiteSpace(source.SourceId) || string.IsNullOrWhiteSpace(source.DecoderName))
            throw new InvalidDataException("External RPC video source reference is invalid.");
        return source;
    }

    internal static IPicture ToPicture(ExternalVideoFrame frame)
    {
        frame.Validate();
        return PictureFP16.FromOwnedChannels(frame.Width, frame.Height, ReadColor(frame.Red, frame), ReadColor(frame.Green, frame), ReadColor(frame.Blue, frame),
            frame.Alpha.Length > 0 ? ReadHalfPlane(frame.Alpha) : null);
    }

    protected internal static HDRPictureFP16 ToHdr(ExternalVideoFrame frame)
    {
        frame.Validate();
        int n = checked(frame.Width * frame.Height);
        return HDRPictureFP16.FromOwnedChannels(frame.Width, frame.Height, ReadColor(frame.Red, frame), ReadColor(frame.Green, frame), ReadColor(frame.Blue, frame),
            frame.Alpha.Length > 0 ? ReadHalfPlane(frame.Alpha) : null,
            frame.Brightness.Length > 0 ? ReadHalfPlane(frame.Brightness) : Enumerable.Repeat(Half.One, n).ToArray(),
            Half.IsFinite(frame.MaximumBrightness) && frame.MaximumBrightness > Half.Zero ? frame.MaximumBrightness : (Half)1000);
    }

    private static Half[] ReadColor(byte[] data, ExternalVideoFrame frame)
    {
        if (frame.UsesFloat16) return ReadHalfPlane(data);
        var result = new Half[checked(frame.Width * frame.Height)];
        for (int i = 0; i < result.Length; i++) result[i] = (Half)(frame.BitsPerChannel == 8 ? data[i] / 255f
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i * 2, 2)) / 65535f);
        return result;
    }

    private static Half[] ReadHalfPlane(byte[] bytes) =>
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(bytes).ToArray();
}

public sealed class RemoteRpcHdrVideoSource(ExternalVideoSourceReference source) : RemoteRpcVideoSource(source), IHDRVideoSource
{
    public HDRPictureFP16 GetHDRFrame(uint targetFrame, bool hasAlpha = false) => ToHdr(ReadFrame(new()
    {
        State = State(),
        TargetFrame = targetFrame,
        RequestHdr = true,
        HasAlpha = hasAlpha,
    }));

    public HDRPictureFP16 GetHDRFrame(uint targetFrame, int sourceX, int sourceY, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, bool hasAlpha = false) => ToHdr(ReadFrame(new()
    {
        State = State(),
        TargetFrame = targetFrame,
        SourceX = sourceX,
        SourceY = sourceY,
        SourceWidth = sourceWidth,
        SourceHeight = sourceHeight,
        TargetWidth = targetWidth,
        TargetHeight = targetHeight,
        UseRegion = true,
        RequestHdr = true,
        HasAlpha = hasAlpha,
    }));
}
