Place the production plugin root CA public certificate at:

    Resources/Raw/PluginTrust/builtin-root-ca.cer

The project embeds this certificate in the main managed assembly as the manifest
resource "projectFrameCut.PluginTrust.builtin-root-ca.cer". It is explicitly
excluded from MAUI raw assets and is not loaded as a standalone package file.

Then set BuiltInRootCertificateSha256 in Services/PluginPackageSecurityService.cs
to the uppercase SHA-256 fingerprint of the DER certificate.

Never place the root CA private key in this repository or application package.
