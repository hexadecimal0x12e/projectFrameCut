using System.Security.Cryptography;
using System.Text.Json;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.Render.RPCProtocol;

public static class ExternalRpcAuthorizationStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string GetPath(string requestDirectory) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(requestDirectory))!, "ExternalRpcClients.json");

    public static IReadOnlyList<ExternalRpcClientAuthorization> Read(string path)
    {
        lock (Gate)
        {
            using var crossProcessLock = AcquireCrossProcessLock(path);
            if (!File.Exists(path)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return JsonSerializer.Deserialize<List<ExternalRpcClientAuthorization>>(stream) ?? [];
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
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return JsonSerializer.Deserialize<List<ExternalRpcClientAuthorization>>(stream) ?? [];
    }

    private static void WriteUnsafe(string path, List<ExternalRpcClientAuthorization> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(items, JsonOptions));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
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
