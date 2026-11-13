using System.Buffers.Binary;
using System.IO.Pipes;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RPCProtocol;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class NamedPipeRenderTransportTests
{
    [TestMethod]
    public async Task ClosingDuringHandshakeCancelsConnectingAndQueuedRequests()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var name = $"pjfc-test-{Guid.NewGuid():N}";
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var transport = new NamedPipeRenderClientTransport(name, "internal-token", "pipe-test");
        var connected = pipe.WaitForConnectionAsync(timeout.Token);
        var first = transport.SendAsync(new() { RequestId = Guid.NewGuid() }, timeout.Token).AsTask();
        await connected;
        Assert.IsNotNull(await RenderPipeFrame.ReadAsync(pipe, timeout.Token));
        var second = transport.SendAsync(new() { RequestId = Guid.NewGuid() }, timeout.Token).AsTask();

        await transport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        });
        Assert.IsTrue(first.IsCanceled);
        Assert.IsTrue(second.IsCanceled);
        var ex = await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await transport.SendAsync(new() { RequestId = Guid.NewGuid() });
        });
        Assert.AreEqual(typeof(NamedPipeRenderClientTransport).FullName, ex.ObjectName);
    }

    [TestMethod]
    public async Task ClosingDuringWriteCancelsWriterAndQueuedRequests()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var name = $"pjfc-test-{Guid.NewGuid():N}";
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096);
        await using var transport = new NamedPipeRenderClientTransport(name, "internal-token", "pipe-test");
        var connected = pipe.WaitForConnectionAsync(timeout.Token);
        var first = transport.SendAsync(new() { RequestId = Guid.NewGuid(), Payload = new byte[8 * 1024 * 1024] }, timeout.Token).AsTask();
        await connected;
        Assert.IsNotNull(await RenderPipeFrame.ReadAsync(pipe, timeout.Token));
        await RenderPipeFrame.WriteAsync(pipe, RenderRpcSerializer.Serialize(new RenderPipeHandshake { Accepted = true }), timeout.Token);
        var header = new byte[sizeof(int)];
        await pipe.ReadExactlyAsync(header, timeout.Token);
        Assert.IsTrue(BinaryPrimitives.ReadInt32LittleEndian(header) > 8 * 1024 * 1024);
        var second = transport.SendAsync(new() { RequestId = Guid.NewGuid() }, timeout.Token).AsTask();

        await transport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        });
        Assert.IsTrue(first.IsCanceled);
        Assert.IsTrue(second.IsCanceled);
    }

    [TestMethod]
    public async Task NamedPipesExpireAfterFirstConnectionAndStopWithHost()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var name = $"pjfc-test-{Guid.NewGuid():N}";
        var server = new NamedPipeRenderServer(new CapabilityService(), allowAdditionalPipes: true)
            .RunAsync(name, "internal-token", cancellationToken: lifetime.Token);
        string[] tokens = [];
        try
        {
            await using (var owner = Connect(name, "internal-token"))
            {
                Assert.IsTrue((await owner.GetCapabilitiesAsync(lifetime.Token)).Operations.Contains(nameof(RenderOperation.CreateAdditionalPipe)));
                tokens = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
                    (await owner.CreateAdditionalPipeAsync(lifetime.Token)).Token));
                Assert.AreEqual(3, tokens.Distinct().Count());
                Assert.IsTrue(tokens.All(t => t.Length == 64 && t.All(Uri.IsHexDigit)));

                await Task.WhenAll(tokens.Select(async token =>
                {
                    await using var client = Connect(RenderProtocol.AdditionalPipePrefix + token, token);
                    var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetCapabilitiesAsync(lifetime.Token).AsTask()));
                    Assert.IsTrue(results.All(r => r.BackendVersion == "shared-instance"));
                    Assert.IsFalse(results[0].Operations.Contains(nameof(RenderOperation.CreateAdditionalPipe)));
                    await Assert.ThrowsAsync<RemoteRenderException>(async () => { await client.CreateAdditionalPipeAsync(lifetime.Token); });
                }));

                foreach (var token in tokens)
                    await AssertPipeUnavailableAsync(RenderProtocol.AdditionalPipePrefix + token);
                Assert.AreEqual("shared-instance", (await owner.GetCapabilitiesAsync(lifetime.Token)).BackendVersion);
            }
            await server.WaitAsync(TimeSpan.FromSeconds(5));
            await AssertPipeUnavailableAsync(name);
        }
        finally
        {
            lifetime.Cancel();
            try { await server.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }

    }

    [TestMethod]
    public async Task AdditionalPipeRejectsInvalidHandshakeAndKeepsListening()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var name = $"pjfc-test-{Guid.NewGuid():N}";
        var server = new NamedPipeRenderServer(new CapabilityService(), allowAdditionalPipes: true)
            .RunAsync(name, "internal-token", cancellationToken: lifetime.Token);
        try
        {
            await using var owner = Connect(name, "internal-token");
            var token = (await owner.CreateAdditionalPipeAsync(lifetime.Token)).Token;
            foreach (var handshake in new[]
            {
                new RenderPipeHandshake { ClientId = "test", Token = "" },
                new RenderPipeHandshake { ClientId = "test", Token = "wrong" },
                new RenderPipeHandshake { ClientId = "test", Token = token, ProtocolVersion = 999 },
            })
            {
                await using var pipe = new NamedPipeClientStream(".", RenderProtocol.AdditionalPipePrefix + token, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(lifetime.Token);
                var payload = RenderRpcSerializer.Serialize(handshake);
                var header = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
                await pipe.WriteAsync(header, lifetime.Token);
                await pipe.WriteAsync(payload, lifetime.Token);
                await pipe.ReadExactlyAsync(header, lifetime.Token);
                var response = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
                await pipe.ReadExactlyAsync(response, lifetime.Token);
                Assert.IsFalse(RenderRpcSerializer.Deserialize<RenderPipeHandshake>(response).Accepted);
            }
            await using var valid = Connect(RenderProtocol.AdditionalPipePrefix + token, token);
            Assert.AreEqual("shared-instance", (await valid.GetCapabilitiesAsync(lifetime.Token)).BackendVersion);
            await valid.DisposeAsync();
            await AssertPipeUnavailableAsync(RenderProtocol.AdditionalPipePrefix + token);
        }
        finally
        {
            lifetime.Cancel();
            try { await server.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
    }

    private static RenderClient Connect(string pipe, string token)
        => new(new NamedPipeRenderClientTransport(pipe, token, "pipe-test"), "pipe-test");

    [TestMethod]
    public async Task CancellationStopsActiveAndQueuedRequestsWithoutBlockingHealthChecks()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var firstCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        using var secondCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        using var service = new BlockingService();
        var name = $"pjfc-test-{Guid.NewGuid():N}";
        var server = new NamedPipeRenderServer(service).RunAsync(name, "internal-token", cancellationToken: lifetime.Token);
        await using var transport = new NamedPipeRenderClientTransport(name, "internal-token", "pipe-test");
        try
        {
            var first = transport.SendAsync(new() { Operation = RenderOperation.RenderClipPreview }, firstCts.Token).AsTask();
            await service.Entered.Task.WaitAsync(lifetime.Token);
            var second = transport.SendAsync(new() { Operation = RenderOperation.RenderClipPreview }, secondCts.Token).AsTask();
            await service.Queued.Task.WaitAsync(lifetime.Token);
            var health = await transport.SendAsync(new() { Operation = RenderOperation.GetCapabilities }, lifetime.Token)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsNull(health.Error);

            secondCts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => { await second; });
            firstCts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => { await first; });
            await service.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.AreEqual(0, service.Completed);

            service.Resume.Set();
            Assert.IsNull((await transport.SendAsync(new() { Operation = RenderOperation.RenderClipPreview }, lifetime.Token)).Error);
            Assert.AreEqual(1, service.Completed);
        }
        finally
        {
            firstCts.Cancel();
            secondCts.Cancel();
            service.Resume.Set();
            await transport.DisposeAsync();
            lifetime.Cancel();
            try { await server.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
    }

    private sealed class BlockingService : IRenderService, IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private int _requests;
        private int _canceled;
        public int Completed;
        public ManualResetEventSlim Resume { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<RenderResponseEnvelope> DispatchAsync(RenderRequestEnvelope request, CancellationToken cancellationToken = default)
        {
            if (request.Operation == RenderOperation.GetCapabilities)
                return new() { RequestId = request.RequestId, Payload = RenderRpcSerializer.Serialize(new RenderCapabilities()) };
            if (Interlocked.Increment(ref _requests) == 2) Queued.TrySetResult();
            try
            {
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    Entered.TrySetResult();
                    Resume.Wait(cancellationToken);
                    Interlocked.Increment(ref Completed);
                    return new() { RequestId = request.RequestId };
                }
                finally { _gate.Release(); }
            }
            catch (OperationCanceledException)
            {
                if (Interlocked.Increment(ref _canceled) == 2) Canceled.TrySetResult();
                throw;
            }
        }

        public void Dispose() { Resume.Dispose(); _gate.Dispose(); }
    }

    private static async Task AssertPipeUnavailableAsync(string pipeName)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAsync<OperationCanceledException>(() => pipe.ConnectAsync(timeout.Token));
    }

    private sealed class CapabilityService : IRenderService
    {
        public ValueTask<RenderResponseEnvelope> DispatchAsync(RenderRequestEnvelope request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(request.Operation == RenderOperation.GetCapabilities
                ? new RenderResponseEnvelope { RequestId = request.RequestId, Payload = RenderRpcSerializer.Serialize(new RenderCapabilities { BackendVersion = "shared-instance" }) }
                : new RenderResponseEnvelope { RequestId = request.RequestId, Error = new() { Code = RenderErrorCode.Unsupported, Message = "Unsupported operation." } });
    }
}
