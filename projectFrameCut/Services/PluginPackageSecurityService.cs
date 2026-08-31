using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Shared;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace projectFrameCut.Services;

internal sealed class PluginPackageVerificationResult : IDisposable
{
    public required PluginMetadata Metadata { get; init; }
    public required PluginPackageManifest Manifest { get; init; }
    public required PluginTrustReport TrustReport { get; init; }
    public required byte[] AssemblyBytes { get; init; }
    public required X509Certificate2 SigningCertificate { get; init; }

    public void Dispose() => SigningCertificate.Dispose();
}

internal sealed class PluginPublisherTrustRecord
{
    public string PublisherId { get; set; } = string.Empty;
    public string? PublisherName { get; set; }
    public string RootCertificateFingerprint { get; set; } = string.Empty;
    public DateTimeOffset TrustedAtUtc { get; set; }
}

internal sealed class DevelopmentPluginRootRecord
{
    public string Fingerprint { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string CertificateDerBase64 { get; set; } = string.Empty;
}

internal static class PluginPackageSecurityService
{
    public const string ManifestFileName = "manifest.json";
    public const string ManifestSignatureFileName = "manifest.sig";
    public const string PublisherChainFileName = "publisher-chain.pem";

    private const string BuiltInRootCertificateSha256 = "88BD4DEBEF9243673892E4DEC11294EF0023D60C4616A0B5B9E40B6B3AB90EC1";
    private const string BuiltInRootResourceName = "projectFrameCut.PluginTrust.builtin-root-ca.cer";

    private const string DevelopmentRootsStorageKey = "plugin_development_roots_v1";
    private const string PublisherTrustStoragePrefix = "plugin_publisher_trust_v1_";
    private const int MaximumArchiveEntries = 4096;
    private const long MaximumExtractedPackageBytes = 2L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static IPluginRevocationSource RevocationSource { get; set; } = EmptyPluginRevocationSource.Instance;

    public static async Task<PluginPackageVerificationResult> VerifyExtractedPackageAsync(
        string pluginRoot,
        bool requirePublisherTrust,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginRoot);

        var metadataPath = Path.Combine(pluginRoot, "metadata.json");
        var manifestPath = Path.Combine(pluginRoot, ManifestFileName);
        var signaturePath = Path.Combine(pluginRoot, ManifestSignatureFileName);
        var chainPath = Path.Combine(pluginRoot, PublisherChainFileName);

        RequireFile(metadataPath);
        RequireFile(manifestPath);
        RequireFile(signaturePath);
        RequireFile(chainPath);

        var metadata = JsonSerializer.Deserialize<PluginMetadata>(
            await File.ReadAllTextAsync(metadataPath, cancellationToken),
            JsonOptions) ?? throw new InvalidDataException("metadata.json is invalid.");
        var manifest = JsonSerializer.Deserialize<PluginPackageManifest>(
            await File.ReadAllTextAsync(manifestPath, cancellationToken),
            JsonOptions) ?? throw new InvalidDataException("manifest.json is invalid.");

        ValidateManifestAndMetadata(metadata, manifest);

