using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RPCProtocol;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class PngPreviewTests
{
    [TestMethod]
    public async Task ServerConvertsCachedVfdToReusableProjectPng()
    {
        using var files = new ProjectExternalSourceTests.SourceFiles();
        var root = files.Directory("project");
        await using var service = new RenderBackendService();
        await using var client = new RenderClient(new DirectRenderTransport(service), "png-preview-test");
        var session = await client.OpenProjectAsync(new()
        {
            ProjectRoot = root, ProjectWidth = 3, ProjectHeight = 2,
            TimelineJson = "{\"Clips\":[],\"SoundTracks\":[],\"Duration\":1}",
        });
        var request = new TimelineFrameRequest { SessionId = session.SessionId, Width = 3, Height = 2 };
        var vfd = await client.RenderTimelineFrameAsync(request);
        request.PreferredPixelFormat = PreviewPixelFormat.PngImage;
        var png = await client.RenderTimelineFrameAsync(request);
        Assert.IsTrue(png.CacheHit);
        Assert.AreEqual("image/png", png.MediaType);
        Assert.AreEqual(PreviewPixelFormat.EncodedImage, png.PixelFormat);
        Assert.AreEqual(3, png.Width);
        Assert.AreEqual(2, png.Height);
        Assert.AreEqual(Path.ChangeExtension(vfd.ProjectRelativePath, ".png"), png.ProjectRelativePath);
        var path = Path.Combine(root, png.ProjectRelativePath);
        CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, File.ReadAllBytes(path)[..8]);
        var timestamp = File.GetLastWriteTimeUtc(path);
        await client.ReleaseArtifactAsync(new() { SessionId = session.SessionId, ArtifactId = png.ArtifactId });
        var repeated = await client.RenderTimelineFrameAsync(request);
        Assert.IsTrue(repeated.CacheHit);
        Assert.AreEqual(png.ProjectRelativePath, repeated.ProjectRelativePath);
        Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(path));
        await client.CloseProjectAsync(session.SessionId);
        Assert.IsTrue(File.Exists(path));
    }
}
