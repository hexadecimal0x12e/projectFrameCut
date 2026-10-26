using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.EncodeAndDecode;

namespace projectFrameCut.Render.Rendering;

public sealed class ChunkRenderOptions
{
    public bool Enabled { get; init; }
    public uint? ChunkFrames { get; init; }
    public double? ChunkSeconds { get; init; }
    public int Parallelism { get; init; } = 1;
    public bool Resume { get; init; } = true;
    public bool KeepChunkFiles { get; init; }
}

public readonly record struct ChunkRenderSegment(int Index, uint StartFrame, uint Duration, string RelativePath);

public readonly record struct ChunkRenderJobProgress(
    int ChunkIndex,
    int ChunkCount,
    double ChunkProgress,
    double GlobalProgress,
    TimeSpan EstimatedRemaining,
    double FramesPerSecond,
    bool Reused);

public delegate Task ChunkRenderDelegate(
    ChunkRenderSegment segment,
    string outputPath,
    int maxRenderThreads,
    Action<double, TimeSpan, double> reportProgress,
    CancellationToken cancellationToken);

public sealed class ChunkRenderCoordinator
{
    private const int ManifestVersion = 2;
    private readonly string _projectRoot;
    private readonly uint _totalFrames;
    private readonly int _frameRate;
    private readonly string _extension;
    private readonly string _renderSignature;
    private readonly int _totalRenderThreads;
    private readonly ChunkRenderOptions _options;
    private readonly string _cacheRoot;
    private readonly SemaphoreSlim _manifestGate = new(1, 1);
    private readonly ConcurrentDictionary<int, double> _runningProgress = new();
    private readonly Stopwatch _renderStopwatch = new();
    private double _reusedFrames;
    private ChunkRenderManifest _manifest = null!;

