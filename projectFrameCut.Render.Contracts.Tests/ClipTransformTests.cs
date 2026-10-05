using System.Text.Json;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.Transform;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class ClipTransformTests
{
    private sealed class SelectableTransform : IOneInputSingleFrameTransform, IContinuousTransform
    {
        public string FromPlugin => "test";
        public string TypeName => "selectable";
        public string Name { get; init; } = "test";
        public TransformType TransformType => TransformType.ContinuousTransform;
        public TransformDefinition Definition { get; set; }
        public Guid BindedLeftClip { get; set; }
        public Guid BindedRightClip { get; set; }
        public uint Duration { get; set; }
        public IPicture GetFrame(IPicture input, double progress, int width, int height) => input.Clone();
        public IPicture GetFrame(IPicture left, IPicture right, double progress, int width, int height) => right.Clone();
    }

    private static TransformClipInfo Clip(uint start, uint duration, uint layer = 0) => new(Guid.NewGuid(), start, duration, layer, 0, new());

    private static TransformBinding Connect(TransformClipInfo left, TransformClipInfo right, uint duration)
    {
        var binding = new TransformBinding { Side = TransformSide.Right, InputMode = TransformInputMode.TwoInput,
            LeftClipId = left.Id, RightClipId = right.Id, Duration = duration };
        TransformBinding.Write(left.Metadata, TransformSide.Right, binding);
        return binding;
    }

    [TestMethod]
    public void ConnectedEdgesShareIdentityAndSplitOddDuration()
    {
        var left = Clip(0, 20);
        var right = Clip(20, 20);
        Connect(left, right, 5);
        var a = ClipTransforms.Resolve([left, right], left.Id, TransformSide.Right)!;
        var b = ClipTransforms.Resolve([left, right], right.Id, TransformSide.Left)!;
        Assert.AreEqual(a.Binding.Id, b.Binding.Id);
        Assert.AreEqual(18ul, a.Start);
        Assert.AreEqual(5u, a.Duration);
        Assert.IsTrue(a.Contains(22));
        Assert.IsFalse(a.Contains(23));
        Assert.IsNull(TransformBinding.Read(right.Metadata, TransformSide.Left));
        Assert.AreEqual(20u, left.Duration);
        Assert.AreEqual(20u, right.Start);
    }

    [TestMethod]
    public void DisconnectionRetainsOriginalBindingAndDoesNotAdoptNewNeighbor()
    {
        var left = Clip(0, 10);
        var right = Clip(10, 10);
        var binding = Connect(left, right, 4);
        var moved = right with { Start = 11 };
        var replacement = Clip(10, 10);
        Assert.IsNull(ClipTransforms.Resolve([left, moved, replacement], left.Id, TransformSide.Right));
        Assert.AreEqual(binding.Id, ClipTransforms.Find([left, moved], moved.Id, TransformSide.Left)!.Value.Binding.Id);
        Assert.IsNotNull(ClipTransforms.Resolve([left, right], left.Id, TransformSide.Right));
        Assert.IsNull(ClipTransforms.Resolve([left, right with { Layer = 1 }], left.Id, TransformSide.Right));
    }

    [TestMethod]
    public void ShortClipsClampDurationAndDoNotOverlapEdgeRanges()
    {
        var clip = Clip(0, 5);
        foreach (var side in Enum.GetValues<TransformSide>())
            TransformBinding.Write(clip.Metadata, side, new() { Side = side, InputMode = TransformInputMode.OneInput, LeftClipId = clip.Id, Duration = 20 });
        var left = ClipTransforms.Resolve([clip], clip.Id, TransformSide.Left)!;
        var right = ClipTransforms.Resolve([clip], clip.Id, TransformSide.Right)!;
        Assert.IsTrue(left.Start + left.Duration <= right.Start);
        var neighbor = Clip(5, 1);
        Connect(clip, neighbor, 100);
        var connected = ClipTransforms.Resolve([clip, neighbor], neighbor.Id, TransformSide.Left)!;
        Assert.IsTrue((connected.Duration + 1) / 2 <= neighbor.Duration);
    }

    [TestMethod]
    public void CopyKeepsIndependentSingleInputAndDropsExternalConnection()
    {
        var clip = Clip(0, 10);
        var next = Clip(10, 10);
        var single = new TransformBinding { Side = TransformSide.Left, LeftClipId = clip.Id, Duration = 3,
            TransformElement = JsonSerializer.SerializeToElement(new { FromPlugin = "plugin", TypeName = "test", Parameters = new { Strength = 2 } }) };
        TransformBinding.Write(clip.Metadata, TransformSide.Left, single);
        Connect(clip, next, 4);
        var copy = new Dictionary<string, object>(clip.Metadata);
        var id = Guid.NewGuid();
        TransformBinding.CopySingleInputs(copy, id);
        var copied = TransformBinding.Read(copy, TransformSide.Left)!;
        Assert.AreNotEqual(single.Id, copied.Id);
        Assert.AreEqual(id, copied.LeftClipId);
        Assert.AreEqual(2, copied.TransformElement.GetProperty("Parameters").GetProperty("Strength").GetInt32());
        Assert.IsNull(TransformBinding.Read(copy, TransformSide.Right));
        Assert.AreEqual(clip.Id, TransformBinding.Read(clip.Metadata, TransformSide.Left)!.LeftClipId);
    }

    [TestMethod]
    public void SingleInputFadeAcceptsMissingRightAndHonorsSide()
    {
        using var input = Picture8bpp.GenerateSolidColor(2, 2, 255, 0, 0, 1);
        var fade = new FadeTransform { Side = TransformSide.Left };
        using var entering = TransformProcessing.ProcessFrames(input, null, fade, TransformInputMode.OneInput, 0, 2, 2);
        Assert.AreEqual(0f, ((IPicture<byte>)entering).a![0]);
        fade.Side = TransformSide.Right;
        using var leaving = TransformProcessing.ProcessFrames(input, null, fade, TransformInputMode.OneInput, 0, 2, 2);
        Assert.AreEqual(1f, ((IPicture<byte>)leaving).a![0]);
        Assert.AreEqual(1f, input.a![0]);
        Assert.IsTrue(((ITransform)fade).Definition.HasFlag(TransformDefinition.SupportOneInput));
        Assert.IsFalse(((ITransform)fade).Definition.HasFlag(TransformDefinition.SupportTwoInput));
    }

    [TestMethod]
    public void ExplicitDefinitionControlsInputModeEvenWhenAllInterfacesArePresent()
    {
        using var left = Picture8bpp.GenerateSolidColor(1, 1, 255, 0, 0, 1);
        using var right = Picture8bpp.GenerateSolidColor(1, 1, 0, 255, 0, 1);
        var transform = new SelectableTransform { Definition = TransformDefinition.Clip | TransformDefinition.SupportOneInput | TransformDefinition.SupportTwoInput };
        using var single = TransformProcessing.ProcessFrames(left, null, transform, TransformInputMode.OneInput, 0.5, 1, 1);
        using var dual = TransformProcessing.ProcessFrames(left, right, transform, TransformInputMode.TwoInput, 0.5, 1, 1);
        Assert.AreEqual((byte)255, ((IPicture<byte>)single).r[0]);
        Assert.AreEqual((byte)255, ((IPicture<byte>)dual).g[0]);
        transform.Definition = TransformDefinition.Audio | TransformDefinition.SupportOneInput;
        Assert.ThrowsExactly<NotSupportedException>(() => TransformProcessing.ProcessFrames(left, null, transform, TransformInputMode.OneInput, 0.5, 1, 1));
        transform.Definition = TransformDefinition.Clip | TransformDefinition.SupportTwoInput;
        Assert.ThrowsExactly<NotSupportedException>(() => TransformProcessing.ProcessFrames(left, null, transform, TransformInputMode.OneInput, 0.5, 1, 1));
    }

    [TestMethod]
    public void CrossfadePreservesTransparentColorAndHdrBrightness()
    {
        using var left = HDRPicture16bpp.GenerateSolidColor(2, 2, ushort.MaxValue, 0, 0, 1, 0.5f, 1000);
        using var right = HDRPicture16bpp.GenerateSolidColor(2, 2, 0, 0, 0, 0, 0.1f, 1000);
        using var result = new CrossfadeTransform().GetFrame(left, right, 0.5, 2, 2);
        Assert.IsInstanceOfType<IHDRPicture<ushort>>(result);
        var hdr = (IHDRPicture<ushort>)result;
        Assert.AreEqual(ushort.MaxValue, hdr.r[0]);
        Assert.AreEqual(0.5f, hdr.a![0]);
        Assert.AreEqual(0.5f, hdr.Brightness[0]);
        Assert.AreEqual(1000f, hdr.MaximumBrightness);
    }

    [TestMethod]
    public void IsolationContractCarriesCapabilitiesSideParametersAndSerialization()
    {
        var source = new IsolationTransformState { Definition = (int)(TransformDefinition.Clip | TransformDefinition.SupportOneInput | TransformDefinition.SupportTwoInput),
            Side = (int)TransformSide.Left, SerializedTransform = "{\"TypeName\":\"test\"}",
            Parameters = new() { ["Strength"] = new() { Kind = IsolationValueKind.Double, NumberValue = 0.5 } },
            ParametersType = new() { ["Strength"] = "double" }, ParametersNeeded = ["Strength"] };
        var clone = RenderRpcSerializer.Clone(source);
        Assert.AreEqual(source.Definition, clone.Definition);
        Assert.AreEqual(source.Side, clone.Side);
        Assert.AreEqual(source.SerializedTransform, clone.SerializedTransform);
        Assert.AreEqual(0.5, clone.Parameters["Strength"].NumberValue);
        Assert.AreEqual("double", clone.ParametersType["Strength"]);
    }
}
