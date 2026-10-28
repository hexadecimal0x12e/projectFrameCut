using projectFrameCut.Render.RenderAPIBase.Project;
using System.Text.Json;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class ProjectSnapshotStoreTests
{
    private static readonly JsonSerializerOptions Options = new();

    [TestMethod]
    public void DefaultUsesRootWhileLatestUsesCurrentSnapshotAndItsAssets()
    {
        using var project = new ProjectFiles();
        var saved = ProjectSnapshotStore.ReadForRender(project.Root, project.Info, Options);
        var latest = ProjectSnapshotStore.ReadForRender(project.Root, project.Info, Options, "latest");

        Assert.AreEqual("saved", saved.Timeline.Clips.Single().Name);
        Assert.AreEqual("saved-asset", saved.Assets.Single().AssetId);
        Assert.AreEqual("current", latest.Timeline.Clips.Single().Name);
        Assert.AreEqual("current-asset", latest.Assets.Single().AssetId);
    }

    [TestMethod]
    public void ExplicitSnapshotCanUseLegacySlotNames()
    {
        using var project = new ProjectFiles();
        var id = Guid.NewGuid();
        WriteState(Path.Combine(project.Root, "saveSlots", "slot_legacy"), id, "history");

        var result = ProjectSnapshotStore.ReadForRender(project.Root, project.Info, Options, $"snapshot:{id}");

        Assert.AreEqual("history", result.Timeline.Clips.Single().Name);
        Assert.AreEqual("history-asset", result.Assets.Single().AssetId);
    }

    [TestMethod]
    public void LatestUsesRootWhenItWasSavedAfterTheCurrentRecord()
    {
        using var project = new ProjectFiles();
        var newer = project.Record.UpdatedAtUtc.AddSeconds(10);
        File.SetLastWriteTimeUtc(Path.Combine(project.Root, "timeline.json"), newer);
        File.SetLastWriteTimeUtc(Path.Combine(project.Root, "assets.json"), newer);

        var latest = ProjectSnapshotStore.ReadForRender(project.Root, project.Info, Options, "latest");

        Assert.AreEqual("saved", latest.Timeline.Clips.Single().Name);
        Assert.AreEqual("saved-asset", latest.Assets.Single().AssetId);
    }

    [TestMethod]
    public void LatestUsesRootWhenCurrentRecordIsClean()
    {
        using var project = new ProjectFiles();
        project.Info.NormallyExited = true;
        project.Record.IsDirty = false;
        File.WriteAllText(Path.Combine(project.Root, "saveSlots", "current-0.json"), JsonSerializer.Serialize(project.Record, Options));

        var latest = ProjectSnapshotStore.ReadForRender(project.Root, project.Info, Options, "latest");

        Assert.AreEqual("saved", latest.Timeline.Clips.Single().Name);
        Assert.AreEqual("saved-asset", latest.Assets.Single().AssetId);
    }

    [TestMethod]
    public void LatestAndExplicitSnapshotDoNotSilentlyFallBack()
    {
        using var project = new ProjectFiles();
        Directory.Delete(Path.Combine(project.Root, "saveSlots", project.Record.SlotName), true);

        Assert.ThrowsExactly<DirectoryNotFoundException>(() => ProjectSnapshotStore.ReadForRender(project.Root, project.Info, Options, "latest"));
        Assert.ThrowsExactly<DirectoryNotFoundException>(() => ProjectSnapshotStore.ReadForRender(project.Root, project.Info, Options, $"snapshot:{project.Record.SnapshotID}"));
        Assert.ThrowsExactly<ArgumentException>(() => ProjectSnapshotStore.ReadForRender(project.Root, project.Info, Options, "snapshot:invalid"));
    }

    private static void WriteState(string path, Guid id, string name)
    {
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "timeline.json"), JsonSerializer.Serialize(new DraftStructureJSON
        {
            SnapshotID = id,
            Clips = [new ClipDraftDTO { Name = name }]
        }, Options));
        File.WriteAllText(Path.Combine(path, "assets.json"), JsonSerializer.Serialize(new[] { new AssetItem { AssetId = name + "-asset" } }, Options));
    }

    private sealed class ProjectFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pjfc-source-" + Guid.NewGuid().ToString("N"));
        public ProjectJSONStructure Info { get; } = new() { NormallyExited = false };
        public CurrentSnapshotRecord Record { get; }

        public ProjectFiles()
        {
            WriteState(Root, Guid.NewGuid(), "saved");
            var id = Guid.NewGuid();
            Record = new() { Sequence = 1, UpdatedAtUtc = DateTime.UtcNow, IsDirty = true, SnapshotID = id, SlotName = $"slot_{id}" };
            WriteState(Path.Combine(Root, "saveSlots", Record.SlotName), id, "current");
            File.WriteAllText(Path.Combine(Root, "saveSlots", "current-0.json"), JsonSerializer.Serialize(Record, Options));
            File.SetLastWriteTimeUtc(Path.Combine(Root, "timeline.json"), Record.UpdatedAtUtc.AddMinutes(-1));
            File.SetLastWriteTimeUtc(Path.Combine(Root, "assets.json"), Record.UpdatedAtUtc.AddMinutes(-1));
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
