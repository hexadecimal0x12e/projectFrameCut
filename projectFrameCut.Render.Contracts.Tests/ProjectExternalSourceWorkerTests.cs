using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.PluginIsolation;
using projectFrameCut.Render.RPCProtocol;
using projectFrameCut.Render.Sources;
using System.Buffers.Binary;
using System.IO.Pipes;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ProjectExternalSourceWorkerTests
{
    [TestMethod]
    [DataRow(IsolationPayloadKind.Inline)]
    [DataRow(IsolationPayloadKind.SharedMemory)]
    [DataRow(IsolationPayloadKind.LocalFile)]
    public async Task MaskOnlyProvidersProcessBackdropsAndReleasePayloads(IsolationPayloadKind kind)
    {
        using var files = new ProjectExternalSourceTests.SourceFiles();
        var provider = new TestMaskSourceProvider();
        var root = files.Directory("mask-session");
        await using var worker = await TestWorker.StartAsync(root, provider, kind);
        var client = new ProjectExternalSourceClient(worker);
        await client.LoadAsync();
        Assert.AreEqual(0, client.Sources.Count);
        Assert.AreEqual(0, client.AudioSources.Count);
        Assert.AreEqual(1, client.MaskSources.Count);
        using var backdrop = new projectFrameCut.Drawing.Base.Picture.HDRPicture16bpp(2, 1)
        {
            HasAlphaChannel = true, a = [(Half)0.5f, Half.One], MaximumBrightness = (Half)1200, Brightness = [(Half)2f, (Half)3f],
        };
        var request = new ExternalMaskSourceProcessRequest
        {
            Source = new() { SourceId = "mask" }, Backdrop = ExternalVideoFrameCodec.Encode(backdrop), Scale = 0.5f,
            Arguments = new() { ["Amount"] = new() { Kind = IsolationValueKind.Single, NumberValue = 4 } },
        };
        var result = await client.ProcessMaskAsync(request);
        Assert.AreEqual((byte)4, result.Red[0]);
        Assert.AreEqual(0.5f, provider.LastRequest!.Scale);
        CollectionAssert.AreEqual(request.Backdrop.Alpha, result.Alpha);
        CollectionAssert.AreEqual(request.Backdrop.Brightness, result.Brightness);
        Assert.AreEqual(1200f, (float)result.MaximumBrightness);
        Assert.IsFalse(Directory.EnumerateFiles(Path.Combine(root, "payloads")).Any());
        provider.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.ProcessMaskAsync(request));
        Assert.IsNotNull(client.FrameError);
        Assert.IsFalse(Directory.EnumerateFiles(Path.Combine(root, "payloads")).Any());
        provider.Fail = false;
        await client.ProcessMaskAsync(request);
        Assert.IsNull(client.FrameError);
        request.Source.SourceId = "unknown";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await client.ProcessMaskAsync(request));
        Assert.IsFalse(Directory.EnumerateFiles(Path.Combine(root, "payloads")).Any());
    }

    [TestMethod]
    public async Task ProjectMaskSourcesCaptureTheirHostAndPersistTheirCatalog()
    {
        using var files = new ProjectExternalSourceTests.SourceFiles();
        var root = files.Directory("mask-project");
        var asset = await ProjectExternalSourceDatabase.ImportAsync(root, await files.CreateAsync("mask-package"));
        var previous = ProjectExternalSourceRuntime.StartWorker;
        await using var host = new ProjectExternalSourceHost();
        try
        {
            ProjectExternalSourceRuntime.StartWorker = async (_, _, _) => await TestWorker.StartAsync(files.Directory("mask-host"), new TestMaskSourceProvider());
            var request = new SetProjectExternalSourcesRequest { ProjectRoot = root, AllowedSources = ProjectExternalSourceDatabase.ParseApprovals(root, "all") };
            var catalog = await host.SetAsync(request);
            Assert.AreEqual(1, catalog.Sources.Single().MaskSources.Count);
            Assert.AreEqual(asset.ImportId, ProjectExternalSourceDatabase.Read(root).Assets.Single().MaskSources.Single().ClientId);
            using var sourceScope = ProjectExternalSourceRuntime.Use(host);
            var source = MaskSources.Create(ProjectExternalMaskSource.CreatePath(asset.ImportId, catalog.Sources.Single().MaskSources.Single()));
            Assert.IsTrue(MaskSources.GetAvailable().Any(x => x.TypeName == source.TypeName));
            using var backdrop = new projectFrameCut.Drawing.Base.Picture.Picture8bpp(2, 1);
            await using var other = new ProjectExternalSourceHost();
            using (ProjectExternalSourceRuntime.Use(other))
            using (var frame = source.Process(backdrop, new Dictionary<string, object>(), 1, CancellationToken.None))
                Assert.AreEqual((byte)2, ((projectFrameCut.Drawing.Base.IPicture<byte>)frame).r[0]);
            await host.CloseAsync();
            Assert.Throws<IOException>(() => source.Process(backdrop, new Dictionary<string, object>(), 1, CancellationToken.None));
            await host.SetAsync(request);
            using var restored = source.Process(backdrop, new Dictionary<string, object>(), 1, CancellationToken.None);
            Assert.AreEqual((byte)2, ((projectFrameCut.Drawing.Base.IPicture<byte>)restored).r[0]);
        }
        finally { ProjectExternalSourceRuntime.StartWorker = previous; }
    }

    [TestMethod]
    [DataRow(IsolationPayloadKind.Inline)]
    [DataRow(IsolationPayloadKind.SharedMemory)]
    [DataRow(IsolationPayloadKind.LocalFile)]
    public async Task AudioOnlyProvidersCrossTheWorkerChannelAndReleasePayloads(IsolationPayloadKind kind)
    {
        using var files = new ProjectExternalSourceTests.SourceFiles();
        var provider = new TestAudioSourceProvider();
        await using var worker = await TestWorker.StartAsync(files.Directory("audio-session"), provider, kind);
        var client = new ProjectExternalSourceClient(worker);
        await client.LoadAsync();
        Assert.AreEqual(0, client.Sources.Count);
        Assert.AreEqual(1, client.AudioSources.Count);
        var instance = await client.CreateAudioAsync(new() { Source = new() { SourceId = "audio", DecoderName = "ExampleAudio" } });
        await client.InitializeAudioAsync(new() { InstanceId = instance.InstanceId });
        var samples = await client.ReadSamplesAsync(new() { State = new() { InstanceId = instance.InstanceId }, StartIndex = 250, Count = 3 });
        Assert.AreEqual(3, samples.SampleCount);
        Assert.AreEqual(0.25f, BinaryPrimitives.ReadSingleLittleEndian(samples.Samples));
        Assert.AreEqual(-0.25f, BinaryPrimitives.ReadSingleLittleEndian(samples.Samples.AsSpan(12)));
        var empty = await client.ReadSamplesAsync(new() { State = new() { InstanceId = instance.InstanceId }, StartIndex = 480000, Count = 3 });
        Assert.AreEqual(0, empty.SampleCount);
        Assert.IsFalse(Directory.EnumerateFiles(Path.Combine(files.Root, "audio-session", "payloads")).Any());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await client.InitializeAudioAsync(new() { InstanceId = Guid.NewGuid() }));
        await client.ReleaseAudioAsync(new() { InstanceId = instance.InstanceId });
        Assert.AreEqual(1, provider.Released);
        await client.CreateAudioAsync(new() { Source = new() { SourceId = "audio", DecoderName = "ExampleAudio" } });
        await worker.DisconnectAsync();
        Assert.AreEqual(2, provider.Released);
    }

    [TestMethod]
    public async Task ProjectAudioSourcesCaptureTheirHostAndPersistTheirCatalog()
    {
        using var files = new ProjectExternalSourceTests.SourceFiles();
        var root = files.Directory("audio-project");
        var asset = await ProjectExternalSourceDatabase.ImportAsync(root, await files.CreateAsync("audio-package"));
        var previous = ProjectExternalSourceRuntime.StartWorker;
        var provider = new TestAudioSourceProvider();
        await using var host = new ProjectExternalSourceHost();
        try
        {
            ProjectExternalSourceRuntime.StartWorker = async (_, _, _) => await TestWorker.StartAsync(files.Directory("audio-host"), provider);
            using var sourceScope = ProjectExternalSourceRuntime.Use(host);
            using var track = new projectFrameCut.Render.ClipsAndTracks.NormalSoundTrack { FilePath = projectFrameCut.Render.EncodeAndDecode.ProjectExternalAudioSource.CreatePath(asset.ImportId, provider.AudioSources[0]), ExtraData = new() };
            track.ReInit();
            Assert.AreEqual("ExternalSourceUnavailable", projectFrameCut.Render.ClipsAndTracks.ClipInitializationFailure.GetStage(track.ExtraData));
            Assert.AreEqual(0, track.SamplePerSecond);
            var catalog = await host.SetAsync(new() { ProjectRoot = root, AllowedSources = ProjectExternalSourceDatabase.ParseApprovals(root, "all") });
            track.ReInit();
            Assert.AreEqual(48000, track.SamplePerSecond);
            Assert.IsFalse(projectFrameCut.Render.ClipsAndTracks.ClipInitializationFailure.IsMarked(track.ExtraData));
            var descriptor = catalog.Sources.Single().AudioSources.Single();
            Assert.AreEqual(1, ProjectExternalSourceDatabase.Read(root).Assets.Single().AudioSources.Count);
            using var source = projectFrameCut.Render.EncodeAndDecode.ProjectExternalAudioSource.Open(
                projectFrameCut.Render.EncodeAndDecode.ProjectExternalAudioSource.CreatePath(asset.ImportId, descriptor), host);
            source.Initialize();
            var samples = source.GetSample(250, 3);
            Assert.AreEqual(0.25f, samples.GetSamples(0)[0]);
            Assert.AreEqual(-0.25f, samples.GetSamples(1)[0]);
            Assert.AreEqual(0.25f, source.GetSingleSample(250));
            await host.CloseAsync();
            Assert.AreEqual(2, provider.Released);
            track.ReInit();
            Assert.AreEqual(0, track.SamplePerSecond);
            await host.SetAsync(new() { ProjectRoot = root, AllowedSources = ProjectExternalSourceDatabase.ParseApprovals(root, "all") });
            track.ReInit();
            Assert.AreEqual(48000, track.SamplePerSecond);
            Assert.AreEqual(0.25f, source.GetSingleSample(250));
            Assert.IsFalse(projectFrameCut.Render.ClipsAndTracks.ClipInitializationFailure.IsMarked(track.ExtraData));
        }
        finally { ProjectExternalSourceRuntime.StartWorker = previous; }
    }

    [TestMethod]
    [DataRow(8, IsolationPayloadKind.Inline, false)]
    [DataRow(16, IsolationPayloadKind.SharedMemory, true)]
    [DataRow(16, IsolationPayloadKind.LocalFile, true)]
    public async Task FramesCrossTheWorkerChannelWithRegionAlphaAndHdr(int bits, IsolationPayloadKind kind, bool hdr)
    {
        using var files = new ProjectExternalSourceTests.SourceFiles();
        var provider = new Provider(bits);
        await using var worker = await TestWorker.StartAsync(files.Directory("session"), provider, kind);
        var client = new ProjectExternalSourceClient(worker);
        await client.LoadAsync();
        Assert.AreEqual(2, client.Sources.Count);
        Assert.IsFalse(client.Sources[0].AllowCachingResult);
        var instance = await client.CreateAsync(new() { Source = new() { SourceId = "main", DecoderName = "Example" } });
        Assert.IsFalse(instance.Descriptor.AllowCachingResult);
        var initialized = await client.InitializeAsync(new() { InstanceId = instance.InstanceId });
        Assert.IsFalse(initialized.Descriptor.AllowCachingResult);
        var frame = await client.ReadFrameAsync(new()
        {
            State = new() { InstanceId = instance.InstanceId }, TargetFrame = 7,
            UseRegion = true, SourceX = 3, SourceY = 4, SourceWidth = 10, SourceHeight = 20,
            TargetWidth = 2, TargetHeight = 1, HasAlpha = true, RequestHdr = hdr,
        });
        Assert.AreEqual(bits, frame.BitsPerChannel);
        Assert.AreEqual(2, frame.Width);
        Assert.AreEqual(1, frame.Height);
        Assert.AreEqual(3, provider.LastRead!.SourceX);
        Assert.AreEqual(4, provider.LastRead.SourceY);
        Assert.AreEqual(7u, provider.LastRead.TargetFrame);
        Assert.AreEqual(0.5f, (float)BitConverter.ToHalf(frame.Alpha));
        if (hdr)
        {
            Assert.AreEqual(1000f, (float)frame.MaximumBrightness);
            Assert.AreEqual(2f, (float)BitConverter.ToHalf(frame.Brightness));
            Assert.AreEqual((ushort)12345, BinaryPrimitives.ReadUInt16LittleEndian(frame.Red));
        }
        else Assert.AreEqual((byte)123, frame.Red[0]);
        Assert.IsFalse(Directory.EnumerateFiles(Path.Combine(files.Root, "session", "payloads")).Any());
        await client.ReleaseAsync(new() { InstanceId = instance.InstanceId });
        Assert.AreEqual(1, provider.Released);
    }

    [TestMethod]
    public async Task NegotiationAndInstanceOwnershipAreEnforced()
    {
        using var files = new ProjectExternalSourceTests.SourceFiles();
        using var lifetime = new CancellationTokenSource();
        await using var payloads = new SessionPayloadExchange(files.Directory("unauthenticated"));
        await using var service = new ProjectExternalSourceWorkerService(new Provider(8), "secret", Environment.ProcessId, payloads, lifetime);
        var response = await service.DispatchAsync(new() { Operation = RenderOperation.IsolationListProjectExternalSources });
        Assert.IsNotNull(response.Error);
        response = await service.DispatchAsync(new() { Operation = RenderOperation.IsolationNegotiate, Payload = RenderRpcSerializer.Serialize(new IsolationNegotiateRequest { AuthenticationToken = "wrong", HostProcessId = Environment.ProcessId }) });
        Assert.IsNotNull(response.Error);
        await using var first = await TestWorker.StartAsync(files.Directory("first"), new Provider(8));
        await using var second = await TestWorker.StartAsync(files.Directory("second"), new Provider(8));
        var instance = await first.InvokeAsync<ExternalVideoSourceCreateRequest, ExternalVideoSourceInstance>(RenderOperation.ExternalVideoSourceCreate,
            new() { Source = new() { SourceId = "main", DecoderName = "Example" } });
        response = await second.Control.SendAsync(new() { Operation = RenderOperation.ExternalVideoSourceInitialize, Payload = RenderRpcSerializer.Serialize(new ExternalVideoSourceStateRequest { InstanceId = instance.InstanceId }) });
        Assert.IsNotNull(response.Error);
    }

    [TestMethod]
    public async Task AllowedSourcesAreScopedToHostsAndDisconnectedWorkersReleaseInstances()
    {
        using var files = new ProjectExternalSourceTests.SourceFiles();
        var root = files.Directory("project");
        var asset = await ProjectExternalSourceDatabase.ImportAsync(root, await files.CreateAsync("package"));
        var previous = ProjectExternalSourceRuntime.StartWorker;
        List<TestWorker> workers = [];
        await using var first = new ProjectExternalSourceHost();
        await using var second = new ProjectExternalSourceHost();
        try
        {
            ProjectExternalSourceRuntime.StartWorker = async (_, _, _) =>
            {
                var worker = await TestWorker.StartAsync(files.Directory("session-" + workers.Count), new Provider(16));
                workers.Add(worker);
                return worker;
            };
            await first.SetAsync(new() { ProjectRoot = root });
            Assert.AreEqual(0, workers.Count);
            var wrong = await first.SetAsync(new() { ProjectRoot = root, AllowedSources = [new() { ImportId = asset.ImportId, ManifestSha256 = new string('0', 64) }] });
            Assert.IsFalse(wrong.Sources.Single().Loaded);
            Assert.IsFalse(string.IsNullOrEmpty(wrong.Sources.Single().Error));
            Assert.AreEqual(0, workers.Count);
            var request = new SetProjectExternalSourcesRequest { ProjectRoot = root, AllowedSources = ProjectExternalSourceDatabase.ParseApprovals(root, "all") };
            Assert.IsTrue((await first.SetAsync(request)).Sources.Single().Loaded);
            Assert.IsTrue((await second.SetAsync(request)).Sources.Single().Loaded);
            Assert.AreEqual(2, workers.Count);
            Assert.AreNotSame(first.GetClient(asset.ImportId), second.GetClient(asset.ImportId));
            using (var decoder = projectFrameCut.Render.EncodeAndDecode.ProjectExternalVideoSource.Open(
                projectFrameCut.Render.EncodeAndDecode.ProjectExternalVideoSource.CreatePath(asset.ImportId, new() { SourceId = "main", DecoderName = "Example" }), first))
            {
                decoder.Initialize();
                using var frame = decoder.GetFrame(7, 0, 0, 16, 16, 2, 1);
                Assert.IsTrue(workers[0].Provider.LastRead!.HasAlpha);
                Assert.IsTrue(workers[0].Provider.LastRead.UseRegion);
            }
            await first.GetClient(asset.ImportId).CreateAsync(new() { Source = new() { SourceId = "main", DecoderName = "Example" } });
            var token = first.CacheToken;
            await workers[0].DisconnectAsync();
            Assert.AreNotEqual(token, first.CacheToken);
            Assert.IsFalse((await first.ListAsync(root)).Sources.Single().Loaded);
            Assert.IsTrue((await second.ListAsync(root)).Sources.Single().Loaded);
            Assert.AreEqual(2, workers[0].Provider.Released);
            await first.SetAsync(new() { ProjectRoot = root });
            Assert.AreEqual(2, workers.Count);
            await second.CloseAsync();
            Assert.IsTrue(workers[1].Control.Completion.IsCompleted);
        }
        finally
        {
            ProjectExternalSourceRuntime.StartWorker = previous;
            foreach (var worker in workers) await worker.DisposeAsync();
        }
    }

    private sealed class Provider(int bits) : IExternalVideoSourceProvider
    {
        private readonly HashSet<Guid> instances = [];
        public int Released { get; private set; }
        public ExternalVideoSourceReadRequest? LastRead { get; private set; }
        public IReadOnlyList<ExternalVideoSourceDescriptor> Sources { get; } = [new() { SourceId = "main", DecoderName = "Example", Name = "Main", Width = 16, Height = 16, SupportsHdr = true, AllowCachingResult = false }, new() { SourceId = "second", DecoderName = "Example", Name = "Second" }];
        public ValueTask<ExternalVideoSourceInstance> CreateAsync(ExternalVideoSourceCreateRequest request, CancellationToken cancellationToken = default)
        {
            var id = Guid.NewGuid();
            instances.Add(id);
            return ValueTask.FromResult(new ExternalVideoSourceInstance { InstanceId = id, Descriptor = Sources.Single(x => x.SourceId == request.Source.SourceId) });
        }
        public ValueTask<ExternalVideoSourceInstance> InitializeAsync(ExternalVideoSourceStateRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(new ExternalVideoSourceInstance { InstanceId = request.InstanceId, Descriptor = Sources[0] });
        public ValueTask<ExternalVideoFrame> ReadFrameAsync(ExternalVideoSourceReadRequest request, CancellationToken cancellationToken = default)
        {
            LastRead = RenderRpcSerializer.Clone(request);
            var pixels = request.TargetWidth * request.TargetHeight;
            var red = new byte[pixels * bits / 8];
            for (var i = 0; i < pixels; i++)
                if (bits == 8) red[i] = 123;
                else BinaryPrimitives.WriteUInt16LittleEndian(red.AsSpan(i * 2), 12345);
            return ValueTask.FromResult(new ExternalVideoFrame
            {
                Width = request.TargetWidth, Height = request.TargetHeight, BitsPerChannel = bits,
                Red = red, Green = red.ToArray(), Blue = red.ToArray(),
                Alpha = request.HasAlpha ? Halves(pixels, (Half)0.5f) : [], Brightness = request.RequestHdr ? Halves(pixels, (Half)2) : [], MaximumBrightness = (Half)1000,
            });
        }
        public ValueTask ReleaseAsync(ExternalVideoSourceStateRequest request, CancellationToken cancellationToken = default)
        {
            if (instances.Remove(request.InstanceId)) Released++;
            return ValueTask.CompletedTask;
        }
        private static byte[] Halves(int count, Half value)
        {
            var bytes = new byte[count * 2];
            for (var i = 0; i < count; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), value);
            return bytes;
        }
    }

    private sealed class TestWorker : IPluginIsolationSession
    {
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(20));
        private readonly SessionPayloadExchange workerPayloads;
        private readonly ProjectExternalSourceWorkerService service;
        private readonly NamedPipeClientStream pipe;
        private readonly Task dispatcher;
        private PluginIsolationSession session = null!;
        private int disposed;
        private readonly object provider;
        public Provider Provider => (Provider)provider;
        private TestWorker(string root, object provider, NamedPipeClientStream pipe)
        {
            this.provider = provider;
            this.pipe = pipe;
            workerPayloads = new(root);
            service = new(provider, "test-token", Environment.ProcessId, workerPayloads, lifetime);
            dispatcher = StreamIsolationRequestDispatcher.RunAsync(pipe, service, lifetime.Token);
        }
        public static async Task<TestWorker> StartAsync(string root, object provider, IsolationPayloadKind kind = IsolationPayloadKind.SharedMemory)
        {
            var name = "pjfc-source-test-" + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Task.WhenAll(server.WaitForConnectionAsync(timeout.Token), pipe.ConnectAsync(timeout.Token));
            var worker = new TestWorker(root, provider, pipe);
            var channel = new StreamIsolationControlChannel(server, IsolationControlMode.NamedPipe);
            var response = await channel.SendAsync(new() { Operation = RenderOperation.IsolationNegotiate, Payload = RenderRpcSerializer.Serialize(new IsolationNegotiateRequest { AuthenticationToken = "test-token", HostProcessId = Environment.ProcessId, PreferredPayloadKind = kind }) }, timeout.Token);
            Assert.IsNull(response.Error);
            worker.session = new("test", channel, new SessionPayloadExchange(root), new SessionResourceBroker(root), RenderRpcSerializer.Deserialize<IsolationChannelCapabilities>(response.Payload), new() { PayloadMode = kind }, _ => { worker.lifetime.Cancel(); return ValueTask.CompletedTask; });
            return worker;
        }
        public string PluginId => session.PluginId;
        public IIsolationControlChannel Control => session.Control;
        public IIsolationPayloadExchange Payloads => session.Payloads;
        public IIsolationResourceBroker Resources => session.Resources;
        public IsolationChannelCapabilities Capabilities => session.Capabilities;
        public IsolationPayloadKind PreferredPayloadKind => session.PreferredPayloadKind;
        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(RenderOperation operation, TRequest request, CancellationToken cancellationToken = default) => session.InvokeAsync<TRequest, TResponse>(operation, request, cancellationToken);
        public ValueTask TerminateAsync(string reason) => session.TerminateAsync(reason);
        public async Task DisconnectAsync()
        {
            await pipe.DisposeAsync();
            await Control.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await DisposeAsync();
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            await session.DisposeAsync();
            lifetime.Cancel();
            await pipe.DisposeAsync();
            try { await dispatcher; } catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
            await service.DisposeAsync();
            await workerPayloads.DisposeAsync();
            lifetime.Dispose();
        }
    }
}
