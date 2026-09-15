using System.Text.Json;
using projectFrameCut.Render.Rendering;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class ChunkRenderCoordinatorTests
{
    [TestMethod]
    public async Task InitializeCreatesExactNonOverlappingRangesUnderProjectThumbs()
    {
        string projectRoot = CreateProjectDirectory();
        try
        {
            var coordinator = CreateCoordinator(projectRoot, totalFrames: 10, chunkFrames: 4);
            await coordinator.InitializeAsync();

            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(coordinator.ManifestPath));
            var chunks = manifest.RootElement.GetProperty("Chunks").EnumerateArray().ToArray();
            CollectionAssert.AreEqual(new uint[] { 0, 4, 8 }, chunks.Select(x => x.GetProperty("StartFrame").GetUInt32()).ToArray());
            CollectionAssert.AreEqual(new uint[] { 4, 4, 2 }, chunks.Select(x => x.GetProperty("Duration").GetUInt32()).ToArray());
            Assert.IsTrue(Path.GetFullPath(coordinator.JobDirectory).StartsWith(
                Path.GetFullPath(Path.Combine(projectRoot, "thumbs", "render-chunks")),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ResumeRendersOnlyMissingOrCorruptChunks()
    {
        string projectRoot = CreateProjectDirectory();
        try
        {
            var first = CreateCoordinator(projectRoot, totalFrames: 12, chunkFrames: 4);
            await first.InitializeAsync();
            await first.RenderPendingChunksAsync(WriteDummyChunkAsync);

            string corruptChunk = Path.Combine(first.JobDirectory, "chunks", "chunk-000001.bin");
            await File.WriteAllTextAsync(corruptChunk, "corrupt");

            var resumed = CreateCoordinator(projectRoot, totalFrames: 12, chunkFrames: 4);
            await resumed.InitializeAsync();
            var rendered = new List<int>();
            await resumed.RenderPendingChunksAsync(async (segment, path, threads, report, token) =>
            {
                rendered.Add(segment.Index);
                await WriteDummyChunkAsync(segment, path, threads, report, token);
            });

            CollectionAssert.AreEqual(new[] { 1 }, rendered);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    private static ChunkRenderCoordinator CreateCoordinator(string projectRoot, uint totalFrames, uint chunkFrames)
        => new(
            projectRoot,
            totalFrames,
            frameRate: 30,
            outputExtension: ".bin",
            renderSignature: "test-render",
            totalRenderThreads: 8,
            options: new ChunkRenderOptions
            {
                Enabled = true,
                ChunkFrames = chunkFrames,
                Parallelism = 2,
                Resume = true,
                KeepChunkFiles = true
            });

    private static async Task WriteDummyChunkAsync(
        ChunkRenderSegment segment,
        string path,
        int threads,
        Action<double, TimeSpan, double> report,
        CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { segment.Index, segment.StartFrame, segment.Duration, threads });
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        report(1, TimeSpan.Zero, 30);
    }

    private static string CreateProjectDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pjfc-chunk-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "project.json"), "{}");
        File.WriteAllText(Path.Combine(path, "timeline.json"), "{}");
        File.WriteAllText(Path.Combine(path, "assets.json"), "[]");
        return path;
    }
}
