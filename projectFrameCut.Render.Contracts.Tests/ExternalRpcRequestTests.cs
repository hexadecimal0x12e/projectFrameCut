using System.Security.Cryptography;
using System.Text.Json;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RPCProtocol;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class ExternalRpcRequestTests
{
    [TestMethod]
    public async Task AuthorizationEncryptsConnectionAndRejectsExternalManagement()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pjfc-rpc-request-{Guid.NewGuid():N}");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var rsa = RSA.Create(3072);
        var pipe = $"pjfc-request-test-{Guid.NewGuid():N}";
        var server = new NamedPipeRenderServer(new CapabilityService(), true, directory)
            .RunAsync(pipe, "owner", cancellationToken: lifetime.Token);
        try
        {
            await using var owner = new RenderClient(new NamedPipeRenderClientTransport(pipe, "owner", "test"));
            var sessionId = Guid.NewGuid();
            await owner.RegisterGuiProjectAsync(new() { SessionId = sessionId }, lifetime.Token);
            foreach (var approved in new[] { false, true })
            {
                var request = new ExternalRpcRequest
                {
                    AppName = "Test app", Author = "Test author", Purpose = "Test approval",
                    PublicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()),
                };
                var path = Path.Combine(directory, $"{request.RequestId:N}.json");
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(request));
                PendingExternalRpcRequest pending;
                do
                {
                    pending = await owner.GetExternalRpcRequestAsync(lifetime.Token);
                    if (pending.ClaimId == Guid.Empty) await Task.Delay(100, lifetime.Token);
                } while (pending.ClaimId == Guid.Empty);
                Assert.AreEqual(request.RequestId, ExternalRpcRequest.ReadFile(path).RequestId);
                if (OperatingSystem.IsWindows())
                    Assert.Throws<IOException>(() => File.WriteAllText(path, "{}"));
                await owner.ResolveExternalRpcRequestAsync(new() { ClaimId = pending.ClaimId, Approved = approved, GuiSessionId = sessionId }, lifetime.Token);
                var response = ExternalRpcRequest.ReadFile(path);
                Assert.AreEqual(approved ? "approved" : "denied", response.Status);
                if (!approved) { Assert.AreEqual("", response.EncryptedConnection); continue; }
                var connection = JsonSerializer.Deserialize<ExternalRpcConnection>(rsa.Decrypt(
                    Convert.FromBase64String(response.EncryptedConnection), RSAEncryptionPadding.OaepSHA256))!;
                Assert.AreEqual(request.RequestId, connection.RequestId);
                Assert.IsFalse(File.ReadAllText(path).Contains(connection.Token));
                using var otherKey = RSA.Create(3072);
                Assert.Throws<CryptographicException>(() => otherKey.Decrypt(Convert.FromBase64String(response.EncryptedConnection), RSAEncryptionPadding.OaepSHA256));
                await using var external = new RenderClient(new NamedPipeRenderClientTransport(connection.PipeName, connection.Token, "external"));
                Assert.IsTrue((await external.GetCapabilitiesAsync(lifetime.Token)).Operations.Contains(nameof(RenderOperation.InvokeGuiProject)));
                Assert.AreEqual(sessionId, (await external.GetGuiProjectSessionAsync(new(), lifetime.Token)).SessionId);
                await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => { await external.GetExternalRpcRequestAsync(lifetime.Token); });
                await Assert.ThrowsAsync<ArgumentException>(async () => { await owner.ResolveExternalRpcRequestAsync(new() { ClaimId = pending.ClaimId, Approved = true, GuiSessionId = sessionId }, lifetime.Token); });
            }
        }
        finally
        {
            lifetime.Cancel();
            try { await server; } catch (OperationCanceledException) { }
            Directory.Delete(directory, true);
        }
    }

    private sealed class CapabilityService : IRenderService
    {
        public ValueTask<RenderResponseEnvelope> DispatchAsync(RenderRequestEnvelope request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(request.Operation == RenderOperation.GetCapabilities
                ? new RenderResponseEnvelope { RequestId = request.RequestId, Payload = RenderRpcSerializer.Serialize(new RenderCapabilities { BackendVersion = "test" }) }
                : new RenderResponseEnvelope { RequestId = request.RequestId, Error = new() { Code = RenderErrorCode.Unsupported } });
    }
}
