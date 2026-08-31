using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace projectFrameCut.Shared;

public enum PluginTrustLevel
{
    Invalid,
    ChainValid,
    PublisherTrusted,
    Revoked
}

public sealed record PluginTrustReport(
    PluginTrustLevel Level,
    string PluginId,
    string PublisherId,
    string? PublisherName,
    string SigningCertificateFingerprint,
    string? FailureReason);

public interface IPluginRevocationSource
{
    ValueTask<bool> IsRevokedAsync(
        string publisherId,
        string pluginId,
        string signingCertificateFingerprint,
        CancellationToken cancellationToken = default);
}

public interface IPluginPackageRevocationSource : IPluginRevocationSource
{
    ValueTask<bool> IsPackageRevokedAsync(
        string packageDigest,
        CancellationToken cancellationToken = default);
}

public sealed class EmptyPluginRevocationSource : IPluginRevocationSource
{
    public static EmptyPluginRevocationSource Instance { get; } = new();

    private EmptyPluginRevocationSource()
    {
    }

    public ValueTask<bool> IsRevokedAsync(
        string publisherId,
        string pluginId,
        string signingCertificateFingerprint,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
}

public sealed class PluginManifestFile
{
    public string Path { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class PluginPackageManifest
{
    public const int CurrentFormatVersion = 2;

    public int FormatVersion { get; set; }
    public string PluginId { get; set; } = string.Empty;
    public string PublisherId { get; set; } = string.Empty;
    public string SigningCertificateFingerprint { get; set; } = string.Empty;
    public string PluginHash { get; set; } = string.Empty;
    public List<PluginManifestFile> Files { get; set; } = [];
}

public sealed class PluginCertificateValidationResult : IDisposable
{
    public PluginTrustReport Report { get; }
    public X509Certificate2? SigningCertificate { get; }
    public X509Certificate2? PublisherCertificate { get; }

    internal PluginCertificateValidationResult(
        PluginTrustReport report,
        X509Certificate2? signingCertificate = null,
        X509Certificate2? publisherCertificate = null)
    {
        Report = report;
        SigningCertificate = signingCertificate;
        PublisherCertificate = publisherCertificate;
    }

    public void Dispose()
    {
        SigningCertificate?.Dispose();
        PublisherCertificate?.Dispose();
    }
}

public static class PluginTrustValidator
{
    private const string CodeSigningEnhancedKeyUsageOid = "1.3.6.1.5.5.7.3.3";

    public static X509Certificate2Collection LoadCertificateChainFromPem(string pem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pem);
        var certificates = new X509Certificate2Collection();
        certificates.ImportFromPem(pem);
        return certificates;
    }

    public static PluginCertificateValidationResult ValidateCertificateChain(
        string pluginId,
        string publisherId,
        string expectedSigningCertificateFingerprint,
        X509Certificate2Collection packageCertificates,
        IReadOnlyCollection<X509Certificate2> trustedRoots,
        bool publisherTrusted,
        DateTimeOffset? verificationTime = null)
    {
        ArgumentNullException.ThrowIfNull(packageCertificates);
        ArgumentNullException.ThrowIfNull(trustedRoots);

        PluginCertificateValidationResult Invalid(string reason, string signingFingerprint = "") =>
            new(new PluginTrustReport(
                PluginTrustLevel.Invalid,
                pluginId,
                publisherId,
                null,
                signingFingerprint,
                reason));

        if (packageCertificates.Count < 2)
        {
            return Invalid("The plugin certificate chain must contain a signing certificate and a publisher CA certificate.");
        }

        if (trustedRoots.Count == 0)
        {
            return Invalid("No trusted plugin root CA certificate is configured.");
        }

        var signingCertificate = packageCertificates[0];
        var publisherCertificate = packageCertificates[1];
        var signingFingerprint = GetCertificateSha256Fingerprint(signingCertificate);
        var publisherFingerprint = GetCertificateSha256Fingerprint(publisherCertificate);

        if (!string.Equals(expectedSigningCertificateFingerprint, signingFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return Invalid("The signing certificate fingerprint does not match the plugin manifest.", signingFingerprint);
        }

        if (!string.Equals(publisherId, publisherFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return Invalid("The publisher id does not match the publisher CA certificate fingerprint.", signingFingerprint);
        }

        for (var index = 0; index < packageCertificates.Count - 1; index++)
        {
            if (!packageCertificates[index].IssuerName.RawData.AsSpan().SequenceEqual(packageCertificates[index + 1].SubjectName.RawData))
            {
                return Invalid("The certificates in publisher-chain.pem are not ordered from leaf to root.", signingFingerprint);
            }
        }

        if (IsCertificateAuthority(signingCertificate))
        {
            return Invalid("The plugin signing certificate must be an end-entity certificate.", signingFingerprint);
        }

        if (!IsCertificateAuthority(publisherCertificate))
        {
            return Invalid("The publisher certificate must be a CA certificate.", signingFingerprint);
        }

        if (!HasDigitalSignatureUsage(signingCertificate))
        {
            return Invalid("The plugin signing certificate is missing the Digital Signature key usage.", signingFingerprint);
        }

        if (!HasCodeSigningEnhancedKeyUsage(signingCertificate))
        {
            return Invalid("The plugin signing certificate is missing the Code Signing enhanced key usage.", signingFingerprint);
        }

        if (!HasCertificateSigningUsage(publisherCertificate))
        {
            return Invalid("The publisher CA certificate is missing the Certificate Signing key usage.", signingFingerprint);
        }

        foreach (var certificate in packageCertificates.Cast<X509Certificate2>().Concat(trustedRoots))
        {
            if (UsesWeakSignatureAlgorithm(certificate))
            {
                return Invalid("The certificate chain contains a certificate signed with a weak algorithm.", signingFingerprint);
            }
        }

        using (var rsa = signingCertificate.GetRSAPublicKey())
        {
            if (rsa is null || rsa.KeySize < 2048)
            {
                return Invalid("The plugin signing certificate must contain an RSA key of at least 2048 bits.", signingFingerprint);
            }
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.VerificationTime = (verificationTime ?? DateTimeOffset.UtcNow).UtcDateTime;
        foreach (var trustedRoot in trustedRoots)
        {
            chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
        }

        for (var index = 1; index < packageCertificates.Count; index++)
        {
            chain.ChainPolicy.ExtraStore.Add(packageCertificates[index]);
        }

        if (!chain.Build(signingCertificate))
        {
            var failures = string.Join(", ", chain.ChainStatus.Select(status => status.StatusInformation.Trim()).Where(message => message.Length > 0));
            return Invalid($"The plugin certificate chain is invalid{(failures.Length == 0 ? "." : $": {failures}")}", signingFingerprint);
        }

        var chainRoot = chain.ChainElements[^1].Certificate;
        var rootTrusted = trustedRoots.Any(root =>
            string.Equals(
                GetCertificateSha256Fingerprint(root),
                GetCertificateSha256Fingerprint(chainRoot),
                StringComparison.OrdinalIgnoreCase));

        if (!rootTrusted)
        {
            return Invalid("The plugin certificate chain does not terminate at a configured plugin root CA.", signingFingerprint);
        }

        var report = new PluginTrustReport(
            publisherTrusted ? PluginTrustLevel.PublisherTrusted : PluginTrustLevel.ChainValid,
            pluginId,
            publisherFingerprint,
            GetCertificateDisplayName(publisherCertificate),
            signingFingerprint,
            null);

        return new PluginCertificateValidationResult(
            report,
            X509CertificateLoader.LoadCertificate(signingCertificate.RawData),
            X509CertificateLoader.LoadCertificate(publisherCertificate.RawData));
    }

    public static byte[] GetCanonicalManifestBytes(PluginPackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", manifest.FormatVersion);
            writer.WriteString("pluginId", manifest.PluginId);
            writer.WriteString("publisherId", NormalizeFingerprint(manifest.PublisherId));
            writer.WriteString("signingCertificateFingerprint", NormalizeFingerprint(manifest.SigningCertificateFingerprint));
            writer.WriteString("pluginHash", NormalizeFingerprint(manifest.PluginHash));
            writer.WritePropertyName("files");
            writer.WriteStartArray();
            foreach (var file in manifest.Files
                         .Select(file => new
                         {
                             Path = NormalizeManifestPath(file.Path),
                             file.Sha256
                         })
                         .OrderBy(file => file.Path, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Path);
                writer.WriteString("sha256", NormalizeFingerprint(file.Sha256));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static bool VerifyManifestSignature(
        PluginPackageManifest manifest,
        string signatureBase64,
        X509Certificate2 signingCertificate)
    {
        ArgumentNullException.ThrowIfNull(signingCertificate);
        using var rsa = signingCertificate.GetRSAPublicKey();
        if (rsa is null)
        {
            return false;
        }

        try
        {
            return rsa.VerifyData(
                GetCanonicalManifestBytes(manifest),
                Convert.FromBase64String(signatureBase64.Trim()),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string GetCertificateSha256Fingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.RawData));
    }

    public static string DerivePluginEncryptionKey(X509Certificate2 signingCertificate)
    {
        ArgumentNullException.ThrowIfNull(signingCertificate);
        using var rsa = signingCertificate.GetRSAPublicKey()
            ?? throw new CryptographicException("The plugin signing certificate does not contain an RSA public key.");
        return Convert.ToHexString(SHA512.HashData(rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
    }

    public static string ComputeSha256Hex(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string NormalizeManifestPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') ||
            Path.IsPathRooted(normalized) ||
            (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':') ||
            normalized.Contains("//", StringComparison.Ordinal) ||
            normalized.Contains('\0'))
        {
            throw new InvalidDataException($"Manifest path '{path}' must be relative.");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException($"Manifest path '{path}' is invalid.");
        }

        return string.Join('/', segments);
    }

    private static string NormalizeFingerprint(string value) => value.Trim().ToLowerInvariant();

    private static bool IsCertificateAuthority(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault()?.CertificateAuthority == true;

    private static bool HasDigitalSignatureUsage(X509Certificate2 certificate)
    {
        var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        return usage is not null && usage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature);
    }

    private static bool HasCertificateSigningUsage(X509Certificate2 certificate)
    {
        var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        return usage is not null && usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign);
    }

    private static bool HasCodeSigningEnhancedKeyUsage(X509Certificate2 certificate)
    {
        var usage = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return usage?.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == CodeSigningEnhancedKeyUsageOid) == true;
    }

    private static bool UsesWeakSignatureAlgorithm(X509Certificate2 certificate)
    {
        var oid = certificate.SignatureAlgorithm.Value ?? string.Empty;
        var friendlyName = certificate.SignatureAlgorithm.FriendlyName ?? string.Empty;
        return oid is "1.2.840.113549.1.1.4" or "1.2.840.113549.1.1.5" or "1.2.840.10045.4.1" ||
               friendlyName.Contains("md5", StringComparison.OrdinalIgnoreCase) ||
               friendlyName.Contains("sha1", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCertificateDisplayName(X509Certificate2 certificate)
    {
        var simpleName = certificate.GetNameInfo(X509NameType.SimpleName, false);
        return string.IsNullOrWhiteSpace(simpleName) ? certificate.Subject : simpleName;
    }
}

public sealed class InMemoryPluginRevocationSource : IPluginRevocationSource
{
    private readonly HashSet<string> _publisherIds;
    private readonly HashSet<string> _pluginIds;
    private readonly HashSet<string> _signingCertificateFingerprints;

    public InMemoryPluginRevocationSource(
        IEnumerable<string>? publisherIds = null,
        IEnumerable<string>? pluginIds = null,
        IEnumerable<string>? signingCertificateFingerprints = null)
    {
        _publisherIds = new HashSet<string>(publisherIds ?? [], StringComparer.OrdinalIgnoreCase);
        _pluginIds = new HashSet<string>(pluginIds ?? [], StringComparer.Ordinal);
        _signingCertificateFingerprints = new HashSet<string>(signingCertificateFingerprints ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public ValueTask<bool> IsRevokedAsync(
        string publisherId,
        string pluginId,
        string signingCertificateFingerprint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            _publisherIds.Contains(publisherId) ||
            _pluginIds.Contains(pluginId) ||
            _signingCertificateFingerprints.Contains(signingCertificateFingerprint));
    }
}

public sealed class PluginRevocationSnapshot
{
    public long Version { get; set; }
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public List<string> RevokedPublisherIds { get; set; } = [];
    public List<string> RevokedPluginIds { get; set; } = [];
    public List<string> RevokedSigningCertificateFingerprints { get; set; } = [];
    public List<string> RevokedPackageDigests { get; set; } = [];
}

public sealed class SignedOfflinePluginRevocationSource : IPluginPackageRevocationSource
{
    private readonly HashSet<string> _publisherIds;
    private readonly HashSet<string> _pluginIds;
    private readonly HashSet<string> _signingCertificateFingerprints;
    private readonly HashSet<string> _packageDigests;

    public long Version { get; }
    public DateTimeOffset ExpiresAtUtc { get; }

    private SignedOfflinePluginRevocationSource(PluginRevocationSnapshot snapshot)
    {
        Version = snapshot.Version;
        ExpiresAtUtc = snapshot.ExpiresAtUtc;
        _publisherIds = new HashSet<string>(snapshot.RevokedPublisherIds, StringComparer.OrdinalIgnoreCase);
        _pluginIds = new HashSet<string>(snapshot.RevokedPluginIds, StringComparer.Ordinal);
        _signingCertificateFingerprints = new HashSet<string>(snapshot.RevokedSigningCertificateFingerprints, StringComparer.OrdinalIgnoreCase);
        _packageDigests = new HashSet<string>(snapshot.RevokedPackageDigests, StringComparer.OrdinalIgnoreCase);
    }

    public static SignedOfflinePluginRevocationSource CreateVerified(
        PluginRevocationSnapshot snapshot,
        string signatureBase64,
        X509Certificate2Collection signerChain,
        IReadOnlyCollection<X509Certificate2> trustedRoots,
        long minimumVersion = 0,
        DateTimeOffset? verificationTime = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(signerChain);
        var now = verificationTime ?? DateTimeOffset.UtcNow;
        if (snapshot.Version <= minimumVersion)
        {
            throw new CryptographicException("The revocation snapshot version is not newer than the accepted version.");
        }
        if (snapshot.GeneratedAtUtc > now.AddMinutes(5))
        {
            throw new CryptographicException("The revocation snapshot was generated in the future.");
        }
        if (snapshot.ExpiresAtUtc <= now || snapshot.ExpiresAtUtc <= snapshot.GeneratedAtUtc)
        {
            throw new CryptographicException("The revocation snapshot has expired or has an invalid validity range.");
        }
        if (signerChain.Count < 2)
        {
            throw new CryptographicException("The revocation snapshot signer chain is incomplete.");
        }

        var publisherId = PluginTrustValidator.GetCertificateSha256Fingerprint(signerChain[1]);
        var signerFingerprint = PluginTrustValidator.GetCertificateSha256Fingerprint(signerChain[0]);
        using var validation = PluginTrustValidator.ValidateCertificateChain(
            "projectframecut.revocation.snapshot",
            publisherId,
            signerFingerprint,
            signerChain,
            trustedRoots,
            publisherTrusted: true,
            now);
        if (validation.Report.Level == PluginTrustLevel.Invalid || validation.SigningCertificate is null)
        {
            throw new CryptographicException(validation.Report.FailureReason ?? "The revocation snapshot signer is invalid.");
        }

        using var rsa = validation.SigningCertificate.GetRSAPublicKey()
            ?? throw new CryptographicException("The revocation snapshot signer must use RSA.");
        bool signatureValid;
        try
        {
            signatureValid = rsa.VerifyData(
                GetCanonicalSnapshotBytes(snapshot),
                Convert.FromBase64String(signatureBase64.Trim()),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        catch (FormatException)
        {
            signatureValid = false;
        }
        if (!signatureValid)
        {
            throw new CryptographicException("The revocation snapshot signature is invalid.");
        }

        return new SignedOfflinePluginRevocationSource(snapshot);
    }

    public ValueTask<bool> IsRevokedAsync(
        string publisherId,
        string pluginId,
        string signingCertificateFingerprint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            _publisherIds.Contains(publisherId) ||
            _pluginIds.Contains(pluginId) ||
            _signingCertificateFingerprints.Contains(signingCertificateFingerprint));
    }

    public ValueTask<bool> IsPackageRevokedAsync(
        string packageDigest,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_packageDigests.Contains(packageDigest));
    }

    public static byte[] GetCanonicalSnapshotBytes(PluginRevocationSnapshot snapshot)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", snapshot.Version);
            writer.WriteString("generatedAtUtc", snapshot.GeneratedAtUtc.ToUniversalTime());
            writer.WriteString("expiresAtUtc", snapshot.ExpiresAtUtc.ToUniversalTime());
            WriteSortedArray(writer, "revokedPublisherIds", snapshot.RevokedPublisherIds);
            WriteSortedArray(writer, "revokedPluginIds", snapshot.RevokedPluginIds);
            WriteSortedArray(writer, "revokedSigningCertificateFingerprints", snapshot.RevokedSigningCertificateFingerprints);
            WriteSortedArray(writer, "revokedPackageDigests", snapshot.RevokedPackageDigests);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteSortedArray(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var value in values.OrderBy(value => value, StringComparer.Ordinal))
        {
            writer.WriteStringValue(value.Trim().ToLowerInvariant());
        }
        writer.WriteEndArray();
    }
}
