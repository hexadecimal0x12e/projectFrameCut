using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Shared;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.Services;

public sealed class ProjectPluginPackageOptions
{
    public required string StagingDirectory { get; init; }
    public required PluginMetadata Metadata { get; init; }
    public ProjectPluginCapability Capabilities { get; init; } = ProjectPluginCapability.Effects;
    public List<ProjectPluginMenuDeclaration> Menus { get; init; } = [];
    public List<ProjectPluginToolDeclaration> Tools { get; init; } = [];
    public List<ProjectPluginSettingDeclaration> Settings { get; init; } = [];
    public List<ProjectPluginPanelDeclaration> PropertyPanels { get; init; } = [];
}

public sealed class ProjectPluginDeclaration
{
    public const int CurrentVersion = 1;
    public int Version { get; set; } = CurrentVersion;
    public string PluginId { get; set; } = string.Empty;
    public ProjectPluginCapability Capabilities { get; set; }
    public List<ProjectPluginMenuDeclaration> Menus { get; set; } = [];
    public List<ProjectPluginToolDeclaration> Tools { get; set; } = [];
    public List<ProjectPluginSettingDeclaration> Settings { get; set; } = [];
    public List<ProjectPluginPanelDeclaration> PropertyPanels { get; set; } = [];
}

public sealed class ProjectPluginMenuDeclaration
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ToolId { get; set; } = string.Empty;
}

public sealed class ProjectPluginToolDeclaration
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string InputSchemaJson { get; set; } = "{}";
}

public enum ProjectPluginSettingKind
{
    String,
    Number,
    Boolean,
    Choice,
}

public sealed class ProjectPluginSettingDeclaration
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ProjectPluginSettingKind Kind { get; set; }
    public string DefaultValue { get; set; } = string.Empty;
    public double? Minimum { get; set; }
    public double? Maximum { get; set; }
    public List<string> Options { get; set; } = [];
}

public sealed class ProjectPluginPanelDeclaration
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ToolId { get; set; } = string.Empty;
    public List<ProjectPluginSettingDeclaration> Fields { get; set; } = [];
}

public sealed record ProjectPluginTrustPrompt(
    Guid ProjectId,
    string ProjectName,
    string PluginId,
    string PluginName,
    string PublisherFingerprint,
    string PackageSha256,
    ProjectPluginCapability Capabilities);

public sealed record ProjectPluginLoadResult(IReadOnlyList<string> Loaded, IReadOnlyDictionary<string, string> Failed);

internal interface IRemoteProjectPluginTools
{
    IReadOnlyList<ProjectPluginToolDeclaration> ToolDeclarations { get; }
    ValueTask<string> InvokeProjectToolAsync(string toolId, string inputJson, CancellationToken cancellationToken = default);
}

public static class ProjectPluginService
{
    public const string ProjectPluginDirectoryName = "projectPlugins";
    public const string DeclarationFileName = "project-plugin.json";

    private const string IdentityCertificateKey = "project_plugin_identity_certificate_v1";
    private const string IdentityPrivateKeyKey = "project_plugin_identity_private_key_v1";
    private const long MaximumPackageBytes = 512L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private static readonly SemaphoreSlim IdentityGate = new(1, 1);
    private static string? activeStageRoot;

    public static async Task<ProjectPluginReference> CreatePackageFromStagingAsync(
        string projectRoot,
        ProjectJSONStructure project,
        string stagingDirectory,
        CancellationToken cancellationToken = default)
    {
        var metadataPath = Path.Combine(stagingDirectory, "metadata.json");
        if (!File.Exists(metadataPath)) throw new FileNotFoundException("metadata.json was not found in the selected plugin directory.", metadataPath);
        var metadata = JsonSerializer.Deserialize<PluginMetadata>(await File.ReadAllTextAsync(metadataPath, cancellationToken), JsonOptions)
            ?? throw new InvalidDataException("metadata.json is invalid.");
        var declarationPath = Path.Combine(stagingDirectory, DeclarationFileName);
        var declaration = File.Exists(declarationPath)
            ? JsonSerializer.Deserialize<ProjectPluginDeclaration>(await File.ReadAllTextAsync(declarationPath, cancellationToken), JsonOptions)
                ?? throw new InvalidDataException($"{DeclarationFileName} is invalid.")
            : new ProjectPluginDeclaration { PluginId = metadata.PluginID, Capabilities = ProjectPluginCapability.Effects };
        return await CreatePackageAsync(projectRoot, project, new()
        {
            StagingDirectory = stagingDirectory,
            Metadata = metadata,
            Capabilities = declaration.Capabilities,
            Menus = declaration.Menus,
            Tools = declaration.Tools,
            Settings = declaration.Settings,
            PropertyPanels = declaration.PropertyPanels,
        }, cancellationToken);
    }

