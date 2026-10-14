using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.PluginIsolation;
using projectFrameCut.Render.RPCProtocol;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System.Text.Json;
using static projectFrameCut.Render.Contracts.Tests.DynamicEffectGraphTests;
using static projectFrameCut.Render.Contracts.Tests.MultipleOutputEffectTests;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
[DoNotParallelize]
public sealed class NativeEffectGraphTests
{
    private sealed class Element : VectorCanvasElement
    {
        public override VectorSegment[] Draw() => [];
    }

    private static VectorPicture Vector(float x = 3) => new() { Elements = [new Element { RelativeX = x }] };
    private static T Native<T>(T provider) where T : IEffectProvider
    {
        provider.SetPipeline(EffectPipeline.NativeContent);
        return provider;
    }

    private static VectorContentClip Clip(uint layer, params IEffectProvider[] providers) => new()
    {
        Id = Guid.NewGuid(), Name = "native graph", LayerIndex = layer, StartFrame = 100, Duration = 10,
        TargetWidth = 16, TargetHeight = 8, ExtraData = [],
        EffectProvidersInstances = providers,
        EffectProviders = providers.Select(EffectBindingHelper.SerializeProvider).ToArray(),
        EffectsInstances = EffectBindingHelper.RebuildAllEffects(providers.ToDictionary(p => p.Id), null)!.Values.ToArray(),
    };

    [TestMethod]
    public void NamedVectorAndScalarOutputsShareOneComputationAndDoNotMutateTheSource()
    {
        int calls = 0;
        var multiple = Native(new MultipleOutputProvider(EffectArgumentFieldType.VectorPicture,
            [Port("Vector", EffectArgumentFieldType.VectorPicture), Port("Amount", EffectArgumentFieldType.Numeric)], c =>
            {
                calls++;
                return new Dictionary<string, object?> { ["Vector"] = c.Input, ["Amount"] = 7f };
            }));
        multiple.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        var consumer = Native(new Provider(EffectArgumentFieldType.Any, EffectArgumentFieldType.VectorPicture, c =>
        {
            Assert.AreEqual(102u, c.FrameIndex);
            Assert.AreEqual(16, c.TargetWidth);
            var vector = (VectorPicture)c.Input!;
            vector.Elements[0].RelativeX = Convert.ToSingle(c.Parameters["Amount"]) + c.FrameIndex;
            return vector;
        }, Port("Amount", EffectArgumentFieldType.Numeric))
        {
            TypeResolver = (_, input, c) => input ? c.InputTypes.GetValueOrDefault(EffectProviderAnchorExtensions.InputKey,
                EffectArgumentFieldType.Any) : EffectArgumentFieldType.VectorPicture,
        });
        consumer.SetMainInputSource(EffectProviderOutputExtensions.CreateOutputSourceId(multiple.Id, "Vector"));
        consumer.SetFieldBinding("Amount", EffectProviderOutputExtensions.CreateOutputSourceId(multiple.Id, "Amount"));
        consumer.SetFinalOutputSource(true);
        using var clip = Clip(0, multiple, consumer);
        using var frame = ValueProviderFrameContext.PushFrame(102, 0.2f);
        var source = Vector();
        var output = VectorPictureEffectProcessing.Process(clip, source, 2, width: 16, height: 8);
        Assert.AreEqual(1, calls);
        Assert.AreEqual(109f, output.Elements[0].RelativeX);
        Assert.AreEqual(3f, source.Elements[0].RelativeX);
    }

