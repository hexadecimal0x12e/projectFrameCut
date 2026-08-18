using projectFrameCut.IntegratedAPIServer;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RPCProtocol;
using System.Net;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class HeadlessRpcTests
{
    private const string ValidToken = "headless-test-token-with-at-least-32-characters";

    [TestMethod]
    public void TokenValidationRejectsShortOrWhitespaceTokens()
    {
        Assert.ThrowsExactly<ArgumentException>(() => IntegratedApiServer.ValidateRpcToken("short"));
        Assert.ThrowsExactly<ArgumentException>(() => IntegratedApiServer.ValidateRpcToken(new string('x', 31) + " "));
        IntegratedApiServer.ValidateRpcToken(ValidToken);
    }

    [TestMethod]
    public async Task HttpTransportSendsBearerAndProtobufEnvelope()
    {
        var handler = new ProtobufHandler();
        using var httpClient = new HttpClient(handler);
        await using var transport = new HttpRenderClientTransport(
            new Uri("http://127.0.0.1:5080"), ValidToken, "test-client", httpClient);
        var request = new RenderRequestEnvelope
        {
            RequestId = Guid.NewGuid(),
            ClientId = "test-client",
            Operation = RenderOperation.GetCapabilities,
            Payload = RenderRpcSerializer.Serialize(new EmptyRequest()),
        };

        RenderResponseEnvelope response = await transport.SendAsync(request);

        Assert.AreEqual(request.RequestId, response.RequestId);
        Assert.AreEqual("Bearer", handler.AuthorizationScheme);
        Assert.AreEqual(ValidToken, handler.AuthorizationParameter);
        Assert.AreEqual("application/x-protobuf", handler.ContentType);
        Assert.AreEqual("/rpc", handler.RequestPath);
    }

    [TestMethod]
    public async Task SnapshotMutationUsesOptimisticConcurrency()
    {
        string root = CreateEmptyProject();
        try
        {
            await using var service = new HeadlessProjectService();
            var openRequest = Request(RenderOperation.OpenHeadlessProject, new OpenHeadlessProjectRequest { ProjectRoot = root });
            RenderResponseEnvelope openResponse = await service.DispatchAsync(openRequest);
            Assert.IsNull(openResponse.Error);
            HeadlessProjectSnapshot opened = RenderRpcSerializer.Deserialize<HeadlessProjectSnapshot>(openResponse.Payload);

            var precondition = new HeadlessMutationPrecondition
            {
                SessionId = opened.SessionId,
                BaseRevision = opened.Revision,
                BaseSnapshotHash = opened.SnapshotHash,
            };
            var apply = new ApplyHeadlessProjectSnapshotRequest
            {
                Precondition = precondition,
                ProjectJson = opened.ProjectJson,
                TimelineJson = "{\"Clips\":[],\"SoundTracks\":[],\"Duration\":1}",
                AssetsJson = opened.AssetsJson,
            };

            RenderResponseEnvelope first = await service.DispatchAsync(Request(RenderOperation.ApplyHeadlessProjectSnapshot, apply));
            RenderResponseEnvelope conflicting = await service.DispatchAsync(Request(RenderOperation.ApplyHeadlessProjectSnapshot, apply));

            Assert.IsNull(first.Error);
            HeadlessProjectSnapshot changed = RenderRpcSerializer.Deserialize<HeadlessProjectSnapshot>(first.Payload);
            Assert.AreEqual(opened.Revision + 1, changed.Revision);
            Assert.AreNotEqual(opened.SnapshotHash, changed.SnapshotHash);
            Assert.AreEqual(RenderErrorCode.VersionConflict, conflicting.Error?.Code);
            StringAssert.Contains(conflicting.Error?.Details ?? string.Empty, "currentRevision");

            await service.DispatchAsync(Request(RenderOperation.CloseProject, new SessionRequest { SessionId = opened.SessionId }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static RenderRequestEnvelope Request<T>(RenderOperation operation, T payload) => new()
    {
        RequestId = Guid.NewGuid(),
        ClientId = "headless-test",
        Operation = operation,
        Payload = RenderRpcSerializer.Serialize(payload),
    };

    private static string CreateEmptyProject()
    {
        string root = Path.Combine(Path.GetTempPath(), $"projectFrameCut-headless-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "project.pjfc"), "{}");
        File.WriteAllText(Path.Combine(root, "timeline.json"), "{\"Clips\":[],\"SoundTracks\":[],\"Duration\":0}");
        File.WriteAllText(Path.Combine(root, "assets.json"), "[]");
        return root;
    }

    private sealed class ProtobufHandler : HttpMessageHandler
    {
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string? ContentType { get; private set; }
        public string? RequestPath { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            RequestPath = request.RequestUri?.AbsolutePath;
            byte[] bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            RenderRequestEnvelope envelope = RenderRpcSerializer.Deserialize<RenderRequestEnvelope>(bytes);
            var response = new RenderResponseEnvelope
            {
                RequestId = envelope.RequestId,
                Payload = RenderRpcSerializer.Serialize(new RenderCapabilities()),
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(RenderRpcSerializer.Serialize(response)),
            };
        }
    }
}
