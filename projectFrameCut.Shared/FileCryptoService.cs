using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace projectFrameCut.Shared
{
    public static class FileCryptoService
    {
        // 12-byte nonce and 16-byte tag are standard for AES-GCM
        private const int NonceSize = 12;
        private const int TagSize = 16;
        private const int SaltSize = 16;
        private const int KdfIterations = 100_000;

        public static string GenerateBase64Key(int keySize = 32)
        {
            var key = new byte[keySize];
            RandomNumberGenerator.Fill(key);
            return Convert.ToBase64String(key);
        }

        public static byte[] Encrypt(byte[] key, byte[] plaintext)
        {
            var nonce = new byte[NonceSize];
            RandomNumberGenerator.Fill(nonce);

            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];

            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, null);

            var outBuf = new byte[NonceSize + TagSize + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, outBuf, 0, NonceSize);
            Buffer.BlockCopy(tag, 0, outBuf, NonceSize, TagSize);
            Buffer.BlockCopy(ciphertext, 0, outBuf, NonceSize + TagSize, ciphertext.Length);
            return outBuf;
        }

        public static byte[] Decrypt(byte[] key, byte[] ciphertextCombined)
        {
            if (ciphertextCombined == null || ciphertextCombined.Length < NonceSize + TagSize)
                throw new ArgumentException("Ciphertext too short", nameof(ciphertextCombined));

            var nonce = new byte[NonceSize];
            var tag = new byte[TagSize];
            Buffer.BlockCopy(ciphertextCombined, 0, nonce, 0, NonceSize);
            Buffer.BlockCopy(ciphertextCombined, NonceSize, tag, 0, TagSize);

            var cipherLen = ciphertextCombined.Length - NonceSize - TagSize;
            var cipher = new byte[cipherLen];
            Buffer.BlockCopy(ciphertextCombined, NonceSize + TagSize, cipher, 0, cipherLen);

            var plaintext = new byte[cipherLen];
            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Decrypt(nonce, cipher, tag, plaintext, null);
            return plaintext;
        }

        public static void EncryptToFile(string base64Key, string inputFilePath, string outputFilePath)
        {
            var key = Convert.FromBase64String(base64Key);
            using var inStream = File.OpenRead(inputFilePath);
            using var ms = new MemoryStream();
            inStream.CopyTo(ms);
            var inputBytes = ms.ToArray();
            var outBytes = Encrypt(key, inputBytes);
            File.WriteAllBytes(outputFilePath, outBytes);
        }

        public static void DecryptToFile(string base64Key, string inputFilePath, string outputFilePath)
        {
            var key = Convert.FromBase64String(base64Key);
            var combined = File.ReadAllBytes(inputFilePath);
            var plain = Decrypt(key, combined);
            File.WriteAllBytes(outputFilePath, plain);
        }

        public static byte[] EncryptToFileWithPassword(string password, byte[] inputBytes)
        {
            // nonce|tag|ciphertext
            var salt = new byte[SaltSize];
            RandomNumberGenerator.Fill(salt);
            var key = DeriveKeyFromPassword(password, salt);

            return Encrypt(key, inputBytes);
        }


        public static void EncryptToFileWithPassword(string password, string inputFilePath, string outputFilePath)
        {
            var salt = new byte[SaltSize];
            RandomNumberGenerator.Fill(salt);
            var key = DeriveKeyFromPassword(password, salt);

            var inputBytes = File.ReadAllBytes(inputFilePath);
            var outBytes = Encrypt(key, inputBytes);

            using var fs = File.Create(outputFilePath);
            fs.Write(salt, 0, salt.Length); // write salt first
            fs.Write(outBytes, 0, outBytes.Length);
        }

        public static void DecryptToFileWithPassword(string password, string inputFilePath, string outputFilePath)
        {
            var combined = File.ReadAllBytes(inputFilePath);
            if (combined.Length < SaltSize + NonceSize + TagSize)
                throw new ArgumentException("Invalid input file.", nameof(inputFilePath));

            var salt = new byte[SaltSize];
            Buffer.BlockCopy(combined, 0, salt, 0, SaltSize);
            var restLen = combined.Length - SaltSize;
            var rest = new byte[restLen];
            Buffer.BlockCopy(combined, SaltSize, rest, 0, restLen);

            var key = DeriveKeyFromPassword(password, salt);
            var plain = Decrypt(key, rest);
            File.WriteAllBytes(outputFilePath, plain);
        }

        public static byte[] DecryptToFileWithPassword(string password, byte[] inputBytes)
        {
            if (inputBytes.Length < SaltSize + NonceSize + TagSize)
                throw new ArgumentException("Invalid input data.", nameof(inputBytes));

            var salt = new byte[SaltSize];
            Buffer.BlockCopy(inputBytes, 0, salt, 0, SaltSize);
            var restLen = inputBytes.Length - SaltSize;
            var rest = new byte[restLen];
            Buffer.BlockCopy(inputBytes, SaltSize, rest, 0, restLen);

            var key = DeriveKeyFromPassword(password, salt);
            return Decrypt(key, rest);
        }

        private static byte[] DeriveKeyFromPassword(string password, byte[] salt, int keySize = 32)
        {
            var pwdBytes = Encoding.UTF8.GetBytes(password);
            return Rfc2898DeriveBytes.Pbkdf2(pwdBytes, salt, KdfIterations, HashAlgorithmName.SHA256, keySize);
        }
    }
}
