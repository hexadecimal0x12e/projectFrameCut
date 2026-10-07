using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.Transform;
using projectFrameCut.Shared;
using System.Text.Json;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class AudioTransformTests
{
    [TestMethod]
    public void FadeHonorsSideAndPreservesInput()
    {
        var input = new FloatAudioSamples { Channels = [[1, 1, 1], [-1, -1, -1]], SampleCount = 3, SamplePerSecond = 100 };
        var fade = new AudioFadeTransform();
        var entering = (FloatAudioSamples)TransformProcessing.ProcessSamples(input, null, fade, TransformInputMode.OneInput, 0, 3, TransformSide.Left);
        var leaving = (FloatAudioSamples)TransformProcessing.ProcessSamples(input, null, fade, TransformInputMode.OneInput, 0, 3, TransformSide.Right);
        CollectionAssert.AreEqual(new float[] { 0, 0.5f, 1 }, entering.Channels[0]);
        CollectionAssert.AreEqual(new float[] { 1, 0.5f, 0 }, leaving.Channels[0]);
        CollectionAssert.AreEqual(new float[] { 0, -0.5f, -1 }, entering.Channels[1]);
        CollectionAssert.AreEqual(new float[] { 1, 1, 1 }, input.Channels[0]);
        Assert.ThrowsExactly<NotSupportedException>(() => TransformProcessing.ProcessSamples(input, input, fade, TransformInputMode.TwoInput, 0, 3, TransformSide.Right));
    }

    [TestMethod]
    public void CrossfadeMixesBothTracksOnceAndSurvivesChunkingAndSeek()
    {
        var right = Track(10, 5, 0.6f, sampleRate: 200, ratio: 2);
        var left = Track(0, 10, 0.2f, next: Guid.Parse(right.Id));
        var whole = Compose([left, right], 200);
        var chunks = Compose([left, right], 7);
        CollectionAssert.AreEqual(whole, chunks);
        CollectionAssert.AreEqual(whole.Skip(90).ToArray(), Compose([left, right], 7, start: 9, duration: 11));
        Assert.AreEqual(0.2f, whole[79], 0.000001f);
        for (int i = 80; i < 120; i++)
            Assert.AreEqual(0.2f * (1 - (i - 80) / 39f) + 0.6f * (i - 80) / 39f, whole[i], 0.000001f);
        Assert.AreEqual(0.6f, whole[120], 0.000001f);
    }

    [TestMethod]
    public void SingleInputsFadeBothEndsWithoutOverlapping()
    {
        var track = Track(0, 10, 0.8f, fade: true);
        var provider = Attach(TransformSide.Right);
        var leaving = new AudioFadeTransform { BindedEffectProvidingSystemID = provider.Id.ToString() };
        track = new NormalSoundTrack
        {
            Id = track.Id, Name = track.Name, FilePath = track.FilePath, Duration = track.Duration,
            AudioSource = track.AudioSource, ExtraData = [], EffectProviders = [track.EffectProviders![0], provider],
            EffectsInstances = [track.EffectsInstances![0], leaving]
        };
        var output = Compose([track], 13, duration: 10);
        Assert.AreEqual(0f, output[0]);
        Assert.AreEqual(0.8f, output[39]);
        Assert.AreEqual(0.8f, output[40]);
        Assert.AreEqual(0.8f, output[60]);
        Assert.AreEqual(0f, output[99]);
    }

    [TestMethod]
    public void MissingSourcePrerollIsSilenceAndDoesNotShiftIncomingAudio()
    {
        var right = Track(10, 10, 0.6f, relativeStart: 0);
        var left = Track(0, 10, 0.2f, next: Guid.Parse(right.Id));
        var output = Compose([left, right], 7);
        Assert.AreEqual(0.2f * (1 - 10 / 39f), output[90], 0.000001f);
        Assert.AreEqual(0.2f * (1 - 20 / 39f) + 0.6f * 20 / 39f, output[100], 0.000001f);
    }

    [TestMethod]
    public void DisconnectedAndDisabledTransformsRetainConfiguration()
    {
        var right = Track(11, 10, 0.6f);
        var left = Track(0, 10, 0.2f, next: Guid.Parse(right.Id));
        var output = Compose([left, right], 7, duration: 21);
        Assert.AreEqual(0.2f, output[99]);
        Assert.AreEqual(0f, output[100]);
        Assert.AreEqual(0.6f, output[110]);
        Assert.AreEqual(Guid.Parse(right.Id), TransformProcessing.ReadNextClip(left.EffectProviders![0].MetaData));
        left.EffectProviders[0].Enabled = false;
        Assert.AreEqual(0.2f, Compose([left], 7, duration: 10)[0]);
        Assert.AreEqual(1, left.EffectProviders.Length);
    }

    [TestMethod]
    public void AudioTransformsUseTheRegistryAndPersistProviderConfiguration()
    {
        var registry = new EffectImplementationRegistry();
        registry.Register("audio", new InternalPluginBase().EffectImplementationProvider);
        foreach (var name in new[] { "AudioFade", "AudioCrossfade" })
            Assert.IsTrue(((ITransform)registry.Create(name, EffectImplementType.NotSpecified, EffectImplementType.NotSpecified, [])).Definition.HasFlag(TransformDefinition.Audio));
        var track = Track(0, 10, 0.2f, fade: true);
        var restored = System.Text.Json.JsonSerializer.SerializeToElement(track).Deserialize<NormalSoundTrack>()!;
        Assert.AreEqual(track.SubLayerIndex, restored.SubLayerIndex);
        Assert.AreEqual(track.EffectProviders![0].Id, restored.EffectProviders![0].Id);
        Assert.AreEqual(4u, TransformProcessing.ReadDuration(restored.EffectProviders[0].MetaData));
    }

    private static NormalSoundTrack Track(uint start, uint duration, float value, Guid next = default,
        int sampleRate = 100, float ratio = 1, uint relativeStart = 5, bool fade = false)
    {
        var provider = Attach(next == Guid.Empty ? TransformSide.Left : TransformSide.Right, next);
        ITransform transform = next == Guid.Empty ? new AudioFadeTransform() : new AudioCrossfadeTransform();
        transform.BindedEffectProvidingSystemID = provider.Id.ToString();
        return new NormalSoundTrack
        {
            Id = Guid.NewGuid().ToString(), Name = "test", FilePath = "test", StartFrame = start, Duration = duration,
            RelativeStartFrame = relativeStart, Ratio = ratio, SubLayerIndex = 2, ExtraData = [],
            AudioSource = new ConstantSource(value, sampleRate),
            EffectProviders = fade || next != Guid.Empty ? [provider] : [], EffectsInstances = fade || next != Guid.Empty ? [transform] : []
        };
    }

    private static EffectProviderJSONStructure Attach(TransformSide side, Guid next = default)
    {
        IEffectProvider provider = next == Guid.Empty ? new AudioFadeTransformProvider() : new AudioCrossfadeTransformProvider();
        TransformProcessing.Configure(provider, side, next == Guid.Empty ? TransformInputMode.OneInput : TransformInputMode.TwoInput,
            4, next, TransformRenderOrder.AfterEffects);
        return EffectBindingHelper.SerializeProvider(provider);
    }

    private static float[] Compose(ISoundTrack[] tracks, int chunk, uint start = 0, uint duration = 20)
    {
        using var writer = new MemoryWriter();
        new AudioComposer<float> { Clips = [], SoundTracks = tracks, Writer = writer, StartFrame = start, Duration = duration }
            .Compose(10, 100, 1, chunk);
        return writer.Samples.ToArray();
    }

    private sealed class ConstantSource(float value, int sampleRate) : IAudioSource
    {
        public string FromPlugin => "test";
        public string TypeName => "constant";
        public string[] PreferredExtension => [];
        public uint Duration => 1000;
        public int ChannelCount => 1;
        public int SamplePerSecond => sampleRate;
        public bool Disposed { get; private set; }
        public void Initialize() { }
        public void Dispose() => Disposed = true;
        public IAudioSource CreateNew(string newSource) => new ConstantSource(value, sampleRate);
        public object GetSingleSample(uint index) => value;
        public IAudioSamples GetSample(uint startIndex, long count) => new FloatAudioSamples
        {
            Channels = [Enumerable.Repeat(value, (int)count).ToArray()], SampleCount = (int)count, SamplePerSecond = sampleRate
        };
    }

    private sealed class MemoryWriter : AudioWriterBase<float>
    {
        public List<float> Samples { get; } = [];
        public override void Write(float data, int channel) => Samples.Add(data);
        public override void Initialize() { }
        public override bool SupportCodec(string codecName) => true;
        public override void Finish() { }
        public override void Dispose() { }
    }
}
