using LocalizedResources;
using projectFrameCut.Asset;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Shared;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.Template;

internal sealed record TemplateLoadResult(ITemplateStructure Template, string SourcePath, int ImportedAssetCount);

internal static class TemplatePackageIO
{
    private const string TemplateJsonFileName = "template.json";
    private const string MetadataJsonFileName = "metadata.json";
    private const string AssetManifestFileName = "assets.json";
    private const string AssetsFolderName = "assets";
    private const string ScriptFileName = "script.ps1";

    private const string PjfcTemplateExtension = ".pjfcTemplate";
    private const string ZipExtension = ".zip";

    public static async Task<string> BuildTemplatePackageAsync(
        ITemplateStructure template,
        IReadOnlyCollection<AssetItem> assetsToPackage,
        TemplateMetadataStructure? metadata,
        string projectRootPath,
        JsonSerializerOptions jsonOptions,
        string? scriptContent = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(assetsToPackage);
        ArgumentNullException.ThrowIfNull(projectRootPath);
        ArgumentNullException.ThrowIfNull(jsonOptions);

        // Deep clone the template.
        // IMPORTANT: serialize through the concrete type, NOT ITemplateStructure,
        // because ITemplateStructure does NOT declare Project / Draft.
        // JsonSerializer.Serialize<ITemplateStructure>(...) would drop them.
        static string SerializeConcrete(ITemplateStructure t, JsonSerializerOptions o) => t switch
        {
            ScriptBasedTemplateStructure s => JsonSerializer.Serialize(s, o),
            JSONBasedTemplateStructure j => JsonSerializer.Serialize(j, o),
            _ => JsonSerializer.Serialize(t, o),
        };
        var clonedJson = SerializeConcrete(template, jsonOptions);
        ITemplateStructure templateClone = template.TemplateType switch
        {
            TemplateType.Script => JsonSerializer.Deserialize<ScriptBasedTemplateStructure>(clonedJson, jsonOptions)
                ?? throw new InvalidOperationException("Failed to clone script template."),
            _ => JsonSerializer.Deserialize<JSONBasedTemplateStructure>(clonedJson, jsonOptions)
                ?? throw new InvalidOperationException("Failed to clone template."),
        };

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

            // ---- 脚本模板特殊处理 ----
            // 将脚本内容写入 script.ps1，不存入 template.json
            if (!string.IsNullOrEmpty(scriptContent))
            {
                var scriptPath = Path.Combine(packageDir, ScriptFileName);
                await File.WriteAllTextAsync(scriptPath, scriptContent, ct);

                (templateClone as ScriptBasedTemplateStructure)?.ScriptHash = SHA256.HashData(Encoding.UTF8.GetBytes(scriptContent)).Aggregate("", (s, h) => $"{s}{h:x2}");
            }

            var templateJsonPath = Path.Combine(packageDir, TemplateJsonFileName);

            await File.WriteAllTextAsync(
                templateJsonPath,
                SerializeConcrete(templateClone, jsonOptions),
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

        var ext = Path.GetExtension(filePath);
        var isPackage = string.Equals(ext, ZipExtension, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(ext, PjfcTemplateExtension, StringComparison.OrdinalIgnoreCase);

        if (!isPackage)
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

    /// <summary>
    /// 从已解压的包目录中加载模板。
    /// 检测 <c>script.ps1</c> 是否存在，以决定反序列化为 <see cref="ScriptBasedTemplateStructure"/>
    /// 还是 <see cref="JSONBasedTemplateStructure"/>。
    /// </summary>
    private static async Task<ITemplateStructure> LoadTemplateFromExtractedDirectoryAsync(
        string extractDir,
        JsonSerializerOptions jsonOptions,
        CancellationToken ct)
    {
        var preferredTemplatePath = Path.Combine(extractDir, TemplateJsonFileName);
        if (File.Exists(preferredTemplatePath))
        {
            var text = await File.ReadAllTextAsync(preferredTemplatePath, ct);
            return DeserializeTemplateWithScript(text, extractDir, jsonOptions);
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
                var template = DeserializeTemplateWithScript(text, extractDir, jsonOptions);
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

    /// <summary>
    /// 反序列化模板 JSON，检测包目录中是否存在 <c>script.ps1</c>
    /// 以决定目标类型为 <see cref="ScriptBasedTemplateStructure"/> 或 <see cref="JSONBasedTemplateStructure"/>。
    /// </summary>
    private static ITemplateStructure DeserializeTemplateWithScript(
        string templateJson,
        string extractDir,
        JsonSerializerOptions jsonOptions)
    {
        var scriptFilePath = Path.Combine(extractDir, ScriptFileName);
        if (File.Exists(scriptFilePath))
        {
            return JsonSerializer.Deserialize<ScriptBasedTemplateStructure>(templateJson, jsonOptions)
                ?? throw new InvalidOperationException("Invalid script template package.");
        }

        return JsonSerializer.Deserialize<JSONBasedTemplateStructure>(templateJson, jsonOptions)
            ?? throw new InvalidOperationException("Invalid template package.");
    }

    private static int InstallPackagedAssets(string extractDir, ITemplateStructure template, JsonSerializerOptions jsonOptions)
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

    private static void PersistPackagedAssetsForTemplate(string extractDir, ITemplateStructure template, JsonSerializerOptions jsonOptions)
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
        ITemplateStructure template,
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

    // ================================================================
    //  新式模板存储：保存 .pjfcTemplate + 轻量元数据，按需解压
    // ================================================================

    /// <summary>
    /// 导入 .pjfcTemplate 包到模板库：
    /// 1. 将原始 .pjfcTemplate 文件存入 My Templates/{TemplateId:N}.pjfcTemplate
    /// 2. 提取轻量元数据保存为 My Templates/{TemplateId:N}.json（不含 Project/Draft）
    /// 3. 返回完整的 <see cref="ITemplateStructure"/>（含 Project/Draft）供直接使用
    /// </summary>
    public static async Task<ITemplateStructure> ImportPjfcTemplateAsync(
        string filePath,
        JsonSerializerOptions jsonOptions,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(jsonOptions);

        var extractDir = Path.Combine(FileSystem.CacheDirectory, $"template_import_{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);

        try
        {
            ZipFile.ExtractToDirectory(filePath, extractDir, overwriteFiles: true);

            // 读取完整模板（含 Project/Draft/脚本）
            var template = await LoadTemplateFromExtractedDirectoryAsync(extractDir, jsonOptions, ct);

            // 确保 TemplateID 不为空
            if (template.TemplateID == Guid.Empty)
                template.TemplateID = Guid.NewGuid();

            // 读取 metadata.json 获取 Readme
            string? readme = null;
            var metadataPath = Path.Combine(extractDir, MetadataJsonFileName);
            if (File.Exists(metadataPath))
            {
                try
                {
                    var metadataText = await File.ReadAllTextAsync(metadataPath, ct);
                    var metadata = JsonSerializer.Deserialize<TemplateMetadataStructure>(metadataText, jsonOptions);
                    readme = metadata?.Readme;
                }
                catch
                {
                    // metadata.json 可选，读取失败不影响导入
                }
            }

            // 将 Readme 设置到模板结构上
            if (template is JSONBasedTemplateStructure jt)
                jt.Readme = readme;
            else if (template is ScriptBasedTemplateStructure st)
                st.Readme = readme;

            var templateDir = Path.Combine(MauiProgram.DataPath, "My Templates");
            Directory.CreateDirectory(templateDir);

            // 保存原始 .pjfcTemplate
            var pjfcPath = Path.Combine(templateDir, $"{template.TemplateID:N}.pjfcTemplate");
            File.Copy(filePath, pjfcPath, overwrite: true);

            // 保存轻量元数据 .json（含 Readme）
            SaveTemplateListingJson(template, templateDir, jsonOptions, readme);

            return template;
        }
        finally
        {
            TryDeleteDirectory(extractDir);
        }
    }

    /// <summary>
    /// 按需解压 My Templates 中已存储的 .pjfcTemplate，返回完整模板结构与临时目录路径。
    /// 调用方必须在用完后调用 <see cref="TryCleanupExtractDir"/> 清理临时目录。
    /// </summary>
    /// <returns>(模板结构, 临时目录路径)</returns>
    public static async Task<(ITemplateStructure Template, string TempDir)> ExtractStoredTemplateAsync(
        Guid templateId,
        JsonSerializerOptions jsonOptions,
        CancellationToken ct = default)
    {
        var templateDir = Path.Combine(MauiProgram.DataPath, "My Templates");
        var pjfcPath = Path.Combine(templateDir, $"{templateId:N}.pjfcTemplate");

        if (!File.Exists(pjfcPath))
            throw new FileNotFoundException(Localized.TemplateExtractPage_FileNotFound, pjfcPath);

        var extractDir = Path.Combine(FileSystem.CacheDirectory, $"template_use_{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);

        try
        {
            ZipFile.ExtractToDirectory(pjfcPath, extractDir, overwriteFiles: true);

            // 读取完整模板（含 Project/Draft）
            var template = await LoadTemplateFromExtractedDirectoryAsync(extractDir, jsonOptions, ct);

            // 解析 asset 清单，将 AssetHashTable 指向解压后的文件路径
            var manifestPath = Path.Combine(extractDir, AssetManifestFileName);
            if (File.Exists(manifestPath))
            {
                var manifest = JsonSerializer.Deserialize<List<AssetItem>>(
                    await File.ReadAllTextAsync(manifestPath, ct), jsonOptions);
                if (manifest?.Count > 0)
                {
                    var hashTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var asset in manifest)
                    {
                        if (string.IsNullOrWhiteSpace(asset.AssetId))
                            continue;

                        var resolvedPath = ResolvePackagedAssetPath(extractDir, asset.AssetId,
                            asset.Path, template.AssetHashTable);
                        if (!string.IsNullOrWhiteSpace(resolvedPath))
                            hashTable[asset.AssetId] = resolvedPath;
                    }
                    template.AssetHashTable = hashTable.Count > 0 ? hashTable : null;
                    template.HaveAsset = hashTable.Count > 0;
                }
            }
            else
            {
                // 没有 manifest，从 assets/ 目录直接构建
                var assetsDir = Path.Combine(extractDir, AssetsFolderName);
                if (Directory.Exists(assetsDir))
                {
                    var hashTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var assetFile in Directory.GetFiles(assetsDir))
                    {
                        var id = Path.GetFileNameWithoutExtension(assetFile);
                        if (!string.IsNullOrWhiteSpace(id))
                            hashTable[id] = assetFile;
                    }
                    template.AssetHashTable = hashTable.Count > 0 ? hashTable : null;
                    template.HaveAsset = hashTable.Count > 0;
                }
                else
                {
                    template.AssetHashTable = null;
                    template.HaveAsset = false;
                }
            }

            return (template, extractDir);
        }
        catch
        {
            TryDeleteDirectory(extractDir);
            throw;
        }
    }

    /// <summary>
    /// 安全删除由 <see cref="ExtractStoredTemplateAsync"/> 创建的临时目录。
    /// </summary>
    public static void TryCleanupExtractDir(string? tempDir)
    {
        if (string.IsNullOrWhiteSpace(tempDir))
            return;
        TryDeleteDirectory(tempDir);
    }

    /// <summary>
    /// 保存轻量级元数据 JSON（仅含展示所需字段 + Variables/VariableDefinitions，不含 Project/Draft）。
    /// </summary>
    private static void SaveTemplateListingJson(
        ITemplateStructure template,
        string templateDir,
        JsonSerializerOptions jsonOptions,
        string? readme = null)
    {
        // 提取 Variables 中的展示字段
        var vars = template.Variables ?? new Dictionary<string, string?>();

        int clipCount = 0, trackCount = 0;
        ClipDraftDTO[]? serializedClips = null;
        SoundtrackDTO[]? serializedTracks = null;
        if (template is JSONBasedTemplateStructure jt)
        {
            clipCount = jt.Draft?.Clips?.Length ?? 0;
            trackCount = jt.Draft?.SoundTracks?.Length ?? 0;
            serializedClips = jt.Draft?.Clips;
            serializedTracks = jt.Draft?.SoundTracks;
        }
        else if (template is ScriptBasedTemplateStructure st)
        {
            clipCount = st.Draft?.Clips?.Length ?? 0;
            trackCount = st.Draft?.SoundTracks?.Length ?? 0;
            serializedClips = st.Draft?.Clips;
            serializedTracks = st.Draft?.SoundTracks;
        }

        var listing = new Dictionary<string, object?>
        {
            ["$schema"] = "template-meta-v2",
            ["TemplateId"] = template.TemplateID,
            ["TemplateName"] = template.TemplateName,
            ["Scope"] = (int)template.Scope,
            ["TemplateVersion"] = template.TemplateVersion,
            ["HaveAsset"] = template.HaveAsset,
            ["IsScript"] = template.TemplateType == TemplateType.Script,
            ["CreatedInAPIVersion"] = template.CreatedInAPIVersion,
            ["ClipCount"] = clipCount,
            ["TrackCount"] = trackCount,
            ["CreatedAt"] = DateTime.Now,
            ["Variables"] = vars,
            ["VariableDefinitions"] = template.VariableDefinitions,
            ["Category"] = vars.GetValueOrDefault("category") ?? "Other",
            ["Description"] = vars.GetValueOrDefault("description") ?? template.TemplateName,
            ["Duration"] = vars.GetValueOrDefault("duration") ?? "00:00",
            ["PreviewPath"] = vars.GetValueOrDefault("previewPath") ?? "",
            ["UsageCount"] = 0,
            ["Readme"] = readme ?? "",
            ["Clips"] = serializedClips ?? Array.Empty<ClipDraftDTO>(),
            ["SoundTracks"] = serializedTracks ?? Array.Empty<SoundtrackDTO>(),
        };

        // 解析 tags
        var tagsStr = vars.GetValueOrDefault("tags") ?? "";
        listing["Tags"] = string.IsNullOrWhiteSpace(tagsStr)
            ? Array.Empty<string>()
            : tagsStr.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // 使用带缩进的写入选项使文件可读
        var writeOptions = new JsonSerializerOptions(jsonOptions)
        {
            WriteIndented = true,
        };

        var metadataPath = Path.Combine(templateDir, $"{template.TemplateID:N}.json");
        File.WriteAllText(
            metadataPath,
            JsonSerializer.Serialize(listing, writeOptions));
    }

    /// <summary>
    /// 从轻量元数据 JSON 文件重建一个可用于列表展示的 <see cref="ITemplateStructure"/>。
    /// 返回的结构中 Project/Draft 为空，AssetHashTable 为 null。
    /// 实际使用时应通过 <see cref="ExtractStoredTemplateAsync"/> 获取完整结构。
    /// </summary>
    public static ITemplateStructure LoadListingTemplate(string metadataJson)
    {
        using var doc = JsonDocument.Parse(metadataJson);
        var root = doc.RootElement;

        var templateId = root.GetProperty("TemplateId").GetGuid();
        var templateName = root.GetProperty("TemplateName").GetString() ?? "Unnamed";
        var scope = (TemplateScope)root.GetProperty("Scope").GetInt32();
        var templateVersion = root.GetProperty("TemplateVersion").GetInt32();
        var haveAsset = root.GetProperty("HaveAsset").GetBoolean();
        var isScript = root.GetProperty("IsScript").GetBoolean();
        var createdInApi = root.GetProperty("CreatedInAPIVersion").GetInt32();
        var clipCount = root.TryGetProperty("ClipCount", out var ccEl) ? ccEl.GetInt32() : 0;
        var trackCount = root.TryGetProperty("TrackCount", out var tcEl) ? tcEl.GetInt32() : 0;

        // 读取 Readme
        var readme = root.TryGetProperty("Readme", out var readmeEl) ? readmeEl.GetString() ?? "" : "";

        // 反序列化 Variables / VariableDefinitions
        var variables = root.TryGetProperty("Variables", out var varsEl)
            ? JsonSerializer.Deserialize<Dictionary<string, string?>>(varsEl.GetRawText()) ?? []
            : [];

        var varDefs = root.TryGetProperty("VariableDefinitions", out var defsEl)
            ? JsonSerializer.Deserialize<Dictionary<string, TemplateVariableDefinition>>(defsEl.GetRawText()) ?? []
            : [];

        // 反序列化 Clips / SoundTracks（新版元数据包含这些数据）
        var clips = root.TryGetProperty("Clips", out var clipsEl)
            ? JsonSerializer.Deserialize<ClipDraftDTO[]>(clipsEl.GetRawText()) ?? []
            : [];
        var soundTracks = root.TryGetProperty("SoundTracks", out var tracksEl)
            ? JsonSerializer.Deserialize<SoundtrackDTO[]>(tracksEl.GetRawText()) ?? []
            : [];

        var draft = new DraftStructureJSON
        {
            Clips = clips,
            SoundTracks = soundTracks,
        };

        if (isScript)
        {
            return new ScriptBasedTemplateStructure
            {
                TemplateID = templateId,
                TemplateName = templateName,
                Scope = scope,
                TemplateVersion = templateVersion,
                HaveAsset = haveAsset,
                CreatedInAPIVersion = createdInApi,
                ClipCount = clipCount,
                TrackCount = trackCount,
                Variables = variables,
                VariableDefinitions = varDefs,
                Project = new ProjectJSONStructure(),
                Draft = draft,
                Readme = readme,
            };
        }

        return new JSONBasedTemplateStructure
        {
            TemplateID = templateId,
            TemplateName = templateName,
            Scope = scope,
            TemplateVersion = templateVersion,
            HaveAsset = haveAsset,
            CreatedInAPIVersion = createdInApi,
            ClipCount = clipCount,
            TrackCount = trackCount,
            Variables = variables,
            VariableDefinitions = varDefs,
            Project = new ProjectJSONStructure(),
            Draft = draft,
            Readme = readme,
        };
    }
}
