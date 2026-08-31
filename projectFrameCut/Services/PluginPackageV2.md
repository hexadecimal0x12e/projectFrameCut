# projectFrameCut external plugin package v2

Version 2 packages are ZIP archives. All paths use `/`, are relative to the archive root,
and are compared case-insensitively. The archive must contain:

- `metadata.json`
- `manifest.json`
- `manifest.sig`
- `publisher-chain.pem`
- `<PluginID>.dll.enc`
- `<PluginID>.dll.sig`
- every immutable dependency listed by `manifest.json`

`publisher-chain.pem` is ordered from the end-entity plugin signing certificate to its
issuer. The second certificate is the publisher CA. A packaged root certificate never
becomes trusted; trust anchors are supplied by the application.

`PublisherId` is the uppercase SHA-256 fingerprint of the publisher CA DER certificate.
`SigningCertificateFingerprint` is the uppercase SHA-256 fingerprint of the signing
certificate DER certificate. The signing certificate must use RSA (at least 2048 bits),
Digital Signature key usage, and the Code Signing EKU (`1.3.6.1.5.5.7.3.3`).

The encryption password in `PluginKey` is the lowercase hexadecimal SHA-512 hash of the
signing certificate's RSA SubjectPublicKeyInfo DER. `<PluginID>.dll.enc` uses the existing
`FileCryptoService.EncryptToFileWithPassword` format. `PluginHash` is the lowercase
SHA-256 hash of the decrypted assembly.

`manifest.json` has this canonical logical shape:

```json
{
  "formatVersion": 2,
  "pluginId": "publisher.Plugin",
  "publisherId": "<publisher CA SHA-256>",
  "signingCertificateFingerprint": "<leaf SHA-256>",
  "pluginHash": "<decrypted assembly SHA-256>",
  "files": [
    { "path": "metadata.json", "sha256": "<file SHA-256>" }
  ]
}
```

For signing, serialize without whitespace in the property order shown above, lowercase all
fingerprints/hashes, normalize file paths, and sort `files` by ordinal path. `manifest.sig`
is a Base64 RSA-PKCS#1 SHA-256 signature over those UTF-8 bytes. `<PluginID>.dll.sig` is a
Base64 RSA-PKCS#1 SHA-256 signature over the decrypted assembly bytes.

`manifest.json`, `manifest.sig`, and host-managed `option.json` are not included in the
manifest file list. Runtime mutable files must be placed under `data/`; executable code and
dependencies must always be present in the signed file list.

The reference packer is `tools/PluginPackageUtility`. It keeps the signing private key
outside the application and generates the package files and signatures described above.
