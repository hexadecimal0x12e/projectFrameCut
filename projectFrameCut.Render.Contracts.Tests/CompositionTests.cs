using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class CompositionTests
{
    [TestMethod]
    [DataRow("Add", 255)]
    [DataRow("Subtract", 0)]
    [DataRow("Multiply", 78)]
    [DataRow("Screen", 221)]
    [DataRow("Overlay", 156)]
    [DataRow("Darken", 100)]
    [DataRow("Lighten", 200)]
    [DataRow("Difference", 100)]
    public void OpaquePixelsApplyTheSelectedBlendMode(string mode, int expected)
    {
        IMixture mixture = mode switch
        {
            "Add" => new AddMixture(),
            "Subtract" => new SubtractMixture(),
            "Multiply" => new MultiplyMixture(),
            "Screen" => new ScreenMixture(),
            "Overlay" => new OverlayBlendMixture(),
            "Darken" => new DarkenMixture(),
            "Lighten" => new LightenMixture(),
            _ => new DifferenceMixture()
        };
        using var bottom = Picture8bpp.GenerateSolidColor(65, 1, 100, 100, 100, null);
        using var top = Picture8bpp.GenerateSolidColor(65, 1, 200, 200, 200, null);
        using var output = mixture.Mix(bottom, top, 8);
        CollectionAssert.AreEqual(Enumerable.Repeat((byte)expected, 65).ToArray(), ((IPicture<byte>)output).r);
        Assert.IsFalse(output.HasAlphaChannel);
    }

    [TestMethod]
    [DataRow(8)]
    [DataRow(16)]
    public void OversizedBaseAndNegativeOffsetsAreClippedToTheCanvas(int bits)
    {
        using var bottom = Picture8bpp.FromOwnedChannels(4, 3, Enumerable.Range(1, 12).Select(i => (byte)i).ToArray(), new byte[12], new byte[12]);
        using var top = Picture8bpp.GenerateSolidColor(2, 2, 100, 0, 0, null);
        using var output = new ClassicOverlayMixture().Mix(bottom, top, bits, -1, -1, 2, 2);
        if (bits == 8) CollectionAssert.AreEqual(new byte[] { 100, 2, 5, 6 }, ((IPicture<byte>)output).r);
        else CollectionAssert.AreEqual(new ushort[] { 25700, 514, 1285, 1542 }, ((IPicture<ushort>)output).r);
        Assert.IsFalse(output is IHDRPicture<ushort>);
    }

    [TestMethod]
    [DataRow(int.MinValue)]
    [DataRow(int.MaxValue)]
    public void OffCanvasCoordinatesDoNotOverflow(int offset)
    {
        using var bottom = Picture8bpp.GenerateSolidColor(2, 2, 75, 0, 0, null);
        using var top = Picture8bpp.GenerateSolidColor(2, 2, 200, 0, 0, null);
        using var output = new ClassicOverlayMixture().Mix(bottom, top, 8, offset, offset, 2, 2);
        CollectionAssert.AreEqual(new byte[] { 75, 75, 75, 75 }, ((IPicture<byte>)output).r);
    }

    [TestMethod]
    [DataRow(8, 8)]
    [DataRow(8, 16)]
    [DataRow(16, 8)]
    [DataRow(16, 16)]
    public void AlphaEdgesAndVectorTailsMatchAcrossPixelFormats(int inputBits, int outputBits)
    {
        Half[] alpha = Enumerable.Range(0, 67).Select(i => (i % 7) switch
        {
            0 => 0f, 1 => 0.05f, 2 => 0.5f, 3 => 1f, 4 => float.NaN, 5 => -1f, _ => float.PositiveInfinity
        }).Select(x => (Half)x).ToArray();
        using IPicture bottom = inputBits == 8 ? Picture8bpp.GenerateSolidColor(67, 1, 100, 100, 100, null)
            : Picture16bpp.GenerateSolidColor(67, 1, 25700, 25700, 25700, null);
        using IPicture top = inputBits == 8
            ? Picture8bpp.FromOwnedChannels(67, 1, Enumerable.Repeat((byte)200, 67).ToArray(), new byte[67], new byte[67], alpha)
            : Picture16bpp.FromOwnedChannels(67, 1, Enumerable.Repeat((ushort)51400, 67).ToArray(), new ushort[67], new ushort[67], alpha);
        using var output = new ClassicOverlayMixture().Mix(bottom, top, outputBits);
        for (int i = 0; i < 67; i++)
        {
            int expected = i % 7 == 2 ? 150 : i % 7 == 3 ? 200 : 100;
            if (outputBits == 8) Assert.AreEqual(expected, ((IPicture<byte>)output).r[i]);
            else Assert.AreEqual(expected * 257, ((IPicture<ushort>)output).r[i]);
            Assert.AreEqual(1f, outputBits == 8 ? (float)((IPicture<byte>)output).a![i] : (float)((IPicture<ushort>)output).a![i]);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void HdrBrightnessUsesTheComposedBrightnessRatherThanAlpha(bool inherited)
    {
        using var bottom = HDRPicture16bpp.GenerateSolidColor(65, 1, 10000, 0, 0, Half.One, (Half)0.8f, (Half)1000);
        using var top = HDRPicture16bpp.GenerateSolidColor(65, 1, 20000, 0, 0, (Half)0.5f, (Half)0.2f, (Half)1000);
        using var output = (inherited ? new InheritedOverlay() : new ClassicOverlayMixture()).Mix(bottom, top, 16);
        var hdr = (IHDRPicture<ushort>)output;
        foreach (float value in hdr.Brightness) Assert.AreEqual(0.5f, value, 0.00001f);
        Assert.AreEqual(1000f, (float)hdr.MaximumBrightness);
    }

    [TestMethod]
    public void OpaqueCoverSharesStorageAndRetainsIndependentLifetime()
    {
        using var bottom = Picture8bpp.FromOwnedChannels(2, 1, [10, 10], [0, 0], [0, 0]);
        using var top = Picture8bpp.FromOwnedChannels(2, 1, [200, 200], [0, 0], [0, 0]);
        using var output = new ClassicOverlayMixture().Mix(bottom, top, 8);
        Assert.AreNotSame(top, output);
        Assert.AreSame(top.ContentIdentity, ((ISharedPicture)output).ContentIdentity);
        ((IPicture<byte>)output).r[0] = 0;
        Assert.AreEqual((byte)200, new PictureReadChannels<byte>(top).Red[0]);
        top.Dispose();
        Assert.AreEqual((byte)200, ((IPicture<byte>)output).r[1]);
    }

    [TestMethod]
    public void InheritedBlendHooksReceiveBoundedBatchesWithoutDroppingPixels()
    {
        using var bottom = Picture8bpp.GenerateSolidColor(257, 257, 100, 100, 100, Half.One);
        using var top = Picture8bpp.GenerateSolidColor(257, 257, 200, 200, 200, (Half)0.5f);
        var mixture = new CountingOverlay();
        using var output = mixture.Mix(bottom, top, 8);
        Assert.AreEqual(3 * 257 * 257, mixture.ProcessedPixels);
        Assert.IsTrue(mixture.MaxBatchPixels <= 65536);
        Assert.IsTrue(((IPicture<byte>)output).r.All(v => v == 150));
    }

    private sealed class InheritedOverlay : ClassicOverlayMixture { }

    private sealed class CountingOverlay : ClassicOverlayMixture
    {
        public int ProcessedPixels { get; private set; }
        public int MaxBatchPixels { get; private set; }

        protected override (byte[] Color, Half[] Alpha) ApproximateOverlay8(float[] top, float[] bottom, Half[] topAlpha, Half[] bottomAlpha, int pixelCount)
        {
            Record(pixelCount);
            return base.ApproximateOverlay8(top, bottom, topAlpha, bottomAlpha, pixelCount);
        }

        protected override (byte[] Color, Half[] Alpha) Overlay8(float[] top, float[] bottom, Half[] topAlpha, Half[] bottomAlpha, int pixelCount)
        {
            Record(pixelCount);
            return base.Overlay8(top, bottom, topAlpha, bottomAlpha, pixelCount);
        }

        private void Record(int count)
        {
            ProcessedPixels += count;
            MaxBatchPixels = Math.Max(MaxBatchPixels, count);
        }
    }
}
