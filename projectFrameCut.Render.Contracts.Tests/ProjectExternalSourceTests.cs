using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.PluginIsolation;
using System.Security.Cryptography;
using System.Text.Json;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class ProjectExternalSourceTests
{
    [TestMethod]
    public async Task ImportReplaceAndMovePreserveRelativeIndex()
    {
        using var files = new SourceFiles();
        var source = await files.CreateAsync("package");
        var project = files.Directory("project");
        var asset = await ProjectExternalSourceDatabase.ImportAsync(project, source);
        Assert.AreEqual($"externalSource/{asset.ImportId:N}", asset.Directory);
        Assert.IsTrue(System.IO.Directory.Exists(Path.Combine(project, asset.Directory, "empty")));
        await ProjectExternalSourceDatabase.UpdateSourcesAsync(project, asset.ImportId, [new() { SourceId = "main", DecoderName = "Example", Name = "Main" }]);
        Assert.AreEqual("main", ProjectExternalSourceDatabase.Read(project).Assets.Single().Sources.Single().SourceId);

        var replaced = await ProjectExternalSourceDatabase.ImportAsync(project, source, asset.ImportId);
        Assert.AreEqual(asset.ImportId, replaced.ImportId);
        var second = await ProjectExternalSourceDatabase.ImportAsync(project, source);
        Assert.AreNotEqual(asset.ImportId, second.ImportId);
        Assert.AreEqual(2, ProjectExternalSourceDatabase.Read(project).Assets.Count);
        var moved = Path.Combine(files.Root, "moved");
        System.IO.Directory.Move(project, moved);
        await ProjectExternalSourceDatabase.ValidateAsync(ProjectExternalSourceDatabase.ResolveAssetDirectory(moved, replaced), replaced.ManifestSha256);
    }

    [TestMethod]
    public async Task RemovingSourcePreservesOtherImportsAndRollsBackOnIndexFailure()
    {
        using var files = new SourceFiles();
        var root = files.Directory("project");
        var first = await ProjectExternalSourceDatabase.ImportAsync(root, await files.CreateAsync("first"));
        var second = await ProjectExternalSourceDatabase.ImportAsync(root, await files.CreateAsync("second"));
        var path = ProjectExternalSourceDatabase.ResolveAssetDirectory(root, first);
        if (OperatingSystem.IsWindows())
        {
            using (var locked = new FileStream(Path.Combine(root, "externalSource", "index.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                await Assert.ThrowsAsync<IOException>(async () => await ProjectExternalSourceDatabase.RemoveAsync(root, first.ImportId));
            Assert.AreEqual(2, ProjectExternalSourceDatabase.Read(root).Assets.Count);
            await ProjectExternalSourceDatabase.ValidateAsync(path, first.ManifestSha256);
        }
        await ProjectExternalSourceDatabase.RemoveAsync(root, first.ImportId);
        Assert.IsFalse(System.IO.Directory.Exists(path));
        Assert.AreEqual(second.ImportId, ProjectExternalSourceDatabase.Read(root).Assets.Single().ImportId);
        Assert.IsFalse(System.IO.Directory.EnumerateDirectories(Path.Combine(root, "externalSource"), ".removed-*").Any());
        System.IO.Directory.Delete(ProjectExternalSourceDatabase.ResolveAssetDirectory(root, second), true);
        await ProjectExternalSourceDatabase.RemoveAsync(root, second.ImportId);
        Assert.AreEqual(0, ProjectExternalSourceDatabase.Read(root).Assets.Count);
    }

    [TestMethod]
    public async Task MissingExtraAndChangedFilesAreRejectedWithoutChangingIndex()
    {
        using var files = new SourceFiles();
        var source = await files.CreateAsync("package");
        var project = files.Directory("project");
        var asset = await ProjectExternalSourceDatabase.ImportAsync(project, source);
        var original = File.ReadAllText(Path.Combine(project, "externalSource", "index.json"));
        await File.WriteAllTextAsync(Path.Combine(source, "source.dll"), "changed");
        await Assert.ThrowsAsync<InvalidDataException>(async () => await ProjectExternalSourceDatabase.ImportAsync(project, source, asset.ImportId));
        Assert.AreEqual(original, File.ReadAllText(Path.Combine(project, "externalSource", "index.json")));
        await ProjectExternalSourceDatabase.ValidateAsync(ProjectExternalSourceDatabase.ResolveAssetDirectory(project, asset), asset.ManifestSha256);
        File.Delete(Path.Combine(source, "source.dll"));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await ProjectExternalSourceDatabase.ValidateAsync(source));
        source = await files.CreateAsync("extra");
        await File.WriteAllTextAsync(Path.Combine(source, "unlisted.txt"), "extra");
        await Assert.ThrowsAsync<InvalidDataException>(async () => await ProjectExternalSourceDatabase.ValidateAsync(source));
        Assert.IsFalse(System.IO.Directory.EnumerateDirectories(Path.Combine(project, "externalSource"), ".import-*").Any());
    }

    [TestMethod]
    public async Task HashApprovalsAreExplicitAndRoundTrip()
    {
        using var files = new SourceFiles();
        var project = files.Directory("project");
        var asset = await ProjectExternalSourceDatabase.ImportAsync(project, await files.CreateAsync("package"));
        Assert.AreEqual(0, ProjectExternalSourceDatabase.ParseApprovals(project, null).Count);
        var all = ProjectExternalSourceDatabase.ParseApprovals(project, "all");
        Assert.AreEqual(asset.ImportId, all.Single().ImportId);
        Assert.AreEqual(asset.ManifestSha256, all.Single().ManifestSha256);
        Assert.AreEqual(asset.ImportId, ProjectExternalSourceDatabase.ParseApprovals(project, asset.ImportId.ToString()).Single().ImportId);
        Assert.Throws<ArgumentException>(() => ProjectExternalSourceDatabase.ParseApprovals(project, Guid.NewGuid().ToString()));
        var request = RenderRpcSerializer.Clone(new OpenProjectRequest { AllowedExternalSources = all });
        Assert.AreEqual(asset.ManifestSha256, request.AllowedExternalSources.Single().ManifestSha256);
        Assert.AreEqual(0, RenderRpcSerializer.Clone(new OpenProjectRequest()).AllowedExternalSources.Count);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await ProjectExternalSourceDatabase.ValidateAsync(Path.Combine(project, asset.Directory), new string('0', 64)));
    }

    [TestMethod]
    public void PathsRejectTraversalAndProjectReferencesKeepSnapshots()
    {
        using var files = new SourceFiles();
        foreach (var path in new[] { "../source.dll", "a/../source.dll", "a\\source.dll", "/source.dll", "a//source.dll" })
            Assert.Throws<InvalidDataException>(() => ProjectExternalSourceDatabase.ResolvePath(files.Root, path));
        var id = Guid.NewGuid();
        var descriptor = new ExternalVideoSourceDescriptor { SourceId = "hdr", DecoderName = "Example", Name = "HDR source", Width = 640, Height = 360, SupportsHdr = true, Metadata = new() { ["channel"] = "main" } };
        var pathValue = ProjectExternalVideoSource.CreatePath(id, descriptor);
        var reference = ExternalVideoSourceReference.Decode(pathValue[(pathValue.IndexOf(':') + 1)..]);
        Assert.AreEqual(id, reference.ClientId);
        Assert.AreEqual("main", reference.Metadata["channel"]);
        Assert.IsTrue(ProjectExternalVideoSource.TryGetDescriptor(pathValue, out var restored));
        Assert.AreEqual(640, restored.Width);
        Assert.IsTrue(restored.SupportsHdr);
        Assert.IsFalse(RemoteRpcVideoSource.IsPath(pathValue));
    }

    [TestMethod]
    public async Task ImportRejectsItsOwnParent()
    {
        using var files = new SourceFiles();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await ProjectExternalSourceDatabase.ImportAsync(files.Directory("project"), files.Root));
    }

    [TestMethod]
    public async Task IndexWriteFailureRestoresReplacedPackage()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var files = new SourceFiles();
        var source = await files.CreateAsync("package");
        var root = files.Directory("project");
        var asset = await ProjectExternalSourceDatabase.ImportAsync(root, source);
        using (var index = new FileStream(Path.Combine(root, "externalSource", "index.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAsync<IOException>(async () => await ProjectExternalSourceDatabase.ImportAsync(root, source, asset.ImportId));
        Assert.AreEqual(asset.ImportId, ProjectExternalSourceDatabase.Read(root).Assets.Single().ImportId);
        await ProjectExternalSourceDatabase.ValidateAsync(ProjectExternalSourceDatabase.ResolveAssetDirectory(root, asset), asset.ManifestSha256);
        Assert.IsFalse(System.IO.Directory.EnumerateDirectories(Path.Combine(root, "externalSource"), ".backup-*").Any());
    }

    [TestMethod]
    public async Task DirectoryLinksAreRejected()
    {
        using var files = new SourceFiles();
        var source = await files.CreateAsync("package");
        var link = Path.Combine(source, "linked");
        try { System.IO.Directory.CreateSymbolicLink(link, files.Directory("outside")); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("This platform cannot create a directory link: " + ex.Message);
        }
        try { await Assert.ThrowsAsync<InvalidDataException>(async () => await ProjectExternalSourceDatabase.ValidateAsync(source)); }
        finally { if (System.IO.Directory.Exists(link)) System.IO.Directory.Delete(link); }
    }

    [TestMethod]
    public async Task Utf8BomDoesNotChangeManifestInterpretation()
    {
        using var files = new SourceFiles();
        var source = await files.CreateAsync("package");
        var path = Path.Combine(source, ProjectExternalSourceDatabase.ManifestFileName);
        await File.WriteAllTextAsync(path, "\uFEFF" + await File.ReadAllTextAsync(path));
        var validated = await ProjectExternalSourceDatabase.ValidateAsync(source);
        Assert.AreEqual("example", validated.Manifest.Id);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant(), validated.Hash);
    }

    internal sealed class SourceFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pjfc-source-test-" + Guid.NewGuid().ToString("N"));
        public string Directory(string name)
        {
            var path = Path.Combine(Root, name);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }
        public async Task<string> CreateAsync(string name)
        {
            var root = Directory(name);
            System.IO.Directory.CreateDirectory(Path.Combine(root, "empty"));
            var bytes = new byte[] { 1, 2, 3 };
            await File.WriteAllBytesAsync(Path.Combine(root, "source.dll"), bytes);
            await File.WriteAllTextAsync(Path.Combine(root, ProjectExternalSourceDatabase.ManifestFileName), JsonSerializer.Serialize(new ProjectExternalSourceManifest
            {
                Id = "example", Name = "Example", Version = "1", Author = "Test", Assembly = "source.dll", EntryPoint = "Example.Provider",
                Files = new() { ["source.dll"] = Convert.ToHexString(SHA256.HashData(bytes)) },
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return root;
        }
        public void Dispose() { if (System.IO.Directory.Exists(Root)) System.IO.Directory.Delete(Root, true); }
    }
}
