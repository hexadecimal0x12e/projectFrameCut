using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RPCProtocol;
using projectFrameCut.Render.Sources;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class ExternalMaskSourceTests
{
    [TestMethod]
    public void ReferencesRestoreMaskFieldsWithoutAConnectedProvider()
    {
        var descriptor = RenderRpcSerializer.Clone(new TestMaskSourceProvider().MaskSources[0]);
        descriptor.ClientId = Guid.NewGuid();
        var rpc = MaskSources.Create(RemoteRpcMaskSource.CreatePath(descriptor));
        Assert.IsInstanceOfType<RemoteRpcMaskSource>(rpc);
        Assert.AreEqual(EffectArgumentFieldType.Numeric, rpc.Fields.Single().FieldType);
        Assert.AreEqual(10f, rpc.ReadArgument("Amount", 100f));
        Assert.AreEqual(2f, rpc.ReadArgument("Amount", null));
        using var clip = new MaskClip
        {
            Id = Guid.NewGuid(), Name = "External mask", ExtraData = new() { [MaskClip.SourceTypeKey] = rpc.TypeName },
        };
        Assert.AreEqual(EffectArgumentFieldType.Numeric, clip.ArgumentFields["Source.Amount"].FieldType);
        var bound = (MaskClip)clip.HandleArguments(new Dictionary<string, object> { ["Source.Amount"] = 3f });
        Assert.AreEqual(3f, bound.SourceArguments["Amount"]);
        Assert.AreEqual(0, clip.SourceArguments.Count);
        var importId = Guid.NewGuid();
        var assembly = (ProjectExternalMaskSource)MaskSources.Create(ProjectExternalMaskSource.CreatePath(importId, descriptor));
        Assert.AreEqual(importId, assembly.Descriptor.ClientId);
        Assert.AreEqual("Amount", assembly.Fields.Single().Id);
        Assert.Throws<InvalidDataException>(() => ExternalMaskSourceReference.Decode(new ExternalMaskSourceReference().Encode()));
        var invalid = RenderRpcSerializer.Clone(descriptor);
        invalid.Fields.Add(RenderRpcSerializer.Clone(invalid.Fields[0]));
        Assert.Throws<InvalidDataException>(invalid.Validate);
    }

    [TestMethod]
    [DataRow(8, false)]
    [DataRow(16, false)]
    [DataRow(16, true)]
    public void AdapterPassesArgumentsScaleAlphaAndHdrWithoutChangingTheBackdrop(int bits, bool hdr)
    {
        var provider = new TestMaskSourceProvider();
        var descriptor = RenderRpcSerializer.Clone(provider.MaskSources[0]);
        descriptor.ClientId = Guid.NewGuid();
        var source = new RemoteRpcMaskSource(descriptor, provider.ProcessMaskAsync);
        using IPicture backdrop = hdr ? new HDRPicture16bpp(2, 1) { MaximumBrightness = 1400, Brightness = [2f, 3f] }
            : bits == 16 ? new Picture16bpp(2, 1) : new Picture8bpp(2, 1);
        if (backdrop is Picture8bpp p8)
        {
            p8.HasAlphaChannel = true;
            p8.a = [0.25f, 0.75f];
        }
        else if (backdrop is Picture16bpp p16)
        {
            p16.HasAlphaChannel = true;
            p16.a = [0.25f, 0.75f];
        }
        using var result = source.Process(backdrop, new Dictionary<string, object> { ["Amount"] = 4f }, 0.5f, CancellationToken.None);
        Assert.AreEqual(0.5f, provider.LastRequest!.Scale);
        Assert.AreEqual(4d, provider.LastRequest.Arguments["Amount"].NumberValue);
        Assert.AreEqual(0.25f, ((float[])result.GetSpecificChannel(IPicture.ChannelId.Alpha)!)[0]);
        if (result is IPicture<byte> r8) Assert.AreEqual((byte)4, r8.r[0]);
        else Assert.AreEqual((ushort)4, ((IPicture<ushort>)result).r[0]);
        Assert.AreEqual((byte)0, ExternalVideoFrameCodec.Encode(backdrop).Red[0]);
        if (hdr)
        {
            Assert.AreEqual(1400f, ((IHDRPicture<ushort>)result).MaximumBrightness);
            CollectionAssert.AreEqual(new[] { 2f, 3f }, ((IHDRPicture<ushort>)result).Brightness);
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => source.Process(backdrop, new Dictionary<string, object>(), 1, cancellation.Token));
    }

    [TestMethod]
    public void AdapterRejectsChangedDimensions()
    {
        var descriptor = RenderRpcSerializer.Clone(new TestMaskSourceProvider().MaskSources[0]);
        descriptor.ClientId = Guid.NewGuid();
        var source = new RemoteRpcMaskSource(descriptor, (_, _) => ValueTask.FromResult(new ExternalVideoFrame
        {
            Width = 1, Height = 1, BitsPerChannel = 8, Red = [0], Green = [0], Blue = [0],
        }));
        using var backdrop = new Picture8bpp(2, 1);
        Assert.Throws<InvalidDataException>(() => source.Process(backdrop, new Dictionary<string, object>(), 1, CancellationToken.None));
    }
}

internal sealed class TestMaskSourceProvider : IExternalMaskSourceProvider
{
    public ExternalMaskSourceProcessRequest? LastRequest { get; private set; }
    public bool Fail { get; set; }
    public IReadOnlyList<ExternalMaskSourceDescriptor> MaskSources { get; } = [new()
    {
        SourceId = "mask", Name = "Test mask", Fields = [new()
        {
            Id = "Amount", FieldType = (ulong)EffectArgumentFieldType.Numeric,
            DefaultValue = "2", MinimumValue = "0", MaximumValue = "10",
        }],
    }];

    public ValueTask<ExternalVideoFrame> ProcessMaskAsync(ExternalMaskSourceProcessRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        request.Validate();
        LastRequest = RenderRpcSerializer.Clone(request);
        if (Fail) throw new InvalidOperationException("Mask processing failed.");
        var frame = RenderRpcSerializer.Clone(request.Backdrop);
        frame.Red[0] = (byte)request.Arguments["Amount"].NumberValue;
        return ValueTask.FromResult(frame);
    }
}
