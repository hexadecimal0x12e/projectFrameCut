using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.PluginIsolation;
using projectFrameCut.Render.RPCProtocol;
using System.Buffers.Binary;
using System.IO.Pipes;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ProjectExternalSourceWorkerTests
{
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
        Assert.AreEqual(0.5f, BitConverter.ToSingle(frame.Alpha));
        if (hdr)
        {
            Assert.AreEqual(1000f, frame.MaximumBrightness);
            Assert.AreEqual(2f, BitConverter.ToSingle(frame.Brightness));
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
                Alpha = request.HasAlpha ? Floats(pixels, 0.5f) : [], Brightness = request.RequestHdr ? Floats(pixels, 2f) : [], MaximumBrightness = 1000,
            });
        }
        public ValueTask ReleaseAsync(ExternalVideoSourceStateRequest request, CancellationToken cancellationToken = default)
        {
            if (instances.Remove(request.InstanceId)) Released++;
            return ValueTask.CompletedTask;
        }
        private static byte[] Floats(int count, float value)
        {
            var bytes = new byte[count * 4];
            for (var i = 0; i < count; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), value);
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
        public Provider Provider { get; }
        private TestWorker(string root, Provider provider, NamedPipeClientStream pipe)
        {
            Provider = provider;
            this.pipe = pipe;
            workerPayloads = new(root);
            service = new(provider, "test-token", Environment.ProcessId, workerPayloads, lifetime);
            dispatcher = StreamIsolationRequestDispatcher.RunAsync(pipe, service, lifetime.Token);
        }
        public static async Task<TestWorker> StartAsync(string root, Provider provider, IsolationPayloadKind kind = IsolationPayloadKind.SharedMemory)
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
