using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RPCProtocol;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class UnixSocketRenderTransportTests
{
    [TestMethod]
    public async Task UnixSocketTransportAuthenticatesAndSupportsConcurrentRequests()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) return;
        var socketPath = CreateSocketPath();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverTask = new UnixSocketRenderServer(new CapabilityService())
            .RunAsync(socketPath, "correct-token", cancellation.Token);
        try
        {
            await using var client = new RenderClient(
                new UnixSocketRenderClientTransport(socketPath, "correct-token", "socket-test"),
                "socket-test");

            var requests = Enumerable.Range(0, 16)
                .Select(_ => client.GetCapabilitiesAsync(cancellation.Token).AsTask())
                .ToArray();
            var responses = await Task.WhenAll(requests);

            Assert.AreEqual(16, responses.Length);
            Assert.IsTrue(responses.All(static response => response.Features.Contains("unix-socket-test")));
        }
        finally
        {
            cancellation.Cancel();
            try { await serverTask; } catch (OperationCanceledException) { }
            try { if (File.Exists(socketPath)) File.Delete(socketPath); } catch { }
        }
    }

    [TestMethod]
    public async Task UnixSocketTransportRejectsAnInvalidToken()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) return;
        var socketPath = CreateSocketPath();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverTask = new UnixSocketRenderServer(new CapabilityService())
            .RunAsync(socketPath, "correct-token", cancellation.Token);
        try
        {
            await using var client = new RenderClient(
                new UnixSocketRenderClientTransport(socketPath, "wrong-token", "socket-test"),
                "socket-test");

            await Assert.ThrowsAsync<RenderPipeException>(async () =>
                _ = await client.GetCapabilitiesAsync(cancellation.Token));
        }
        finally
        {
            cancellation.Cancel();
            try { await serverTask; } catch (OperationCanceledException) { }
            try { if (File.Exists(socketPath)) File.Delete(socketPath); } catch { }
        }
    }

    [TestMethod]
    public async Task UnixSocketTransportFramesLargeAndOutOfOrderResponses()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) return;
        var socketPath = CreateSocketPath();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverTask = new UnixSocketRenderServer(new DelayedEchoService())
            .RunAsync(socketPath, "correct-token", cancellation.Token);
        try
        {
            await using var transport = new UnixSocketRenderClientTransport(socketPath, "correct-token", "socket-test");
            var slowPayload = new byte[2 * 1024 * 1024];
            slowPayload[0] = 100;
            var fastPayload = new byte[] { 1, 2, 3, 4 };
            var slowRequest = new RenderRequestEnvelope { RequestId = Guid.NewGuid(), Payload = slowPayload };
            var fastRequest = new RenderRequestEnvelope { RequestId = Guid.NewGuid(), Payload = fastPayload };

            var slowTask = transport.SendAsync(slowRequest, cancellation.Token).AsTask();
            var fastTask = transport.SendAsync(fastRequest, cancellation.Token).AsTask();
            var fastResponse = await fastTask;
            var slowResponse = await slowTask;

            CollectionAssert.AreEqual(fastPayload, fastResponse.Payload);
            Assert.AreEqual(slowPayload.Length, slowResponse.Payload.Length);
            Assert.AreEqual(slowRequest.RequestId, slowResponse.RequestId);
            Assert.AreEqual(fastRequest.RequestId, fastResponse.RequestId);
        }
        finally
        {
            cancellation.Cancel();
            try { await serverTask; } catch (OperationCanceledException) { }
            try { if (File.Exists(socketPath)) File.Delete(socketPath); } catch { }
        }
    }

    private static string CreateSocketPath()
        => Path.Combine(Path.GetTempPath(), $"pjfc-{Guid.NewGuid():N}.sock");

    private sealed class CapabilityService : IRenderService
    {
        public ValueTask<RenderResponseEnvelope> DispatchAsync(RenderRequestEnvelope request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new RenderResponseEnvelope
            {
                RequestId = request.RequestId,
                Payload = RenderRpcSerializer.Serialize(new RenderCapabilities
                {
                    ProtocolVersion = RenderProtocol.CurrentVersion,
                    MinimumProtocolVersion = RenderProtocol.MinimumSupportedVersion,
                    Features = ["unix-socket-test"],
                }),
            });
    }

    private sealed class DelayedEchoService : IRenderService
    {
        public async ValueTask<RenderResponseEnvelope> DispatchAsync(RenderRequestEnvelope request, CancellationToken cancellationToken = default)
        {
            await Task.Delay(request.Payload.Length > 0 ? request.Payload[0] : 0, cancellationToken);
            return new RenderResponseEnvelope
            {
                RequestId = request.RequestId,
                Payload = request.Payload,
            };
        }
    }
}
