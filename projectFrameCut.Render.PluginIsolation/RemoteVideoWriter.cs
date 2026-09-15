using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.Sources;

namespace projectFrameCut.Render.PluginIsolation;

internal sealed class RemoteVideoWriter : IVideoWriter
{
    private readonly IPluginIsolationSession _session;
    private readonly long _objectId;
    private bool _disposed;

    public RemoteVideoWriter(IPluginIsolationSession session, IsolationVideoWriterState state)
    {
        _session = session;
        _objectId = state.ObjectId;
        Apply(state);
    }

    public int Width { get; set; }
    public int Height { get; set; }
    public string OutputPath { get; set; } = string.Empty;
    public int FramePerSecond { get; set; }
    public string CodecName { get; set; } = string.Empty;
    public string PixelFormat { get; set; } = string.Empty;
    public long BitRate { get; set; }
    public bool PreferToSpeed { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
    public uint DurationWritten { get; private set; }
    public IPicture.PicturePixelMode? TargetPPB { get; private set; }

    public void Initialize() => Apply(Invoke<IsolationVideoWriterState, IsolationVideoWriterState>(RenderOperation.IsolationInitializeVideoWriter, State()));
    public bool SupportCodec(string codecName) => Invoke<IsolationVideoWriterCodecRequest, IsolationBooleanResponse>(
        RenderOperation.IsolationVideoWriterSupportsCodec, new() { ObjectId = _objectId, CodecName = codecName }).Value;
    public void Finish() => Apply(Invoke<IsolationVideoWriterState, IsolationVideoWriterState>(RenderOperation.IsolationFinishVideoWriter, State()));
    public void Append(IPicture<ushort> picture) => AppendCore(picture);
    public void Append(IPicture<byte> picture) => AppendCore(picture);

    private void AppendCore(IPicture picture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var lease = PicturePayloadCodec.WriteAsync(picture, _session.Payloads, _session.PreferredPayloadKind, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        try
        {
            Apply(Invoke<IsolationVideoWriterFrameRequest, IsolationVideoWriterState>(RenderOperation.IsolationAppendVideoWriterFrame,
                new() { State = State(), Picture = lease.Reference }));
        }
        finally { lease.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Invoke<IsolationReleaseObjectRequest, EmptyResponse>(RenderOperation.IsolationReleaseObject, new() { ObjectId = _objectId }); } catch { }
    }

    private IsolationVideoWriterState State() => new()
    {
        ObjectId = _objectId,
        Width = Width,
        Height = Height,
        OutputPath = OutputPath,
        FramePerSecond = FramePerSecond,
        CodecName = CodecName,
        PixelFormat = PixelFormat,
        BitRate = BitRate,
        PreferToSpeed = PreferToSpeed,
        Metadata = Metadata ?? [],
    };

    private void Apply(IsolationVideoWriterState state)
    {
        Width = state.Width;
        Height = state.Height;
        OutputPath = state.OutputPath;
        FramePerSecond = state.FramePerSecond;
        CodecName = state.CodecName;
        PixelFormat = state.PixelFormat;
        BitRate = state.BitRate;
        PreferToSpeed = state.PreferToSpeed;
        Metadata = state.Metadata;
        DurationWritten = state.DurationWritten;
        TargetPPB = state.HasTargetPixelMode ? (IPicture.PicturePixelMode)state.TargetPixelMode : null;
    }

    private TResponse Invoke<TRequest, TResponse>(RenderOperation operation, TRequest request) =>
        _session.InvokeAsync<TRequest, TResponse>(operation, request).AsTask().GetAwaiter().GetResult();
}
