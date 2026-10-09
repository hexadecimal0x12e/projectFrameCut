using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.PluginIsolation;
using projectFrameCut.Render.RPCProtocol;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Sources;
using static projectFrameCut.Render.Contracts.Tests.DynamicEffectGraphTests;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
[DoNotParallelize]
public sealed class DynamicEffectIsolationTests
{
    private sealed class ParameterPictureEffect() : TestEffect(_ => null)
    {
        public override IEffect WithParameters(Dictionary<string, object> parameters) => new ParameterPictureEffect { Parameters = parameters };
        public override object? Compute(EffectExecutionContext context)
        {
            var other = DynamicParam.Resolve<IPicture>(Parameters["Other"], null!);
            Assert.AreEqual(((IPicture<byte>)other).r[0], ((IPicture<byte>)context.Parameters["Other"]!).r[0]);
            return Picture((byte)((int)context.Input! + ((IPicture<byte>)other).r[0]));
        }
    }

    internal sealed class Plugin(Dictionary<string, Func<IEffectProvider>> providers) : IPluginBase
    {
        public string PluginID => "test.dynamic-isolation";
        public int PluginAPIVersion => IPluginBase.CurrentPluginAPIVersion;
        public string Name => PluginID;
        public string Author => "test";
        public string Description => "test";
        public Version Version => new(1, 0);
        public string AuthorUrl => "";
        public string? PublishingUrl => null;
        public Dictionary<string, Dictionary<string, string>> LocalizationProvider => [];
        public Dictionary<string, Func<IEffectProvider>> EffectProviderProvider => providers;
        public Dictionary<string, Func<string, string, ISoundTrack>> SoundTrackProvider => [];
        public Dictionary<string, IVideoSource> VideoSourceProvider => [];
        public Dictionary<string, Func<string, IAudioSource>> AudioSourceProvider => [];
        public Dictionary<string, Func<string, IVideoWriter>> VideoWriterProvider => [];
        public Dictionary<string, string> Configuration { get; set; } = [];
        public Dictionary<string, Dictionary<string, string>> ConfigurationDisplayString => [];
    }

