using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.RPCProtocol;

namespace projectFrameCut.Render.EncodeAndDecode;

public class RemoteRpcVideoSource : IVideoSource
{
    public const string DecoderTypeName = nameof(RemoteRpcVideoSource);
    private readonly ExternalVideoSourceReference? _source;
    private ExternalVideoSourceDescriptor? _descriptor;
    private Guid _instanceId;

    public RemoteRpcVideoSource() { }

    public RemoteRpcVideoSource(string source) : this(ParsePath(source)) { }

    protected RemoteRpcVideoSource(ExternalVideoSourceReference source)
    {
        _source = source;
        _descriptor = ExternalVideoSourceRegistry.Find(source) ?? source.Descriptor;
    }

    public string TypeName => DecoderTypeName;
    public int? ResultBitPerPixel => _descriptor?.HasKnownResultBitsPerPixel == true ? _descriptor.ResultBitsPerPixel : null;
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
            var created = ExternalVideoSourceRegistry.CreateAsync(_source, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            if (created.InstanceId == Guid.Empty) throw new InvalidDataException("External RPC video source returned an empty instance ID.");
            _instanceId = created.InstanceId;
            var initialized = ExternalVideoSourceRegistry.InitializeAsync(_source.ClientId, State(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            if (initialized.InstanceId != _instanceId) throw new InvalidDataException("External RPC video source initialization returned a different instance ID.");
            _descriptor = initialized.Descriptor;
            Log($"Initialized external RPC video source {_source.ClientId}/{_source.SourceId}.");
        }
        catch
        {
            if (_instanceId != Guid.Empty)
                try { ExternalVideoSourceRegistry.ReleaseAsync(_source.ClientId, State()).AsTask().GetAwaiter().GetResult(); } catch { }
            _instanceId = Guid.Empty;
            throw;
        }
    }

    public IVideoSource CreateNew(string newSource) => Open(newSource);

    public IVideoSource FromStream(Stream source, long length, bool leaveOpen = false) =>
        throw new NotSupportedException("External RPC video sources cannot be created from streams.");

    public IPicture GetFrame(uint targetFrame) => Read(new()
    {
        State = State(),
        TargetFrame = targetFrame,
    });

    public IPicture GetFrame(uint targetFrame, int sourceX, int sourceY, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight) => Read(new()
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
            try { ExternalVideoSourceRegistry.ReleaseAsync(_source.ClientId, State()).AsTask().GetAwaiter().GetResult(); }
            catch (Exception ex) { Log(ex, "Release external RPC video source", this); }
        }
        _instanceId = Guid.Empty;
    }

    private IPicture Read(ExternalVideoSourceReadRequest request)
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
            var frame = ExternalVideoSourceRegistry.ReadAsync(_source!.ClientId, request, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            frame.Validate();
            Index = request.TargetFrame == uint.MaxValue ? request.TargetFrame : request.TargetFrame + 1;
            return frame;
        }
        catch (IOException)
        {
            _instanceId = Guid.Empty;
            Initialize();
            request.State = State();
            var frame = ExternalVideoSourceRegistry.ReadAsync(_source!.ClientId, request, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            frame.Validate();
            Index = request.TargetFrame == uint.MaxValue ? request.TargetFrame : request.TargetFrame + 1;
            return frame;
        }
    }

    private void EnsureInitialized()
    {
        if (_instanceId == Guid.Empty) Initialize();
    }

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

    private static IPicture ToPicture(ExternalVideoFrame frame)
    {
        if (frame.BitsPerChannel == 8)
        {
            var picture = new Picture8bpp(frame.Width, frame.Height);
            frame.Red.CopyTo(picture.r, 0);
            frame.Green.CopyTo(picture.g, 0);
            frame.Blue.CopyTo(picture.b, 0);
            ApplyAlpha(frame, picture);
            return picture;
        }
        var result = new Picture16bpp(frame.Width, frame.Height);
        Buffer.BlockCopy(frame.Red, 0, result.r, 0, frame.Red.Length);
        Buffer.BlockCopy(frame.Green, 0, result.g, 0, frame.Green.Length);
        Buffer.BlockCopy(frame.Blue, 0, result.b, 0, frame.Blue.Length);
        ApplyAlpha(frame, result);
        return result;
    }

    protected static HDRPicture16bpp ToHdr(ExternalVideoFrame frame)
    {
        var result = new HDRPicture16bpp(frame.Width, frame.Height)
        {
            MaximumBrightness = float.IsFinite(frame.MaximumBrightness) && frame.MaximumBrightness > 0 ? frame.MaximumBrightness : 1000f,
        };
        if (frame.BitsPerChannel == 8)
        {
            for (int i = 0; i < result.Pixels; i++)
            {
                result.r[i] = (ushort)(frame.Red[i] * 257);
                result.g[i] = (ushort)(frame.Green[i] * 257);
                result.b[i] = (ushort)(frame.Blue[i] * 257);
            }
        }
        else
        {
            Buffer.BlockCopy(frame.Red, 0, result.r, 0, frame.Red.Length);
            Buffer.BlockCopy(frame.Green, 0, result.g, 0, frame.Green.Length);
            Buffer.BlockCopy(frame.Blue, 0, result.b, 0, frame.Blue.Length);
        }
        ApplyAlpha(frame, result);
        if (frame.Brightness.Length > 0) Buffer.BlockCopy(frame.Brightness, 0, result.Brightness, 0, frame.Brightness.Length);
        else Array.Fill(result.Brightness, 1f);
        return result;
    }

    private static void ApplyAlpha(ExternalVideoFrame frame, IPicture picture)
    {
        if (frame.Alpha.Length == 0) return;
        var alpha = new float[checked(frame.Width * frame.Height)];
        Buffer.BlockCopy(frame.Alpha, 0, alpha, 0, frame.Alpha.Length);
        if (picture is Picture8bpp p8) { p8.a = alpha; p8.HasAlphaChannel = true; }
        else if (picture is Picture16bpp p16) { p16.a = alpha; p16.HasAlphaChannel = true; }
    }
}

public sealed class RemoteRpcHdrVideoSource(ExternalVideoSourceReference source) : RemoteRpcVideoSource(source), IHDRVideoSource
{
    public HDRPicture16bpp GetHDRFrame(uint targetFrame, bool hasAlpha = false) => ToHdr(ReadFrame(new()
    {
        State = State(),
        TargetFrame = targetFrame,
        RequestHdr = true,
        HasAlpha = hasAlpha,
    }));

    public HDRPicture16bpp GetHDRFrame(uint targetFrame, int sourceX, int sourceY, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, bool hasAlpha = false) => ToHdr(ReadFrame(new()
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
