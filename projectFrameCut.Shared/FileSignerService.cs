using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace projectFrameCut.Shared
{
    public static class FileSignerService 
    {
        public static (string publicKeyPem, string privateKeyPem) GenerateKeyPair(int keySize = 2048)
        {
            using var rsa = RSA.Create(keySize);
            var privateKeyPem = ExportPrivateKeyToPem(rsa);
            var publicKeyPem = ExportPublicKeyToPem(rsa);
            return (publicKeyPem, privateKeyPem);
        }

        public static string SignFile(string privateKeyPem, string filePath)
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKeyPem.ToCharArray());
            using var stream = File.OpenRead(filePath);
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(stream);
            var signature = rsa.SignHash(hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return Convert.ToBase64String(signature);
        }

        public static bool VerifyFileSignature(string publicKeyPem, string filePath, string signatureBase64)
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem.ToCharArray());
            using var stream = File.OpenRead(filePath);
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(stream);
            var signature = Convert.FromBase64String(signatureBase64);
            return rsa.VerifyHash(hash, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        public static bool VerifyFileSignature(string publicKeyPem, byte[] data, string signatureBase64)
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem.ToCharArray());
            using var stream = new MemoryStream(data);
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(stream);
            var signature = Convert.FromBase64String(signatureBase64);
            return rsa.VerifyHash(hash, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        static string ExportPrivateKeyToPem(RSA rsa)
        {
            var bytes = rsa.ExportPkcs8PrivateKey();
            var base64 = Convert.ToBase64String(bytes);
            return "-----BEGIN PRIVATE KEY-----\n" + InsertLineBreaks(base64) + "-----END PRIVATE KEY-----\n";
        }

        static string ExportPublicKeyToPem(RSA rsa)
        {
            var bytes = rsa.ExportSubjectPublicKeyInfo();
            var base64 = Convert.ToBase64String(bytes);
            return "-----BEGIN PUBLIC KEY-----\n" + InsertLineBreaks(base64) + "-----END PUBLIC KEY-----\n";
        }

        static string InsertLineBreaks(string base64)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < base64.Length; i += 64)
            {
                sb.AppendLine(base64.Substring(i, Math.Min(64, base64.Length - i)));
            }
            return sb.ToString();
        }
    }
}
