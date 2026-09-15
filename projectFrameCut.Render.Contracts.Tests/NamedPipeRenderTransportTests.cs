using System.Buffers.Binary;
using System.IO.Pipes;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RPCProtocol;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class NamedPipeRenderTransportTests
{
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
