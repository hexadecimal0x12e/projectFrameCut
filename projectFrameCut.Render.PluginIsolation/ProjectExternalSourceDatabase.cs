using projectFrameCut.Render.Contracts;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;

namespace projectFrameCut.Render.PluginIsolation;

public static class ProjectExternalSourceDatabase
{
    public const string DirectoryName = "externalSource";
    public const string ManifestFileName = "external-source.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static ProjectExternalSourceIndex Read(string projectRoot)
    {
        var path = ResolvePath(projectRoot, $"{DirectoryName}/index.json");
        if (!File.Exists(path)) return new();
        var index = JsonSerializer.Deserialize<ProjectExternalSourceIndex>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Invalid external source index.");
        if (index.Version != 1 || index.Assets is null || index.Assets.Any(x => x is null || x.ImportId == Guid.Empty || x.Manifest is null || x.Sources is null)
            || index.Assets.Select(x => x.ImportId).Distinct().Count() != index.Assets.Count)
            throw new InvalidDataException("Unsupported or invalid external source index.");
        foreach (var asset in index.Assets) ResolveAssetDirectory(projectRoot, asset);
        return index;
    }

    public static string ResolveAssetDirectory(string projectRoot, ProjectExternalSourceAsset asset)
    {
        var expected = $"{DirectoryName}/{asset.ImportId:N}";
        if (!string.Equals(asset.Directory, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"Invalid source directory '{asset.Directory}'.");
        return ResolvePath(projectRoot, expected);
    }

    public static string ResolvePath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)
            || relativePath.Contains('\\') || relativePath.Split('/').Any(x => x is "" or "." or ".."))
            throw new InvalidDataException($"Invalid relative source path '{relativePath}'.");
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException($"Source path '{relativePath}' escapes its directory.");
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || System.IO.Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Source path crosses a directory link: '{current}'.");
            if (string.Equals(current, fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
        }
        return path;
    }

    public static async Task<(ProjectExternalSourceManifest Manifest, string Hash)> ValidateAsync(string root, string? expectedHash = null, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(ResolvePath(root, ManifestFileName), cancellationToken).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (expectedHash is not null && !string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("External source manifest hash does not match the project index.");
        var manifest = JsonSerializer.Deserialize<ProjectExternalSourceManifest>(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'), JsonOptions)
            ?? throw new InvalidDataException("Invalid external source manifest.");
        if (manifest.FormatVersion != 1) throw new NotSupportedException($"Unsupported external source manifest version {manifest.FormatVersion}.");
        foreach (var value in new[] { manifest.Id, manifest.Name, manifest.Version, manifest.Author, manifest.Assembly, manifest.EntryPoint })
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (manifest.Files is null || manifest.Files.Count == 0) throw new InvalidDataException("External source manifest has no file hashes.");
        var actual = EnumerateFiles(root).Select(x => Path.GetRelativePath(root, x).Replace('\\', '/'))
            .Where(x => x != ManifestFileName).ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(manifest.Files.Keys)) throw new InvalidDataException("External source file set does not match its manifest.");
        if (!manifest.Files.ContainsKey(manifest.Assembly)) throw new InvalidDataException("Main source assembly is not in the manifest file list.");
        foreach (var file in manifest.Files)
        {
            if (file.Value is null || file.Value.Length != 64 || !file.Value.All(Uri.IsHexDigit))
                throw new InvalidDataException($"Invalid SHA-256 for source file '{file.Key}'.");
            await using var stream = File.OpenRead(ResolvePath(root, file.Key));
            if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)), file.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"External source file hash mismatch: '{file.Key}'.");
        }
        return (manifest, hash);
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(root))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Directory links are not supported in external sources: '{entry}'.");
            if (System.IO.Directory.Exists(entry))
            {
                foreach (var file in EnumerateFiles(entry)) yield return file;
            }
            else yield return entry;
        }
    }

    public static async Task CopyAsync(string source, string destination, CancellationToken cancellationToken = default)
    {
        System.IO.Directory.CreateDirectory(destination);
        foreach (var file in System.IO.Directory.EnumerateFileSystemEntries(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Directory links are not supported in external sources: '{file}'.");
            var target = ResolvePath(destination, Path.GetRelativePath(source, file).Replace('\\', '/'));
            if (System.IO.Directory.Exists(file))
            {
                await CopyAsync(file, target, cancellationToken).ConfigureAwait(false);
                continue;
            }
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = File.OpenRead(file);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<ProjectExternalSourceAsset> ImportAsync(string projectRoot, string sourceDirectory, Guid? replaceId = null, CancellationToken cancellationToken = default)
    {
        var importId = replaceId ?? Guid.NewGuid();
        var root = ResolvePath(projectRoot, DirectoryName);
        var destination = ResolvePath(projectRoot, $"{DirectoryName}/{importId:N}");
        var sourceRoot = Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (root.StartsWith(sourceRoot + Path.DirectorySeparatorChar, comparison) || string.Equals(root, sourceRoot, comparison))
            throw new InvalidDataException("The import directory cannot contain the project's external source directory.");
        var staging = Path.Combine(root, $".import-{Guid.NewGuid():N}");
        var backup = Path.Combine(root, $".backup-{Guid.NewGuid():N}");
        var installed = false;
        var committed = false;
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = Read(projectRoot);
            if (replaceId.HasValue && !index.Assets.Any(x => x.ImportId == replaceId.Value))
                throw new ArgumentException("The source to replace does not exist.");
            var original = await ValidateAsync(sourceDirectory, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (replaceId.HasValue && index.Assets.Single(x => x.ImportId == replaceId.Value).Manifest.Id != original.Manifest.Id)
                throw new InvalidDataException("Only an external source with the same manifest ID can be replaced.");
            await CopyAsync(sourceDirectory, staging, cancellationToken).ConfigureAwait(false);
            var validated = await ValidateAsync(staging, original.Hash, cancellationToken).ConfigureAwait(false);
            var asset = new ProjectExternalSourceAsset
            {
                ImportId = importId, Directory = $"{DirectoryName}/{importId:N}", Manifest = validated.Manifest, ManifestSha256 = validated.Hash,
                Sources = replaceId.HasValue ? index.Assets.Single(x => x.ImportId == replaceId.Value).Sources.Select(x => RenderRpcSerializer.Clone(x)).ToList() : [],
            };
            if (System.IO.Directory.Exists(destination)) System.IO.Directory.Move(destination, backup);
            System.IO.Directory.Move(staging, destination);
            installed = true;
            index.Assets.RemoveAll(x => x.ImportId == importId);
            index.Assets.Add(asset);
            await WriteAsync(projectRoot, index, cancellationToken).ConfigureAwait(false);
            committed = true;
            Log($"Imported project external source '{asset.Manifest.Id}' as {importId}.");
            return asset;
        }
        catch
        {
            if (installed && System.IO.Directory.Exists(destination)) System.IO.Directory.Delete(destination, true);
            if (System.IO.Directory.Exists(backup)) System.IO.Directory.Move(backup, destination);
            throw;
        }
        finally
        {
            foreach (var path in committed ? new[] { staging, backup } : new[] { staging })
                if (System.IO.Directory.Exists(path))
                    try { System.IO.Directory.Delete(path, true); }
                    catch (Exception ex) { Log(ex, $"Clean external source import directory '{path}'."); }
            Gate.Release();
        }
    }

    public static async Task UpdateSourcesAsync(string projectRoot, Guid importId, List<ExternalVideoSourceDescriptor> sources, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = Read(projectRoot);
            var asset = index.Assets.Single(x => x.ImportId == importId);
            asset.Sources = sources.Select(x => RenderRpcSerializer.Clone(x)).ToList();
            await WriteAsync(projectRoot, index, cancellationToken).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    public static async Task RemoveAsync(string projectRoot, Guid importId, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? source = null;
        string? backup = null;
        var committed = false;
        try
        {
            var index = Read(projectRoot);
            var asset = index.Assets.Single(x => x.ImportId == importId);
            source = ResolveAssetDirectory(projectRoot, asset);
            backup = ResolvePath(projectRoot, $"{DirectoryName}/.removed-{Guid.NewGuid():N}");
            if (System.IO.Directory.Exists(source))
            {
                _ = EnumerateFiles(source).ToArray();
                System.IO.Directory.Move(source, backup);
            }
            index.Assets.Remove(asset);
            await WriteAsync(projectRoot, index, cancellationToken).ConfigureAwait(false);
            committed = true;
            Log($"Removed project external source '{asset.Manifest.Id}' ({importId}).");
        }
        catch
        {
            if (backup is not null && source is not null && System.IO.Directory.Exists(backup))
                System.IO.Directory.Move(backup, source);
            throw;
        }
        finally
        {
            if (committed && backup is not null && System.IO.Directory.Exists(backup))
            {
                try
                {
                    _ = EnumerateFiles(backup).ToArray();
                    System.IO.Directory.Delete(backup, true);
                }
                catch (Exception ex) { Log(ex, $"Clean removed external source directory '{backup}'."); }
            }
            Gate.Release();
        }
    }

    private static async Task WriteAsync(string projectRoot, ProjectExternalSourceIndex index, CancellationToken cancellationToken)
    {
        var root = ResolvePath(projectRoot, DirectoryName);
        System.IO.Directory.CreateDirectory(root);
        var temp = Path.Combine(root, $".index-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(index, JsonOptions), cancellationToken).ConfigureAwait(false);
            File.Move(temp, ResolvePath(projectRoot, $"{DirectoryName}/index.json"), true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static List<ProjectExternalSourceApproval> ParseApprovals(string projectRoot, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var assets = Read(projectRoot).Assets;
        var ids = value == "all" ? assets.Select(x => x.ImportId).ToHashSet()
            : value.Split(',').Select(Guid.Parse).ToHashSet();
        if (ids.Except(assets.Select(x => x.ImportId)).Any()) throw new ArgumentException("The external source allow list contains an unknown ImportId.");
        return assets.Where(x => ids.Contains(x.ImportId)).Select(x => new ProjectExternalSourceApproval { ImportId = x.ImportId, ManifestSha256 = x.ManifestSha256 }).ToList();
    }
}
