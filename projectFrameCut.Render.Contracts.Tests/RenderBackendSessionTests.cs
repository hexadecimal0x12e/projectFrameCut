using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RPCProtocol;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RenderBackendSessionTests
{
    [TestMethod]
    [DataRow("close")]
    [DataRow("replace")]
    [DataRow("dispose")]
    public async Task SessionShutdownDrainsActivePreviewsAndCancelsQueuedPreviews(string change)
    {
        using var files = new ProjectExternalSourceTests.SourceFiles();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var root = files.Directory("project");
        var artifacts = new BlockingArtifactStore();
        await using var service = new RenderBackendService(artifacts);
        var sessionId = Guid.NewGuid();
        var opened = await service.DispatchAsync(Request(RenderOperation.OpenProject, new OpenProjectRequest
        {
            SessionId = sessionId, ProjectRoot = root,
            TimelineJson = "{\"Clips\":[],\"SoundTracks\":[],\"Duration\":1}",
        }), timeout.Token);
        Assert.IsNull(opened.Error);

        var frame = new TimelineFrameRequest { SessionId = sessionId, Width = 2, Height = 2 };
        var active = Task.Run(async () => await service.DispatchAsync(Request(RenderOperation.RenderTimelineFrame, frame), timeout.Token));
        await artifacts.Entered.Task.WaitAsync(timeout.Token);
        var queued = service.DispatchAsync(Request(RenderOperation.RenderTimelineFrame, frame), timeout.Token).AsTask();
        var transition = ChangeSessionAsync();
        try
        {
            Assert.AreEqual(RenderErrorCode.Canceled, (await queued.WaitAsync(TimeSpan.FromSeconds(5))).Error?.Code);
            Assert.IsFalse(transition.IsCompleted);
            artifacts.Resume.TrySetResult();
            Assert.IsNull((await active.WaitAsync(TimeSpan.FromSeconds(5))).Error);
            Assert.IsNull((await transition.WaitAsync(TimeSpan.FromSeconds(5))).Error);

            var current = await service.DispatchAsync(Request(RenderOperation.GetTimeline, new SessionRequest { SessionId = sessionId }), timeout.Token);
            if (change == "replace")
            {
                Assert.IsNull(current.Error);
                Assert.AreEqual(2u, RenderRpcSerializer.Deserialize<TimelineSnapshot>(current.Payload).Duration);
            }
            else
            {
                Assert.AreEqual(change == "close" ? RenderErrorCode.SessionNotFound : RenderErrorCode.Canceled, current.Error?.Code);
            }
            if (change == "dispose")
            {
                await using var client = new RenderClient(new DirectRenderTransport(service), "session-test");
                await Assert.ThrowsAsync<OperationCanceledException>(async () => { await client.GetCapabilitiesAsync(); });
            }
        }
        finally
        {
            artifacts.Resume.TrySetResult();
            await Task.WhenAll(active, queued, transition).WaitAsync(TimeSpan.FromSeconds(5));
        }

        async Task<RenderResponseEnvelope> ChangeSessionAsync()
        {
            switch (change)
            {
                case "close":
                    return await service.DispatchAsync(Request(RenderOperation.CloseProject, new SessionRequest { SessionId = sessionId }), timeout.Token);
                case "replace":
                    return await service.DispatchAsync(Request(RenderOperation.OpenProject, new OpenProjectRequest
                    {
                        SessionId = sessionId, ProjectRoot = root,
                        TimelineJson = "{\"Clips\":[],\"SoundTracks\":[],\"Duration\":2}",
                    }), timeout.Token);
                default:
                    await service.DisposeAsync();
                    return new();
            }
        }
    }

    private static RenderRequestEnvelope Request<T>(RenderOperation operation, T payload) => new()
    {
        RequestId = Guid.NewGuid(), Operation = operation, Payload = RenderRpcSerializer.Serialize(payload),
    };

    private sealed class BlockingArtifactStore : IRenderArtifactStore
    {
        private readonly RenderArtifactStore _store = new();
        private int _blocked;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ResolveProjectPath(string projectRoot, string projectRelativePath)
        {
            var path = _store.ResolveProjectPath(projectRoot, projectRelativePath);
            if (Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, Array.Empty<byte>());
                Entered.TrySetResult();
                if (!Resume.Task.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The preview was not resumed.");
            }
            return path;
        }

        public string CreateTemporaryPath(string finalPath) => _store.CreateTemporaryPath(finalPath);
        public void CommitTemporaryFile(string temporaryPath, string finalPath) => _store.CommitTemporaryFile(temporaryPath, finalPath);
        public RenderArtifact Register(Guid sessionId, string projectRoot, string projectRelativePath, string mediaType, bool cacheHit, bool isPreview, int width = 0, int height = 0, double frameRate = 0, PreviewPixelFormat pixelFormat = PreviewPixelFormat.EncodedImage, int stride = 0, string? colorSpace = null)
            => _store.Register(sessionId, projectRoot, projectRelativePath, mediaType, cacheHit, isPreview, width, height, frameRate, pixelFormat, stride, colorSpace);
        public bool Release(Guid sessionId, Guid artifactId) => _store.Release(sessionId, artifactId);
        public bool TryGetPath(Guid sessionId, Guid artifactId, out string fullPath) => _store.TryGetPath(sessionId, artifactId, out fullPath);
    }
}
