using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Shared;

return await PluginPackageCli.RunAsync(args);

internal static class PluginPackageCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        if (!string.Equals(args[0], "pack", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Unknown command '{args[0]}'.");
            PrintUsage();
            return 2;
        }

        try
        {
            var options = ParsePackOptions(args.AsSpan(1));
            var result = await PluginPackageBuilder.PackAsync(options);
            Console.WriteLine($"Created {result.OutputPath}");
            Console.WriteLine($"Plugin: {result.PluginId}");
            Console.WriteLine($"Publisher: {result.PublisherId}");
            Console.WriteLine($"Signing certificate: {result.SigningCertificateFingerprint}");
            Console.WriteLine($"Immutable files: {result.ImmutableFileCount}");
            Console.WriteLine($"Package SHA-256: {result.PackageSha256}");
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or CryptographicException or JsonException)
        {
            Console.Error.WriteLine($"Package failed: {exception.Message}");
            return 1;
        }
    }

    private static PluginPackageOptions ParsePackOptions(ReadOnlySpan<string> args)
    {
        string? input = null;
        string? output = null;
        string? certificate = null;
        string? chain = null;
        string? password = null;
        string? passwordEnvironmentVariable = null;
        var passwordFromStandardInput = false;
        var force = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--input":
                    input = ReadValue(args, ref index, argument);
                    break;
                case "--output":
                    output = ReadValue(args, ref index, argument);
                    break;
                case "--certificate":
                    certificate = ReadValue(args, ref index, argument);
                    break;
                case "--chain":
                    chain = ReadValue(args, ref index, argument);
                    break;
                case "--password":
                    password = ReadValue(args, ref index, argument);
                    break;
                case "--password-env":
                    passwordEnvironmentVariable = ReadValue(args, ref index, argument);
                    break;
                case "--password-stdin":
                    passwordFromStandardInput = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case "-h":
                case "--help":
                    PrintUsage();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{argument}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output) ||
            string.IsNullOrWhiteSpace(certificate) || string.IsNullOrWhiteSpace(chain))
        {
            throw new ArgumentException("--input, --output, --certificate, and --chain are required.");
        }

        var passwordSources = (password is not null ? 1 : 0) +
                              (passwordEnvironmentVariable is not null ? 1 : 0) +
                              (passwordFromStandardInput ? 1 : 0);
        if (passwordSources > 1)
        {
            throw new ArgumentException("Choose only one of --password, --password-env, or --password-stdin.");
        }

        if (passwordEnvironmentVariable is not null)
        {
            password = Environment.GetEnvironmentVariable(passwordEnvironmentVariable)
                ?? throw new ArgumentException($"Environment variable '{passwordEnvironmentVariable}' is not set.");
        }
        else if (passwordFromStandardInput)
        {
            password = Console.In.ReadLine() ?? string.Empty;
        }

        return new PluginPackageOptions
        {
            InputDirectory = input,
            OutputPath = output,
            SigningCertificatePath = certificate,
            CertificatePassword = password,
            CertificateChainPath = chain,
            Force = force
        };
    }

    private static string ReadValue(ReadOnlySpan<string> args, ref int index, string option)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"Option '{option}' requires a value.");
        }

        return args[index];
    }

    private static void PrintUsage()
    {
        Console.WriteLine("projectFrameCut Plugin Package Utility");
        Console.WriteLine();
        Console.WriteLine("Create a certificate-chain protected external plugin package:");
        Console.WriteLine("  pack --input <staging-dir> --output <package.pjfc-plugin> ");
        Console.WriteLine("       --certificate <signing-leaf.pfx> --chain <leaf-to-root.pem>");
        Console.WriteLine("       [--password <value> | --password-env <NAME> | --password-stdin] [--force]");
        Console.WriteLine();
        Console.WriteLine("The staging directory must contain metadata.json and <PluginID>.dll.");
        Console.WriteLine("All files outside data/ and option.json are immutable and are covered by the manifest.");
    }
}

public sealed class PluginPackageOptions
{
    public required string InputDirectory { get; init; }
    public required string OutputPath { get; init; }
    public required string SigningCertificatePath { get; init; }
    public required string CertificateChainPath { get; init; }
    public string? CertificatePassword { get; init; }
    public bool Force { get; init; }
}

public sealed record PluginPackageResult(
    string OutputPath,
    string PluginId,
    string PublisherId,
    string SigningCertificateFingerprint,
    int ImmutableFileCount,
    string PackageSha256);