    public static async Task<ProjectPluginReference> CreatePackageAsync(
        string projectRoot,
        ProjectJSONStructure project,
        ProjectPluginPackageOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(options);
        if (project.ProjectUniqueId == Guid.Empty) project.ProjectUniqueId = Guid.CreateVersion7();

        var pluginId = options.Metadata.PluginID;
        ValidatePluginId(pluginId);
        if (options.Metadata.IsAppLevelPlugin == true)
            throw new NotSupportedException("Application-level plugins cannot be packaged as project plugins.");
        if (!PluginService.SupportsIsolationMode(PluginIsolationMode.Containerized, options.Metadata.MaximumSupportedIsolationMode))
            throw new NotSupportedException($"Project plugins require {PluginIsolationMode.Containerized} isolation, which this plugin does not support.");
        var stagingRoot = Path.GetFullPath(options.StagingDirectory);
        var assemblyPath = Path.Combine(stagingRoot, pluginId + ".dll");
        if (!Directory.Exists(stagingRoot)) throw new DirectoryNotFoundException(stagingRoot);
        if (!File.Exists(assemblyPath)) throw new FileNotFoundException("The project plugin assembly was not found.", assemblyPath);

        var identity = await GetOrCreateIdentityAsync(cancellationToken);
        using var publisherKey = RSA.Create();
        publisherKey.ImportPkcs8PrivateKey(identity.PrivateKey, out _);
        using var publisher = X509CertificateLoader.LoadCertificate(identity.Certificate);
        using var publisherWithKey = publisher.CopyWithPrivateKey(publisherKey);
        using var signingKey = RSA.Create(3072);
        var request = new CertificateRequest($"CN={pluginId}", signingKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.3") }, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var publicSigningCertificate = request.Create(publisherWithKey, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5), serial);
        using var signingCertificate = publicSigningCertificate.CopyWithPrivateKey(signingKey);

        var assemblyBytes = await File.ReadAllBytesAsync(assemblyPath, cancellationToken);
        var assemblyHash = PluginTrustValidator.ComputeSha256Hex(assemblyBytes);
        var publisherFingerprint = PluginTrustValidator.GetCertificateSha256Fingerprint(publisher);
        var signingFingerprint = PluginTrustValidator.GetCertificateSha256Fingerprint(signingCertificate);
        var encryptionKey = PluginTrustValidator.DerivePluginEncryptionKey(signingCertificate);
        var metadata = options.Metadata;
        metadata.PackageFormatVersion = PluginPackageManifest.ManagedAssemblyFormatVersion;
        metadata.PublisherId = publisherFingerprint;
        metadata.SigningCertificateFingerprint = signingFingerprint;
        metadata.PluginKey = encryptionKey;
        metadata.PluginHash = assemblyHash;

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories))
        {
            var relative = PluginTrustValidator.NormalizeManifestPath(Path.GetRelativePath(stagingRoot, path));
            if (string.Equals(path, assemblyPath, StringComparison.OrdinalIgnoreCase) || IsGeneratedFile(relative, pluginId)) continue;
            files.Add(relative, await File.ReadAllBytesAsync(path, cancellationToken));
        }
        files["metadata.json"] = JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions);
        var declaration = new ProjectPluginDeclaration
        {
            PluginId = pluginId,
            Capabilities = options.Capabilities,
            Menus = options.Menus,
            Tools = options.Tools,
            Settings = options.Settings,
            PropertyPanels = options.PropertyPanels,
        };
        ValidateDeclaration(declaration);
        files[DeclarationFileName] = JsonSerializer.SerializeToUtf8Bytes(declaration, JsonOptions);
        files[PluginPackageSecurityService.PublisherChainFileName] = Encoding.UTF8.GetBytes(
            signingCertificate.ExportCertificatePem().TrimEnd() + Environment.NewLine + publisher.ExportCertificatePem().TrimEnd() + Environment.NewLine);
        files[pluginId + ".dll.enc"] = FileCryptoService.EncryptToFileWithPassword(encryptionKey, assemblyBytes);
        files[pluginId + ".dll.sig"] = Encoding.UTF8.GetBytes(Convert.ToBase64String(Sign(signingCertificate, assemblyBytes)));

        var manifest = new PluginPackageManifest
        {
            FormatVersion = PluginPackageManifest.ManagedAssemblyFormatVersion,
            PluginId = pluginId,
            PublisherId = publisherFingerprint,
            SigningCertificateFingerprint = signingFingerprint,
            PluginHash = assemblyHash,
            Files = files.Select(x => new PluginManifestFile
            {
                Path = x.Key,
                Sha256 = PluginTrustValidator.ComputeSha256Hex(x.Value),
            }).OrderBy(x => x.Path, StringComparer.Ordinal).ToList(),
        };
        var canonicalManifest = PluginTrustValidator.GetCanonicalManifestBytes(manifest);
        files[PluginPackageSecurityService.ManifestFileName] = canonicalManifest;
        files[PluginPackageSecurityService.ManifestSignatureFileName] = Encoding.UTF8.GetBytes(Convert.ToBase64String(Sign(signingCertificate, canonicalManifest)));

        var pluginDirectory = Path.Combine(Path.GetFullPath(projectRoot), ProjectPluginDirectoryName);
        Directory.CreateDirectory(pluginDirectory);
        var packagePath = Path.Combine(pluginDirectory, pluginId + ".pjfcPlugin");
        var tempPath = packagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await WritePackageAsync(tempPath, files, cancellationToken);
            File.Move(tempPath, packagePath, true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }

        var packageHash = await ComputeFileHashAsync(packagePath, cancellationToken);
        var reference = new ProjectPluginReference
        {
            PluginId = pluginId,
            PackagePath = $"{ProjectPluginDirectoryName}/{pluginId}.pjfcPlugin",
            PackageSha256 = packageHash,
            PublisherCertificateDer = Convert.ToBase64String(publisher.RawData),
            PublisherCertificateFingerprint = publisherFingerprint,
            Capabilities = options.Capabilities,
        };
        project.ProjectPlugins ??= [];
        project.ProjectPlugins.RemoveAll(x => string.Equals(x.PluginId, pluginId, StringComparison.Ordinal));
        project.ProjectPlugins.Add(reference);
        await TrustAsync(project.ProjectUniqueId, pluginId, publisherFingerprint);
        Logger.Log($"Created signed project plugin package '{packagePath}'.");
        return reference;
    }

    public static async Task<ProjectPluginLoadResult> LoadProjectPluginsAsync(
        string projectRoot,
        ProjectJSONStructure project,
        Func<ProjectPluginTrustPrompt, Task<bool>> confirmTrust,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(confirmTrust);
        await UnloadProjectPluginsAsync();
        project.ProjectPlugins ??= [];
        if (project.ProjectPlugins.Count == 0) return new([], new Dictionary<string, string>());
        if (project.ProjectUniqueId == Guid.Empty) throw new InvalidDataException("A project containing project plugins must have a ProjectUniqueId.");

#if !WINDOWS
        return new([], project.ProjectPlugins.Where(x => x.Enabled).ToDictionary(x => x.PluginId, _ => "Project plugins require Windows AppContainer isolation."));
#else
        var loaded = new List<string>();
        var failed = new Dictionary<string, string>(StringComparer.Ordinal);
        activeStageRoot = Path.Combine(Platforms.Windows.WindowsPluginIsolationPlatform.ProjectPluginDirectory, project.ProjectUniqueId.ToString("N"));
        Directory.CreateDirectory(activeStageRoot);
        foreach (var reference in project.ProjectPlugins.Where(x => x.Enabled))
        {
            try
            {
                ValidateReference(projectRoot, reference);
                var packagePath = Path.GetFullPath(Path.Combine(projectRoot, reference.PackagePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!string.Equals(await ComputeFileHashAsync(packagePath, cancellationToken), reference.PackageSha256, StringComparison.OrdinalIgnoreCase))
                    throw new CryptographicException("The project plugin package hash does not match project metadata.");
                var publisherDer = Convert.FromBase64String(reference.PublisherCertificateDer);
                using var publisher = X509CertificateLoader.LoadCertificate(publisherDer);
                var publisherFingerprint = PluginTrustValidator.GetCertificateSha256Fingerprint(publisher);
                if (!string.Equals(publisherFingerprint, reference.PublisherCertificateFingerprint, StringComparison.OrdinalIgnoreCase))
                    throw new CryptographicException("The project plugin publisher certificate fingerprint does not match project metadata.");

                var pluginRoot = Path.Combine(activeStageRoot, reference.PluginId);
                if (Directory.Exists(pluginRoot)) Directory.Delete(pluginRoot, true);
                await PluginPackageSecurityService.ExtractPackageSafelyAsync(packagePath, pluginRoot, cancellationToken);
                using var verification = await PluginPackageSecurityService.VerifyExtractedProjectPackageAsync(pluginRoot, publisherDer, cancellationToken);
                var declaration = JsonSerializer.Deserialize<ProjectPluginDeclaration>(
                    await File.ReadAllTextAsync(Path.Combine(pluginRoot, DeclarationFileName), cancellationToken), JsonOptions)
                    ?? throw new InvalidDataException($"{DeclarationFileName} is invalid.");
                if (declaration.Version != ProjectPluginDeclaration.CurrentVersion ||
                    !string.Equals(declaration.PluginId, reference.PluginId, StringComparison.Ordinal) ||
                    declaration.Capabilities != reference.Capabilities)
                    throw new InvalidDataException("The signed project plugin declaration does not match project metadata.");
                ValidateDeclaration(declaration);

                if (!await IsTrustedAsync(project.ProjectUniqueId, reference.PluginId, publisherFingerprint))
                {
                    var accepted = await confirmTrust(new(
                        project.ProjectUniqueId,
                        project.ProjectName ?? "Project",
                        reference.PluginId,
                        verification.Metadata.Name,
                        publisherFingerprint,
                        reference.PackageSha256,
                        declaration.Capabilities));
                    if (!accepted) throw new UnauthorizedAccessException("The project plugin was not trusted by the user.");
                    await TrustAsync(project.ProjectUniqueId, reference.PluginId, publisherFingerprint);
                }

                reference.Configuration ??= [];
                foreach (var setting in declaration.Settings)
                    reference.Configuration.TryAdd(setting.Id, setting.DefaultValue);
                var plugin = await PluginIsolationFactory.CreateProjectPluginAsync(verification, pluginRoot, declaration, reference.Configuration, cancellationToken);
                try { PluginManager.LoadProjectPlugin(plugin); }
                catch
                {
                    plugin.OnClosing();
                    throw;
                }
                loaded.Add(reference.PluginId);
            }
            catch (Exception ex)
            {
                Logger.Log(ex, $"load project plugin '{reference.PluginId}'", typeof(ProjectPluginService));
                failed[reference.PluginId] = ex.Message;
            }
        }
        return new(loaded, failed);
#endif
    }

    public static Task DisableProjectPluginAsync(ProjectJSONStructure project, string pluginId)
    {
        var reference = project.ProjectPlugins.FirstOrDefault(x => string.Equals(x.PluginId, pluginId, StringComparison.Ordinal));
        if (reference is null) throw new KeyNotFoundException($"Project plugin '{pluginId}' was not found.");
        reference.Enabled = false;
        return Task.CompletedTask;
    }

    public static Task RemoveProjectPluginAsync(string projectRoot, ProjectJSONStructure project, string pluginId)
    {
        var reference = project.ProjectPlugins.FirstOrDefault(x => string.Equals(x.PluginId, pluginId, StringComparison.Ordinal));
        if (reference is null) throw new KeyNotFoundException($"Project plugin '{pluginId}' was not found.");
        var packagePath = ResolvePackagePath(projectRoot, reference);
        if (File.Exists(packagePath)) File.Delete(packagePath);
        project.ProjectPlugins.Remove(reference);
        SecureStorage.Default.Remove(TrustKey(project.ProjectUniqueId, pluginId));
        Logger.Log($"Removed project plugin '{pluginId}' from project '{project.ProjectUniqueId}'.");
        return Task.CompletedTask;
    }

    public static async Task UnloadProjectPluginsAsync()
    {
        PluginManager.UnloadProjectPlugins();
        var root = Interlocked.Exchange(ref activeStageRoot, null);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        try { Directory.Delete(root, true); }
        catch (Exception ex) { Logger.Log(ex, $"clean project plugin staging directory '{root}'", typeof(ProjectPluginService)); }
        await Task.CompletedTask;
    }

    private static async Task<(byte[] Certificate, byte[] PrivateKey)> GetOrCreateIdentityAsync(CancellationToken cancellationToken)
    {
        await IdentityGate.WaitAsync(cancellationToken);
        try
        {
            var certificate = await SecureStorage.Default.GetAsync(IdentityCertificateKey);
            var privateKey = await SecureStorage.Default.GetAsync(IdentityPrivateKeyKey);
            if (!string.IsNullOrWhiteSpace(certificate) && !string.IsNullOrWhiteSpace(privateKey))
                return (Convert.FromBase64String(certificate), Convert.FromBase64String(privateKey));

            cancellationToken.ThrowIfCancellationRequested();
            using var key = RSA.Create(3072);
            var request = new CertificateRequest("CN=projectFrameCut Local Project Plugin Publisher", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using var root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(20));
            var certificateBytes = root.RawData;
            var privateKeyBytes = key.ExportPkcs8PrivateKey();
            await SecureStorage.Default.SetAsync(IdentityCertificateKey, Convert.ToBase64String(certificateBytes));
            await SecureStorage.Default.SetAsync(IdentityPrivateKeyKey, Convert.ToBase64String(privateKeyBytes));
            Logger.Log("Created the local project plugin signing identity.");
            return (certificateBytes, privateKeyBytes);
        }
        finally
        {
            IdentityGate.Release();
        }
    }

    private static string TrustKey(Guid projectId, string pluginId) =>
        $"project_plugin_trust_v1_{projectId:N}_{PluginTrustValidator.ComputeSha256Hex(Encoding.UTF8.GetBytes(pluginId))}";

    private static async Task<bool> IsTrustedAsync(Guid projectId, string pluginId, string publisherFingerprint) =>
        string.Equals(await SecureStorage.Default.GetAsync(TrustKey(projectId, pluginId)), publisherFingerprint, StringComparison.OrdinalIgnoreCase);

    private static Task TrustAsync(Guid projectId, string pluginId, string publisherFingerprint) =>
        SecureStorage.Default.SetAsync(TrustKey(projectId, pluginId), publisherFingerprint);

    private static void ValidateReference(string projectRoot, ProjectPluginReference reference)
    {
        ValidatePluginId(reference.PluginId);
        var packagePath = ResolvePackagePath(projectRoot, reference);
        if (!File.Exists(packagePath)) throw new FileNotFoundException("The project plugin package was not found.", packagePath);
        if (new FileInfo(packagePath).Length > MaximumPackageBytes) throw new InvalidDataException("The project plugin package is too large.");
    }

    private static string ResolvePackagePath(string projectRoot, ProjectPluginReference reference)
    {
        ValidatePluginId(reference.PluginId);
        var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var pluginDirectory = Path.GetFullPath(Path.Combine(root, ProjectPluginDirectoryName)) + Path.DirectorySeparatorChar;
        var packagePath = Path.GetFullPath(Path.Combine(root, reference.PackagePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!packagePath.StartsWith(pluginDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(packagePath), reference.PluginId + ".pjfcPlugin", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The project plugin package path is invalid.");
        return packagePath;
    }

    private static void ValidatePluginId(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || pluginId is "." or ".." || pluginId != Path.GetFileName(pluginId) || pluginId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("The project plugin id is invalid.");
    }

    private static void ValidateDeclaration(ProjectPluginDeclaration declaration)
    {
        if (declaration.Tools.Any() && !declaration.Capabilities.HasFlag(ProjectPluginCapability.Tools))
            throw new InvalidDataException("The declaration contains tools without the Tools capability.");
        if (declaration.Menus.Any() && (!declaration.Capabilities.HasFlag(ProjectPluginCapability.Menus) || !declaration.Capabilities.HasFlag(ProjectPluginCapability.Tools)))
            throw new InvalidDataException("Menu declarations require the Menus and Tools capabilities.");
        if (declaration.Settings.Any() && !declaration.Capabilities.HasFlag(ProjectPluginCapability.Settings))
            throw new InvalidDataException("The declaration contains settings without the Settings capability.");
        if (declaration.PropertyPanels.Any() && (!declaration.Capabilities.HasFlag(ProjectPluginCapability.PropertyPanels) || !declaration.Capabilities.HasFlag(ProjectPluginCapability.Tools)))
            throw new InvalidDataException("Property panel declarations require the PropertyPanels and Tools capabilities.");
        if ((declaration.Capabilities & (ProjectPluginCapability.TextStyles | ProjectPluginCapability.VectorHandlers)) != 0)
            throw new NotSupportedException("TextStyles and VectorHandlers are reserved until their AppContainer protocols are available.");
        if (declaration.Tools.Any(x => string.IsNullOrWhiteSpace(x.Id) || string.IsNullOrWhiteSpace(x.Name)) ||
            declaration.Tools.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != declaration.Tools.Count)
            throw new InvalidDataException("Project tool declarations contain invalid or duplicate ids.");
        var tools = declaration.Tools.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        if (declaration.Menus.Any(x => string.IsNullOrWhiteSpace(x.Id) || string.IsNullOrWhiteSpace(x.Title) || !tools.Contains(x.ToolId)) ||
            declaration.Menus.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != declaration.Menus.Count)
            throw new InvalidDataException("Project menu declarations are invalid or reference an unknown tool.");
        foreach (var tool in declaration.Tools)
        {
            using var schema = JsonDocument.Parse(tool.InputSchemaJson);
            if (schema.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Project tool '{tool.Id}' input schema must be a JSON object.");
        }
        if (declaration.Settings.Any(x => string.IsNullOrWhiteSpace(x.Id) || string.IsNullOrWhiteSpace(x.Title)) ||
            declaration.Settings.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != declaration.Settings.Count)
            throw new InvalidDataException("Project setting declarations contain invalid or duplicate ids.");
        if (declaration.Settings.Any(x => x.Kind == ProjectPluginSettingKind.Choice && (x.Options.Count == 0 || !x.Options.Contains(x.DefaultValue, StringComparer.Ordinal))))
            throw new InvalidDataException("Choice settings require options containing the default value.");
        if (declaration.PropertyPanels.Any(x => string.IsNullOrWhiteSpace(x.Id) || string.IsNullOrWhiteSpace(x.Title) || !tools.Contains(x.ToolId)) ||
            declaration.PropertyPanels.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != declaration.PropertyPanels.Count)
            throw new InvalidDataException("Project property panel declarations are invalid or reference an unknown tool.");
        foreach (var panel in declaration.PropertyPanels)
        {
            if (panel.Fields.Any(x => string.IsNullOrWhiteSpace(x.Id) || string.IsNullOrWhiteSpace(x.Title)) ||
                panel.Fields.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != panel.Fields.Count)
                throw new InvalidDataException($"Project property panel '{panel.Id}' contains invalid or duplicate field ids.");
            if (panel.Fields.Any(x => x.Kind == ProjectPluginSettingKind.Choice && (x.Options.Count == 0 || !x.Options.Contains(x.DefaultValue, StringComparer.Ordinal))))
                throw new InvalidDataException($"Project property panel '{panel.Id}' contains an invalid choice field.");
        }
    }

    private static bool IsGeneratedFile(string path, string pluginId) =>
        path.Equals("metadata.json", StringComparison.OrdinalIgnoreCase) ||
        path.Equals(DeclarationFileName, StringComparison.OrdinalIgnoreCase) ||
        path.Equals(PluginPackageSecurityService.ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
        path.Equals(PluginPackageSecurityService.ManifestSignatureFileName, StringComparison.OrdinalIgnoreCase) ||
        path.Equals(PluginPackageSecurityService.PublisherChainFileName, StringComparison.OrdinalIgnoreCase) ||
        path.Equals(pluginId + ".dll.enc", StringComparison.OrdinalIgnoreCase) ||
        path.Equals(pluginId + ".dll.sig", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("option.json", StringComparison.OrdinalIgnoreCase);

    private static byte[] Sign(X509Certificate2 certificate, byte[] data)
    {
        using var key = certificate.GetRSAPrivateKey() ?? throw new CryptographicException("The project plugin signing key is unavailable.");
        return key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    private static async Task WritePackageAsync(string path, IReadOnlyDictionary<string, byte[]> files, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, true);
        foreach (var file in files.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var entry = archive.CreateEntry(file.Key, CompressionLevel.SmallestSize);
            await using var output = entry.Open();
            await output.WriteAsync(file.Value, cancellationToken);
        }
    }

    private static async Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }
}
