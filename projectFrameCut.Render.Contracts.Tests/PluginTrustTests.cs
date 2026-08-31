using projectFrameCut.Shared;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class PluginTrustTests
{
    [TestMethod]
    public void ValidRootPublisherAndSigningChainIsAccepted()
    {
        using var certificates = TestCertificateChain.Create();
        using var result = PluginTrustValidator.ValidateCertificateChain(
            "example.plugin",
            PluginTrustValidator.GetCertificateSha256Fingerprint(certificates.Publisher),
            PluginTrustValidator.GetCertificateSha256Fingerprint(certificates.Leaf),
            certificates.PackageChain,
            [certificates.Root],
            publisherTrusted: true);

        Assert.AreEqual(PluginTrustLevel.PublisherTrusted, result.Report.Level);
        Assert.IsNull(result.Report.FailureReason);
    }

    [TestMethod]
    public void UnknownRootIsRejected()
    {
        using var certificates = TestCertificateChain.Create();
        using var unknown = TestCertificateChain.Create("CN=Unknown Plugin Root");
        using var result = PluginTrustValidator.ValidateCertificateChain(
            "example.plugin",
            PluginTrustValidator.GetCertificateSha256Fingerprint(certificates.Publisher),
            PluginTrustValidator.GetCertificateSha256Fingerprint(certificates.Leaf),
            certificates.PackageChain,
            [unknown.Root],
            publisherTrusted: false);

        Assert.AreEqual(PluginTrustLevel.Invalid, result.Report.Level);
    }

    [TestMethod]
    public void MissingPublisherCertificateIsRejected()
    {
        using var certificates = TestCertificateChain.Create();
        var packageChain = new X509Certificate2Collection(certificates.Leaf);
        using var result = PluginTrustValidator.ValidateCertificateChain(
            "example.plugin",
            PluginTrustValidator.GetCertificateSha256Fingerprint(certificates.Publisher),
            PluginTrustValidator.GetCertificateSha256Fingerprint(certificates.Leaf),
            packageChain,
            [certificates.Root],
            publisherTrusted: false);

        Assert.AreEqual(PluginTrustLevel.Invalid, result.Report.Level);
        StringAssert.Contains(result.Report.FailureReason, "publisher CA");
    }

    [TestMethod]
    public void ReorderedCertificateChainIsRejected()
    {
        using var certificates = TestCertificateChain.Create();
        var packageChain = new X509Certificate2Collection(new[] { certificates.Publisher, certificates.Leaf });
        using var result = PluginTrustValidator.ValidateCertificateChain(
            "example.plugin",
            PluginTrustValidator.GetCertificateSha256Fingerprint(certificates.Publisher),
            PluginTrustValidator.GetCertificateSha256Fingerprint(certificates.Leaf),
            packageChain,
            [certificates.Root],
            publisherTrusted: false);

        Assert.AreEqual(PluginTrustLevel.Invalid, result.Report.Level);
    }

    [TestMethod]
    public void PublisherIdMustMatchPublisherCertificate()
    {
        using var certificates = TestCertificateChain.Create();
        using var result = PluginTrustValidator.ValidateCertificateChain(
            "example.plugin",
            new string('0', 64),
            PluginTrustValidator.GetCertificateSha256Fingerprint(certificates.Leaf),
            certificates.PackageChain,
            [certificates.Root],
            publisherTrusted: false);

        Assert.AreEqual(PluginTrustLevel.Invalid, result.Report.Level);
        StringAssert.Contains(result.Report.FailureReason, "publisher id");
    }

    [TestMethod]
    public void SigningCertificateWithoutCodeSigningUsageIsRejected()
    {
        using var certificates = TestCertificateChain.Create(includeCodeSigningEku: false);
        using var result = PluginTrustValidator.ValidateCertificateChain(
            "example.plugin",
            PluginTrustValidator.GetCertificateSha256Fingerprint(certificates.Publisher),
            PluginTrustValidator.GetCertificateSha256Fingerprint(certificates.Leaf),
            certificates.PackageChain,
            [certificates.Root],
            publisherTrusted: false);

        Assert.AreEqual(PluginTrustLevel.Invalid, result.Report.Level);
        StringAssert.Contains(result.Report.FailureReason, "Code Signing");
    }

    [TestMethod]
    public void ManifestPathsCannotEscapePackageRoot()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => PluginTrustValidator.NormalizeManifestPath("../outside.dll"));
        Assert.ThrowsExactly<InvalidDataException>(() => PluginTrustValidator.NormalizeManifestPath("/absolute.dll"));
    }

    [TestMethod]
    public void ManifestSignatureCoversCanonicalManifestAndFileHashes()
    {
        using var certificates = TestCertificateChain.Create();
        var manifest = new PluginPackageManifest
        {
            FormatVersion = PluginPackageManifest.CurrentFormatVersion,
            PluginId = "example.plugin",
            PublisherId = PluginTrustValidator.GetCertificateSha256Fingerprint(certificates.Publisher),
            SigningCertificateFingerprint = PluginTrustValidator.GetCertificateSha256Fingerprint(certificates.Leaf),
            PluginHash = new string('a', 64),
            Files =
            [
                new PluginManifestFile { Path = "metadata.json", Sha256 = new string('b', 64) }
            ]
        };
        var signature = Convert.ToBase64String(certificates.LeafKey.SignData(
            PluginTrustValidator.GetCanonicalManifestBytes(manifest),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));

        Assert.IsTrue(PluginTrustValidator.VerifyManifestSignature(manifest, signature, certificates.Leaf));
        manifest.Files[0].Sha256 = new string('c', 64);
        Assert.IsFalse(PluginTrustValidator.VerifyManifestSignature(manifest, signature, certificates.Leaf));
    }

    [TestMethod]
    public async Task RevocationSourceRejectsPublisherPluginOrCertificate()
    {
        var source = new InMemoryPluginRevocationSource(pluginIds: ["example.plugin"]);
        Assert.IsTrue(await source.IsRevokedAsync("publisher", "example.plugin", "certificate"));
        Assert.IsFalse(await source.IsRevokedAsync("publisher", "another.plugin", "certificate"));
    }

    [TestMethod]
    public async Task SignedOfflineRevocationSnapshotIsVerifiedAndApplied()
    {
        using var certificates = TestCertificateChain.Create();
        var snapshot = new PluginRevocationSnapshot
        {
            Version = 3,
            GeneratedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
            RevokedPluginIds = ["example.plugin"],
            RevokedPackageDigests = [new string('d', 64)]
        };
        var signature = Convert.ToBase64String(certificates.LeafKey.SignData(
            SignedOfflinePluginRevocationSource.GetCanonicalSnapshotBytes(snapshot),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));

        var source = SignedOfflinePluginRevocationSource.CreateVerified(
            snapshot,
            signature,
            certificates.PackageChain,
            [certificates.Root],
            minimumVersion: 2);

        Assert.IsTrue(await source.IsRevokedAsync("publisher", "example.plugin", "certificate"));
        Assert.IsTrue(await source.IsPackageRevokedAsync(new string('d', 64)));
    }

    private sealed class TestCertificateChain : IDisposable
    {
        public RSA RootKey { get; }
        public RSA PublisherKey { get; }
        public RSA LeafKey { get; }
        public X509Certificate2 Root { get; }
        public X509Certificate2 Publisher { get; }
        public X509Certificate2 Leaf { get; }
        public X509Certificate2Collection PackageChain { get; }

        private TestCertificateChain(
            RSA rootKey,
            RSA publisherKey,
            RSA leafKey,
            X509Certificate2 root,
            X509Certificate2 publisher,
            X509Certificate2 leaf)
        {
            RootKey = rootKey;
            PublisherKey = publisherKey;
            LeafKey = leafKey;
            Root = root;
            Publisher = publisher;
            Leaf = leaf;
            PackageChain = new X509Certificate2Collection(new[] { Leaf, Publisher });
        }

        public static TestCertificateChain Create(
            string rootSubject = "CN=projectFrameCut Test Plugin Root",
            bool includeCodeSigningEku = true)
        {
            var now = DateTimeOffset.UtcNow;
            var rootKey = RSA.Create(2048);
            var rootRequest = new CertificateRequest(rootSubject, rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                true));
            rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
            var root = rootRequest.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));

            var publisherKey = RSA.Create(2048);
            var publisherRequest = new CertificateRequest(
                "CN=Example Plugin Publisher",
                publisherKey,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            publisherRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            publisherRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                true));
            publisherRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(publisherRequest.PublicKey, false));
            var publisherPublic = publisherRequest.Create(root, now.AddDays(-1), now.AddYears(5), RandomNumberGenerator.GetBytes(16));
            var publisher = publisherPublic.CopyWithPrivateKey(publisherKey);
            publisherPublic.Dispose();

            var leafKey = RSA.Create(2048);
            var leafRequest = new CertificateRequest(
                "CN=Example Plugin Signing Certificate",
                leafKey,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            if (includeCodeSigningEku)
            {
                var enhancedUsages = new OidCollection { new Oid("1.3.6.1.5.5.7.3.3") };
                leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(enhancedUsages, true));
            }
            leafRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(leafRequest.PublicKey, false));
            var leafPublic = leafRequest.Create(publisher, now.AddDays(-1), now.AddYears(1), RandomNumberGenerator.GetBytes(16));
            var leaf = leafPublic.CopyWithPrivateKey(leafKey);
            leafPublic.Dispose();

            return new TestCertificateChain(rootKey, publisherKey, leafKey, root, publisher, leaf);
        }

        public void Dispose()
        {
            Leaf.Dispose();
            Publisher.Dispose();
            Root.Dispose();
            LeafKey.Dispose();
            PublisherKey.Dispose();
            RootKey.Dispose();
        }
    }
}