    [TestMethod]
    public void NativeConditionalOnlyEvaluatesTheSelectedVectorBranch()
    {
        var unused = Native(new Provider(null, EffectArgumentFieldType.VectorPicture,
            _ => throw new InvalidOperationException("The unused branch must not run.")));
        var other = Native(new Provider(null, EffectArgumentFieldType.VectorPicture, _ => Vector()));
        var equal = Native(new EqualityComparisonNodeEffectProvider());
        equal.SetFieldBinding("A", IEffectProvider.InputAnchorGUID.ToString());
        equal.SetFieldBinding("B", other.Id.ToString());
        var condition = Native(new IfConditionNodeEffectProvider());
        condition.SetFieldBinding(IfConditionNodeEffectProvider.ConditionKey, equal.Id.ToString());
        condition.SetFieldBinding("TrueValue", IEffectProvider.InputAnchorGUID.ToString());
        condition.SetFieldBinding("FalseValue", unused.Id.ToString());
        condition.SetFinalOutputSource(true);
        using var clip = Clip(0, unused, other, equal, condition);
        var source = Vector();
        var output = VectorPictureEffectProcessing.Process(clip, source, 2, width: 16, height: 8);
        Assert.AreEqual(3f, output.Elements[0].RelativeX);
        Assert.AreNotSame(source, output);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void NativeConditionComputeShortCircuitsAndPreservesVectorIsolation(bool condition)
    {
        var gate = Native(new ConditionComputeNodeEffectProvider
        {
            Fields = new() { ["Condition"] = new StaticEffectArgumentField(condition, EffectArgumentFieldType.Boolean) },
        });
        gate.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        var expensive = Native(new Provider(null, EffectArgumentFieldType.Integer,
            _ => throw new AssertFailedException("The inactive vector route's parameter must not run.")));
        Provider Route(bool branch)
        {
            var provider = Native(new Provider(EffectArgumentFieldType.Any, EffectArgumentFieldType.VectorPicture, c =>
            {
                Assert.AreEqual(condition, branch);
                var vector = (VectorPicture)c.Input!;
                vector.Elements[0].RelativeX = 8;
                return vector;
            }, Port("Amount", EffectArgumentFieldType.Integer)));
            provider.SetMainInputSource(EffectProviderOutputExtensions.CreateOutputSourceId(gate.Id, branch ? "True" : "False"));
            if (branch != condition) provider.SetFieldBinding("Amount", expensive.Id.ToString());
            return provider;
        }
        var yes = Route(true);
        var no = Route(false);
        var join = Native(new IsUpstreamAvailableOrDefaultEffectProvider());
        join.SetMainInputSource(yes.Id);
        join.SetFieldBinding("DefaultValue", no.Id.ToString());
        join.SetFinalOutputSource(true);
        using var clip = Clip(0, gate, expensive, yes, no, join);
        var source = Vector();
        var output = VectorPictureEffectProcessing.Process(clip, source, 2, width: 16, height: 8);
        Assert.AreEqual(8f, output.Elements[0].RelativeX);
        Assert.AreEqual(3f, source.Elements[0].RelativeX);
        Assert.AreNotSame(source, output);
    }

    [TestMethod]
    public void NativeEndpointImportsAnIndependentSnapshotAndRejectsChangedPipeline()
    {
        var export = Native(new ClipValueExportProvider { ValueType = EffectArgumentFieldType.VectorPicture });
        export.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        using var source = Clip(0, export);
        var import = Native(new ClipValueImportProvider
        {
            SourceClipId = source.Id, SourceEndpointId = export.Id, ValueType = EffectArgumentFieldType.VectorPicture,
        });
        import.SetFinalOutputSource(true);
        using var consumer = Clip(1, import);
        var original = Vector();
        int calls = 0;
        using var endpoints = new ClipValueFrameContext([source, consumer], 102, c =>
        {
            calls++;
            VectorPictureEffectProcessing.Process(c, original, 2, width: 16, height: 8);
        }, DynamicEffectGraph.CopyPicture);
        var output = VectorPictureEffectProcessing.Process(consumer, Vector(0), 2, width: 16, height: 8);
        output.Elements[0].RelativeX = 99;
        Assert.AreEqual(3f, original.Elements[0].RelativeX);
        Assert.AreEqual(3f, ((VectorPicture)endpoints.Read(consumer, 102, import)).Elements[0].RelativeX);
        Assert.AreEqual(1, calls);
        import.SetPipeline(EffectPipeline.Picture);
        Assert.ThrowsExactly<ClipValueBindingException>(() => endpoints.Read(consumer, 102, import));
    }

    [TestMethod]
    public void NativeAndPictureGraphsHaveIndependentOutputsAndLifetimes()
    {
        var native = Native(new Provider(EffectArgumentFieldType.Any, EffectArgumentFieldType.VectorPicture, c => c.Input));
        native.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        native.SetFinalOutputSource(true);
        var picture = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c => c.Input);
        picture.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        picture.SetFinalOutputSource(true);
        using var clip = Clip(0, native, picture);
        var effects = clip.EffectsInstances!;
        Assert.AreEqual(1, effects.OfType<IVectorPictureEffect>().Count());
        var graph = effects.OfType<DynamicEffectGraph>().Single();
        ((IDisposable)effects.OfType<IVectorPictureEffect>().Single()).Dispose();
        using var input = Picture8bpp.GenerateSolidColor(2, 1, 1, 2, 3, 1);
        using var output = graph.Evaluate(input, clip, 102, 2, 1, 2, 1, new(0, 0, 2, 1, false)).Picture;
        Assert.AreEqual(2, output.Width);
        Assert.IsFalse(picture.LastEffect!.Disposed);
    }

