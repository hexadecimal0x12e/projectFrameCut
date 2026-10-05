using System.Security.Cryptography;
using System.Text.Json;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.RPCProtocol;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class ExternalRpcRequestTests
{
    public ExternalRpcRequestTests() =>
        ExternalRpcAuthorizationStore.SetEncryptionKey(new byte[32]);

    [TestMethod]
    public async Task PersistentAuthorizationUsesClientIdAndSignature()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pjfc-rpc-persistent-{Guid.NewGuid():N}");
        var directory = Path.Combine(root, "RpcRequest");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var rsa = RSA.Create(3072);
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        var clientId = Guid.NewGuid();
        var authorization = ExternalRpcAuthorizationStore.Add(ExternalRpcAuthorizationStore.GetPath(directory), new()
        {
            ClientId = clientId,
            AppName = "Persistent test",
            Author = "Tests",
            Purpose = "RPC",
            PublicKey = publicKey,
            PublicKeyFingerprint = ExternalRpcAuthorizationStore.Fingerprint(publicKey),
        });
        var pipe = $"pjfc-persistent-test-{Guid.NewGuid():N}";
        var server = new NamedPipeRenderServer(new CapabilityService(), true, directory)
            .RunAsync(pipe, "owner", cancellationToken: lifetime.Token);
        try
        {
            await using var owner = new RenderClient(new NamedPipeRenderClientTransport(pipe, "owner", "test", [authorization]));
            var sessionId = Guid.NewGuid();
            await owner.RegisterGuiProjectAsync(new() { SessionId = sessionId }, lifetime.Token);
            var request = new ExternalRpcRequest
            {
                Version = 2,
                Mode = "persistent",
                ClientId = clientId,
                ServiceId = "external-source",
                PublicKey = publicKey,
            };
            request.Sign(rsa);
            var path = Path.Combine(directory, $"{request.RequestId:N}.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(request));
            PendingExternalRpcRequest pending;
            do
            {
                pending = await owner.GetExternalRpcRequestAsync(lifetime.Token);
                if (pending.ClaimId == Guid.Empty) await Task.Delay(100, lifetime.Token);
            } while (pending.ClaimId == Guid.Empty);
            await owner.ResolveExternalRpcRequestAsync(new() { ClaimId = pending.ClaimId, Approved = true, GuiSessionId = sessionId }, lifetime.Token);
            var response = ExternalRpcRequest.ReadFile(path);
            var connection = JsonSerializer.Deserialize<ExternalRpcConnection>(rsa.Decrypt(
                Convert.FromBase64String(response.EncryptedConnection), RSAEncryptionPadding.OaepSHA256))!;
            Assert.AreEqual(clientId, connection.ClientId);
            Assert.AreEqual("external-source", connection.ServiceId);
            Assert.IsNotNull(ExternalRpcAuthorizationStore.Find(ExternalRpcAuthorizationStore.GetPath(directory), clientId)!.LastUsedAt);

            await using (var impostor = new RenderClient(new NamedPipeRenderClientTransport(connection.PipeName, Guid.NewGuid().ToString("D"))))
                await Assert.ThrowsAsync<RenderPipeException>(async () => await impostor.GetCapabilitiesAsync(lifetime.Token));

            var provider = new TestVideoSourceProvider();
            await using var external = new RenderClient(
                new NamedPipeRenderClientTransport(connection.PipeName, clientId.ToString("D")),
                clientId.ToString("D"), provider);
            await external.RegisterExternalVideoSourcesAsync(new() { Sources = provider.Sources.ToList() }, lifetime.Token);
            var catalog = await owner.ListExternalVideoSourcesAsync(lifetime.Token);
            Assert.AreEqual(1, catalog.Sources.Count);
            Assert.AreEqual(clientId, catalog.Sources[0].ClientId);
            Assert.IsTrue(catalog.Sources[0].AllowCachingResult);
            using (var source = new RemoteRpcVideoSource(RemoteRpcVideoSource.CreatePath(catalog.Sources[0])))
            {
                source.Initialize();
                Assert.IsFalse(source.AllowCachingResult);
                using var frame = source.GetFrame(0);
                Assert.AreEqual(2, frame.Width);
                Assert.AreEqual(1, frame.Height);
            }

            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await external.ManageExternalVideoSourceClientAsync(
                new() { ClientId = clientId, Action = ExternalVideoSourceClientAction.Remove }, lifetime.Token));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await external.ListExternalVideoSourceClientsAsync(lifetime.Token));
            using (var active = new RemoteRpcVideoSource(RemoteRpcVideoSource.CreatePath(catalog.Sources[0])))
            {
                active.Initialize();
                var released = provider.Released;
                await owner.ManageExternalVideoSourceClientAsync(new() { ClientId = clientId, Action = ExternalVideoSourceClientAction.Unload }, lifetime.Token);
                Assert.AreEqual(released + 1, provider.Released);
                Assert.AreEqual(0, (await owner.ListExternalVideoSourcesAsync(lifetime.Token)).Sources.Count);
                var unloaded = (await owner.ListExternalVideoSourceClientsAsync(lifetime.Token)).Clients.Single();
                Assert.IsFalse(unloaded.Loaded);
                Assert.AreEqual(1, unloaded.Sources.Count);
                Assert.Throws<IOException>(() => active.GetFrame(0));
                await external.RegisterExternalVideoSourcesAsync(new() { Sources = provider.Sources.ToList() }, lifetime.Token);
                Assert.IsFalse((await owner.ListExternalVideoSourceClientsAsync(lifetime.Token)).Clients.Single().Loaded);
                await owner.ManageExternalVideoSourceClientAsync(new() { ClientId = clientId, Action = ExternalVideoSourceClientAction.Load }, lifetime.Token);
                using var reloaded = new RemoteRpcVideoSource(RemoteRpcVideoSource.CreatePath(catalog.Sources[0]));
                reloaded.Initialize();
                using var frame = reloaded.GetFrame(0);
                Assert.AreEqual(2, frame.Width);
            }
            await owner.ManageExternalVideoSourceClientAsync(new() { ClientId = clientId, Action = ExternalVideoSourceClientAction.Remove }, lifetime.Token);
            Assert.AreEqual(0, (await owner.ListExternalVideoSourceClientsAsync(lifetime.Token)).Clients.Count);
            Assert.IsFalse(ExternalRpcAuthorizationStore.Find(ExternalRpcAuthorizationStore.GetPath(directory), clientId)!.Revoked);
            await external.RegisterExternalVideoSourcesAsync(new() { Sources = provider.Sources.ToList() }, lifetime.Token);
            Assert.IsTrue((await owner.ListExternalVideoSourceClientsAsync(lifetime.Token)).Clients.Single().Loaded);

            var second = new ExternalRpcRequest
            {
                Version = 2,
                Mode = "persistent",
                ClientId = clientId,
                ServiceId = "another-service",
                PublicKey = publicKey,
            };
            second.Sign(rsa);
            var secondPath = Path.Combine(directory, $"{second.RequestId:N}.json");
            await File.WriteAllTextAsync(secondPath, JsonSerializer.Serialize(second));
            do
            {
                pending = await owner.GetExternalRpcRequestAsync(lifetime.Token);
                if (pending.ClaimId == Guid.Empty) await Task.Delay(100, lifetime.Token);
            } while (pending.ClaimId == Guid.Empty);
            await owner.ResolveExternalRpcRequestAsync(new() { ClaimId = pending.ClaimId, Approved = true, GuiSessionId = sessionId }, lifetime.Token);
            Assert.AreEqual("approved", ExternalRpcRequest.ReadFile(secondPath).Status);

            ExternalRpcAuthorizationStore.Revoke(ExternalRpcAuthorizationStore.GetPath(directory), clientId);
            await Task.Delay(1100, lifetime.Token);
            Assert.AreEqual(0, (await owner.ListExternalVideoSourcesAsync(lifetime.Token)).Sources.Count);
        }
        finally
        {
            lifetime.Cancel();
            try { await server; } catch (OperationCanceledException) { }
            Directory.Delete(root, true);
        }
    }

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
                Assert.IsFalse(File.ReadAllText(path).Contains(connection.PipeName));
                using var otherKey = RSA.Create(3072);
                Assert.Throws<CryptographicException>(() => otherKey.Decrypt(Convert.FromBase64String(response.EncryptedConnection), RSAEncryptionPadding.OaepSHA256));
                await using var external = new RenderClient(new NamedPipeRenderClientTransport(connection.PipeName, "external"));
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

    private sealed class TestVideoSourceProvider : IExternalVideoSourceProvider
    {
        public int Released { get; private set; }
        private readonly ExternalVideoSourceDescriptor _source = new()
        {
            SourceId = "test-source",
            Name = "Test source",
            DecoderName = "TestDecoder",
            TotalFrames = 10,
            Fps = 25,
            Width = 2,
            Height = 1,
            ResultBitsPerPixel = 8,
            HasKnownResultBitsPerPixel = true,
            SupportsAlpha = true,
        };

        public IReadOnlyList<ExternalVideoSourceDescriptor> Sources => [_source];

        public ValueTask<ExternalVideoSourceInstance> CreateAsync(ExternalVideoSourceCreateRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExternalVideoSourceInstance { InstanceId = Guid.NewGuid(), Descriptor = _source });

        public ValueTask<ExternalVideoSourceInstance> InitializeAsync(ExternalVideoSourceStateRequest request, CancellationToken cancellationToken = default)
        {
            var descriptor = RenderRpcSerializer.Clone(_source);
            descriptor.AllowCachingResult = false;
            return ValueTask.FromResult(new ExternalVideoSourceInstance { InstanceId = request.InstanceId, Descriptor = descriptor });
        }

        public ValueTask<ExternalVideoFrame> ReadFrameAsync(ExternalVideoSourceReadRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExternalVideoFrame
            {
                Width = 2,
                Height = 1,
                BitsPerChannel = 8,
                Red = [255, 0],
                Green = [0, 255],
                Blue = [0, 0],
                Alpha = new byte[8],
            });

        public ValueTask ReleaseAsync(ExternalVideoSourceStateRequest request, CancellationToken cancellationToken = default)
        {
            Released++;
            return ValueTask.CompletedTask;
        }
    }
}
