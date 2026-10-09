using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using static projectFrameCut.Render.Contracts.Tests.DynamicEffectGraphTests;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class MergePictureChannelsTests
{
    [TestMethod]
    public void SplitAndMergeSdrPreservesRgbAndAlphaWithoutSharingBuffers()
    {
        using var source = Picture8bpp.GenerateSolidColor(2, 1, 51, 102, 153, 0.25f);
        source.r[1] = 255;
        source.a![1] = 0.75f;
        var channels = new SplitPictureChannelsEffect().ComputeOutputs(new() { Input = source });
        try
        {
            using var output = (IPicture)new MergePictureChannelsEffect().ComputeOutputs(new()
            {
                Parameters = channels.Where(p => p.Key != "Brightness").ToDictionary(p => p.Key, p => p.Value)
            })["Picture"]!;
            var picture = (IPicture<byte>)output;
            CollectionAssert.AreEqual(source.r, picture.r);
            CollectionAssert.AreEqual(source.g, picture.g);
            CollectionAssert.AreEqual(source.b, picture.b);
            CollectionAssert.AreEqual(source.a, picture.a);
            picture.r[0] = 0;
            picture.a![0] = 0;
            Assert.AreEqual((byte)51, ((IPicture<byte>)channels["R"]!).r[0]);
            Assert.AreEqual(0.25f, ((IPicture<byte>)channels["A"]!).a![0]);
            Assert.AreEqual((byte)51, source.r[0]);
        }
        finally
        {
            foreach (var picture in channels.Values.OfType<IPicture>()) picture.Dispose();
        }
    }

    [TestMethod]
    public void SplitAndMergeHdrThroughGraphPreservesEveryChannel()
    {
        using var source = HDRPicture16bpp.GenerateSolidColor(2, 1, 12000, 34000, 56000, 0.3f, 0.2f, 1400f);
        source.r[1] = 65535;
        source.Brightness[1] = 0.85f;
        var split = new SplitPictureChannelsEffectProvider();
        IEffectProvider merge = new InternalPluginBase().EffectProviderProvider["MergePictureChannels"]();
        split.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        foreach (string channel in new[] { "R", "G", "B", "A", "Brightness" })
            merge.SetFieldBinding(channel, EffectProviderOutputExtensions.CreateOutputSourceId(split.Id, channel));
        merge.SetFinalOutputSource(true, "Picture");
        Assert.IsInstanceOfType<IMultipleOutputEffectProvider>(merge);
        Assert.AreEqual(1, merge.GetOutputFields().Count);
        Assert.AreEqual(0, EffectBindingHelper.ValidateBindings(Providers(split, merge)).Count);
        using var graph = Build(Providers(split, merge));
        using var output = Evaluate(graph, source);
        Assert.IsInstanceOfType<IHDRPicture<ushort>>(output);
        var picture = (IHDRPicture<ushort>)output;
        CollectionAssert.AreEqual(source.r, picture.r);
        CollectionAssert.AreEqual(source.g, picture.g);
        CollectionAssert.AreEqual(source.b, picture.b);
        CollectionAssert.AreEqual(source.a, picture.a);
        CollectionAssert.AreEqual(source.Brightness, picture.Brightness);
        Assert.AreEqual(source.MaximumBrightness, picture.MaximumBrightness);
        Assert.IsFalse(source.Disposed);
    }

    [TestMethod]
    public void MixedBitDepthsPromoteRgbAndOptionalBrightnessProducesHdr()
    {
        using var r = Picture8bpp.GenerateSolidColor(1, 1, 128, 200, 200, null);
        using var g = Picture16bpp.GenerateSolidColor(1, 1, 100, 12345, 100, null);
        using var b = Picture8bpp.GenerateSolidColor(1, 1, 200, 200, 255, null);
        var fields = new Dictionary<string, object?> { ["R"] = r, ["G"] = g, ["B"] = b };
        var effect = new MergePictureChannelsEffect();
        using (var output = (IPicture)effect.ComputeOutputs(new() { Parameters = fields })["Picture"]!)
        {
            Assert.IsInstanceOfType<Picture16bpp>(output);
            Assert.IsFalse(output is IHDRPicture<ushort>);
            Assert.AreEqual((ushort)(128 * 257), ((IPicture<ushort>)output).r[0]);
            Assert.AreEqual((ushort)12345, ((IPicture<ushort>)output).g[0]);
            Assert.AreEqual(ushort.MaxValue, ((IPicture<ushort>)output).b[0]);
            Assert.IsFalse(output.HasAlphaChannel);
        }
        using var brightness = Picture8bpp.GenerateSolidColor(1, 1, 128, 128, 128, null);
        fields["Brightness"] = brightness;
        using var hdr = (HDRPicture16bpp)effect.ComputeOutputs(new() { Parameters = fields })["Picture"]!;
        Assert.AreEqual(128f / 255, hdr.Brightness[0], 0.00001f);
        Assert.AreEqual(203f, hdr.MaximumBrightness);
    }

    [TestMethod]
    public void MissingRgbMismatchedDimensionsAndCancellationAreRejected()
    {
        using var picture = Picture(10);
        using var otherSize = new Picture8bpp(2, 1);
        var effect = new MergePictureChannelsEffect();
        var fields = new Dictionary<string, object?> { ["R"] = picture, ["G"] = picture };
        Assert.ThrowsExactly<ArgumentException>(() => effect.ComputeOutputs(new() { Parameters = fields }));
        fields["B"] = otherSize;
        Assert.ThrowsExactly<ArgumentException>(() => effect.ComputeOutputs(new() { Parameters = fields }));
        fields["B"] = picture;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => effect.ComputeOutputs(new()
        {
            Parameters = fields, CancellationToken = cancellation.Token
        }));
        Assert.IsFalse(picture.Disposed);
    }
}
