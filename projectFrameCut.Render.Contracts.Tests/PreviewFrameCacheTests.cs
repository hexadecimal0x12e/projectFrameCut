using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Rendering;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class PreviewFrameCacheTests
{
    [TestMethod]
    public void ContainerKeepsSourceAndFinalPixelsSeparate()
    {
        using var files = new ProjectExternalSourceTests.SourceFiles();
        string path = Path.Combine(files.Directory("cache"), "frame.vfd2m");
        using var source = new Picture8bpp(2, 1);
        source.r[0] = 32;
        using var output = new Picture8bpp(2, 1) { HasAlphaChannel = true, a = [1, 0] };
        output.g[0] = 255;
        var data = PreviewFrameData.Create(output, "source", "effect", true);
        PreviewFrameContainer.Save(path, source, data, default);

        using var stream = File.OpenRead(path);
        Assert.IsTrue(PictureExtensions.SharedVfdPictureDecoder.TryLoad(stream, out IPicture? raw, out byte[]? metadata));
        using (raw)
        {
            Assert.AreEqual(32, ((IPicture<byte>)raw!).r[0]);
            Assert.IsNotNull(metadata);
        }
        var preview = PreviewFrameContainer.Read(path, "effect", true);
        CollectionAssert.AreEqual(data.Pixels, preview.Pixels);
        Assert.AreEqual(2, preview.AlphaMask[0]);
        Assert.IsTrue(preview.HasTransparency);
    }

    [TestMethod]
    public void DamagedMetadataCanBeReplacedWithoutLosingSource()
    {
        using var files = new ProjectExternalSourceTests.SourceFiles();
        string path = Path.Combine(files.Directory("cache"), "frame.vfd2m");
        using var source = new Picture8bpp(2, 1);
        source.r[0] = 32;
        var data = PreviewFrameData.Create(source, "source", "old", true);
        PreviewFrameContainer.Save(path, source, data, default);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 1;
        File.WriteAllBytes(path, bytes);

        Assert.ThrowsException<InvalidDataException>(() => PreviewFrameContainer.Read(path));
        using var recovered = PreviewFrameContainer.LoadSource(path);
        using var opened = PreviewFrameContainer.Open(path);
        PreviewFrameContainer.ReplaceMetadata(path, recovered, data with { EffectHash = "new" }, default);
        Assert.AreEqual("new", PreviewFrameContainer.Read(path).EffectHash);
        Assert.ThrowsException<InvalidDataException>(() => PreviewFrameContainer.Read(opened));
        using var raw = PreviewFrameContainer.LoadSource(path);
        Assert.AreEqual(32, ((IPicture<byte>)raw).r[0]);
    }

    [TestMethod]
    public void ReadingMetadataDoesNotDecodeImageBlocks()
    {
        using var files = new ProjectExternalSourceTests.SourceFiles();
        string path = Path.Combine(files.Directory("cache"), "frame.vfd2m");
        using var source = new Picture8bpp(2, 1);
        var data = PreviewFrameData.Create(source, "source", "effect", true);
        PreviewFrameContainer.Save(path, source, data, default);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[32] ^= 1; // First image block's CRC; metadata remains intact.
        File.WriteAllBytes(path, bytes);

        CollectionAssert.AreEqual(data.Pixels, PreviewFrameContainer.Read(path).Pixels);
        Assert.ThrowsException<InvalidDataException>(() => { using var raw = PreviewFrameContainer.LoadSource(path); });
    }

    [TestMethod]
    public void EffectChangeReusesSourceAndReplacesThePreviousResult()
    {
        using var files = new ProjectExternalSourceTests.SourceFiles();
        string root = files.Directory("cache");
        using IClip clip = new PhotoClip { Id = Guid.NewGuid(), Name = "photo", Duration = 1, TargetWidth = 2, TargetHeight = 1, ExtraData = [] };
        using var source = new Picture8bpp(2, 1);
        source.r[0] = 32;
        int reads = 0, effects = 0;

        (string Path, PreviewFrameData Data, bool CacheHit, bool Persistent) Prepare(string effect)
        {
            using var cache = new PreviewFrameCache(_ => root, 0, 2, 1, 2, 1, false, false, default);
            return cache.GetOrCreate(clip, [clip], effect, true, Path.Combine(root, "temporary.vfd2m"), () =>
            {
                effects++;
                var frame = PreviewSourceFrameContext.Current!.Read(clip, 0, 2, 1, IPicture.PicturePixelMode.BytePicture,
                    () => { reads++; return source.Clone(); });
                ((IPicture<byte>)frame).g[0] = effect == "first" ? (byte)100 : (byte)200;
                return frame;
            }, () => true);
        }

        var first = Prepare("first");
        var second = Prepare("second");
        var hit = Prepare("second");
        Assert.AreNotEqual(first.Path, second.Path);
        Assert.IsFalse(File.Exists(first.Path));
        Assert.IsTrue(Path.GetFileName(second.Path).Contains(second.Data.EffectHash, StringComparison.Ordinal));
        Assert.AreEqual(second.Path, hit.Path);
        Assert.AreEqual(1, reads);
        Assert.AreEqual(2, effects);
        Assert.IsTrue(hit.CacheHit);
        Assert.AreEqual(second.Data.EffectHash, PreviewFrameContainer.Read(second.Path).EffectHash);
        Assert.AreEqual(1, Directory.GetFiles(root, "*.vfd2m").Length);
        using var raw = PreviewFrameContainer.LoadSource(second.Path);
        Assert.AreEqual(0, ((IPicture<byte>)raw).g[0]);
    }
}
