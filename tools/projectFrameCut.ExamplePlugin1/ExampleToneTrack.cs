using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace SomePublisher;

public sealed class ExampleToneTrack : ISoundTrack
{
    private ExampleAudioSource _source = new(null);

    public ExampleToneTrack(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string FromPlugin => ExamplePluginConstants.PluginId;
    public TrackMode TrackType => TrackMode.ExtendTrack;
    public string TypeName => "ExampleToneTrack";
    public string Id { get; init; }
    public string Name { get; init; }
    public uint LayerIndex { get; init; }
    public uint StartFrame { get; init; }
    public uint RelativeStartFrame { get; init; }
    public uint Duration { get; init; } = 300;
    public string? FilePath { get; set; }
    public bool NeedFilePath => false;
    public float Ratio { get; set; } = 1;
    public float Volume { get; set; } = 1;
    public int SamplePerSecond => _source.SamplePerSecond;
    public EffectAndMixtureJSONStructure[]? Effects { get; init; }
    public IEffect[]? EffectsInstances { get; set; }
    public Dictionary<string, object> ExtraData { get; set; } = new();

    public IAudioSamples GetAudioSamplesRelatedToStartPointOfSource(uint startIndex, int length) => _source.GetSample(startIndex, length);

    public void ReInit()
    {
        _source.Dispose();
        _source = new ExampleAudioSource(null);
    }

    public void Dispose() => _source.Dispose();
}
