using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.PluginIsolation;

internal sealed class RemoteTransform : ISingleFrameTransform, IOneInputSingleFrameTransform, IContinuousTransform, IDisposable
{
    private readonly IPluginIsolationSession _session;
    private readonly long _objectId;

    public RemoteTransform(IPluginIsolationSession session, IsolationTransformState state)
    {
        _session = session;
        _objectId = state.ObjectId;
        FromPlugin = state.FromPlugin;
        TypeName = state.TypeName;
        TransformType = (TransformType)state.TransformType;
        Name = state.Name;
        Apply(state);
    }

    public string FromPlugin { get; }
    public string TypeName { get; }
    public TransformType TransformType { get; }
    public string Name { get; init; }
    public Guid BindedLeftClip { get; set; }
    public Guid BindedRightClip { get; set; }
    public uint Duration { get; set; }
    public TransformDefinition Definition { get; private set; }
    public TransformSide Side { get; set; }
    public Dictionary<string, object> Parameters { get; set; } = new();
    public Dictionary<string, string> ParametersType { get; private set; } = new();
    public List<string> ParametersNeeded { get; private set; } = [];
    private string _serialized = string.Empty;
    private bool _disposed;
    public System.Text.Json.JsonElement Serialize()
    {
        Init();
        using var document = System.Text.Json.JsonDocument.Parse(_serialized);
        return document.RootElement.Clone();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Invoke<IsolationReleaseObjectRequest, EmptyResponse>(RenderOperation.IsolationReleaseObject, new() { ObjectId = _objectId }); }
        catch { }
    }

    public void Init() => Apply(Invoke<IsolationTransformState, IsolationTransformState>(RenderOperation.IsolationInitializeTransform, State()));
    public IPicture GetFrame(IPicture left, IPicture right, int targetWidth, int targetHeight) =>
        Process(left, right, 0, false, targetWidth, targetHeight);
    public IPicture GetFrame(IPicture input, double progress, int targetWidth, int targetHeight) =>
        Process(input, null, progress, true, targetWidth, targetHeight);
    public IPicture GetFrame(IPicture left, IPicture right, double progress, int targetWidth, int targetHeight) =>
        Process(left, right, progress, true, targetWidth, targetHeight);

    private IPicture Process(IPicture input, IPicture? second, double progress, bool hasProgress, int width, int height)
    {
        var firstLease = PicturePayloadCodec.WriteAsync(input, _session.Payloads, _session.PreferredPayloadKind, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        IsolationPayloadLease? secondLease = null;
        try
        {
            if (second is not null) secondLease = PicturePayloadCodec.WriteAsync(second, _session.Payloads, _session.PreferredPayloadKind, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            var response = Invoke<IsolationTransformFrameRequest, IsolationPictureResponse>(RenderOperation.IsolationProcessTransform, new()
            {
                State = State(),
                Input = firstLease.Reference,
                SecondInput = secondLease?.Reference,
                Progress = progress,
                HasProgress = hasProgress,
                TargetWidth = width,
                TargetHeight = height,
            });
            try { return PicturePayloadCodec.ReadAsync(response.Picture, _session.Payloads, CancellationToken.None).AsTask().GetAwaiter().GetResult(); }
            finally { _session.Payloads.ReleaseAsync(response.Picture).AsTask().GetAwaiter().GetResult(); }
        }
        finally
        {
            secondLease?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            firstLease.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private IsolationTransformState State() => new()
    {
        ObjectId = _objectId,
        FromPlugin = FromPlugin,
        TypeName = TypeName,
        TransformType = (int)TransformType,
        Name = Name,
        LeftClipId = BindedLeftClip.ToString(),
        RightClipId = BindedRightClip.ToString(),
        Duration = Duration,
        Definition = (int)Definition,
        Side = (int)Side,
        Parameters = Parameters.ToDictionary(x => x.Key, x => IsolationValueConverter.FromObject(x.Value)),
    };

    private void Apply(IsolationTransformState state)
    {
        BindedLeftClip = Guid.Parse(state.LeftClipId);
        BindedRightClip = Guid.Parse(state.RightClipId);
        Duration = state.Duration;
        Definition = (TransformDefinition)state.Definition;
        Side = (TransformSide)state.Side;
        Parameters = state.Parameters.ToDictionary(x => x.Key, x => IsolationValueConverter.ToObject(x.Value)!);
        ParametersType = state.ParametersType;
        ParametersNeeded = state.ParametersNeeded;
        _serialized = state.SerializedTransform;
    }

    private TResponse Invoke<TRequest, TResponse>(RenderOperation operation, TRequest request) =>
        _session.InvokeAsync<TRequest, TResponse>(operation, request).AsTask().GetAwaiter().GetResult();
}