    [TestMethod]
    public void VectorContentKeepsItsTypeAcrossIsolationAndSerialization()
    {
        var value = Vector(7);
        var restored = (VectorPicture)IsolationValueConverter.ToObject(IsolationValueConverter.FromObject(value))!;
        Assert.AreEqual(7f, restored.Elements[0].RelativeX);
        Assert.IsFalse(EffectFieldTypes.AreCompatible(EffectArgumentFieldType.VectorPicture, EffectArgumentFieldType.IPicture));
        Assert.IsFalse(EffectFieldTypes.AreCompatible(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.VectorPicture));
        var provider = Native(new ClipValueExportProvider { ValueType = EffectArgumentFieldType.VectorPicture });
        var dto = EffectBindingHelper.SerializeProvider(provider);
        var restoredDto = JsonSerializer.Deserialize<EffectProviderJSONStructure>(JsonSerializer.Serialize(dto))!;
        var copy = new ClipValueExportProvider { MetaData = restoredDto.MetaData! };
        Assert.AreEqual(EffectPipeline.NativeContent, copy.GetPipeline());
        Assert.AreEqual(EffectArgumentFieldType.VectorPicture, ((IClipValueEndpointProvider)copy).ValueType);
    }

    [TestMethod]
    public async Task VectorInputsParametersAndNamedOutputsCrossTheIsolationWorker()
    {
        using var files = new ProjectExternalSourceTests.SourceFiles();
        using var lifetime = new CancellationTokenSource();
        var root = files.Directory("native-session");
        await using var payloads = new SessionPayloadExchange(root);
        await using var resources = new SessionResourceBroker(root);
        var plugin = new DynamicEffectIsolationTests.Plugin(new()
        {
            ["NativeMultiple"] = () => Native(new MultipleOutputProvider(EffectArgumentFieldType.VectorPicture,
                [Port("Vector", EffectArgumentFieldType.VectorPicture), Port("Value", EffectArgumentFieldType.Numeric)], c =>
                {
                    var input = (VectorPicture)c.Input!;
                    input.Elements[0].RelativeX += ((VectorPicture)c.Parameters["Other"]!).Elements[0].RelativeX;
                    return new Dictionary<string, object?> { ["Vector"] = input, ["Value"] = (float)c.FrameIndex };
                }, Port("Other", EffectArgumentFieldType.VectorPicture))),
        });
        await using var communication = new NamedPipePluginCommunicationService(plugin.PluginID, lifetime.Token);
        using var worker = new PluginIsolationWorkerService("secret", Environment.ProcessId, root, plugin.PluginID,
            root, "", payloads, resources, communication, lifetime, _ => plugin);
        await using var control = new DirectIsolationControlChannel(worker);
        var negotiated = await control.SendAsync(new()
        {
            Operation = RenderOperation.IsolationNegotiate,
            Payload = RenderRpcSerializer.Serialize(new IsolationNegotiateRequest
            { AuthenticationToken = "secret", HostProcessId = Environment.ProcessId, PreferredPayloadKind = IsolationPayloadKind.Inline }),
        });
        Assert.IsNull(negotiated.Error);
        await using var session = new PluginIsolationSession(plugin.PluginID, control, payloads, resources,
            RenderRpcSerializer.Deserialize<IsolationChannelCapabilities>(negotiated.Payload),
            new() { PayloadMode = IsolationPayloadKind.Inline }, _ => ValueTask.CompletedTask);
        await session.InvokeAsync<IsolationLoadPluginRequest, IsolationPluginDescriptor>(RenderOperation.IsolationLoadPlugin,
            new() { PluginId = plugin.PluginID });
        var descriptor = await session.InvokeAsync<IsolationCreateProviderRequest, IsolationProviderDescriptor>(RenderOperation.IsolationCreateProvider,
            new() { TypeName = "NativeMultiple" });
        var remote = new RemoteMultipleOutputEffectProvider(session, descriptor);
        remote.SetPipeline(EffectPipeline.NativeContent);
        var effects = remote.Build();
        try
        {
            var input = Vector();
            var outputs = ((IMultipleOutputEffect)effects.Single()).ComputeOutputs(new()
            {
                Input = input, FrameIndex = 102,
                Parameters = new Dictionary<string, object?> { ["Other"] = Vector(7) },
            });
            Assert.AreEqual(10f, ((VectorPicture)outputs["Vector"]!).Elements[0].RelativeX);
            Assert.AreEqual(102f, outputs["Value"]);
            Assert.AreEqual(3f, input.Elements[0].RelativeX);
        }
        finally
        {
            foreach (var effect in effects.OfType<IDisposable>()) effect.Dispose();
        }
    }
}
