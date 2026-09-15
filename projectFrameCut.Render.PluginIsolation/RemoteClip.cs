using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System.Text.Json;

namespace projectFrameCut.Render.PluginIsolation;

internal sealed class RemoteClip : IClip
{
    private readonly IPluginIsolationSession _session;
    private readonly long _objectId;
    private bool _disposed;

    public RemoteClip(IPluginIsolationSession session, IsolationClipObjectDescriptor descriptor)
    {
        _session = session;
        _objectId = descriptor.ObjectId;
        var state = descriptor.State;
        FromPlugin = state.FromPlugin;
        ClipType = (ClipMode)state.ClipType;
        TypeName = state.TypeName;
        Id = Guid.Parse(state.Id);
        Name = state.Name;
        BindedSoundTrack = state.BoundSoundTrack;
        LayerIndex = state.LayerIndex;
        SubLayerIndex = state.SubLayerIndex;
        StartFrame = state.StartFrame;
        RelativeStartFrame = state.RelativeStartFrame;
        FrameTime = state.FrameTime;
        NeedFilePath = state.NeedFilePath;
        Effects = JsonSerializer.Deserialize<EffectAndMixtureJSONStructure[]?>(descriptor.EffectsJson);
        EffectProviders = JsonSerializer.Deserialize<EffectProviderJSONStructure[]?>(descriptor.EffectProvidersJson);
        ExtraData = JsonSerializer.Deserialize<Dictionary<string, object>>(descriptor.ExtraDataJson) ?? [];
        Apply(state);
    }

    public string FromPlugin { get; }
    public ClipMode ClipType { get; }
    public string TypeName { get; }
    public Guid Id { get; init; }
    public string Name { get; init; }
    public string BindedSoundTrack { get; init; }
    public uint LayerIndex { get; init; }
    public uint SubLayerIndex { get; init; }
    public uint StartFrame { get; init; }
    public uint RelativeStartFrame { get; init; }
    public uint Duration { get; set; }
    public int TargetWidth { get; set; }
    public int TargetHeight { get; set; }
    public int TargetX { get; set; }
    public int TargetY { get; set; }
    public int StartingX { get; set; }
    public int StartingY { get; set; }
    public float FrameTime { get; init; }
    public ISpeedVarianceProvider? SpeedVarianceProviderInstance { get; set; }
    public IMixture? MixtureInstance { get; set; }
    public ISourceReplacementEffect? AlternativeSource { get; set; }
    public bool ExtendToWholeDraft { get; set; }
    public EffectAndMixtureJSONStructure[]? Effects { get; init; }
    public EffectProviderJSONStructure[]? EffectProviders { get; init; }
    public IEffect[]? EffectsInstances { get; set; }
    public IEffectProvider[]? EffectProvidersInstances { get; set; }
    public string? FilePath { get; set; }
    public bool NeedFilePath { get; }
    public Dictionary<string, object> ExtraData { get; set; }

    public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int requiredWidth, int requiredHeight, IPicture.PicturePixelMode targetPPB)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var response = Invoke<IsolationClipFrameRequest, IsolationPictureResponse>(RenderOperation.IsolationReadClipFrame, new()
        {
            Clip = Descriptor(),
            FrameIndex = frameIndex,
            Width = requiredWidth,
            Height = requiredHeight,
            PixelMode = (int)targetPPB,
        });
        try { return PicturePayloadCodec.ReadAsync(response.Picture, _session.Payloads, CancellationToken.None).AsTask().GetAwaiter().GetResult(); }
        finally { _session.Payloads.ReleaseAsync(response.Picture).AsTask().GetAwaiter().GetResult(); }
    }

    public void ReInit(IPicture.PicturePixelMode targetPPB)
    {
        var result = Invoke<IsolationClipFrameRequest, IsolationClipObjectDescriptor>(RenderOperation.IsolationReinitializeClip,
            new() { Clip = Descriptor(), PixelMode = (int)targetPPB });
        Apply(result.State);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Invoke<IsolationReleaseObjectRequest, EmptyResponse>(RenderOperation.IsolationReleaseObject, new() { ObjectId = _objectId }); } catch { }
    }

    private IsolationClipObjectDescriptor Descriptor() => new() { ObjectId = _objectId, State = IsolationClipSnapshotFactory.Create(this) };
    private void Apply(IsolationClipSnapshot state)
    {
        Duration = state.Duration;
        TargetWidth = state.TargetWidth;
        TargetHeight = state.TargetHeight;
        TargetX = state.TargetX;
        TargetY = state.TargetY;
        StartingX = state.StartingX;
        StartingY = state.StartingY;
        ExtendToWholeDraft = state.ExtendToWholeDraft;
        FilePath = string.IsNullOrWhiteSpace(state.FilePath) ? null : state.FilePath;
    }
    private TResponse Invoke<TRequest, TResponse>(RenderOperation operation, TRequest request) =>
        _session.InvokeAsync<TRequest, TResponse>(operation, request).AsTask().GetAwaiter().GetResult();
}
