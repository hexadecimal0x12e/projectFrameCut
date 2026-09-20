using System.Security.Cryptography;
using System.Text.Json;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.RPCProtocol;

public static class ExternalRpcAuthorizationStore
{
    private const string EncryptedFileName = "ExternalRpcClients.dat";
    private const string LegacyFileName = "ExternalRpcClients.json";
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static byte[]? _encryptionKey;

    public static string GetPath(string requestDirectory) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(requestDirectory))!, EncryptedFileName);

    public static void SetEncryptionKey(byte[] key)
    {
        if (key is null || key.Length is not (16 or 24 or 32)) throw new ArgumentException("The encryption key must be 128, 192, or 256 bits.", nameof(key));
        _encryptionKey = key.ToArray();
    }

    public static IReadOnlyList<ExternalRpcClientAuthorization> Read(string path)
    {
        lock (Gate)
        {
            using var crossProcessLock = AcquireCrossProcessLock(path);
            return ReadUnsafe(path);
        }
    }

    public static ExternalRpcClientAuthorization? Find(string path, Guid clientId) => Read(path).FirstOrDefault(c => c.ClientId == clientId);

    public static ExternalRpcClientAuthorization Add(string path, ExternalRpcClientAuthorization authorization)
    {
        lock (Gate)
        {
            using var crossProcessLock = AcquireCrossProcessLock(path);
            var items = ReadUnsafe(path);
            if (items.Any(c => c.ClientId == authorization.ClientId || c.PublicKey == authorization.PublicKey && !c.Revoked))
                throw new InvalidOperationException("This external RPC client is already authorized.");
            items.Add(authorization);
            WriteUnsafe(path, items);
            return authorization;
        }
    }

    public static void MarkUsed(string path, Guid clientId)
    {
        lock (Gate)
        {
            using var crossProcessLock = AcquireCrossProcessLock(path);
            var items = ReadUnsafe(path);
            var item = items.FirstOrDefault(c => c.ClientId == clientId) ?? throw new KeyNotFoundException("External RPC client authorization was not found.");
            item.LastUsedAt = DateTimeOffset.UtcNow;
            WriteUnsafe(path, items);
        }
    }

    public static void Revoke(string path, Guid clientId)
    {
        lock (Gate)
        {
            using var crossProcessLock = AcquireCrossProcessLock(path);
            var items = ReadUnsafe(path);
            var item = items.FirstOrDefault(c => c.ClientId == clientId) ?? throw new KeyNotFoundException("External RPC client authorization was not found.");
            item.Revoked = true;
            WriteUnsafe(path, items);
        }
    }

    public static string Fingerprint(string publicKey) => Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(publicKey)));

    private static IDisposable AcquireCrossProcessLock(string path)
    {
        var mutex = new Mutex(false, "projectFrameCut.ExternalRpc." + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path)))));
        try
        {
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(5))) throw new TimeoutException("Timed out waiting for the external RPC authorization store.");
            }
            catch (AbandonedMutexException) { }
            return new MutexReleaser(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    private static List<ExternalRpcClientAuthorization> ReadUnsafe(string path)
    {
        var actualPath = File.Exists(path) ? path : GetLegacyPath(path);
        if (!File.Exists(actualPath)) return [];

        var data = File.ReadAllBytes(actualPath);
        if (string.Equals(Path.GetExtension(actualPath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            var legacyItems = JsonSerializer.Deserialize<List<ExternalRpcClientAuthorization>>(data) ?? [];
            WriteUnsafe(path, legacyItems);
            return legacyItems;
        }

        return JsonSerializer.Deserialize<List<ExternalRpcClientAuthorization>>(FileCryptoService.Decrypt(GetEncryptionKey(), data)) ?? [];
    }

    private static void WriteUnsafe(string path, List<ExternalRpcClientAuthorization> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(items, JsonOptions);
            File.WriteAllBytes(temporary, FileCryptoService.Encrypt(GetEncryptionKey(), json));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string GetLegacyPath(string path) => Path.Combine(Path.GetDirectoryName(path)!, LegacyFileName);

    private static byte[] GetEncryptionKey()
    {
        return _encryptionKey?.ToArray()
            ?? throw new InvalidOperationException("External RPC authorization encryption has not been initialized by the application.");
    }

    private sealed class MutexReleaser(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try { mutex.ReleaseMutex(); }
            finally { mutex.Dispose(); }
        }
    }
}
