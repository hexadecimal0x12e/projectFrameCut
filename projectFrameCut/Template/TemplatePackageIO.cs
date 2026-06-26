using projectFrameCut.Asset;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Shared;
using System.IO.Compression;
using System.Text.Json;

namespace projectFrameCut.Template;

internal sealed record TemplateLoadResult(JSONBasedTemplateStructure Template, string SourcePath, int ImportedAssetCount);

internal static class TemplatePackageIO
{
    private const string TemplateJsonFileName = "template.json";
    private const string MetadataJsonFileName = "metadata.json";
    private const string AssetManifestFileName = "assets.json";
    private const string AssetsFolderName = "assets";

    public static async Task<string> BuildTemplatePackageAsync(
        JSONBasedTemplateStructure template,
        IReadOnlyCollection<AssetItem> assetsToPackage,
        TemplateMetadataStructure? metadata,
        string projectRootPath,
        JsonSerializerOptions jsonOptions,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(assetsToPackage);
        ArgumentNullException.ThrowIfNull(projectRootPath);
        ArgumentNullException.ThrowIfNull(jsonOptions);

        var templateClone = JsonSerializer.Deserialize<JSONBasedTemplateStructure>(
            JsonSerializer.Serialize(template, jsonOptions),
            jsonOptions)
            ?? throw new InvalidOperationException("Failed to clone template.");

        var packageDir = Path.Combine(FileSystem.CacheDirectory, $"template_package_{Guid.NewGuid():N}");
        var zipPath = Path.Combine(FileSystem.CacheDirectory, $"template_package_{Guid.NewGuid():N}.zip");

        Directory.CreateDirectory(packageDir);

        try
        {
            var manifestAssets = new List<AssetItem>();
            var packagedAssetPathMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var usedAssetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (assetsToPackage.Count > 0)
            {
                var assetsDir = Path.Combine(packageDir, AssetsFolderName);
                Directory.CreateDirectory(assetsDir);

                foreach (var sourceAsset in assetsToPackage)
                {
                    ct.ThrowIfCancellationRequested();

                    if (sourceAsset is null)
                    {
                        continue;
                    }

                    var asset = CloneAsset(sourceAsset, jsonOptions);
                    var assetId = string.IsNullOrWhiteSpace(asset.AssetId)
                        ? Guid.NewGuid().ToString("N")
                        : asset.AssetId.Trim();
                    if (!usedAssetIds.Add(assetId))
                    {
                        continue;
                    }

                    var sourcePath = ResolveAssetSourcePath(asset.Path, projectRootPath);
                    if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                    {
                        continue;
                    }

                    var extension = Path.GetExtension(sourcePath);
                    if (string.IsNullOrWhiteSpace(extension))
                    {
                        extension = ".bin";
                    }

                    var packagedFileName = assetId + extension;
                    var packagedRelativePath = $"{AssetsFolderName}/{packagedFileName}";
                    var packagedAbsolutePath = Path.Combine(assetsDir, packagedFileName);

                    File.Copy(sourcePath, packagedAbsolutePath, overwrite: true);

                    asset.Path = packagedRelativePath;
                    asset.AssetId = assetId;
                    if (asset.CreatedAt == default)
                    {
                        asset.CreatedAt = DateTime.Now;
                    }

                    if (string.IsNullOrWhiteSpace(asset.Name))
                    {
                        var fileName = Path.GetFileNameWithoutExtension(sourcePath);
                        asset.Name = string.IsNullOrWhiteSpace(fileName)
                            ? $"Asset@{assetId[..Math.Min(assetId.Length, 8)]}"
                            : fileName;
                    }

                    manifestAssets.Add(asset);
                    packagedAssetPathMap[assetId] = packagedRelativePath;
                }
            }

            templateClone.HaveAsset = manifestAssets.Count > 0;
            templateClone.AssetHashTable = manifestAssets.Count > 0 ? packagedAssetPathMap : null;

            var templateJsonPath = Path.Combine(packageDir, TemplateJsonFileName);
            await File.WriteAllTextAsync(
                templateJsonPath,
                JsonSerializer.Serialize(templateClone, jsonOptions),
                ct);

            var metadataJsonPath = Path.Combine(packageDir, MetadataJsonFileName);
            await File.WriteAllTextAsync(
                metadataJsonPath,
                JsonSerializer.Serialize(metadata, jsonOptions),
                ct);

            if (manifestAssets.Count > 0)
            {
                var manifestPath = Path.Combine(packageDir, AssetManifestFileName);
                await File.WriteAllTextAsync(
                    manifestPath,
                    JsonSerializer.Serialize(manifestAssets, jsonOptions),
                    ct);
            }

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            ZipFile.CreateFromDirectory(packageDir, zipPath, CompressionLevel.SmallestSize, includeBaseDirectory: false);
            return zipPath;
        }
        finally
        {
            TryDeleteDirectory(packageDir);
        }
    }

