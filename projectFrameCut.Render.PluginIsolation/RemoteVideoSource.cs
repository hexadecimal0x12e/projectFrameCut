using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.Sources;

namespace projectFrameCut.Render.PluginIsolation;

public class RemoteVideoSource : IVideoSource
{
    protected readonly IPluginIsolationSession Session;
    private IsolationVideoSourceDescriptor _descriptor;
    private IsolationPayloadLease? _resource;
    private bool _disposed;

    protected RemoteVideoSource(IPluginIsolationSession session, IsolationVideoSourceDescriptor descriptor, IsolationPayloadLease? resource = null)
    {
        Session = session;
        _descriptor = descriptor;
        _resource = resource;
    }

    public static IVideoSource CreatePrototype(IPluginIsolationSession session, IsolationVideoSourceDescriptor descriptor)
        => descriptor.SupportsHdr ? new RemoteHdrVideoSource(session, descriptor) : new RemoteVideoSource(session, descriptor);

    public string TypeName => _descriptor.TypeName;
    public int? ResultBitPerPixel => _descriptor.HasKnownResultBitsPerPixel ? _descriptor.ResultBitsPerPixel : null;
    public string[] PreferredExtension => _descriptor.PreferredExtensions.ToArray();
    public uint Index { get; set; }
    public long TotalFrames => _descriptor.TotalFrames;
    public double Fps => _descriptor.Fps;
    public int Width => _descriptor.Width;
    public int Height => _descriptor.Height;
    public bool Disposed => _disposed;
    public bool EnableLock { get; set; }
    public bool StrictMode { get; set; }

    public virtual IVideoSource CreateNew(string newSource)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var lease = Session.Resources.BrokerReadOnlyFileAsync(newSource).AsTask().GetAwaiter().GetResult();
        try
        {
            var descriptor = Invoke<IsolationCreateVideoSourceRequest, IsolationVideoSourceDescriptor>(RenderOperation.IsolationCreateVideoSource, new()
            {
                TypeName = TypeName,
                Source = lease.Reference,
            });
            return descriptor.SupportsHdr ? new RemoteHdrVideoSource(Session, descriptor, lease) : new RemoteVideoSource(Session, descriptor, lease);
        }
        catch
        {
            lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    public virtual IVideoSource FromStream(Stream source, long length, bool leaveOpen = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IsolationPayloadLease lease;
        try { lease = Session.Resources.BrokerReadOnlyStreamAsync(source, length).AsTask().GetAwaiter().GetResult(); }
        finally { if (!leaveOpen) source.Dispose(); }
        try
        {
            var descriptor = Invoke<IsolationCreateVideoSourceRequest, IsolationVideoSourceDescriptor>(RenderOperation.IsolationCreateVideoSource, new()
            {
                TypeName = TypeName,
                Source = lease.Reference,
            });
            return descriptor.SupportsHdr ? new RemoteHdrVideoSource(Session, descriptor, lease) : new RemoteVideoSource(Session, descriptor, lease);
        }
        catch
        {
            lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    public void Initialize()
    {
        EnsureRemoteInstance();
        _descriptor = Invoke<IsolationVideoSourceStateRequest, IsolationVideoSourceDescriptor>(RenderOperation.IsolationInitializeVideoSource, State());
    }

    public virtual IPicture GetFrame(uint targetFrame)
        => ReadFrame(new() { State = State(), TargetFrame = targetFrame });

    public virtual IPicture GetFrame(uint targetFrame, int sourceX, int sourceY, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
        => ReadFrame(new()
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

    protected IPicture ReadFrame(IsolationReadVideoFrameRequest request)
    {
        EnsureRemoteInstance();
        var response = Invoke<IsolationReadVideoFrameRequest, IsolationPictureResponse>(RenderOperation.IsolationReadVideoFrame, request);
        try { return PicturePayloadCodec.ReadAsync(response.Picture, Session.Payloads).AsTask().GetAwaiter().GetResult(); }
        finally { Session.Payloads.ReleaseAsync(response.Picture).AsTask().GetAwaiter().GetResult(); }
    }

    protected IsolationVideoSourceStateRequest State() => new()
    {
        ObjectId = _descriptor.ObjectId,
        Index = Index,
        EnableLock = EnableLock,
        StrictMode = StrictMode,
    };

    protected TResponse Invoke<TRequest, TResponse>(RenderOperation operation, TRequest request)
        => Session.InvokeAsync<TRequest, TResponse>(operation, request).AsTask().GetAwaiter().GetResult();

    private void EnsureRemoteInstance()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_descriptor.ObjectId == 0) throw new InvalidOperationException("A video source prototype must be cloned with CreateNew or FromStream before use.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_descriptor.ObjectId != 0)
        {
            try { Invoke<IsolationReleaseObjectRequest, EmptyResponse>(RenderOperation.IsolationReleaseObject, new() { ObjectId = _descriptor.ObjectId }); } catch { }
        }
        if (_resource is not null)
        {
            try { _resource.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            _resource = null;
        }
    }
}

public sealed class RemoteHdrVideoSource : RemoteVideoSource, IHDRVideoSource
{
    internal RemoteHdrVideoSource(IPluginIsolationSession session, IsolationVideoSourceDescriptor descriptor, IsolationPayloadLease? resource = null)
        : base(session, descriptor, resource) { }

    public HDRPicture16bpp GetHDRFrame(uint targetFrame, bool hasAlpha = false)
        => (HDRPicture16bpp)ReadFrame(new() { State = State(), TargetFrame = targetFrame, RequestHdr = true, HasAlpha = hasAlpha });

    public HDRPicture16bpp GetHDRFrame(uint targetFrame, int sourceX, int sourceY, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, bool hasAlpha = false)
        => (HDRPicture16bpp)ReadFrame(new()
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
        });
}