public static class PluginPackageBuilder
{
    private const string MetadataFileName = "metadata.json";
    private const string ManifestFileName = "manifest.json";
    private const string ManifestSignatureFileName = "manifest.sig";
    private const string PublisherChainFileName = "publisher-chain.pem";
    private const string OptionFileName = "option.json";
    private const string LegacyPublicKeyFileName = "publickey.pem";

    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<PluginPackageResult> PackAsync(
        PluginPackageOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var inputDirectory = Path.GetFullPath(options.InputDirectory);
        var outputPath = Path.GetFullPath(options.OutputPath);
        var certificatePath = Path.GetFullPath(options.SigningCertificatePath);
        var chainPath = Path.GetFullPath(options.CertificateChainPath);

        if (!Directory.Exists(inputDirectory))
        {
            throw new DirectoryNotFoundException($"Plugin staging directory '{inputDirectory}' was not found.");
        }
        if (!File.Exists(certificatePath))
        {
            throw new FileNotFoundException("The signing certificate was not found.", certificatePath);
        }
        if (!File.Exists(chainPath))
        {
            throw new FileNotFoundException("The certificate chain was not found.", chainPath);
        }
        if (File.Exists(outputPath) && !options.Force)
        {
            throw new IOException($"Output file '{outputPath}' already exists. Use --force to replace it.");
        }

        var metadataPath = Path.Combine(inputDirectory, MetadataFileName);
        if (!File.Exists(metadataPath))
        {
            throw new FileNotFoundException("The staging directory must contain metadata.json.", metadataPath);
        }

        using var signingCertificate = LoadSigningCertificate(certificatePath, options.CertificatePassword);
        var chainCertificates = PluginTrustValidator.LoadCertificateChainFromPem(
            await File.ReadAllTextAsync(chainPath, cancellationToken));
        try
        {
            ValidateSigningMaterial(signingCertificate, chainCertificates);

            var metadata = JsonSerializer.Deserialize<PluginMetadata>(
                await File.ReadAllTextAsync(metadataPath, cancellationToken),
                MetadataJsonOptions)
                ?? throw new InvalidDataException("metadata.json is invalid.");

            ValidatePluginId(metadata.PluginID);
            var assemblyPath = Path.Combine(inputDirectory, metadata.PluginID + ".dll");
            if (!File.Exists(assemblyPath))
            {
                throw new FileNotFoundException($"The main plugin assembly '{metadata.PluginID}.dll' was not found.", assemblyPath);
            }

            var publisherId = PluginTrustValidator.GetCertificateSha256Fingerprint(chainCertificates[1]);
            var signingFingerprint = PluginTrustValidator.GetCertificateSha256Fingerprint(signingCertificate);
            var encryptionKey = PluginTrustValidator.DerivePluginEncryptionKey(signingCertificate);
            var assemblyBytes = await File.ReadAllBytesAsync(assemblyPath, cancellationToken);
            var assemblyHash = PluginTrustValidator.ComputeSha256Hex(assemblyBytes);

            metadata.PackageFormatVersion = PluginPackageManifest.CurrentFormatVersion;
            metadata.PublisherId = publisherId;
            metadata.SigningCertificateFingerprint = signingFingerprint;
            metadata.PluginKey = encryptionKey;
            metadata.PluginHash = assemblyHash;

            var packageFiles = await ReadImmutableStagingFilesAsync(
                inputDirectory,
                metadata.PluginID,
                cancellationToken);
            packageFiles[MetadataFileName] = JsonSerializer.SerializeToUtf8Bytes(metadata, MetadataJsonOptions);
            packageFiles[PublisherChainFileName] = ExportCertificateChainPem(chainCertificates);

            var encryptedAssembly = FileCryptoService.EncryptToFileWithPassword(encryptionKey, assemblyBytes);
            var encryptedAssemblyName = metadata.PluginID + ".dll.enc";
            var assemblySignatureName = metadata.PluginID + ".dll.sig";
            packageFiles[encryptedAssemblyName] = encryptedAssembly;
            packageFiles[assemblySignatureName] = Encoding.UTF8.GetBytes(
                Convert.ToBase64String(Sign(signingCertificate, assemblyBytes)));

            var manifest = new PluginPackageManifest
            {
                FormatVersion = PluginPackageManifest.CurrentFormatVersion,
                PluginId = metadata.PluginID,
                PublisherId = publisherId,
                SigningCertificateFingerprint = signingFingerprint,
                PluginHash = assemblyHash,
                Files = packageFiles
                    .Where(file => !IsExcludedFromManifest(file.Key))
                    .Select(file => new PluginManifestFile
                    {
                        Path = file.Key,
                        Sha256 = PluginTrustValidator.ComputeSha256Hex(file.Value)
                    })
                    .OrderBy(file => file.Path, StringComparer.Ordinal)
                    .ToList()
            };

            EnsureRequiredManifestFiles(manifest, metadata.PluginID);
            var canonicalManifest = PluginTrustValidator.GetCanonicalManifestBytes(manifest);
            packageFiles[ManifestFileName] = canonicalManifest;
            packageFiles[ManifestSignatureFileName] = Encoding.UTF8.GetBytes(
                Convert.ToBase64String(Sign(signingCertificate, canonicalManifest)));

            await WritePackageAsync(outputPath, packageFiles, options.Force, cancellationToken);
            var packageHash = await ComputeFileSha256Async(outputPath, cancellationToken);
            return new PluginPackageResult(
                outputPath,
                metadata.PluginID,
                publisherId,
                signingFingerprint,
                manifest.Files.Count,
                packageHash);
        }
        finally
        {
            DisposeCertificates(chainCertificates);
        }
    }

