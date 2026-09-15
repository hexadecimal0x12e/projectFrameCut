using System.Buffers.Binary;
using System.Security.Cryptography;

namespace projectFrameCut.Shared;

public static class EncryptedStreamCrypto
{
    internal const int HeaderDataSize = 40;
    internal const int HeaderSize = 56;
    internal const int TagSize = 16;
    internal const int NonceSize = 12;
    public const int DefaultBlockSize = 1024 * 1024;
    internal const int MinBlockSize = 4 * 1024;
    internal const int MaxBlockSize = 64 * 1024 * 1024;
    internal const ushort Version = 1;
    internal static ReadOnlySpan<byte> Magic => "PJFCENC1"u8;

    public static byte[] GenerateKey()
    {
        byte[] key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    public static string GenerateBase64Key() => Convert.ToBase64String(GenerateKey());

    public static byte[] KeyFromBase64(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        byte[] result = Convert.FromBase64String(key);
        ValidateKey(result);
        return result;
    }

    public static EncryptedReadStream OpenRead(Stream source, byte[] key, bool leaveOpen = false) =>
        new(source, key, leaveOpen);

    public static EncryptedReadStream OpenRead(string path, byte[] key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var source = File.OpenRead(path);
        try
        {
            return new EncryptedReadStream(source, key);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    public static void Encrypt(Stream plaintext, Stream destination, byte[] key,
        int blockSize = DefaultBlockSize)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(destination);
        ValidateKey(key);
        ValidateBlockSize(blockSize);
        if (!plaintext.CanRead) throw new ArgumentException("The plaintext stream must be readable.", nameof(plaintext));
        if (!plaintext.CanSeek) throw new ArgumentException("The plaintext stream must be seekable.", nameof(plaintext));
        if (plaintext.Position != 0) throw new ArgumentException("The plaintext stream position must be zero.", nameof(plaintext));
        if (!destination.CanWrite) throw new ArgumentException("The destination stream must be writable.", nameof(destination));

        long plaintextLength = plaintext.Length;
        long blockCount = GetBlockCount(plaintextLength, blockSize);
        if (blockCount > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(plaintext), "The plaintext is too large for the selected block size.");

        byte[] header = new byte[HeaderSize];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), blockSize);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), plaintextLength);
        RandomNumberGenerator.Fill(header.AsSpan(24, NonceSize));

        byte[] nonce = new byte[NonceSize];
        byte[] aad = new byte[HeaderDataSize + sizeof(uint)];
        header.AsSpan(0, HeaderDataSize).CopyTo(aad);
        header.AsSpan(24, NonceSize).CopyTo(nonce);
        uint nonceTail = BinaryPrimitives.ReadUInt32LittleEndian(nonce.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(nonce.AsSpan(8), nonceTail ^ uint.MaxValue);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, ReadOnlySpan<byte>.Empty, Span<byte>.Empty,
            header.AsSpan(HeaderDataSize, TagSize), header.AsSpan(0, HeaderDataSize));
        destination.Write(header);

        byte[] plainBlock = new byte[blockSize];
        byte[] cipherBlock = new byte[blockSize];
        byte[] tag = new byte[TagSize];
        try
        {
            for (uint i = 0; i < blockCount; i++)
            {
                int length = (int)Math.Min(blockSize, plaintextLength - (long)i * blockSize);
                plaintext.ReadExactly(plainBlock.AsSpan(0, length));
                BinaryPrimitives.WriteUInt32LittleEndian(nonce.AsSpan(8), nonceTail ^ i);
                BinaryPrimitives.WriteUInt32LittleEndian(aad.AsSpan(HeaderDataSize), i);
                aes.Encrypt(nonce, plainBlock.AsSpan(0, length), cipherBlock.AsSpan(0, length), tag, aad);
                destination.Write(cipherBlock, 0, length);
                destination.Write(tag);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBlock);
            CryptographicOperations.ZeroMemory(cipherBlock);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    public static void EncryptFile(string inputPath, string outputPath, byte[] key,
        int blockSize = DefaultBlockSize, bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        string input = Path.GetFullPath(inputPath);
        string output = Path.GetFullPath(outputPath);
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The input and output paths must be different.", nameof(outputPath));
        if (!overwrite && File.Exists(output)) throw new IOException($"The output file already exists: '{output}'.");

        string? dir = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir ?? Directory.GetCurrentDirectory(), $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var source = File.OpenRead(input))
            using (var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                Encrypt(source, destination, key, blockSize);
            File.Move(temp, output, overwrite);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    internal static void ValidateKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length is not (16 or 24 or 32))
            throw new ArgumentException("AES keys must contain 16, 24, or 32 bytes.", nameof(key));
    }

    internal static void ValidateBlockSize(int blockSize)
    {
        if (blockSize < MinBlockSize || blockSize > MaxBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be between {MinBlockSize} and {MaxBlockSize} bytes.");
    }

    internal static long GetBlockCount(long plaintextLength, int blockSize)
    {
        if (plaintextLength < 0) throw new InvalidDataException("The plaintext length cannot be negative.");
        return plaintextLength == 0 ? 0 : checked((plaintextLength - 1) / blockSize + 1);
    }

    internal static long GetEncryptedLength(long plaintextLength, int blockSize) =>
        checked(HeaderSize + plaintextLength + GetBlockCount(plaintextLength, blockSize) * TagSize);
}
