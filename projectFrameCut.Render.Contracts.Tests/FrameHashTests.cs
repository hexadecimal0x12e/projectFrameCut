using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Rendering;
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

    [TestMethod]
    public void CanonicalValuesIgnoreDictionaryInsertionOrder()
    {
        Assert.AreEqual(FrameHashHelper.HashValue(new Dictionary<string, object> { ["a"] = 1, ["b"] = 2 }),
            FrameHashHelper.HashValue(new Dictionary<string, object> { ["b"] = 2, ["a"] = 1 }));
        Assert.AreNotEqual(FrameHashHelper.HashValue((1, 2)), FrameHashHelper.HashValue((2, 1)));
    }

    [TestMethod]
    public void StaticMaskHashesNeverRenderOrBecomeReusable()
    {
        using var clip = new MaskClip { Id = Guid.NewGuid(), Name = "mask", Duration = 1 };
        int calls = 0;
        var options = new FrameHashOptions { Mode = FrameHashMode.Static };
        var first = FrameHashHelper.GetClipHash([clip], clip, 0, options, () => ++calls);
        var second = FrameHashHelper.GetClipHash([clip], clip, 0, options, () => ++calls);
        Assert.IsFalse(first.CanReuse);
        Assert.AreNotEqual(first.Hash, second.Hash);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void DynamicHashesUseThisInvocationsActualOutput()
    {
        using var clip = new SolidColorClip
        {
            Id = Guid.NewGuid(), Name = "changing", Duration = 1, ExtraData = [],
            EffectsInstances = [new JitterEffect()],
        };
        using var left = Picture(10);
        using var right = Picture(20);
        int calls = 0;
        var options = new FrameHashOptions();
        var first = FrameHashHelper.GetClipHash([clip], clip, 0, options, () => { calls++; return left; });
        var same = FrameHashHelper.GetClipHash([clip], clip, 0, options, () => { calls++; return left; });
        var changed = FrameHashHelper.GetClipHash([clip], clip, 0, options, () => { calls++; return right; });
        Assert.IsTrue(first.CanReuse);
        Assert.AreEqual(first.Hash, same.Hash);
        Assert.AreNotEqual(first.Hash, changed.Hash);
        Assert.AreEqual(3, calls);
    }

    [TestMethod]
    public void PictureHashesIncludeAlphaDimensionsAndHdrBrightness()
    {
        using var a = Picture8bpp.GenerateSolidColor(1, 2, 1, 2, 3, 1);
        using var b = Picture8bpp.GenerateSolidColor(2, 1, 1, 2, 3, 1);
        Assert.AreNotEqual(FrameHashHelper.HashPicture(a), FrameHashHelper.HashPicture(b));
        string opaque = FrameHashHelper.HashPicture(a);
        ((IPicture<byte>)a).a![0] = 0.5f;
        Assert.AreNotEqual(opaque, FrameHashHelper.HashPicture(a));
        using var hdr = HDRPicture16bpp.GenerateSolidColor(1, 1, 1, 2, 3, 1, 1);
        string original = FrameHashHelper.HashPicture(hdr);
        hdr.MaximumBrightness += 1;
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
    public void NonDeterministicDependenciesRunAgainWhileDeterministicConsumersReuseTheirOutput()
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
        Assert.AreEqual(2, consumerCalls);
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
