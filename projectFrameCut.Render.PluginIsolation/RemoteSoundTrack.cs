using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System.Text.Json;

namespace projectFrameCut.Render.PluginIsolation;

internal sealed class RemoteSoundTrack : ISoundTrack
{
    private readonly IPluginIsolationSession _session;
    private readonly long _objectId;
    private bool _disposed;

    public RemoteSoundTrack(IPluginIsolationSession session, IsolationSoundTrackDescriptor state)
    {
        _session = session;
        _objectId = state.ObjectId;
        FromPlugin = state.FromPlugin;
        TrackType = (TrackMode)state.TrackType;
        TypeName = state.TypeName;
        Id = state.Id;
        Name = state.Name;
        LayerIndex = state.LayerIndex;
        StartFrame = state.StartFrame;
        RelativeStartFrame = state.RelativeStartFrame;
        Duration = state.Duration;
        NeedFilePath = state.NeedFilePath;
        SamplePerSecond = state.SamplePerSecond;
        Effects = JsonSerializer.Deserialize<EffectAndMixtureJSONStructure[]?>(state.EffectsJson);
        ExtraData = JsonSerializer.Deserialize<Dictionary<string, object>>(state.ExtraDataJson) ?? [];
        Apply(state);
    }

    public string FromPlugin { get; }
    public TrackMode TrackType { get; }
    public string TypeName { get; }
    public string Id { get; init; }
    public string Name { get; init; }
    public uint LayerIndex { get; init; }
    public uint StartFrame { get; init; }
    public uint RelativeStartFrame { get; init; }
    public uint Duration { get; init; }
    public string? FilePath { get; set; }
    public bool NeedFilePath { get; }
    public float Ratio { get; set; }
    public float Volume { get; set; }
    public int SamplePerSecond { get; }
    public EffectAndMixtureJSONStructure[]? Effects { get; init; }
    public IEffect[]? EffectsInstances { get; set; }
    public Dictionary<string, object> ExtraData { get; set; }

    public IAudioSamples GetAudioSamplesRelatedToStartPointOfSource(uint startIndex, int length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var response = Invoke<IsolationSoundTrackSamplesRequest, IsolationAudioSamplesResponse>(RenderOperation.IsolationReadSoundTrackSamples,
            new() { Track = State(), StartIndex = startIndex, Length = length });
        try { return AudioPayloadCodec.ReadAsync(response, _session.Payloads, CancellationToken.None).AsTask().GetAwaiter().GetResult(); }
        finally { _session.Payloads.ReleaseAsync(response.Samples).AsTask().GetAwaiter().GetResult(); }
    }

    public void ReInit() => Apply(Invoke<IsolationSoundTrackDescriptor, IsolationSoundTrackDescriptor>(RenderOperation.IsolationReinitializeSoundTrack, State()));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Invoke<IsolationReleaseObjectRequest, EmptyResponse>(RenderOperation.IsolationReleaseObject, new() { ObjectId = _objectId }); } catch { }
    }

    private IsolationSoundTrackDescriptor State() => new()
    {
        ObjectId = _objectId,
        FromPlugin = FromPlugin,
        TrackType = (int)TrackType,
        TypeName = TypeName,
        Id = Id,
        Name = Name,
        LayerIndex = LayerIndex,
        StartFrame = StartFrame,
        RelativeStartFrame = RelativeStartFrame,
        Duration = Duration,
        FilePath = FilePath ?? string.Empty,
        NeedFilePath = NeedFilePath,
        Ratio = Ratio,
        Volume = Volume,
        SamplePerSecond = SamplePerSecond,
    };
    private void Apply(IsolationSoundTrackDescriptor state)
    {
        FilePath = string.IsNullOrWhiteSpace(state.FilePath) ? null : state.FilePath;
        Ratio = state.Ratio;
        Volume = state.Volume;
    }
    private TResponse Invoke<TRequest, TResponse>(RenderOperation operation, TRequest request) =>
        _session.InvokeAsync<TRequest, TResponse>(operation, request).AsTask().GetAwaiter().GetResult();
}