    private static X509Certificate2 LoadSigningCertificate(string path, string? password)
    {
        var certificate = new X509Certificate2(
            path,
            password,
            X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        using var privateKey = certificate.GetRSAPrivateKey();
        if (!certificate.HasPrivateKey || privateKey is null)
        {
            certificate.Dispose();
            throw new CryptographicException("The signing PFX must contain an RSA private key.");
        }

        return certificate;
    }

    private static void ValidateSigningMaterial(
        X509Certificate2 signingCertificate,
        X509Certificate2Collection chain)
    {
        if (chain.Count < 2)
        {
            throw new CryptographicException("The certificate chain must contain a signing certificate and a publisher CA certificate.");
        }

        var chainLeaf = chain[0];
        var publisher = chain[1];
        if (!signingCertificate.RawData.AsSpan().SequenceEqual(chainLeaf.RawData))
        {
            throw new CryptographicException("The PFX certificate does not match the first certificate in the supplied chain.");
        }

        using var signingKey = signingCertificate.GetRSAPrivateKey();
        using var chainKey = chainLeaf.GetRSAPublicKey();
        if (signingKey is null || chainKey is null ||
            !signingKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(chainKey.ExportSubjectPublicKeyInfo()))
        {
            throw new CryptographicException("The PFX private key does not match the first certificate in the supplied chain.");
        }

        if (signingKey.KeySize < 2048 || IsCertificateAuthority(chainLeaf) ||
            !HasDigitalSignatureUsage(chainLeaf) || !HasCodeSigningUsage(chainLeaf))
        {
            throw new CryptographicException("The signing certificate must be an RSA 2048+ end-entity certificate with Digital Signature and Code Signing usage.");
        }
        if (!IsCertificateAuthority(publisher) || !HasCertificateSigningUsage(publisher))
        {
            throw new CryptographicException("The second certificate must be a publisher CA with Certificate Signing usage.");
        }

        for (var index = 0; index < chain.Count - 1; index++)
        {
            if (!chain[index].IssuerName.RawData.AsSpan().SequenceEqual(chain[index + 1].SubjectName.RawData))
            {
                throw new CryptographicException("The certificate chain must be ordered from leaf to root.");
            }
        }

        foreach (var certificate in chain.Cast<X509Certificate2>())
        {
            if (certificate.NotBefore > DateTime.UtcNow || certificate.NotAfter < DateTime.UtcNow)
            {
                throw new CryptographicException($"The certificate '{certificate.Subject}' is outside its validity period.");
            }
            if (UsesWeakSignatureAlgorithm(certificate))
            {
                throw new CryptographicException("The certificate chain contains a weakly signed certificate.");
            }
        }
    }

    private static async Task<Dictionary<string, byte[]>> ReadImmutableStagingFilesAsync(
        string inputDirectory,
        string pluginId,
        CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(inputDirectory);
        foreach (var filePath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(filePath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Symbolic links and reparse points are not allowed in a plugin staging directory: '{filePath}'.");
            }

            var relativePath = PluginTrustValidator.NormalizeManifestPath(
                Path.GetRelativePath(root, filePath).Replace('\\', '/'));
            if (relativePath.Equals(pluginId + ".dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (relativePath.Equals(OptionFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (relativePath.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
                relativePath.Equals(ManifestSignatureFileName, StringComparison.OrdinalIgnoreCase) ||
                relativePath.Equals(PublisherChainFileName, StringComparison.OrdinalIgnoreCase) ||
                relativePath.Equals(LegacyPublicKeyFileName, StringComparison.OrdinalIgnoreCase) ||
                relativePath.Equals(pluginId + ".dll.enc", StringComparison.OrdinalIgnoreCase) ||
                relativePath.Equals(pluginId + ".dll.sig", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The staging directory contains a generated or legacy package file that must not be supplied: '{relativePath}'.");
            }

            if (!files.TryAdd(relativePath, await File.ReadAllBytesAsync(filePath, cancellationToken)))
            {
                throw new InvalidDataException($"The staging directory contains duplicate files after case normalization: '{relativePath}'.");
            }
        }

        return files;
    }

    private static bool IsExcludedFromManifest(string path) =>
        path.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
        path.Equals(ManifestSignatureFileName, StringComparison.OrdinalIgnoreCase) ||
        path.Equals(OptionFileName, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("data/", StringComparison.OrdinalIgnoreCase);

    private static void EnsureRequiredManifestFiles(PluginPackageManifest manifest, string pluginId)
    {
        var required = new[]
        {
            MetadataFileName,
            PublisherChainFileName,
            pluginId + ".dll.enc",
            pluginId + ".dll.sig"
        };
        var paths = manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in required)
        {
            if (!paths.Contains(path))
            {
                throw new InvalidDataException($"The generated manifest does not cover required file '{path}'.");
            }
        }
    }

    private static async Task WritePackageAsync(
        string outputPath,
        IReadOnlyDictionary<string, byte[]> files,
        bool force,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var temporaryPath = outputPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                useAsync: true))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var file in files.OrderBy(file => file.Key, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(file.Key, CompressionLevel.SmallestSize);
                    entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    await using var entryStream = entry.Open();
                    await entryStream.WriteAsync(file.Value, cancellationToken);
                }
            }

            if (File.Exists(outputPath) && !force)
            {
                throw new IOException($"Output file '{outputPath}' already exists. Use --force to replace it.");
            }

            File.Move(temporaryPath, outputPath, overwrite: force);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static byte[] Sign(X509Certificate2 certificate, ReadOnlySpan<byte> data)
    {
        using var rsa = certificate.GetRSAPrivateKey()
            ?? throw new CryptographicException("The signing certificate does not contain an RSA private key.");
        return rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    private static byte[] ExportCertificateChainPem(X509Certificate2Collection chain)
    {
        var builder = new StringBuilder();
        foreach (var certificate in chain.Cast<X509Certificate2>())
        {
            builder.Append(certificate.ExportCertificatePem());
        }

        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static void ValidatePluginId(string? pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || pluginId is "." or ".." ||
            pluginId.Contains('/') || pluginId.Contains('\\') || pluginId.Contains(':') ||
            pluginId != Path.GetFileName(pluginId) ||
            pluginId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException("metadata.json contains an invalid PluginID; it must be a single safe file-name segment.");
        }
    }

    private static bool IsCertificateAuthority(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault()?.CertificateAuthority == true;

    private static bool HasDigitalSignatureUsage(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault()?.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature) == true;

    private static bool HasCertificateSigningUsage(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault()?.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign) == true;

    private static bool HasCodeSigningUsage(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault()?.EnhancedKeyUsages
            .Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.3") == true;

    private static bool UsesWeakSignatureAlgorithm(X509Certificate2 certificate)
    {
        var oid = certificate.SignatureAlgorithm.Value ?? string.Empty;
        var friendlyName = certificate.SignatureAlgorithm.FriendlyName ?? string.Empty;
        return oid is "1.2.840.113549.1.1.4" or "1.2.840.113549.1.1.5" or "1.2.840.10045.4.1" ||
               friendlyName.Contains("md5", StringComparison.OrdinalIgnoreCase) ||
               friendlyName.Contains("sha1", StringComparison.OrdinalIgnoreCase);
    }

    private static void DisposeCertificates(X509Certificate2Collection certificates)
    {
        foreach (var certificate in certificates.Cast<X509Certificate2>())
        {
            certificate.Dispose();
        }
    }
}