    public ChunkRenderCoordinator(
        string projectRoot,
        uint totalFrames,
        int frameRate,
        string outputExtension,
        string renderSignature,
        int totalRenderThreads,
        ChunkRenderOptions options)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
        _totalFrames = totalFrames;
        _frameRate = Math.Max(1, frameRate);
        _extension = NormalizeExtension(outputExtension);
        _renderSignature = renderSignature;
        _totalRenderThreads = Math.Max(1, totalRenderThreads);
        _options = options;
        _cacheRoot = EnsureContainedPath(Path.Combine(_projectRoot, "thumbs", "render-chunks"), _projectRoot);
    }

    public string JobDirectory { get; private set; } = string.Empty;
    public string ManifestPath => Path.Combine(JobDirectory, "manifest.json");
    public string MergedPath => Path.Combine(JobDirectory, $"merged{_extension}");
    public int ChunkCount => _manifest?.Chunks.Count ?? 0;
    public event Action<ChunkRenderSegment>? OnChunkCompleted;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_totalFrames == 0) throw new ArgumentOutOfRangeException(nameof(_totalFrames));
        Directory.CreateDirectory(_cacheRoot);
        string fingerprint = await ComputeFingerprintAsync(cancellationToken).ConfigureAwait(false);
        JobDirectory = EnsureContainedPath(Path.Combine(_cacheRoot, fingerprint), _cacheRoot);
        Directory.CreateDirectory(Path.Combine(JobDirectory, "chunks"));

        ChunkRenderManifest? existing = null;
        if (_options.Resume && File.Exists(ManifestPath))
        {
            try
            {
                existing = JsonSerializer.Deserialize<ChunkRenderManifest>(
                    await File.ReadAllTextAsync(ManifestPath, cancellationToken).ConfigureAwait(false));
            }
            catch
            {
                existing = null;
            }
        }

        var planned = BuildSegments();
        if (existing is not null
            && existing.Version == ManifestVersion
            && string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal)
            && SegmentsMatch(existing.Chunks, planned))
        {
            _manifest = existing;
            _manifest.UpdatedUtc = DateTimeOffset.UtcNow;
            foreach (var chunk in _manifest.Chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (chunk.Status != ChunkStatus.Completed || !await IsValidChunkAsync(chunk, cancellationToken).ConfigureAwait(false))
                {
                    chunk.Status = ChunkStatus.Pending;
                    chunk.Error = null;
                    chunk.FileSize = 0;
                    chunk.Sha256 = null;
                }
            }
        }
        else
        {
            _manifest = new ChunkRenderManifest
            {
                Version = ManifestVersion,
                Fingerprint = fingerprint,
                ProjectPath = _projectRoot,
                RenderSignature = _renderSignature,
                TotalFrames = _totalFrames,
                FrameRate = _frameRate,
                CreatedUtc = DateTimeOffset.UtcNow,
                UpdatedUtc = DateTimeOffset.UtcNow,
                Chunks = planned.Select(segment => new ChunkManifestEntry
                {
                    Index = segment.Index,
                    StartFrame = segment.StartFrame,
                    Duration = segment.Duration,
                    RelativePath = segment.RelativePath,
                    Status = ChunkStatus.Pending
                }).ToList()
            };
        }

        await SaveManifestAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RenderPendingChunksAsync(
        ChunkRenderDelegate renderChunk,
        Action<ChunkRenderJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(renderChunk);
        if (_manifest is null) await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var pending = _manifest.Chunks.Where(chunk => chunk.Status != ChunkStatus.Completed).ToArray();
        _runningProgress.Clear();
        _reusedFrames = _manifest.Chunks.Where(chunk => chunk.Status == ChunkStatus.Completed).Sum(chunk => (double)chunk.Duration);
        _renderStopwatch.Restart();
        foreach (var reused in _manifest.Chunks.Where(chunk => chunk.Status == ChunkStatus.Completed))
        {
            progress?.Invoke(CreateProgress(reused.Index, 1, reused: true));
        }
        if (pending.Length == 0) return;

        int parallelism = Math.Clamp(_options.Parallelism, 1, Math.Min(pending.Length, _totalRenderThreads));
        int threadsPerChunk = Math.Max(1, _totalRenderThreads / parallelism);
        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
            async (entry, token) =>
            {
                string outputPath = ResolveChunkPath(entry.RelativePath);
                try
                {
                    await UpdateEntryAsync(entry, ChunkStatus.Rendering, null, token).ConfigureAwait(false);
                    if (File.Exists(outputPath)) File.Delete(outputPath);
                    var segment = new ChunkRenderSegment(entry.Index, entry.StartFrame, entry.Duration, entry.RelativePath);
                    await renderChunk(
                        segment,
                        outputPath,
                        threadsPerChunk,
                        (value, _, _) =>
                        {
                            var state = CreateProgress(entry.Index, value, reused: false);
                            progress?.Invoke(state);
                        },
                        token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                        throw new InvalidDataException($"Chunk {entry.Index} did not produce a valid output file.");

                    entry.FileSize = new FileInfo(outputPath).Length;
                    entry.Sha256 = await ComputeFileHashAsync(outputPath, token).ConfigureAwait(false);
                    await UpdateEntryAsync(entry, ChunkStatus.Completed, null, token).ConfigureAwait(false);
                    var completed = CreateProgress(entry.Index, 1, reused: false);
                    progress?.Invoke(completed);
                    OnChunkCompleted?.Invoke(segment);
                }
                catch (OperationCanceledException)
                {
                    await UpdateEntryAsync(entry, ChunkStatus.Pending, "Cancelled", CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                catch (Exception ex)
                {
                    await UpdateEntryAsync(entry, ChunkStatus.Failed, ex.Message, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }).ConfigureAwait(false);
    }

    public async Task<string> MergeAsync(
        Action<double, TimeSpan>? progress = null,
        CancellationToken cancellationToken = default,
        AudioComposer<float>? audioComposer = null)
    {
        if (_manifest is null) await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (_manifest.Chunks.Any(chunk => chunk.Status != ChunkStatus.Completed))
            throw new InvalidOperationException("All chunks must be completed before merging.");

        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(MergedPath)) File.Delete(MergedPath);
        try
        {
            var chunks = _manifest.Chunks.OrderBy(chunk => chunk.Index)
                .Select(chunk => (Path: ResolveChunkPath(chunk.RelativePath), chunk.Duration)).ToArray();
            await Task.Run(() => VideoChunkMerger.Merge(chunks, MergedPath, _frameRate, _totalFrames,
                audioComposer, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { if (File.Exists(MergedPath)) File.Delete(MergedPath); } catch { }
            throw;
        }
        if (!File.Exists(MergedPath) || new FileInfo(MergedPath).Length == 0)
            throw new InvalidDataException("Chunk merge did not produce a valid output file.");
        return MergedPath;
    }

    public static async Task PublishAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        string destination = Path.GetFullPath(destinationPath);
        string? directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("The output path has no parent directory.", nameof(destinationPath));
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Cleanup()
    {
        if (string.IsNullOrWhiteSpace(JobDirectory) || !Directory.Exists(JobDirectory)) return;
        string safeJobDirectory = EnsureContainedPath(JobDirectory, _cacheRoot);
        if (string.Equals(safeJobDirectory.TrimEnd(Path.DirectorySeparatorChar), _cacheRoot.TrimEnd(Path.DirectorySeparatorChar), PathComparison))
            throw new InvalidOperationException("Refusing to delete the chunk cache root.");
        Directory.Delete(safeJobDirectory, recursive: true);
    }

    private List<ChunkRenderSegment> BuildSegments()
    {
        uint chunkFrames = _options.ChunkFrames.GetValueOrDefault();
        if (chunkFrames == 0 && _options.ChunkSeconds is > 0)
            chunkFrames = checked((uint)Math.Max(1, Math.Round(_options.ChunkSeconds.Value * _frameRate)));
        if (chunkFrames == 0) chunkFrames = checked((uint)_frameRate * 60u);

        var result = new List<ChunkRenderSegment>();
        int index = 0;
        for (uint start = 0; start < _totalFrames;)
        {
            uint duration = Math.Min(chunkFrames, _totalFrames - start);
            string relativePath = Path.Combine("chunks", $"chunk-{index:D6}{_extension}");
            result.Add(new ChunkRenderSegment(index++, start, duration, relativePath));
            if (duration == _totalFrames - start) break;
            start = checked(start + duration);
        }
        return result;
    }

    private async Task<string> ComputeFingerprintAsync(CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHash(hash, $"v{ManifestVersion}|{_totalFrames}|{_frameRate}|{_extension}|{_renderSignature}|{_options.ChunkFrames}|{_options.ChunkSeconds}");
        foreach (string name in new[] { "project.pjfc", "project.json", "timeline.json", "assets.json" })
        {
            string path = Path.Combine(_projectRoot, name);
            if (!File.Exists(path)) continue;
            AppendHash(hash, name);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] buffer = new byte[1024 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                hash.AppendData(buffer.AsSpan(0, read));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendHash(IncrementalHash hash, string value) => hash.AppendData(Encoding.UTF8.GetBytes(value));

    private static bool SegmentsMatch(IReadOnlyList<ChunkManifestEntry> existing, IReadOnlyList<ChunkRenderSegment> planned)
        => existing.Count == planned.Count && existing.OrderBy(x => x.Index).Zip(planned.OrderBy(x => x.Index))
            .All(pair => pair.First.Index == pair.Second.Index
                && pair.First.StartFrame == pair.Second.StartFrame
                && pair.First.Duration == pair.Second.Duration
                && string.Equals(pair.First.RelativePath, pair.Second.RelativePath, StringComparison.Ordinal));

    private async Task<bool> IsValidChunkAsync(ChunkManifestEntry entry, CancellationToken cancellationToken)
    {
        string path = ResolveChunkPath(entry.RelativePath);
        if (!File.Exists(path)) return false;
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length != entry.FileSize || string.IsNullOrWhiteSpace(entry.Sha256)) return false;
        return string.Equals(await ComputeFileHashAsync(path, cancellationToken).ConfigureAwait(false), entry.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private async Task UpdateEntryAsync(ChunkManifestEntry entry, string status, string? error, CancellationToken cancellationToken)
    {
        await _manifestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            entry.Status = status;
            entry.Error = error;
            _manifest.UpdatedUtc = DateTimeOffset.UtcNow;
            await SaveManifestCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _manifestGate.Release();
        }
    }

    private async Task SaveManifestAsync(CancellationToken cancellationToken)
    {
        await _manifestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SaveManifestCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _manifestGate.Release(); }
    }

    private async Task SaveManifestCoreAsync(CancellationToken cancellationToken)
    {
        string temporary = ManifestPath + ".tmp";
        string json = JsonSerializer.Serialize(_manifest, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(temporary, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, ManifestPath, overwrite: true);
    }

    private ChunkRenderJobProgress CreateProgress(int chunkIndex, double chunkProgress, bool reused)
    {
        _runningProgress[chunkIndex] = Math.Clamp(chunkProgress, 0, 1);
        double completedFrames = _manifest.Chunks.Sum(chunk =>
            chunk.Status == ChunkStatus.Completed
                ? chunk.Duration
                : chunk.Duration * _runningProgress.GetValueOrDefault(chunk.Index, 0));
        double elapsed = _renderStopwatch.Elapsed.TotalSeconds;
        double fps = elapsed > 0 ? Math.Max(0, completedFrames - _reusedFrames) / elapsed : 0;
        return new ChunkRenderJobProgress(
            chunkIndex,
            _manifest.Chunks.Count,
            Math.Clamp(chunkProgress, 0, 1),
            _totalFrames > 0 ? Math.Clamp(completedFrames / _totalFrames, 0, 1) : 0,
            Renderer.GetEstimatedRemaining(_totalFrames - completedFrames, fps),
            fps,
            reused);
    }

    private string ResolveChunkPath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new InvalidDataException("Chunk paths in the manifest must be relative.");
        return EnsureContainedPath(Path.Combine(JobDirectory, relativePath), JobDirectory);
    }

    private static string EnsureContainedPath(string path, string root)
    {
        string fullPath = Path.GetFullPath(path);
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(fullRoot, PathComparison) && !string.Equals(fullPath, fullRoot.TrimEnd(Path.DirectorySeparatorChar), PathComparison))
            throw new InvalidOperationException($"Path '{fullPath}' is outside the allowed cache root '{fullRoot}'.");
        return fullPath;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return ".mkv";
        string normalized = extension.StartsWith('.') ? extension : $".{extension}";
        if (normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("Invalid output extension.", nameof(extension));
        return normalized;
    }

    private static async Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static class ChunkStatus
    {
        public const string Pending = "pending";
        public const string Rendering = "rendering";
        public const string Completed = "completed";
        public const string Failed = "failed";
    }

    public sealed class ChunkRenderManifest
    {
        public int Version { get; set; }
        public string Fingerprint { get; set; } = string.Empty;
        public string ProjectPath { get; set; } = string.Empty;
        public string RenderSignature { get; set; } = string.Empty;
        public uint TotalFrames { get; set; }
        public int FrameRate { get; set; }
        public DateTimeOffset CreatedUtc { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
        public List<ChunkManifestEntry> Chunks { get; set; } = [];
    }

    public sealed class ChunkManifestEntry
    {
        public int Index { get; set; }
        public uint StartFrame { get; set; }
        public uint Duration { get; set; }
        public string RelativePath { get; set; } = string.Empty;
        public string Status { get; set; } = ChunkStatus.Pending;
        public long FileSize { get; set; }
        public string? Sha256 { get; set; }
        public string? Error { get; set; }
    }
}