    public static async Task<TemplateLoadResult> LoadTemplateAsync(
        string filePath,
        JsonSerializerOptions jsonOptions,
        bool installPackagedAssets,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(jsonOptions);

        if (!(string.Equals(Path.GetExtension(filePath), ".zip", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetExtension(filePath), ".pjfcTemplate", StringComparison.OrdinalIgnoreCase)))
        {
            var text = await File.ReadAllTextAsync(filePath, ct);
            var template = JsonSerializer.Deserialize<JSONBasedTemplateStructure>(text, jsonOptions)
                ?? throw new InvalidOperationException("Invalid template file.");
            return new TemplateLoadResult(template, filePath, 0);
        }

        var extractDir = Path.Combine(FileSystem.CacheDirectory, $"template_extract_{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);

        try
        {
            ZipFile.ExtractToDirectory(filePath, extractDir, overwriteFiles: true);
            var template = await LoadTemplateFromExtractedDirectoryAsync(extractDir, jsonOptions, ct);

            var importedAssetCount = 0;
            if (installPackagedAssets)
            {
                importedAssetCount = InstallPackagedAssets(extractDir, template, jsonOptions);
            }
            else
            {
                PersistPackagedAssetsForTemplate(extractDir, template, jsonOptions);
            }

            return new TemplateLoadResult(template, filePath, importedAssetCount);
        }
        finally
        {
            TryDeleteDirectory(extractDir);
        }
    }

    private static async Task<JSONBasedTemplateStructure> LoadTemplateFromExtractedDirectoryAsync(
        string extractDir,
        JsonSerializerOptions jsonOptions,
        CancellationToken ct)
    {
        var preferredTemplatePath = Path.Combine(extractDir, TemplateJsonFileName);
        if (File.Exists(preferredTemplatePath))
        {
            var text = await File.ReadAllTextAsync(preferredTemplatePath, ct);
            return JsonSerializer.Deserialize<JSONBasedTemplateStructure>(text, jsonOptions)
                ?? throw new InvalidOperationException("Invalid template package.");
        }

        foreach (var jsonFile in Directory.GetFiles(extractDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(Path.GetFileName(jsonFile), AssetManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var text = await File.ReadAllTextAsync(jsonFile, ct);
                var template = JsonSerializer.Deserialize<JSONBasedTemplateStructure>(text, jsonOptions);
                if (template is not null)
                {
                    return template;
                }
            }
            catch
            {
                // Continue probing other json files.
            }
        }

        throw new InvalidOperationException("No template json found in package.");
    }

    private static int InstallPackagedAssets(string extractDir, JSONBasedTemplateStructure template, JsonSerializerOptions jsonOptions)
    {
        var packagedAssets = GetPackagedAssetsFromExtractDirectory(extractDir, template, jsonOptions);
        if (packagedAssets.Count == 0)
        {
            return 0;
        }

        var importedCount = 0;
        foreach (var packagedAsset in packagedAssets)
        {
            var extension = Path.GetExtension(packagedAsset.SourcePath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".bin";
            }

            var targetPath = Path.Combine(MauiProgram.DataPath, "My Assets", packagedAsset.AssetId + extension);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(packagedAsset.SourcePath, targetPath, overwrite: true);

            var importedAsset = CloneAsset(packagedAsset.Asset, jsonOptions);
            importedAsset.AssetId = packagedAsset.AssetId;
            importedAsset.Path = targetPath;
            if (importedAsset.CreatedAt == default)
            {
                importedAsset.CreatedAt = DateTime.Now;
            }

            if (string.IsNullOrWhiteSpace(importedAsset.Name))
            {
                importedAsset.Name = $"TemplateAsset-{packagedAsset.AssetId[..Math.Min(packagedAsset.AssetId.Length, 8)]}";
            }

            AssetDatabase.Assets[packagedAsset.AssetId] = importedAsset;
            importedCount++;
        }

        if (importedCount > 0)
        {
            var dbPath = Path.Combine(MauiProgram.DataPath, "My Assets", ".database", "database.json");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            File.WriteAllText(dbPath, JsonSerializer.Serialize(AssetDatabase.Assets, jsonOptions));
        }

        return importedCount;
    }

    private static void PersistPackagedAssetsForTemplate(string extractDir, JSONBasedTemplateStructure template, JsonSerializerOptions jsonOptions)
    {
        var packagedAssets = GetPackagedAssetsFromExtractDirectory(extractDir, template, jsonOptions);
        if (packagedAssets.Count == 0)
        {
            template.HaveAsset = false;
            template.AssetHashTable = null;
            return;
        }

        if (template.TemplateID == Guid.Empty)
        {
            template.TemplateID = Guid.NewGuid();
        }

        var templateAssetDir = Path.Combine(
            MauiProgram.DataPath,
            "My Templates",
            ".packaged-assets",
            template.TemplateID.ToString("N"));

        TryDeleteDirectory(templateAssetDir);
        Directory.CreateDirectory(templateAssetDir);

        var persistedPathMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var packagedAsset in packagedAssets)
        {
            var extension = Path.GetExtension(packagedAsset.SourcePath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".bin";
            }

            var targetPath = Path.Combine(templateAssetDir, packagedAsset.AssetId + extension);
            File.Copy(packagedAsset.SourcePath, targetPath, overwrite: true);
            persistedPathMap[packagedAsset.AssetId] = targetPath;
        }

        template.HaveAsset = persistedPathMap.Count > 0;
        template.AssetHashTable = persistedPathMap.Count > 0 ? persistedPathMap : null;
    }

    private static List<PackagedAssetEntry> GetPackagedAssetsFromExtractDirectory(
        string extractDir,
        JSONBasedTemplateStructure template,
        JsonSerializerOptions jsonOptions)
    {
        var manifestPath = Path.Combine(extractDir, AssetManifestFileName);
        var packagedAssets = new List<AssetItem>();

        if (File.Exists(manifestPath))
        {
            try
            {
                packagedAssets = JsonSerializer.Deserialize<List<AssetItem>>(File.ReadAllText(manifestPath), jsonOptions) ?? [];
            }
            catch
            {
                packagedAssets = [];
            }
        }

        if (packagedAssets.Count == 0 && template.AssetHashTable is { Count: > 0 })
        {
            foreach (var kv in template.AssetHashTable)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value))
                {
                    continue;
                }

                packagedAssets.Add(new AssetItem
                {
                    AssetId = kv.Key,
                    Name = $"Asset@{kv.Key}",
                    Path = kv.Value,
                    AssetType = AssetType.Other,
                    CreatedAt = DateTime.Now
                });
            }
        }

        var resolved = new List<PackagedAssetEntry>();
        foreach (var manifestAsset in packagedAssets)
        {
            var assetId = manifestAsset.AssetId?.Trim();
            if (string.IsNullOrWhiteSpace(assetId))
            {
                continue;
            }

            var sourcePath = ResolvePackagedAssetPath(extractDir, assetId, manifestAsset.Path, template.AssetHashTable);
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                continue;
            }

            var clonedAsset = CloneAsset(manifestAsset, jsonOptions);
            clonedAsset.AssetId = assetId;
            resolved.Add(new PackagedAssetEntry(assetId, clonedAsset, sourcePath));
        }

        return resolved;
    }

    private sealed record PackagedAssetEntry(string AssetId, AssetItem Asset, string SourcePath);

    private static string? ResolvePackagedAssetPath(
        string extractDir,
        string assetId,
        string? manifestPath,
        IReadOnlyDictionary<string, string>? assetPathMap)
    {
        var byManifest = ResolvePackageRelativePath(extractDir, manifestPath);
        if (!string.IsNullOrWhiteSpace(byManifest) && File.Exists(byManifest))
        {
            return byManifest;
        }

        if (assetPathMap is not null && assetPathMap.TryGetValue(assetId, out var mappedPath))
        {
            var byMap = ResolvePackageRelativePath(extractDir, mappedPath);
            if (!string.IsNullOrWhiteSpace(byMap) && File.Exists(byMap))
            {
                return byMap;
            }
        }

        var assetsDir = Path.Combine(extractDir, AssetsFolderName);
        if (Directory.Exists(assetsDir))
        {
            var fallback = Directory
                .GetFiles(assetsDir, assetId + ".*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                return fallback;
            }
        }

        return null;
    }

    private static string? ResolvePackageRelativePath(string extractDir, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (Path.IsPathRooted(path))
        {
            return path;
        }

        var normalized = path
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        return Path.Combine(extractDir, normalized);
    }

    private static string? ResolveAssetSourcePath(string? path, string projectRootPath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.Combine(projectRootPath, path);
    }

    private static AssetItem CloneAsset(AssetItem source, JsonSerializerOptions jsonOptions)
    {
        return JsonSerializer.Deserialize<AssetItem>(JsonSerializer.Serialize(source, jsonOptions), jsonOptions)
            ?? new AssetItem();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // Ignore cleanup failures from cache folders.
        }
    }
}
