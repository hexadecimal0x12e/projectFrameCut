using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.Transform;
using projectFrameCut.Shared;
using System.Collections.Concurrent;
using System.Text.Json;
using static projectFrameCut.Render.Contracts.Tests.DynamicEffectGraphTests;
using static projectFrameCut.Render.Contracts.Tests.MultipleOutputEffectTests;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class ClipValuePassThroughTests
{
    private static SolidColorClip Clip(uint layer, params IEffectProvider[] providers)
    {
        var clip = new SolidColorClip
        {
            Id = Guid.NewGuid(), Name = $"layer {layer}", LayerIndex = layer, Duration = 10, ExtraData = [],
            TargetWidth = 2, TargetHeight = 1, OutputWidth = 2, OutputHeight = 1, A = 1,
            EffectProviders = providers.Select(EffectBindingHelper.SerializeProvider).ToArray(),
        };
        clip.EffectProvidersInstances = providers;
        clip.EffectsInstances = EffectBindingHelper.RebuildAllEffects(providers.ToDictionary(p => p.Id), null)!.Values.ToArray();
        return clip;
    }

    private static ClipValueExportProvider Output(IEffectProvider source, EffectArgumentFieldType type, string? port = null)
    {
        var output = new ClipValueExportProvider { ValueType = type };
        output.SetMainInputSource(port is null ? source.Id.ToString() : EffectProviderOutputExtensions.CreateOutputSourceId(source.Id, port));
        return output;
    }

    private static ClipValueImportProvider Input(IClip source, ClipValueExportProvider output) => new()
    {
        SourceClipId = source.Id, SourceEndpointId = output.Id, ValueType = output.ValueType,
    };

    private static Provider Consumer(IEffectProvider input, Action<EffectExecutionContext> observe)
    {
        var consumer = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c =>
        {
            observe(c);
            return c.Input;
        }, Port("Amount", input.OutField.FieldType));
        consumer.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        consumer.SetFieldBinding("Amount", input.Id.ToString());
        consumer.SetFinalOutputSource(true);
        return consumer;
    }

    private static void Render(IClip[] clips, uint frame, IReadOnlyList<IClip>? allClips = null)
    {
        var frames = Timeline.GetFramesInOneFrame(clips, frame, 2, 1, 8, 2, 1, initializeClips: false).ToArray();
        try
        {
            using var picture = Timeline.MixtureLayers(frames, frame, 2, 1, 8,
                projectRelativeWidth: 2, projectRelativeHeight: 1, transparentBackground: true,
                disposeIntermediateFrames: true, allClips: allClips ?? clips);
        }
        finally
        {
            foreach (var item in frames) item.Clip.Dispose(true);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SameClipPictureParametersReadNativeEndpoints(bool alreadyPublished)
    {
        int calls = 0;
        var value = new Provider(null, EffectArgumentFieldType.Numeric, c =>
        {
            calls++;
            return (float)c.FrameIndex;
        });
        value.SetPipeline(EffectPipeline.NativeContent);
        var output = Output(value, EffectArgumentFieldType.Numeric);
        output.SetPipeline(EffectPipeline.NativeContent);
        var id = Guid.NewGuid();
        var input = new ClipValueImportProvider
        {
            SourceClipId = id, SourceEndpointId = output.Id, ValueType = output.ValueType,
        };
        float actual = 0;
        IEffectProvider[] providers = [value, output, input, Consumer(input, c => actual = (float)c.Parameters["Amount"]!)];
        using var clip = new VectorCanvasClip
        {
            Id = id, Name = "native endpoint", Duration = 10, TargetWidth = 2, TargetHeight = 1,
            EffectProvidersInstances = providers,
            EffectProviders = providers.Select(EffectBindingHelper.SerializeProvider).ToArray(),
            EffectsInstances = EffectBindingHelper.RebuildAllEffects(providers.ToDictionary(p => p.Id), null)!.Values.ToArray(),
        };
        using var frame = ValueProviderFrameContext.PushFrame(2, 0.2f);
        using var endpoints = new ClipValueFrameContext([clip], 2,
            c => VectorPictureEffectProcessing.Process(c, new VectorPicture(), 2, width: 2, height: 1),
            DynamicEffectGraph.CopyPicture);
        if (alreadyPublished) VectorPictureEffectProcessing.Process(clip, new VectorPicture(), 2, width: 2, height: 1);
        Assert.AreEqual(0, ClipValuePassThrough.Validate(clip, [clip]).Count);
        using var raster = Picture8bpp.GenerateSolidColor(2, 1, 1, 2, 3, 1);
        using var result = clip.EffectsInstances!.OfType<DynamicEffectGraph>().Single()
            .Evaluate(raster, clip, 2, 2, 1, 2, 1, new(0, 0, 2, 1, false)).Picture;
        Assert.AreEqual(2f, actual);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    [DataRow(EffectPipeline.Picture, EffectPipeline.Picture, EffectArgumentFieldType.Numeric)]
    [DataRow(EffectPipeline.NativeContent, EffectPipeline.NativeContent, EffectArgumentFieldType.Numeric)]
    [DataRow(EffectPipeline.Picture, EffectPipeline.NativeContent, EffectArgumentFieldType.Numeric)]
    [DataRow(EffectPipeline.NativeContent, EffectPipeline.Picture, EffectArgumentFieldType.VectorPicture)]
    public void SameClipEndpointsRejectInvalidStageDirectionsAndContent(EffectPipeline sourcePipeline,
        EffectPipeline targetPipeline, EffectArgumentFieldType type)
    {
        var output = new ClipValueExportProvider { ValueType = type };
        output.SetPipeline(sourcePipeline);
        output.SetMainInputSource(type.IsContent() ? IEffectProvider.InputAnchorGUID.ToString() : ValueProviderFrameContext.BuiltInFrameProviderId);
        using var clip = Clip(1, output);
        var input = Input(clip, output);
        input.SetPipeline(targetPipeline);
        clip.EffectProvidersInstances = [output, input];
        using var endpoints = new ClipValueFrameContext([clip], 2, _ => Assert.Fail(), DynamicEffectGraph.CopyPicture);
        Assert.ThrowsExactly<ClipValueBindingException>(() => endpoints.Read(clip, 2, input));
        Assert.IsTrue(ClipValuePassThrough.Validate(clip, [clip]).Any(d => d.Code == "InvalidClipEndpoint"));
    }

    [TestMethod]
    public void ExportOnlyBranchesAndNamedOutputsAreComputedOnceForSeveralConsumers()
    {
        int calls = 0;
        var analysis = new MultipleOutputProvider(EffectArgumentFieldType.IPicture,
            [Port("A", EffectArgumentFieldType.Integer), Port("B", EffectArgumentFieldType.Integer)], c =>
            {
                Interlocked.Increment(ref calls);
                return new Dictionary<string, object?> { ["A"] = 20 + (int)c.FrameIndex, ["B"] = 30 + (int)c.FrameIndex };
            });
        analysis.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        var a = Output(analysis, EffectArgumentFieldType.Integer, "A");
        var b = Output(analysis, EffectArgumentFieldType.Integer, "B");
        using var source = Clip(3, analysis, a, b);
        var first = Input(source, a);
        var second = Input(source, b);
        int firstValue = 0, secondValue = 0;
        using var left = Clip(2, first, Consumer(first, c => firstValue = (int)c.Parameters["Amount"]!));
        using var right = Clip(1, second, Consumer(second, c => secondValue = (int)c.Parameters["Amount"]!));
        Render([source, left, right], 2);
        Assert.AreEqual(22, firstValue);
        Assert.AreEqual(32, secondValue);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void SingleClipPreviewResolvesTransitiveSourcesWithoutShowingThem()
    {
        int calls = 0;
        var constant = new Provider(null, EffectArgumentFieldType.Integer, _ => { calls++; return 12; });
        var firstOutput = Output(constant, EffectArgumentFieldType.Integer);
        using var first = Clip(3, constant, firstOutput);
        var middleInput = Input(first, firstOutput);
        var middleOutput = Output(middleInput, EffectArgumentFieldType.Integer);
        using var middle = Clip(2, middleInput, middleOutput);
        var lastInput = Input(middle, middleOutput);
        int actual = 0;
        using var last = Clip(1, lastInput, Consumer(lastInput, c => actual = (int)c.Parameters["Amount"]!));
        Render([last], 1, [first, middle, last]);
        Assert.AreEqual(12, actual);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void PictureConsumersCannotModifyAnotherConsumersSnapshot()
    {
        var output = new ClipValueExportProvider { ValueType = EffectArgumentFieldType.IPicture };
        output.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        using var source = Clip(3, output);
        var input = Input(source, output);
        byte actual = 0;
        var consumer = Consumer(input, c =>
        {
            var picture = (IPicture<byte>)c.Parameters["Amount"]!;
            actual = picture.r[0];
            picture.r[0] = 0;
        });
        using var target = Clip(1, input, consumer);
        using var picture = Picture8bpp.GenerateSolidColor(2, 1, 52, 0, 0, 0.4f);
        using var context = new ClipValueFrameContext([source, target], 1, _ => Assert.Fail("Already published."), DynamicEffectGraph.CopyPicture);
        context.Publish(source, 1, output.Id, picture);
        var graph = target.EffectsInstances!.OfType<DynamicEffectGraph>().Single();
        using var rendered = graph.Evaluate(picture, target, 1, 2, 1, 2, 1, new(0, 0, 2, 1, false)).Picture;
        Assert.AreEqual((byte)52, actual);
        Assert.AreEqual((byte)52, picture.r[0]);
        Assert.AreEqual((byte)52, ((IPicture<byte>)context.Read(target, 1, input)).r[0]);
    }

    [TestMethod]
    public void HdrEndpointSnapshotsSurviveProducerDisposalAndAreReleasedWithTheFrame()
    {
        var output = new ClipValueExportProvider { ValueType = EffectArgumentFieldType.IPicture };
        output.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        using var source = Clip(2, output);
        var input = Input(source, output);
        using var target = Clip(1, input);
        var picture = HDRPicture16bpp.GenerateSolidColor(2, 1, 12000, 34000, 56000, 0.3f, 0.2f, 1400f);
        IHDRPicture<ushort> snapshot;
        using (var context = new ClipValueFrameContext([source, target], 0, _ => Assert.Fail(), DynamicEffectGraph.CopyPicture))
        {
            context.Publish(source, 0, output.Id, picture);
            picture.Dispose();
            snapshot = (IHDRPicture<ushort>)context.Read(target, 0, input);
            Assert.AreEqual((ushort)12000, snapshot.r[0]);
            Assert.AreEqual(0.3f, snapshot.a![0]);
            Assert.AreEqual(0.2f, snapshot.Brightness[0]);
            Assert.AreEqual(1400f, snapshot.MaximumBrightness);
            Assert.IsFalse(snapshot.Disposed);
        }
        Assert.IsTrue(snapshot.Disposed);
        Assert.IsNull(ClipValueFrameContext.Current);
    }

    [TestMethod]
    public void UnavailableEndpointsThrowInsteadOfUsingStaticFallbacks()
    {
        var constant = new Provider(null, EffectArgumentFieldType.Integer, _ => 12);
        var output = Output(constant, EffectArgumentFieldType.Integer);
        using var source = Clip(2, constant, output);
        var input = Input(source, output);
        bool observed = false;
        using var target = Clip(1, input, Consumer(input, _ => observed = true));
        output.Enabled = false;
        Assert.ThrowsExactly<ClipValueBindingException>(() => Render([target], 0, [source, target]));
        Assert.IsFalse(observed);
        output.Enabled = true;
        output.ValueType = EffectArgumentFieldType.Numeric;
        Assert.ThrowsExactly<ClipValueBindingException>(() => Render([target], 0, [source, target]));
        source.EffectProvidersInstances = [];
        using var context = new ClipValueFrameContext([source, target], 0, _ => Assert.Fail(), DynamicEffectGraph.CopyPicture);
        Assert.ThrowsExactly<ClipValueBindingException>(() => context.Read(target, 0, input));
    }

    [TestMethod]
    public void ReverseLayerReferencesAndClampedFramePublicationAreRejected()
    {
        var output = new ClipValueExportProvider { ValueType = EffectArgumentFieldType.Integer };
        output.SetMainInputSource(ValueProviderFrameContext.BuiltInFrameProviderId);
        using var source = Clip(1, output);
        var input = Input(source, output);
        using var target = Clip(2, input);
        using var context = new ClipValueFrameContext([source, target], 1, _ => Assert.Fail(), DynamicEffectGraph.CopyPicture);
        context.Publish(source, 0, output.Id, 25);
        Assert.ThrowsExactly<ClipValueBindingException>(() => context.Read(target, 1, input));
        Assert.IsTrue(ClipValuePassThrough.Validate(target, [source, target]).Any(d => d.Code == "InvalidClipEndpoint"));
    }

    [TestMethod]
    public void MissingProducerValuesCannotBecomeDefaultEndpointValues()
    {
        var analysis = new MultipleOutputProvider(EffectArgumentFieldType.IPicture,
            [Port("Value", EffectArgumentFieldType.Integer)], _ => new Dictionary<string, object?> { ["Value"] = null });
        analysis.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        var output = Output(analysis, EffectArgumentFieldType.Integer, "Value");
        using var source = Clip(2, analysis, output);
        var input = Input(source, output);
        using var target = Clip(1, input, Consumer(input, _ => Assert.Fail()));
        Assert.ThrowsExactly<ClipValueBindingException>(() => Render([source, target], 0));
    }

    [TestMethod]
    public void LazySourceEvaluationIsReusedWhenTheSourceIsRenderedLater()
    {
        int calls = 0;
        var value = new Provider(null, EffectArgumentFieldType.Integer, _ => { calls++; return 23; });
        var output = Output(value, EffectArgumentFieldType.Integer);
        using var source = Clip(2, value, output);
        var input = Input(source, output);
        int actual = 0;
        using var target = Clip(1, input, Consumer(input, c => actual = (int)c.Parameters["Amount"]!));
        using var scope = ClipValuePassThrough.Open([source, target], 0, 2, 1, 2, 1, 8, CancellationToken.None);
        Render([target], 0, [source, target]);
        Render([source], 0, [source, target]);
        Assert.AreEqual(23, actual);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void TransitionNeighborEvaluationCannotPublishTheSourcesEndpoint()
    {
        int calls = 0;
        var value = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.Integer, c =>
        {
            calls++;
            return (int)((IPicture<byte>)c.Input!).r[0];
        });
        value.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        var output = Output(value, EffectArgumentFieldType.Integer);
        using var source = Clip(2, value, output);
        var input = Input(source, output);
        using var target = Clip(1, input);
        var graph = source.EffectsInstances!.OfType<DynamicEffectGraph>().Single();
        using var proper = Picture8bpp.GenerateSolidColor(2, 1, 20, 0, 0, 1);
        using var neighbor = Picture8bpp.GenerateSolidColor(2, 1, 90, 0, 0, 1);
        using var context = new ClipValueFrameContext([source, target], 0, _ =>
        {
            using var owner = ClipValueFrameContext.Current!.EnterClip(source.Id, independent: true);
            using var result = graph.Evaluate(proper, source, 0, 2, 1, 2, 1, new(0, 0, 2, 1, false)).Picture;
        }, DynamicEffectGraph.CopyPicture);
        using (context.EnterClip(target.Id))
        using (context.EnterTransformInput())
        using (var result = graph.Evaluate(neighbor, source, 0, 2, 1, 2, 1, new(0, 0, 2, 1, false)).Picture) { }
        Assert.AreEqual(20, context.Read(target, 0, input));
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public void InactiveDisconnectedAndSamePositionSourcesAreRejected()
    {
        var output = new ClipValueExportProvider { ValueType = EffectArgumentFieldType.Integer };
        using var source = Clip(2, output);
        var input = Input(source, output);
        using var target = Clip(1, input);
        using var samePosition = Clip(2, input);
        using (var context = new ClipValueFrameContext([source, target, samePosition], 0, _ => Assert.Fail(), DynamicEffectGraph.CopyPicture))
        {
            Assert.ThrowsExactly<ClipValueBindingException>(() => context.Read(target, 0, input));
            output.SetMainInputSource(ValueProviderFrameContext.BuiltInFrameProviderId);
            Assert.ThrowsExactly<ClipValueBindingException>(() => context.Read(samePosition, 0, input));
        }
        using (var context = new ClipValueFrameContext([source, target], 10, _ => Assert.Fail(), DynamicEffectGraph.CopyPicture))
            Assert.ThrowsExactly<ClipValueBindingException>(() => context.Read(target, 10, input));
        Assert.IsNull(ClipValueFrameContext.Current);
    }

    [TestMethod]
    public void TransitionFailuresKeepEndpointBindingErrors()
    {
        var output = new ClipValueExportProvider { ValueType = EffectArgumentFieldType.Integer, Enabled = false };
        output.SetMainInputSource(ValueProviderFrameContext.BuiltInFrameProviderId);
        using var source = Clip(2, output);
        var input = Input(source, output);
        var fade = new FadeTransformProvider();
        TransformProcessing.Configure(fade, TransformSide.Left, TransformInputMode.OneInput, 2, Guid.Empty, TransformRenderOrder.AfterEffects);
        using var target = Clip(1, input, Consumer(input, _ => Assert.Fail()), fade);
        using var scope = ClipValuePassThrough.Open([source, target], 0, 2, 1, 2, 1, 8, CancellationToken.None);
        Assert.ThrowsExactly<ClipValueBindingException>(() => Render([target], 0, [source, target]));
    }

    [TestMethod]
    public void CancelledExportComputationsReleaseTheirPictures()
    {
        using var cancellation = new CancellationTokenSource();
        IPicture? produced = null;
        var value = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, _ =>
        {
            produced = Picture8bpp.GenerateSolidColor(2, 1, 35, 0, 0, 0.2f);
            cancellation.Cancel();
            return produced;
        });
        value.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        var output = Output(value, EffectArgumentFieldType.IPicture);
        using var source = Clip(2, value, output);
        using var picture = Picture8bpp.GenerateSolidColor(2, 1, 0, 0, 0, 1);
        using (var context = new ClipValueFrameContext([source], 0, _ => Assert.Fail(), DynamicEffectGraph.CopyPicture))
        {
            var graph = source.EffectsInstances!.OfType<DynamicEffectGraph>().Single();
            Assert.ThrowsExactly<OperationCanceledException>(() => graph.Evaluate(picture, source, 0, 2, 1, 2, 1,
                new(0, 0, 2, 1, false), cancellationToken: cancellation.Token));
            Assert.IsNotNull(produced);
            Assert.IsTrue(produced.Disposed);
        }
        Assert.IsNull(ClipValueFrameContext.Current);
    }

    [TestMethod]
    public void ParallelFramesKeepTheirOwnEndpointValues()
    {
        var value = new Provider(null, EffectArgumentFieldType.Integer, c => (int)c.FrameIndex);
        var output = Output(value, EffectArgumentFieldType.Integer);
        using var source = Clip(2, value, output);
        var input = Input(source, output);
        var values = new ConcurrentDictionary<uint, int>();
        using var target = Clip(1, input, Consumer(input, c => values[c.FrameIndex] = (int)c.Parameters["Amount"]!));
        Parallel.For(0, 8, frame => Render([source, target], (uint)frame));
        Assert.AreEqual(8, values.Count);
        foreach (var (frame, actual) in values) Assert.AreEqual((int)frame, actual);
    }

    [TestMethod]
    public void EndpointMetadataRoundTripsAndSourcesParticipateInPreviewHashes()
    {
        var output = new ClipValueExportProvider { ValueType = EffectArgumentFieldType.Integer, Name = "amplitude" };
        output.SetMainInputSource(ValueProviderFrameContext.BuiltInFrameProviderId);
        using var source = Clip(2, output);
        var input = Input(source, output);
        using var target = Clip(1, input);
        var dto = JsonSerializer.SerializeToElement(EffectBindingHelper.SerializeProvider(input)).Deserialize<EffectProviderJSONStructure>()!;
        var restored = new ClipValueImportProvider { Id = dto.Id, MetaData = dto.MetaData!, AnchorsBindingState = dto.AnchorsBindingState };
        Assert.AreEqual(source.Id, restored.SourceClipId);
        Assert.AreEqual(output.Id, restored.SourceEndpointId);
        Assert.AreEqual(EffectArgumentFieldType.Integer, restored.ValueType);
        var previous = Timeline.GetClipFrameHash([source, target], target, 1);
        source.ExtraData["changed"] = true;
        Assert.AreNotEqual(previous, Timeline.GetClipFrameHash([source, target], target, 1));
        output.Name = "renamed";
        Assert.AreEqual(output.Id, restored.SourceEndpointId);
    }

    [TestMethod]
    public void EndpointInputsCannotBeInlinedIntoClipArguments()
    {
        var input = new ClipValueImportProvider { ValueType = EffectArgumentFieldType.Integer };
        using var clip = Clip(1);
        var arguments = new ClipArgumentProvider();
        arguments.Attach(clip);
        arguments.SetFieldBinding(nameof(IClip.TargetX), input.Id.ToString());
        var providers = new Dictionary<Guid, IEffectProvider> { [input.Id] = input, [arguments.Id] = arguments };
        Assert.IsFalse(DynamicEffectBindings.CanInlineValue(input, providers));
        Assert.IsTrue(EffectBindingHelper.ValidateBindings(providers).Any(d => d.Code == "CrossStageBinding"));
    }
}
