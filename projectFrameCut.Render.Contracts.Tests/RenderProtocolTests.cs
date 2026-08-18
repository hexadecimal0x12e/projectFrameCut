using projectFrameCut.Render.Contracts;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class RenderProtocolTests
{
    [TestMethod]
    public void OpenProjectRequestRoundTripsThroughProtobuf()
    {
        var source = new OpenProjectRequest
        {
            SessionId = Guid.NewGuid(),
            ProjectRoot = "project",
            TimelineJson = "{\"clips\":[]}",
            ProjectWidth = 1920,
            ProjectHeight = 1080,
            FrameRate = 60,
            CacheNamespace = "headless-revision",
            Assets = [new AssetPathEntry { AssetId = "asset-1", Path = "assets/video.mp4" }],
        };

        var clone = RenderRpcSerializer.Clone(source);

        Assert.AreEqual(source.SessionId, clone.SessionId);
        Assert.AreEqual(1920, clone.ProjectWidth);
        Assert.AreEqual("asset-1", clone.Assets.Single().AssetId);
        Assert.AreEqual("headless-revision", clone.CacheNamespace);
    }

    [TestMethod]
    public void RenderSessionRoundTripsFrameHashIndexThroughProtobuf()
    {
        var source = new RenderSession
        {
            SessionId = Guid.NewGuid(),
            SnapshotHash = "snapshot",
            HashIndex = new FrameHashIndex
            {
                Version = "v1",
                SnapshotHash = "snapshot",
                FrameHashes = [new FrameHashEntry { FrameIndex = 12, Hash = "frame" }],
                ClipHashes = [new ClipFrameHashIndex
                {
                    ClipId = Guid.NewGuid(),
                    FrameHashes = [new FrameHashEntry { FrameIndex = 12, Hash = "clip" }],
                }],
            },
        };

        var clone = RenderRpcSerializer.Clone(source);

        Assert.AreEqual("frame", clone.HashIndex.FrameHashes.Single().Hash);
        Assert.AreEqual("clip", clone.HashIndex.ClipHashes.Single().FrameHashes.Single().Hash);
    }

    [TestMethod]
    public async Task DirectTransportUsesTheSameEnvelopeAsANetworkTransport()
    {
        await using var client = new RenderClient(new DirectRenderTransport(new CapabilityService()), "test-client");

        var capabilities = await client.GetCapabilitiesAsync();

        Assert.AreEqual(RenderProtocol.CurrentVersion, capabilities.ProtocolVersion);
        CollectionAssert.Contains(capabilities.Features, "test");
    }

    [TestMethod]
    public async Task ClientSurfacesProtocolErrors()
    {
        await using var client = new RenderClient(new DirectRenderTransport(new FailingService()), "test-client");

        var exception = await Assert.ThrowsAsync<RenderRpcException>(async () =>
        {
            _ = await client.GetCapabilitiesAsync();
        });

        Assert.AreEqual(RenderErrorCode.Unsupported, exception.Error.Code);
    }

    [TestMethod]
    public void HeadlessSnapshotRoundTripsRevisionAndRenderSession()
    {
        var source = new HeadlessProjectSnapshot
        {
            SessionId = Guid.NewGuid(),
            ProjectRoot = "project",
            Revision = 42,
            SnapshotHash = "ABC123",
            ProjectJson = "{}",
            TimelineJson = "{\"clips\":[]}",
            AssetsJson = "[]",
            RenderSession = new RenderSession { SessionId = Guid.NewGuid(), SnapshotHash = "render" },
        };

        var clone = RenderRpcSerializer.Clone(source);

        Assert.AreEqual(42, clone.Revision);
        Assert.AreEqual("ABC123", clone.SnapshotHash);
        Assert.AreEqual(source.RenderSession.SessionId, clone.RenderSession.SessionId);
    }

    [TestMethod]
    public async Task StronglyTypedHeadlessClientUsesHeadlessOperation()
    {
        var service = new HeadlessCapabilityService();
        await using var client = new RenderClient(new DirectRenderTransport(service), "headless-test");

        HeadlessProjectSnapshot snapshot = await client.OpenHeadlessProjectAsync(new OpenHeadlessProjectRequest
        {
            ProjectRoot = "project",
        });

        Assert.AreEqual(RenderOperation.OpenHeadlessProject, service.LastOperation);
        Assert.AreEqual(1, snapshot.Revision);
    }

    private sealed class CapabilityService : IRenderService
    {
        public ValueTask<RenderResponseEnvelope> DispatchAsync(RenderRequestEnvelope request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new RenderResponseEnvelope
            {
                RequestId = request.RequestId,
                Payload = RenderRpcSerializer.Serialize(new RenderCapabilities
                {
                    ProtocolVersion = RenderProtocol.CurrentVersion,
                    Features = ["test"],
                }),
            });
    }

    private sealed class FailingService : IRenderService
    {
        public ValueTask<RenderResponseEnvelope> DispatchAsync(RenderRequestEnvelope request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new RenderResponseEnvelope
            {
                RequestId = request.RequestId,
                Error = new RenderError { Code = RenderErrorCode.Unsupported, Message = "unsupported" },
            });
    }

    private sealed class HeadlessCapabilityService : IRenderService
    {
        public RenderOperation LastOperation { get; private set; }

        public ValueTask<RenderResponseEnvelope> DispatchAsync(RenderRequestEnvelope request, CancellationToken cancellationToken = default)
        {
            LastOperation = request.Operation;
            return ValueTask.FromResult(new RenderResponseEnvelope
            {
                RequestId = request.RequestId,
                Payload = RenderRpcSerializer.Serialize(new HeadlessProjectSnapshot { Revision = 1 }),
            });
        }
    }
}
