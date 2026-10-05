using projectFrameCut.Render.Contracts;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class ExternalVideoSourceContractTests
{
    [TestMethod]
    public void SourceReferenceRoundTripsWithoutCredentials()
    {
        var source = new ExternalVideoSourceReference
        {
            ClientId = Guid.NewGuid(),
            SourceId = "camera/main",
            DecoderName = "CameraDecoder",
            Metadata = new() { ["device"] = "front" },
            Descriptor = new() { Name = "Front camera", Width = 1920, Height = 1080, SupportsHdr = true, AllowCachingResult = false },
        };

        var encoded = source.Encode();
        var decoded = ExternalVideoSourceReference.Decode(encoded);

        Assert.AreEqual(source.ClientId, decoded.ClientId);
        Assert.AreEqual(source.SourceId, decoded.SourceId);
        Assert.AreEqual(source.DecoderName, decoded.DecoderName);
        Assert.AreEqual("front", decoded.Metadata["device"]);
        Assert.AreEqual(1920, decoded.Descriptor.Width);
        Assert.IsTrue(decoded.Descriptor.SupportsHdr);
        Assert.IsFalse(decoded.Descriptor.AllowCachingResult);
        Assert.IsFalse(encoded.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CacheCapabilityRoundTripsForExternalAndIsolatedSources(bool allowCaching)
    {
        Assert.AreEqual(allowCaching, RenderRpcSerializer.Clone(new ExternalVideoSourceDescriptor { AllowCachingResult = allowCaching }).AllowCachingResult);
        Assert.AreEqual(allowCaching, RenderRpcSerializer.Clone(new IsolationVideoSourceDescriptor { AllowCachingResult = allowCaching }).AllowCachingResult);
    }

    [TestMethod]
    public void LegacyDescriptorsAllowCachingWhenFieldIsMissing()
    {
        Assert.IsTrue(RenderRpcSerializer.Deserialize<ExternalVideoSourceDescriptor>([]).AllowCachingResult);
        Assert.IsTrue(RenderRpcSerializer.Deserialize<IsolationVideoSourceDescriptor>([]).AllowCachingResult);
    }

    [TestMethod]
    public void FrameValidationAcceptsEightAndSixteenBitHdrFrames()
    {
        new ExternalVideoFrame
        {
            Width = 2,
            Height = 2,
            BitsPerChannel = 8,
            Red = new byte[4],
            Green = new byte[4],
            Blue = new byte[4],
            Alpha = new byte[16],
        }.Validate();

        new ExternalVideoFrame
        {
            Width = 2,
            Height = 2,
            BitsPerChannel = 16,
            Red = new byte[8],
            Green = new byte[8],
            Blue = new byte[8],
            Alpha = new byte[16],
            Brightness = new byte[16],
            MaximumBrightness = 1000,
        }.Validate();
    }

    [TestMethod]
    public void FrameValidationRejectsInvalidPlaneLength()
    {
        var frame = new ExternalVideoFrame
        {
            Width = 2,
            Height = 2,
            BitsPerChannel = 16,
            Red = new byte[7],
            Green = new byte[8],
            Blue = new byte[8],
        };

        Assert.Throws<InvalidDataException>(frame.Validate);
    }
}
