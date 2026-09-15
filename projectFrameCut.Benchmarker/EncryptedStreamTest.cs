using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Shared;
using System.Security.Cryptography;

namespace projectFrameCut.Benchmarker;

[TestClass]
public class EncryptedStreamTest
{
    [TestMethod]
    public void RoundTripAndRandomSeek()
    {
        byte[] key = EncryptedStreamCrypto.GenerateKey();
        byte[] data = new byte[3 * 4096 + 137];
        RandomNumberGenerator.Fill(data);
        using var encrypted = new MemoryStream();
        using (var plaintext = new MemoryStream(data))
            EncryptedStreamCrypto.Encrypt(plaintext, encrypted, key, 4096);

        encrypted.Position = 0;
        using var decrypted = EncryptedStreamCrypto.OpenRead(encrypted, key, true);
        Assert.AreEqual(data.Length, decrypted.Length);

        byte[] range = new byte[5000];
        decrypted.Position = 3800;
        decrypted.ReadExactly(range);
        CollectionAssert.AreEqual(data.AsSpan(3800, range.Length).ToArray(), range);

        decrypted.Seek(-137, SeekOrigin.End);
        byte[] tail = new byte[137];
        decrypted.ReadExactly(tail);
        CollectionAssert.AreEqual(data[^137..], tail);

        decrypted.Position = 0;
        using var restored = new MemoryStream();
        decrypted.CopyTo(restored);
        CollectionAssert.AreEqual(data, restored.ToArray());
    }

    [TestMethod]
    public void RejectsWrongKeyTamperingAndTruncation()
    {
        byte[] key = EncryptedStreamCrypto.GenerateKey();
        byte[] data = new byte[5000];
        RandomNumberGenerator.Fill(data);
        byte[] encrypted;
        using (var output = new MemoryStream())
        {
            using var input = new MemoryStream(data);
            EncryptedStreamCrypto.Encrypt(input, output, key, 4096);
            encrypted = output.ToArray();
        }

        ExpectCryptographicFailure(() =>
        {
            using var stream = new MemoryStream(encrypted);
            using var _ = EncryptedStreamCrypto.OpenRead(stream, EncryptedStreamCrypto.GenerateKey());
        });

        byte[] changedHeader = encrypted.ToArray();
        changedHeader[24] ^= 0x20;
        ExpectCryptographicFailure(() =>
        {
            using var stream = new MemoryStream(changedHeader);
            using var _ = EncryptedStreamCrypto.OpenRead(stream, key);
        });

        byte[] tampered = encrypted.ToArray();
        tampered[^17] ^= 0x40;
        using (var stream = new MemoryStream(tampered))
        using (var decrypted = EncryptedStreamCrypto.OpenRead(stream, key))
        {
            decrypted.Position = data.Length - 1;
            ExpectCryptographicFailure(() => decrypted.ReadByte());
        }

        using var truncated = new MemoryStream(encrypted[..^1]);
        Assert.ThrowsExactly<InvalidDataException>(() => EncryptedStreamCrypto.OpenRead(truncated, key));
    }

    [TestMethod]
    public void HonorsLeaveOpen()
    {
        byte[] key = EncryptedStreamCrypto.GenerateKey();
        using var plaintext = new MemoryStream([1, 2, 3]);
        var encrypted = new MemoryStream();
        EncryptedStreamCrypto.Encrypt(plaintext, encrypted, key, 4096);
        encrypted.Position = 0;
        EncryptedStreamCrypto.OpenRead(encrypted, key, true).Dispose();
        Assert.IsTrue(encrypted.CanRead);

        encrypted.Position = 0;
        EncryptedStreamCrypto.OpenRead(encrypted, key).Dispose();
        Assert.IsFalse(encrypted.CanRead);
    }

    [TestMethod]
    public void DecodesEncryptedVideoThroughStreamSource()
    {
        string path = FindSampleVideo();
        byte[] key = EncryptedStreamCrypto.GenerateKey();
        using var encrypted = new MemoryStream();
        using (var input = File.OpenRead(path))
            EncryptedStreamCrypto.Encrypt(input, encrypted, key);

        encrypted.Position = 0;
        using var decrypted = EncryptedStreamCrypto.OpenRead(encrypted, key, true);
        using var source = new HDRDecoderContext().FromStream(decrypted, decrypted.Length, true);
        using var first = source.GetFrame(0);
        using var later = source.GetFrame(10);
        Assert.AreEqual(first.Width, later.Width);
        Assert.AreEqual(first.Height, later.Height);
    }

    private static string FindSampleVideo()
    {
        string? dir = AppDomain.CurrentDomain.BaseDirectory;
        while (dir is not null)
        {
            string path = Path.Combine(dir, "SampleMedia.mp4");
            if (File.Exists(path)) return path;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("Test video 'SampleMedia.mp4' was not found.");
    }

    private static void ExpectCryptographicFailure(Action action)
    {
        try
        {
            action();
            Assert.Fail("Expected encrypted stream authentication to fail.");
        }
        catch (CryptographicException)
        {
        }
    }
}