    [TestMethod]
    [DataRow(IsolationPayloadKind.Inline)]
    [DataRow(IsolationPayloadKind.LocalFile)]
    [DataRow(IsolationPayloadKind.SharedMemory)]
    public async Task PictureAndScalarInputsCrossTheWorkerChannel(IsolationPayloadKind kind)
    {
        using var files = new ProjectExternalSourceTests.SourceFiles();
        using var lifetime = new CancellationTokenSource();
        var root = files.Directory("session");
        await using var payloads = new SessionPayloadExchange(root);
        await using var resources = new SessionResourceBroker(root);
        var plugin = new Plugin(new()
        {
            ["Scalar"] = () => new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.Integer,
                c => ((IPicture<byte>)c.Input!).r[0] + (int)c.FrameIndex),
            ["Picture"] = () => new Provider(EffectArgumentFieldType.Integer, EffectArgumentFieldType.IPicture, _ => null,
                new EffectArgumentFieldDescriptor { Id = "Other", FieldType = EffectArgumentFieldType.IPicture }) { Factory = () => new ParameterPictureEffect() },
            ["Multiple"] = () => new MultipleOutputEffectTests.MultipleOutputProvider(EffectArgumentFieldType.IPicture,
                [MultipleOutputEffectTests.Port("Left", EffectArgumentFieldType.IPicture), MultipleOutputEffectTests.Port("Right", EffectArgumentFieldType.IPicture),
                 MultipleOutputEffectTests.Port("Value", EffectArgumentFieldType.Integer)], c => new Dictionary<string, object?>
                {
                    ["Left"] = Picture((byte)(10 + c.FrameIndex)), ["Right"] = c.Input, ["Value"] = (int)c.FrameIndex,
                }) { Prefix = [new TestEffect(c => Picture((byte)(((IPicture<byte>)c.Input!).r[0] + 1)))] },
        });
        await using var communication = new NamedPipePluginCommunicationService(plugin.PluginID, lifetime.Token);
        using var worker = new PluginIsolationWorkerService("secret", Environment.ProcessId, root, plugin.PluginID,
            root, "", payloads, resources, communication, lifetime, _ => plugin);
        await using var control = new DirectIsolationControlChannel(worker);
        var negotiated = await control.SendAsync(new()
        {
            Operation = RenderOperation.IsolationNegotiate,
            Payload = RenderRpcSerializer.Serialize(new IsolationNegotiateRequest
            { AuthenticationToken = "secret", HostProcessId = Environment.ProcessId, PreferredPayloadKind = kind }),
        });
        Assert.IsNull(negotiated.Error);
        await using var session = new PluginIsolationSession(plugin.PluginID, control, payloads, resources,
            RenderRpcSerializer.Deserialize<IsolationChannelCapabilities>(negotiated.Payload), new() { PayloadMode = kind }, _ => ValueTask.CompletedTask);
        await session.InvokeAsync<IsolationLoadPluginRequest, IsolationPluginDescriptor>(RenderOperation.IsolationLoadPlugin,
            new() { PluginId = plugin.PluginID });
        async Task<IEffect[]> Create(string type)
        {
            var provider = await session.InvokeAsync<IsolationCreateProviderRequest, IsolationProviderDescriptor>(RenderOperation.IsolationCreateProvider, new() { TypeName = type });
            if (type == "Picture") Assert.AreEqual("Other", provider.Fields.Single().Id);
            if (type == "Multiple")
            {
                Assert.IsTrue(provider.HasMultipleOutputs);
                Assert.AreEqual(3, provider.OutputFields.Count);
                var remote = new RemoteMultipleOutputEffectProvider(session, provider);
                Assert.IsInstanceOfType<IMultipleOutputEffectProvider>(remote);
                var built = remote.Build();
                Assert.AreEqual(2, built.Length);
                Assert.IsInstanceOfType<IMultipleOutputEffect>(built[^1]);
                return built;
            }
            var effects = await session.InvokeAsync<IsolationBuildProviderRequest, IsolationEffectList>(RenderOperation.IsolationBuildProvider,
                new() { Provider = new() { ObjectId = provider.ObjectId, InstanceId = provider.InstanceId, Enabled = true } });
            return effects.Effects.Select(e => RemoteEffectFactory.Create(session, e)).ToArray();
        }
        var scalar = (await Create("Scalar")).Single();
        var picture = (await Create("Picture")).Single();
        var multiple = await Create("Multiple");
        try
        {
            using var source = Picture(10);
            var prototype = picture;
            picture = picture.WithParameters(new() { ["Other"] = (Func<object>)(() => source) });
            ((IDisposable)prototype).Dispose();
            for (uint frame = 1; frame <= 2; frame++)
            {
                var value = scalar.Compute(new() { Input = source, FrameIndex = frame });
                Assert.AreEqual(10 + (int)frame, value);
                using var output = (IPicture)picture.Compute(new()
                { Input = value, Parameters = new Dictionary<string, object?> { ["Other"] = source }, FrameIndex = frame })!;
                Assert.AreEqual((byte)(20 + frame), ((IPicture<byte>)output).r[0]);
                using var intermediate = (IPicture)multiple[0].Compute(new() { Input = source, FrameIndex = frame })!;
                var outputs = ((IMultipleOutputEffect)multiple[^1]).ComputeOutputs(new() { Input = intermediate, FrameIndex = frame });
                try
                {
                    Assert.AreEqual((int)frame, outputs["Value"]);
                    Assert.AreEqual((byte)(10 + frame), ((IPicture<byte>)outputs["Left"]!).r[0]);
                    Assert.AreEqual((byte)11, ((IPicture<byte>)outputs["Right"]!).r[0]);
                }
                finally
                {
                    foreach (var result in outputs.Values.OfType<IPicture>()) result.Dispose();
                }
            }
            Assert.IsFalse(source.Disposed);
            Assert.IsFalse(Directory.EnumerateFiles(Path.Combine(root, "payloads")).Any());
        }
        finally
        {
            ((IDisposable)scalar).Dispose();
            ((IDisposable)picture).Dispose();
            foreach (var effect in multiple.OfType<IDisposable>()) effect.Dispose();
        }
    }
}
