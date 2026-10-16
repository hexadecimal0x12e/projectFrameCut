using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.RPCProtocol;
using projectFrameCut.Shared;
using System.Text.Json;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class PreviewCacheTests
{
    [TestMethod]
    public void MarkedNonVideoClipsCannotBeCachedUntilRecovered()
    {
        using var clip = new SolidColorClip { Id = Guid.NewGuid(), Name = "clip", Duration = 1, ExtraData = [] };
        Assert.IsTrue(Timeline.CanCacheClipFrame([clip], clip));
        ClipInitializationFailure.Mark(clip, "SourceReading", new IOException("Source unavailable."));
        Assert.IsFalse(Timeline.CanCacheClipSource(clip));
        Assert.IsFalse(Timeline.CanCacheClipFrame([clip], clip));
        Assert.IsFalse(Timeline.CanCacheFrame([clip], 0));
        ClipInitializationFailure.Clear(clip);
        Assert.IsTrue(Timeline.CanCacheClipFrame([clip], clip));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SourceFailureDuringRenderingDoesNotCreateReusablePreview(bool clipPreview)
    {
        using var files = new ProjectExternalSourceTests.SourceFiles();
        var root = files.Directory("project");
        var sourcePath = Path.Combine(root, "source.png");
        using (var picture = Picture8bpp.GenerateSolidColor(32, 32, 255, 0, 0, 1)) picture.SaveToPng(sourcePath);
        var clip = new ClipDraftDTO
        {
            Id = Guid.NewGuid(), Name = "photo", FromPlugin = InternalPluginBase.InternalPluginBaseID,
            ClipType = ClipMode.PhotoClip, TypeName = nameof(PhotoClip), FilePath = sourcePath,
            Duration = 1, SecondPerFrameRatio = 1, TargetWidth = 32, TargetHeight = 32,
        };
        await using var service = new RenderBackendService();
        await using var client = new RenderClient(new DirectRenderTransport(service), "preview-cache-test");
        var session = await client.OpenProjectAsync(new()
        {
            ProjectRoot = root, ProjectWidth = 32, ProjectHeight = 32,
            TimelineJson = JsonSerializer.Serialize(new DraftStructureJSON { Clips = [clip], Duration = 1 }),
        });
        CollectionAssert.Contains(session.CacheableClipIds, clip.Id);
        File.Delete(sourcePath);
        var first = await Render();
        var repeated = await Render();
        Assert.IsFalse(first.CacheHit);
        Assert.IsFalse(repeated.CacheHit);
        StringAssert.StartsWith(first.ProjectRelativePath, "thumbs/unCacheablePerClip/");
        StringAssert.StartsWith(repeated.ProjectRelativePath, "thumbs/unCacheablePerClip/");
        Assert.AreNotEqual(first.ProjectRelativePath, repeated.ProjectRelativePath);

        async Task<RenderArtifact> Render() => clipPreview
            ? await client.RenderClipPreviewAsync(new()
            {
                SessionId = session.SessionId, ClipId = clip.Id, CanvasWidth = 32, CanvasHeight = 32,
            })
            : await client.RenderTimelineFrameAsync(new()
            {
                SessionId = session.SessionId, Width = 32, Height = 32,
            });
    }
}
