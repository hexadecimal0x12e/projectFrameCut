using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;
using static projectFrameCut.Render.Contracts.Tests.DynamicEffectGraphTests;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class FrameHashTests
{
    private sealed class ChangingProvider(Func<EffectExecutionContext, object?> compute)
        : Provider(null, EffectArgumentFieldType.IPicture, compute)
    {
        public override bool IsDeterministic => false;
    }

    private sealed class AudioSource(bool deterministic) : IAudioSource
    {
        public bool IsDeterministic => deterministic;
        public string TypeName => nameof(AudioSource);
        public string[] PreferredExtension => [];
        public uint Duration => 48000;
        public int ChannelCount => 2;
        public int SamplePerSecond => 48000;
        public bool Disposed => false;
        public void Initialize() { }
        public IAudioSource CreateNew(string source) => new AudioSource(deterministic);
        public IAudioSamples GetSample(uint start, long count) => throw new AssertFailedException("Hashing read audio samples.");
        public object GetSingleSample(uint index) => throw new AssertFailedException("Hashing read audio samples.");
        public void Dispose() { }
    }

    [TestMethod]
    public void CanonicalValuesIgnoreDictionaryInsertionOrder()
    {
        Assert.AreEqual(FrameHashHelper.HashValue(new Dictionary<string, object> { ["a"] = 1, ["b"] = 2 }),
            FrameHashHelper.HashValue(new Dictionary<string, object> { ["b"] = 2, ["a"] = 1 }));
        Assert.AreNotEqual(FrameHashHelper.HashValue((1, 2)), FrameHashHelper.HashValue((2, 1)));
    }

    [TestMethod]
    public void MaskHashesIncludeBackdropAndLayout()
    {
        using var lower = new SolidColorClip { Id = Guid.NewGuid(), Name = "lower", Duration = 1, ExtraData = [] };
        using var clip = new MaskClip { Id = Guid.NewGuid(), Name = "mask", Duration = 1, LayerIndex = 1 };
        var first = FrameHashHelper.GetClipHash([lower, clip], clip, 0);
        Assert.IsTrue(first.CanReuse);
        lower.TargetX = 20;
        Assert.AreNotEqual(first.Hash, FrameHashHelper.GetClipHash([lower, clip], clip, 0).Hash);
        var second = FrameHashHelper.GetClipHash([lower, clip], clip, 0);
        clip.Rotation = 90;
        Assert.AreNotEqual(second.Hash, FrameHashHelper.GetClipHash([lower, clip], clip, 0).Hash);
    }

    [TestMethod]
    public void ClipContentIgnoresLayoutAndPositionEffects()
    {
        using var clip = new SolidColorClip
        {
            Id = Guid.NewGuid(), Name = "changing", Duration = 1, ExtraData = [],
            EffectsInstances = [new JitterEffect()],
        };
        var first = FrameHashHelper.GetClipHash([clip], clip, 0);
        clip.TargetX = 10;
        clip.TargetY = 20;
        clip.TargetWidth = 100;
        clip.TargetHeight = 200;
        clip.Rotation = 90;
        var same = FrameHashHelper.GetClipHash([clip], clip, 0);
        clip.EffectsInstances = [];
        var withoutPosition = FrameHashHelper.GetClipHash([clip], clip, 0);
        Assert.IsTrue(first.CanReuse);
        Assert.AreEqual(first.Hash, same.Hash);
        Assert.AreEqual(first.Hash, withoutPosition.Hash);
        var frame = FrameHashHelper.GetFrameHash([clip], 0);
        clip.TargetX++;
        Assert.AreNotEqual(frame.Hash, FrameHashHelper.GetFrameHash([clip], 0).Hash);
    }

    [TestMethod]
    public void PictureHashesIncludeAlphaDimensionsAndHdrBrightness()
    {
        using var a = Picture8bpp.GenerateSolidColor(1, 2, 1, 2, 3, Half.One);
        using var b = Picture8bpp.GenerateSolidColor(2, 1, 1, 2, 3, Half.One);
        Assert.AreNotEqual(FrameHashHelper.HashPicture(a), FrameHashHelper.HashPicture(b));
        string opaque = FrameHashHelper.HashPicture(a);
        ((IPicture<byte>)a).a![0] = (Half)0.5f;
        Assert.AreNotEqual(opaque, FrameHashHelper.HashPicture(a));
        using var hdr = HDRPicture16bpp.GenerateSolidColor(1, 1, 1, 2, 3, Half.One, Half.One);
        string original = FrameHashHelper.HashPicture(hdr);
        hdr.MaximumBrightness += Half.One;
        Assert.AreNotEqual(original, FrameHashHelper.HashPicture(hdr));
    }

    [TestMethod]
    public void FrameSnapshotsAreEvaluatedOnceAndReturnedAsOwnedCopies()
    {
        using var context = new FrameHashContext(new());
        int calls = 0;
        using var first = context.ReadPicture("source", () => { calls++; return Picture(10); })!;
        ((IPicture<byte>)first).r[0] = 20;
        using var second = context.ReadPicture("source", () => { calls++; return Picture(30); })!;
        Assert.AreEqual(1, calls);
        Assert.AreEqual((byte)10, ((IPicture<byte>)second).r[0]);
        Assert.AreNotSame(first, second);
    }

    [TestMethod]
    public void NonDeterministicInputsPreventConsumerReuse()
    {
        int sourceCalls = 0, consumerCalls = 0;
        byte value = 10;
        var source = new ChangingProvider(_ => { sourceCalls++; return Picture(value); });
        var consumer = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c =>
        {
            consumerCalls++;
            return DynamicEffectGraph.CopyPicture((IPicture)c.Parameters["Other"]!);
        }, new EffectArgumentFieldDescriptor { Id = "Other", FieldType = EffectArgumentFieldType.IPicture });
        consumer.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        consumer.SetFieldBinding("Other", source.Id.ToString());
        consumer.SetFinalOutputSource(true);
        using var graph = Build(Providers(source, consumer));
        using var cache = new FrameHashCache();
        using var input = Picture(1);
        for (int i = 0; i < 3; i++)
        {
            if (i == 2) value = 20;
            using var frame = new FrameHashContext(new(), cache: cache);
            using var scope = frame.Enter();
            using var result = Evaluate(graph, input);
            Assert.AreEqual(value, ((IPicture<byte>)result).r[0]);
        }
        Assert.AreEqual(3, sourceCalls);
        Assert.AreEqual(3, consumerCalls);
    }

    [TestMethod]
    public void ContentBindingsFollowUpstreamDeterminismWithoutEvaluation()
    {
        int calls = 0;
        var source = new Provider(null, EffectArgumentFieldType.IPicture, _ => { calls++; return Picture(1); });
        var consumer = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture,
            c => c.Input);
        consumer.SetMainInputSource(source.Id);
        consumer.SetFinalOutputSource(true);
        using var clip = new SolidColorClip
        {
            Id = Guid.NewGuid(), Name = "graph", Duration = 2, ExtraData = [],
            EffectProvidersInstances = [source, consumer],
        };
        Assert.IsTrue(FrameHashHelper.CanPersistClipHash([clip], clip));
        var first = FrameHashHelper.GetClipHash([clip], clip, 0);
        Assert.IsTrue(first.CanReuse);
        Assert.AreNotEqual(first.Hash, FrameHashHelper.GetClipHash([clip], clip, 1).Hash);
        var changing = new ChangingProvider(_ => { calls++; return Picture(2); });
        consumer.SetMainInputSource(changing.Id);
        clip.EffectProvidersInstances = [changing, consumer];
        Assert.IsFalse(FrameHashHelper.CanPersistClipHash([clip], clip));
        consumer.SetMainInputSource(Guid.NewGuid());
        Assert.IsFalse(FrameHashHelper.CanPersistClipHash([clip], clip));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void ProviderCyclesCannotProducePersistentHashes()
    {
        var first = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c => c.Input);
        var second = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c => c.Input);
        first.SetMainInputSource(second.Id);
        second.SetMainInputSource(first.Id);
        second.SetFinalOutputSource(true);
        using var clip = new SolidColorClip
        {
            Id = Guid.NewGuid(), Name = "cycle", Duration = 1, ExtraData = [],
            EffectProvidersInstances = [first, second],
        };
        Assert.IsFalse(FrameHashHelper.CanPersistClipHash([clip], clip));
    }

    [TestMethod]
    public void AudioBindingsUseSourceDeterminismAndMixConfiguration()
    {
        var provider = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture,
            c => c.Input, new EffectArgumentFieldDescriptor { Id = "Audio", FieldType = EffectArgumentFieldType.AudioSample });
        provider.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        provider.SetFieldBinding("Audio", ValueProviderFrameContext.BuiltInAudioSampleProviderId);
        provider.SetFinalOutputSource(true);
        using var clip = new SolidColorClip
        {
            Id = Guid.NewGuid(), Name = "audio", Duration = 1, ExtraData = [], EffectProvidersInstances = [provider],
        };
        using var track = new NormalSoundTrack
        {
            Id = "audio", Name = "audio", ExtraData = [], AudioSource = new AudioSource(true),
        };
        using var scope = EffectProcessing.OpenAudioFrame(new AudioSampleSource([clip], [track], 30));
        var first = FrameHashHelper.GetClipHash([clip], clip, 0);
        Assert.IsTrue(first.CanReuse);
        track.Volume = 0.5f;
        Assert.AreNotEqual(first.Hash, FrameHashHelper.GetClipHash([clip], clip, 0).Hash);
        track.AudioSource = new AudioSource(false);
        Assert.IsFalse(FrameHashHelper.CanPersistClipHash([clip], clip));
    }

    [TestMethod]
    public void DependencyCyclesAndCancellationDoNotPublishSnapshots()
    {
        using var context = new FrameHashContext(new());
        Assert.ThrowsExactly<InvalidOperationException>(() => context.ReadPicture("cycle",
            () => context.ReadPicture("cycle", () => Picture(1))));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var cancelled = new FrameHashContext(new(), cancellation.Token);
        Assert.ThrowsExactly<OperationCanceledException>(() => cancelled.ReadPicture("source", () => Picture(1)));
    }
}
