using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System.Text.Json;
using static projectFrameCut.Render.Contracts.Tests.DynamicEffectGraphTests;
using static projectFrameCut.Render.Contracts.Tests.MultipleOutputEffectTests;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class AnyEffectTests
{
    private sealed class NativeProvider(params EffectArgumentFieldDescriptor[] fields)
        : Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c => c.Input, fields)
    {
        public override EffectType TypeOfEffect => EffectType.TextEffect;
        public override EffectTarget Target => EffectTarget.Text;
    }

    private sealed class NativeEffect() : TestEffect(c => c.Input), ITextEffect
    {
        public override EffectType TypeOfEffect => EffectType.TextEffect;
        public projectFrameCut.Drawing.Text.Entry.TextEntry[] Process(projectFrameCut.Drawing.Text.Entry.TextEntry[] source) => source;
    }

    private sealed class NativeAnyProvider() : Provider(EffectArgumentFieldType.Any, EffectArgumentFieldType.IPicture, c => c.Input)
    {
        public override EffectType TypeOfEffect => EffectType.TextEffect;
        public override EffectTarget Target => EffectTarget.Text;
    }

    private sealed class ScopedEffect() : TestEffect(c => c.Input), IContinuousEffect
    {
        public override EffectType TypeOfEffect => EffectType.ContinuousEffect;
        public int StartPoint { get; set; } = 10;
        public int EndPoint { get; set; } = 20;
        public bool IsScoped { get; set; } = true;
        public IPicture Render(IPicture source, float progress, int width, int height) => source;
    }

    [TestMethod]
    public void ConcreteOutputsReachAnyInputsWithoutConversion()
    {
        (EffectArgumentFieldType Type, object Value)[] values =
        [
            (EffectArgumentFieldType.Integer, 3), (EffectArgumentFieldType.UnsignedInteger, 4u),
            (EffectArgumentFieldType.Long, 5L), (EffectArgumentFieldType.UnsignedLong, 6UL),
            (EffectArgumentFieldType.Numeric, 0.25f), (EffectArgumentFieldType.Numeric, 0.5d),
            (EffectArgumentFieldType.Boolean, true), (EffectArgumentFieldType.String, "text"),
            (EffectArgumentFieldType.Color, new PictureExtensions.Pixel<byte> { r = 1, g = 2, b = 3, a = 0.5f }),
            (EffectArgumentFieldType.Color, new PictureExtensions.Pixel<ushort> { r = 1000, g = 2000, b = 3000, a = 0.25f }),
            (EffectArgumentFieldType.Size, new ClipPositionTuple(0, 0, 3, 4, false)),
            (EffectArgumentFieldType.Position, new ClipPositionTuple(1, 2, 0, 0, false)),
            (EffectArgumentFieldType.SizeAndPosition, new ClipPositionTuple(1, 2, 3, 4, false)),
            (EffectArgumentFieldType.Unknown, new object()),
            (EffectArgumentFieldType.Unknown, JsonSerializer.SerializeToElement(new { value = 7 })),
        ];
        foreach (var (type, value) in values)
        {
            int calls = 0;
            var source = new Provider(null, type, _ => { calls++; return value; });
            var target = new Provider(EffectArgumentFieldType.Any | EffectArgumentFieldType.Mandatory,
                EffectArgumentFieldType.IPicture, c =>
                {
                    Assert.AreSame(value, c.Input);
                    Assert.AreSame(value, c.Parameters["Other"]);
                    return Picture(9);
                }, Port("Other", EffectArgumentFieldType.Any | EffectArgumentFieldType.Numeric));
            target.SetMainInputSource(source.Id);
            target.SetFieldBinding("Other", source.Id.ToString());
            target.SetFinalOutputSource(true);
            Assert.AreEqual(0, EffectBindingHelper.ValidateBindings(Providers(source, target)).Count);
            using var graph = Build(Providers(source, target));
            using var input = Picture(0);
            using var result = Evaluate(graph, input);
            Assert.AreEqual((byte)9, ((IPicture<byte>)result).r[0]);
            Assert.AreEqual(1, calls);
        }
    }

    [TestMethod]
    public void AnyPictureBranchesKeepSeparateCopiesAndReleaseThem()
    {
        int calls = 0;
        IPicture? produced = null, primary = null, parameter = null;
        var source = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, _ =>
        {
            calls++;
            return produced = Picture(7);
        });
        var target = new Provider(EffectArgumentFieldType.Any, EffectArgumentFieldType.IPicture, c =>
        {
            primary = (IPicture)c.Input!;
            parameter = (IPicture)c.Parameters["Other"]!;
            Assert.AreNotSame(primary, parameter);
            ((IPicture<byte>)primary).r[0] = 12;
            Assert.AreEqual((byte)7, ((IPicture<byte>)parameter).r[0]);
            return primary;
        }, Port("Other", EffectArgumentFieldType.Any));
        source.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        target.SetMainInputSource(source.Id);
        target.SetFieldBinding("Other", source.Id.ToString());
        target.SetFinalOutputSource(true);
        using var graph = Build(Providers(source, target));
        using var input = Picture(0);
        using var result = Evaluate(graph, input);
        Assert.AreEqual(1, calls);
        Assert.AreEqual((byte)12, ((IPicture<byte>)result).r[0]);
        Assert.IsTrue(produced!.Disposed);
        Assert.IsTrue(parameter!.Disposed);
        Assert.IsFalse(primary!.Disposed);
        Assert.IsFalse(input.Disposed);
    }

    [TestMethod]
    public void SourcePictureAndBuiltInsCanFeedAnyPorts()
    {
        using var scope = ValueProviderFrameContext.PushFrame(0, 0);
        var target = new Provider(EffectArgumentFieldType.Any, EffectArgumentFieldType.IPicture, c =>
        {
            Assert.IsInstanceOfType<IPicture>(c.Input);
            Assert.IsInstanceOfType<IPicture>(c.Parameters["Picture"]);
            Assert.AreEqual(3f, c.Parameters["Frame"]);
            Assert.AreEqual(0f, c.Parameters["Progress"]);
            return c.Input;
        }, Port("Picture", EffectArgumentFieldType.Any), Port("Frame", EffectArgumentFieldType.Any),
            Port("Progress", EffectArgumentFieldType.Any));
        target.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        target.SetFieldBinding("Picture", IEffectProvider.InputAnchorGUID.ToString());
        target.SetFieldBinding("Frame", ValueProviderFrameContext.BuiltInFrameProviderId);
        target.SetFieldBinding("Progress", ValueProviderFrameContext.BuiltInProgressProviderId);
        target.SetFinalOutputSource(true);
        using var graph = Build(Providers(target));
        using var input = Picture(2);
        using var result = Evaluate(graph, input, 3);
        Assert.AreEqual((byte)2, ((IPicture<byte>)result).r[0]);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UnresolvedOutputsCanRemainDisconnected(bool enabled)
    {
        var single = new Provider(null, EffectArgumentFieldType.Any | EffectArgumentFieldType.Mandatory,
            _ => throw new AssertFailedException("Disconnected provider was evaluated.")) { Enabled = enabled };
        var multiple = new MultipleOutputProvider(null,
            [Port("Value", EffectArgumentFieldType.Integer), Port("Invalid", EffectArgumentFieldType.Any | EffectArgumentFieldType.IPicture)],
            _ => throw new AssertFailedException("Disconnected provider was evaluated.")) { Enabled = enabled };
        foreach (IEffectProvider provider in new IEffectProvider[] { single, multiple })
        {
            var diagnostics = EffectBindingHelper.ValidateBindings(Providers(provider));
            Assert.AreEqual(0, diagnostics.Count);
            provider.ValidateOutputFields();
            using var graph = Build(Providers(provider));
            using var input = Picture(1);
            Assert.AreSame(input, Evaluate(graph, input));
        }
    }

    [TestMethod]
    public void NestedAnyTypesReceiveParameterAndBindingStateWithoutEvaluatingValues()
    {
        int calls = 0, resolutions = 0;
        var source = new Provider(null, EffectArgumentFieldType.Long, _ => { calls++; return 7L; },
            Port("Config", EffectArgumentFieldType.String));
        source.Fields = new() { ["Config"] = new StaticEffectArgumentField("configured", EffectArgumentFieldType.String) };
        source.MetaData["Tag"] = "source";
        var relay = new Provider(EffectArgumentFieldType.Any, EffectArgumentFieldType.Any, c => c.Input,
            Port("Expected", EffectArgumentFieldType.String))
        {
            TypeResolver = (_, _, c) =>
            {
                resolutions++;
                Assert.AreEqual("Long", c.Parameters["Expected"]);
                Assert.AreEqual("configured", c.BoundProviders[EffectProviderAnchorExtensions.InputKey].Parameters["Config"]);
                Assert.AreEqual("source", c.BoundProviders[EffectProviderAnchorExtensions.InputKey].Metadata["Tag"]);
                Assert.AreEqual(source.Id.ToString(), c.State.Bindings[EffectProviderAnchorExtensions.InputKey]);
                return c.InputTypes[EffectProviderAnchorExtensions.InputKey];
            },
        };
        relay.Fields = new() { ["Expected"] = new StaticEffectArgumentField("Long", EffectArgumentFieldType.String) };
        relay.SetMainInputSource(source.Id);
        var nested = new Provider(EffectArgumentFieldType.Any, EffectArgumentFieldType.Any, c => c.Input)
        {
            TypeResolver = (_, _, c) => c.InputTypes[EffectProviderAnchorExtensions.InputKey],
        };
        nested.SetMainInputSource(relay.Id);
        var target = new Provider(EffectArgumentFieldType.Any, EffectArgumentFieldType.IPicture, c =>
        {
            Assert.AreEqual(7L, c.Input);
            return Picture(7);
        });
        target.SetMainInputSource(nested.Id);
        target.SetFinalOutputSource(true);
        var providers = Providers(source, relay, nested, target);
        var types = new EffectFieldTypeResolution(providers);
        Assert.AreEqual(EffectArgumentFieldType.Long, types.GetOutputType(nested));
        Assert.AreEqual(EffectArgumentFieldType.Long, types.GetInputType(relay, EffectProviderAnchorExtensions.InputKey));
        Assert.AreEqual(0, calls);
        using var graph = Build(providers);
        int before = resolutions;
        using var input = Picture(0);
        for (uint frame = 0; frame < 2; frame++)
        {
            using var result = Evaluate(graph, input, frame);
            Assert.AreEqual((byte)7, ((IPicture<byte>)result).r[0]);
        }
        Assert.AreEqual(2, calls);
        Assert.AreEqual(before, resolutions);
    }

    [TestMethod]
    public void UsedUnresolvedAndIncorrectOutputsFailBeforePreviewCallbacks()
    {
        foreach (bool resolved in new[] { false, true })
        {
            var source = new Provider(null, EffectArgumentFieldType.Any, _ => "incorrect")
            {
                TypeResolver = (_, _, _) => resolved ? EffectArgumentFieldType.Integer : EffectArgumentFieldType.Any,
            };
            var target = new Provider(EffectArgumentFieldType.Any, EffectArgumentFieldType.IPicture, _ => Picture(1));
            target.SetMainInputSource(source.Id);
            target.SetFinalOutputSource(true);
            using var graph = Build(Providers(source, target));
            using var input = Picture(0);
            int callbacks = 0;
            Assert.ThrowsExactly<InvalidOperationException>(() => graph.Evaluate(input, null, 3, 1, 1, 1, 1,
                new(0, 0, 1, 1, false), afterNode: (_, _) => callbacks++));
            Assert.AreEqual(0, callbacks);
        }
    }

    [TestMethod]
    public void AnyMultipleOutputsResolveIndependentlyAndComputeOnce()
    {
        int calls = 0;
        var source = new MultipleOutputProvider(null,
            [Port("Image", EffectArgumentFieldType.Any), Port("Value", EffectArgumentFieldType.Any)], c =>
            {
                calls++;
                return new Dictionary<string, object?> { ["Image"] = Picture(8), ["Value"] = (int)c.FrameIndex };
            })
        {
            TypeResolver = (key, _, _) => key == "Image" ? EffectArgumentFieldType.IPicture : EffectArgumentFieldType.Integer,
        };
        var target = new Provider(EffectArgumentFieldType.Any, EffectArgumentFieldType.IPicture, c =>
        {
            Assert.AreEqual((int)c.FrameIndex, c.Parameters["Value"]);
            return c.Input;
        }, Port("Value", EffectArgumentFieldType.Any));
        target.SetMainInputSource(EffectProviderOutputExtensions.CreateOutputSourceId(source.Id, "Image"));
        target.SetFieldBinding("Value", EffectProviderOutputExtensions.CreateOutputSourceId(source.Id, "Value"));
        var providers = Providers(source, target);
        EffectBindingHelper.SetFinalOutput(providers, target.Id);
        using var graph = Build(providers);
        using var input = Picture(0);
        for (uint frame = 0; frame < 2; frame++)
        {
            using var result = Evaluate(graph, input, frame);
            Assert.AreEqual((byte)8, ((IPicture<byte>)result).r[0]);
        }
        Assert.AreEqual(2, calls);
        Assert.AreEqual(EffectArgumentFieldType.Any, source.OutFields["Image"].FieldType);
        Assert.ThrowsExactly<ArgumentException>(() => EffectBindingHelper.SetFinalOutput(providers, source.Id, EffectPipeline.Picture, "Value"));
        EffectBindingHelper.SetFinalOutput(providers, source.Id, EffectPipeline.Picture, "Image");
    }

    [TestMethod]
    public void AnySinglePictureOutputCanBeTheFinalOutput()
    {
        var source = new Provider(null, EffectArgumentFieldType.Any, _ => Picture(9))
        {
            TypeResolver = (_, _, _) => EffectArgumentFieldType.IPicture,
        };
        var providers = Providers(source);
        Assert.IsTrue(DynamicEffectBindings.RequiresGraph(providers));
        Assert.IsFalse(DynamicEffectBindings.CanInlineValue(source, providers));
        EffectBindingHelper.SetFinalOutput(providers, source.Id);
        using var graph = Build(providers);
        using var input = Picture(0);
        using var result = Evaluate(graph, input);
        Assert.AreEqual((byte)9, ((IPicture<byte>)result).r[0]);
    }

    [TestMethod]
    public void UnusedUnresolvedNamedOutputsDoNotBlockAResolvedPicturePort()
    {
        int calls = 0;
        var source = new MultipleOutputProvider(null,
            [Port("Image", EffectArgumentFieldType.IPicture), Port("Unused", EffectArgumentFieldType.Any)], _ =>
            {
                calls++;
                return new Dictionary<string, object?> { ["Image"] = Picture(3), ["Unused"] = new object() };
            });
        var providers = Providers(source);
        EffectBindingHelper.SetFinalOutput(providers, source.Id, EffectPipeline.Picture, "Image");
        using var input = Picture(0);
        using (var graph = Build(providers))
        using (var result = Evaluate(graph, input)) Assert.AreEqual((byte)3, ((IPicture<byte>)result).r[0]);
        Assert.AreEqual(1, calls);
        var target = new Provider(EffectArgumentFieldType.Any, EffectArgumentFieldType.IPicture, _ => Picture(1));
        target.SetMainInputSource(EffectProviderOutputExtensions.CreateOutputSourceId(source.Id, "Unused"));
        providers.Add(target.Id, target);
        EffectBindingHelper.SetFinalOutput(providers, target.Id);
        using var invalid = Build(providers);
        Assert.ThrowsExactly<InvalidOperationException>(() => Evaluate(invalid, input));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void ResolvedAnyInputRejectsIncompatibleValues()
    {
        var source = new Provider(null, EffectArgumentFieldType.Integer, _ => 2);
        var target = new Provider(EffectArgumentFieldType.Any, EffectArgumentFieldType.IPicture, _ => Picture(1))
        {
            TypeResolver = (_, _, _) => EffectArgumentFieldType.Boolean,
        };
        target.SetMainInputSource(source.Id);
        target.SetFinalOutputSource(true);
        var providers = Providers(source, target);
        Assert.IsTrue(EffectBindingHelper.ValidateBindings(providers).Any(d => d.ProviderId == target.Id && d.Code == "IncompatibleInput"));
        using var graph = new DynamicEffectGraph(providers, providers.ToDictionary(p => p.Key, p => p.Value.Build()));
        using var input = Picture(0);
        Assert.ThrowsExactly<InvalidOperationException>(() => Evaluate(graph, input));
    }

    [TestMethod]
    public void AnyFallbacksAndMissingMandatoryValuesUseExistingRules()
    {
        var fallback = new object();
        var source = new Provider(EffectArgumentFieldType.Any, EffectArgumentFieldType.Integer,
            _ => throw new AssertFailedException("Disabled provider was evaluated.")) { Enabled = false };
        var target = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c =>
        {
            Assert.AreSame(fallback, c.Parameters["Other"]);
            return c.Input;
        }, Port("Other", EffectArgumentFieldType.Any));
        target.Fields = new() { ["Other"] = new StaticEffectArgumentField(fallback, EffectArgumentFieldType.Any) };
        source.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        target.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        target.SetFieldBinding("Other", source.Id.ToString());
        target.SetFinalOutputSource(true);
        using var input = Picture(4);
        using (var graph = Build(Providers(source, target)))
        using (var result = Evaluate(graph, input)) Assert.AreEqual((byte)4, ((IPicture<byte>)result).r[0]);

        var mandatory = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c => c.Input,
            new EffectArgumentFieldDescriptor { Id = "Required", FieldType = EffectArgumentFieldType.Any | EffectArgumentFieldType.Mandatory, DefaultValue = null! });
        mandatory.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        mandatory.SetFinalOutputSource(true);
        using var mandatoryGraph = Build(Providers(mandatory));
        Assert.ThrowsExactly<InvalidOperationException>(() => Evaluate(mandatoryGraph, input));
    }

    [TestMethod]
    public void DisabledAnyInputsOnlyPassValuesMatchingTheDeclaredOutput()
    {
        var target = new Provider(EffectArgumentFieldType.Any, EffectArgumentFieldType.IPicture,
            _ => throw new AssertFailedException("Disabled provider was evaluated.")) { Enabled = false };
        target.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        target.SetFinalOutputSource(true);
        using var graph = Build(Providers(target));
        using var input = Picture(5);
        Assert.AreSame(input, Evaluate(graph, input));
        var scoped = new Provider(EffectArgumentFieldType.Any, EffectArgumentFieldType.IPicture, c => c.Input)
            { Factory = () => new ScopedEffect() };
        scoped.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        scoped.SetFinalOutputSource(true);
        using var scopedGraph = Build(Providers(scoped));
        Assert.AreSame(input, Evaluate(scopedGraph, input, 0));
        using var active = Evaluate(scopedGraph, input, 15);
        Assert.AreNotSame(input, active);
    }

    [TestMethod]
    public void PureAnyValueDependenciesRemainInlineableAndCacheTheirResult()
    {
        int calls = 0;
        var value = new Provider(null, EffectArgumentFieldType.Numeric, c => { calls++; return c.Parameters["Value"]; },
            Port("Value", EffectArgumentFieldType.Any));
        var native = new NativeProvider(Port("Value", EffectArgumentFieldType.Any)) { Factory = () => new NativeEffect() };
        value.SetFieldBinding("Value", ValueProviderFrameContext.BuiltInFrameProviderId);
        native.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        native.SetFieldBinding("Value", value.Id.ToString());
        native.SetFinalOutputSource(true);
        Assert.IsTrue(DynamicEffectBindings.CanInlineValue(value, Providers(value, native)));
        var effects = EffectBindingHelper.RebuildAllEffects(Providers(value, native), null)!;
        using var graph = effects.Values.OfType<DynamicEffectGraph>().Single();
        using var nativeEffect = (IDisposable)native.LastEffect!;
        using var scope = ValueProviderFrameContext.PushFrame(3, 0);
        Assert.AreEqual(3f, DynamicParam.Resolve<object>(native.LastEffect!.Parameters["Value"], null!));
        Assert.AreEqual(3f, DynamicParam.Resolve<object>(native.LastEffect.Parameters["Value"], null!));
        Assert.AreEqual(1, calls);

        using var picture = Picture(1);
        value.ClearFieldBinding("Value");
        value.Fields = new() { ["Value"] = new StaticEffectArgumentField(picture, EffectArgumentFieldType.Any) };
        Assert.IsFalse(DynamicEffectBindings.CanInlineValue(value, Providers(value)));
        Assert.IsFalse(EffectBindingHelper.SerializeProvider(value).StaticFields!.ContainsKey("Value"));
    }

    [TestMethod]
    public void AnyDoesNotAllowNativeContentToEnterTheRasterStage()
    {
        var native = new NativeProvider();
        var target = new Provider(EffectArgumentFieldType.Any, EffectArgumentFieldType.IPicture, c => c.Input);
        target.SetMainInputSource(native.Id);
        target.SetFinalOutputSource(true);
        Assert.IsTrue(EffectBindingHelper.ValidateBindings(Providers(native, target)).Any(d => d.Code == "CrossStageBinding"));
        var nativeTarget = new NativeAnyProvider();
        native.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        nativeTarget.SetMainInputSource(native.Id);
        var providers = Providers(native, nativeTarget);
        Assert.IsTrue(native.CanConnectContent(nativeTarget));
        Assert.AreEqual(0, EffectBindingHelper.ValidateBindings(providers).Count);
        EffectBindingHelper.NormalizeStoredBindings(providers);
        Assert.AreEqual(native.Id.ToString(), nativeTarget.GetMainInputSource());
    }

    [TestMethod]
    public void AnyMetadataAndLegacyParametersRetainObjects()
    {
        object json = JsonSerializer.SerializeToElement(new { values = new[] { 1, 2 } });
        var target = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c => c.Input,
            Port("Value", EffectArgumentFieldType.Any));
        target.Fields = new() { ["Value"] = new StaticEffectArgumentField(json, EffectArgumentFieldType.Any) };
        Assert.AreEqual(EffectArgumentFieldType.Any, (EffectArgumentFieldType.Any | EffectArgumentFieldType.Mandatory).BaseType());
        Assert.IsFalse(EffectFieldTypes.AreCompatible(EffectArgumentFieldType.Any, EffectArgumentFieldType.Any));
        Assert.AreEqual("object", ((IEffectProvider)target).ParametersType["Value"]);
        Assert.AreSame(json, EffectBindingHelper.SerializeProvider(target).StaticFields!["Value"]);
#pragma warning disable CS0618
        var converted = EffectArgsHelper.ConvertElementDictToObjectDict(new() { ["Value"] = json }, ((IEffectProvider)target).ParametersType);
#pragma warning restore CS0618
        Assert.AreSame(json, converted["Value"]);
    }
}