        var packageCertificates = PluginTrustValidator.LoadCertificateChainFromPem(
            await File.ReadAllTextAsync(chainPath, cancellationToken));
        var trustedRoots = await LoadTrustedRootsAsync(cancellationToken);
        var publisherTrusted = await IsPublisherTrustedAsync(manifest.PublisherId, manifest.PluginId);
        PluginCertificateValidationResult certificateValidation;
        try
        {
            certificateValidation = PluginTrustValidator.ValidateCertificateChain(
                manifest.PluginId,
                manifest.PublisherId,
                manifest.SigningCertificateFingerprint,
                packageCertificates,
                trustedRoots.Cast<X509Certificate2>().ToArray(),
                publisherTrusted);
        }
        finally
        {
            DisposeCertificateCollection(packageCertificates);
            DisposeCertificateCollection(trustedRoots);
        }
        using (certificateValidation)
        {

            if (certificateValidation.Report.Level == PluginTrustLevel.Invalid || certificateValidation.SigningCertificate is null)
            {
                throw new CryptographicException(certificateValidation.Report.FailureReason ?? "The plugin certificate chain is invalid.");
            }

            if (await RevocationSource.IsRevokedAsync(
                    manifest.PublisherId,
                    manifest.PluginId,
                    manifest.SigningCertificateFingerprint,
                    cancellationToken))
            {
                throw new CryptographicException("The plugin publisher, signing certificate, or plugin id has been revoked.");
            }

            if (requirePublisherTrust && !publisherTrusted)
            {
                throw new CryptographicException("The plugin certificate chain is valid, but the publisher has not been trusted by the user.");
            }

            var signature = await File.ReadAllTextAsync(signaturePath, cancellationToken);
            if (!PluginTrustValidator.VerifyManifestSignature(manifest, signature, certificateValidation.SigningCertificate))
            {
                throw new CryptographicException("The plugin manifest signature is invalid.");
            }

            var packageDigest = PluginTrustValidator.ComputeSha256Hex(
                PluginTrustValidator.GetCanonicalManifestBytes(manifest));
            if (RevocationSource is IPluginPackageRevocationSource packageRevocationSource &&
                await packageRevocationSource.IsPackageRevokedAsync(packageDigest, cancellationToken))
            {
                throw new CryptographicException("This plugin package version has been revoked.");
            }

            await ValidateManifestFilesAsync(pluginRoot, manifest, cancellationToken);

            var expectedEncryptionKey = PluginTrustValidator.DerivePluginEncryptionKey(certificateValidation.SigningCertificate);
            if (!string.Equals(metadata.PluginKey, expectedEncryptionKey, StringComparison.OrdinalIgnoreCase))
            {
                throw new CryptographicException("The plugin encryption key does not match the signing certificate public key.");
            }

            var encryptedAssemblyPath = Path.Combine(pluginRoot, metadata.PluginID + ".dll.enc");
            var assemblySignaturePath = Path.Combine(pluginRoot, metadata.PluginID + ".dll.sig");
            RequireFile(encryptedAssemblyPath);
            RequireFile(assemblySignaturePath);

            var encryptedAssembly = await File.ReadAllBytesAsync(encryptedAssemblyPath, cancellationToken);
            var assemblyBytes = FileCryptoService.DecryptToFileWithPassword(expectedEncryptionKey, encryptedAssembly);
            var assemblyHash = PluginTrustValidator.ComputeSha256Hex(assemblyBytes);
            if (!string.Equals(assemblyHash, manifest.PluginHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(assemblyHash, metadata.PluginHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new CryptographicException("The decrypted plugin assembly hash does not match the signed manifest.");
            }

            if (!VerifyAssemblySignature(
                    assemblyBytes,
                    await File.ReadAllTextAsync(assemblySignaturePath, cancellationToken),
                    certificateValidation.SigningCertificate))
            {
                throw new CryptographicException("The decrypted plugin assembly signature is invalid.");
            }

            return new PluginPackageVerificationResult
            {
                Metadata = metadata,
                Manifest = manifest,
                TrustReport = certificateValidation.Report with
                {
                    Level = publisherTrusted ? PluginTrustLevel.PublisherTrusted : PluginTrustLevel.ChainValid
                },
                AssemblyBytes = assemblyBytes,
                SigningCertificate = X509CertificateLoader.LoadCertificate(certificateValidation.SigningCertificate.RawData)
            };
        }
    }

    public static async Task TrustPublisherAsync(PluginTrustReport report, string rootCertificateFingerprint = "")
    {
        if (report.Level is PluginTrustLevel.Invalid or PluginTrustLevel.Revoked)
        {
            throw new InvalidOperationException("An invalid or revoked publisher cannot be trusted.");
        }

        var record = new PluginPublisherTrustRecord
        {
            PublisherId = report.PublisherId,
            PublisherName = report.PublisherName,
            RootCertificateFingerprint = rootCertificateFingerprint,
            TrustedAtUtc = DateTimeOffset.UtcNow
        };
        await SecureStorage.Default.SetAsync(GetPublisherTrustStorageKey(report.PublisherId), JsonSerializer.Serialize(record));
        // Compatibility marker for the existing non-UTF8 settings page. The value is a
        // publisher fingerprint, never a PEM key. Removing it revokes this plugin binding.
        await SecureStorage.Default.SetAsync($"plugin_pem_{report.PluginId}", report.PublisherId);
    }

    public static Task ForgetPublisherAsync(string publisherId)
    {
        SecureStorage.Default.Remove(GetPublisherTrustStorageKey(publisherId));
        return Task.CompletedTask;
    }

    public static async Task<bool> IsPublisherTrustedAsync(string publisherId, string pluginId)
    {
        if (string.IsNullOrWhiteSpace(publisherId))
        {
            return false;
        }

        var value = await SecureStorage.Default.GetAsync(GetPublisherTrustStorageKey(publisherId));
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var record = JsonSerializer.Deserialize<PluginPublisherTrustRecord>(value, JsonOptions);
            if (!string.Equals(record?.PublisherId, publisherId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var pluginBinding = await SecureStorage.Default.GetAsync($"plugin_pem_{pluginId}");
            return string.Equals(pluginBinding, publisherId, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static async Task RegisterDevelopmentRootCertificateAsync(byte[] certificateDer, string? label = null)
    {
#if DEBUG
        ArgumentNullException.ThrowIfNull(certificateDer);
        using var certificate = X509CertificateLoader.LoadCertificate(certificateDer);
        if (!certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority))
        {
            throw new CryptographicException("A development plugin root must be a CA certificate.");
        }

        var fingerprint = PluginTrustValidator.GetCertificateSha256Fingerprint(certificate);
        var roots = await ReadDevelopmentRootsAsync();
        roots.RemoveAll(root => string.Equals(root.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
        roots.Add(new DevelopmentPluginRootRecord
        {
            Fingerprint = fingerprint,
            Label = label,
            CertificateDerBase64 = Convert.ToBase64String(certificate.RawData)
        });
        await SecureStorage.Default.SetAsync(DevelopmentRootsStorageKey, JsonSerializer.Serialize(roots));
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("Custom plugin root certificates are only supported in Debug builds.");
#endif
    }

    public static async Task RemoveDevelopmentRootCertificateAsync(string fingerprint)
    {
#if DEBUG
        var roots = await ReadDevelopmentRootsAsync();
        roots.RemoveAll(root => string.Equals(root.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
        await SecureStorage.Default.SetAsync(DevelopmentRootsStorageKey, JsonSerializer.Serialize(roots));
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("Custom plugin root certificates are only supported in Debug builds.");
#endif
    }

    public static async Task ExtractPackageSafelyAsync(
        string packagePath,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);
        var seenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var packageStream = File.OpenRead(packagePath);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaximumArchiveEntries)
        {
            throw new InvalidDataException($"The plugin package contains more than {MaximumArchiveEntries} entries.");
        }

        long extractedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.FullName))
            {
                continue;
            }

            extractedBytes = checked(extractedBytes + entry.Length);
            if (extractedBytes > MaximumExtractedPackageBytes)
            {
                throw new InvalidDataException("The uncompressed plugin package is too large.");
            }

            var relativePath = PluginTrustValidator.NormalizeManifestPath(entry.FullName);
            if (!seenEntries.Add(relativePath))
            {
                throw new InvalidDataException($"The plugin package contains a duplicate entry: {relativePath}");
            }

            var destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!destinationPath.StartsWith(destinationRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The plugin package contains an unsafe path: {entry.FullName}");
            }

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var input = entry.Open();
            await using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    private static async Task ValidateManifestFilesAsync(
        string pluginRoot,
        PluginPackageManifest manifest,
        CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, PluginManifestFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var normalizedPath = PluginTrustValidator.NormalizeManifestPath(file.Path);
            if (!entries.TryAdd(normalizedPath, file))
            {
                throw new InvalidDataException($"The plugin manifest contains a duplicate file entry: {normalizedPath}");
            }
        }

        var requiredFiles = new[]
        {
            "metadata.json",
            PublisherChainFileName,
            manifest.PluginId + ".dll.enc",
            manifest.PluginId + ".dll.sig"
        };
        foreach (var requiredFile in requiredFiles)
        {
            if (!entries.ContainsKey(requiredFile))
            {
                throw new InvalidDataException($"The plugin manifest does not cover required file '{requiredFile}'.");
            }
        }

        var rootPath = Path.GetFullPath(pluginRoot);
        foreach (var entry in entries)
        {
            var filePath = Path.GetFullPath(Path.Combine(rootPath, entry.Key.Replace('/', Path.DirectorySeparatorChar)));
            if (!filePath.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
            {
                throw new InvalidDataException($"Manifest file '{entry.Key}' is missing or outside the plugin directory.");
            }

            await using var stream = File.OpenRead(filePath);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(actualHash, entry.Value.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new CryptographicException($"Manifest file hash mismatch for '{entry.Key}'.");
            }
        }

        foreach (var filePath in Directory.EnumerateFiles(pluginRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(pluginRoot, filePath).Replace('\\', '/');
            if (relativePath is ManifestFileName or ManifestSignatureFileName or "option.json" ||
                relativePath.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!entries.ContainsKey(relativePath))
            {
                throw new InvalidDataException($"The plugin package contains an unsigned file: {relativePath}");
            }
        }
    }

    private static void ValidateManifestAndMetadata(PluginMetadata metadata, PluginPackageManifest manifest)
    {
        if (metadata.PackageFormatVersion != PluginPackageManifest.CurrentFormatVersion ||
            manifest.FormatVersion != PluginPackageManifest.CurrentFormatVersion)
        {
            throw new NotSupportedException("Legacy plugin packages are no longer supported. A certificate-chain package with format version 2 is required.");
        }

        if (string.IsNullOrWhiteSpace(metadata.PluginID) ||
            metadata.PluginID is "." or ".." ||
            metadata.PluginID != Path.GetFileName(metadata.PluginID) ||
            metadata.PluginID.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            metadata.PluginAPIVersion != PluginService.PluginAPIVersion ||
            !string.Equals(metadata.PluginID, manifest.PluginId, StringComparison.Ordinal) ||
            !string.Equals(metadata.PublisherId, manifest.PublisherId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(metadata.SigningCertificateFingerprint, manifest.SigningCertificateFingerprint, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(metadata.PluginHash, manifest.PluginHash, StringComparison.OrdinalIgnoreCase) ||
            !IsSha256Fingerprint(manifest.PublisherId) ||
            !IsSha256Fingerprint(manifest.SigningCertificateFingerprint) ||
            !IsSha256Fingerprint(manifest.PluginHash))
        {
            throw new InvalidDataException("metadata.json does not match the signed plugin manifest.");
        }
    }

    private static bool VerifyAssemblySignature(byte[] assemblyBytes, string signatureBase64, X509Certificate2 signingCertificate)
    {
        using var rsa = signingCertificate.GetRSAPublicKey();
        if (rsa is null)
        {
            return false;
        }

        try
        {
            return rsa.VerifyData(
                assemblyBytes,
                Convert.FromBase64String(signatureBase64.Trim()),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static async Task<X509Certificate2Collection> LoadTrustedRootsAsync(CancellationToken cancellationToken)
    {
        var roots = new X509Certificate2Collection();
        if (!string.IsNullOrWhiteSpace(BuiltInRootCertificateSha256))
        {
            await using var stream = typeof(PluginPackageSecurityService).Assembly
                .GetManifestResourceStream(BuiltInRootResourceName)
                ?? throw new CryptographicException(
                    $"The embedded plugin root CA resource '{BuiltInRootResourceName}' was not found.");
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);
            var certificate = X509CertificateLoader.LoadCertificate(memory.ToArray());
            var fingerprint = PluginTrustValidator.GetCertificateSha256Fingerprint(certificate);
            if (!string.Equals(fingerprint, BuiltInRootCertificateSha256, StringComparison.OrdinalIgnoreCase))
            {
                certificate.Dispose();
                throw new CryptographicException("The embedded plugin root CA fingerprint does not match the pinned fingerprint.");
            }

            roots.Add(certificate);
        }

#if DEBUG
        foreach (var root in await ReadDevelopmentRootsAsync())
        {
            try
            {
                var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(root.CertificateDerBase64));
                if (string.Equals(
                        PluginTrustValidator.GetCertificateSha256Fingerprint(certificate),
                        root.Fingerprint,
                        StringComparison.OrdinalIgnoreCase))
                {
                    roots.Add(certificate);
                }
                else
                {
                    certificate.Dispose();
                }
            }
            catch (Exception) when (root.CertificateDerBase64.Length > 0)
            {
                // Ignore a damaged development root record; it must never broaden trust.
            }
        }
#endif
        return roots;
    }

    private static async Task<List<DevelopmentPluginRootRecord>> ReadDevelopmentRootsAsync()
    {
        var value = await SecureStorage.Default.GetAsync(DevelopmentRootsStorageKey);
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<DevelopmentPluginRootRecord>>(value, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string GetPublisherTrustStorageKey(string publisherId)
    {
        var normalized = publisherId.Trim().ToLowerInvariant();
        return PublisherTrustStoragePrefix + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    private static void RequireFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("A required plugin package file is missing.", path);
        }
    }

    private static bool IsSha256Fingerprint(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static void DisposeCertificateCollection(X509Certificate2Collection certificates)
    {
        foreach (var certificate in certificates.Cast<X509Certificate2>())
        {
            certificate.Dispose();
        }
    }
}
