using projectFrameCut.Drawing.Base.ReadWriteConvert;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace projectFrameCut.Render.Rendering;

internal sealed class DeferredRenderCache : IDisposable
{
    internal const int Version = 1;
    private const int Magic = 0x43444650;
    private static readonly JsonSerializerOptions jsonOptions = new() { IncludeFields = true };
    private readonly IPictureEncoder encoder;
    private readonly ConcurrentDictionary<string, (long Length, DateTime Modified)> verified = new();
    private readonly ConcurrentDictionary<string, (long Length, DateTime Modified)> createdFiles = new();
    private readonly FileStream jobLock;
    private readonly string fingerprint;
    private bool disposed;
    private string stage = "rendering";

    public string DirectoryPath { get; }
    public bool Restoring { get; }

    internal sealed record FrameInfo
    {
        public FrameInfo() { }
        public int Version { get; init; } = DeferredRenderCache.Version;
        public Guid Clip { get; init; }
        public uint Frame { get; init; }
        public bool Composed { get; init; }
        public bool HasPicture { get; init; }
        public bool Reusable { get; init; } = true;
        public ClipPositionTuple Position { get; init; }
        public bool PreserveAspect { get; init; }
        public bool IsCanvas { get; init; }
        public bool CenterImplicit { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public int Bits { get; init; }
        public bool Hdr { get; init; }
    }

    private sealed record Manifest
    {
        public Manifest() { }
        public int Version { get; init; }
        public string Fingerprint { get; init; } = "";
        public string Stage { get; init; } = "";
        public DateTime UpdatedUtc { get; init; }
    }

    public DeferredRenderCache(string? projectRoot, string? signature, bool resume, IPictureEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        this.encoder = encoder;
        string root = string.IsNullOrWhiteSpace(projectRoot)
            ? Path.Combine(Path.GetTempPath(), "projectFrameCut", "render-deferred", Guid.NewGuid().ToString("N"))
            : Path.Combine(Path.GetFullPath(projectRoot), "thumbs", "render-deferred");
        fingerprint = signature ?? Guid.NewGuid().ToString("N");
        string jobs = Path.Combine(root, fingerprint);
        Directory.CreateDirectory(jobs);
        if (resume && signature is not null)
        {
            foreach (string candidate in Directory.EnumerateDirectories(jobs).OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal))
            {
                FileStream? handle = null;
                try
                {
                    handle = OpenLock(candidate);
                    var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(candidate, "manifest.json")));
                    if (manifest?.Version == Version && manifest.Fingerprint == fingerprint)
                    {
                        DirectoryPath = candidate;
                        jobLock = handle;
                        Restoring = true;
                        return;
                    }
                }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { break; }
                catch (IOException) { }
                catch (JsonException) { }
                finally
                {
                    if (!Restoring) handle?.Dispose();
                }
            }
        }
        DirectoryPath = Path.Combine(jobs, $"{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(DirectoryPath);
        jobLock = OpenLock(DirectoryPath);
        try { SetStage("rendering"); }
        catch { jobLock.Dispose(); throw; }
    }

    private static FileStream OpenLock(string path) => new(Path.Combine(path, "job.lock"), FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.None);

    public string ClipPath(Guid clip, uint frame) => Path.Combine(DirectoryPath, "clips", clip.ToString("N"), $"{frame}.vfd");
    public string ComposedPath(uint frame) => Path.Combine(DirectoryPath, "composed", $"{frame}.vfd");

    public void SetStage(string value)
    {
        stage = value;
        string path = Path.Combine(DirectoryPath, "manifest.json");
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Manifest
        {
            Version = DeferredRenderCache.Version, Fingerprint = fingerprint, Stage = stage, UpdatedUtc = DateTime.UtcNow,
        }));
        File.Move(temporary, path, true);
    }

    public bool TryRead(string path, CancellationToken token, out FrameInfo info, out IPicture? picture, bool readPicture = true)
    {
        info = null!;
        picture = null;
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                65536, FileOptions.SequentialScan);
            var stamp = (stream.Length, File.GetLastWriteTimeUtc(path));
            if (!verified.TryGetValue(path, out var saved) || saved != stamp)
            {
                if (stream.Length < 40) return false;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[65536];
                long remaining = stream.Length - 32;
                while (remaining > 0)
                {
                    token.ThrowIfCancellationRequested();
                    int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (count == 0) return false;
                    hash.AppendData(buffer.AsSpan(0, count));
                    remaining -= count;
                }
                byte[] checksum = new byte[32];
                stream.ReadExactly(checksum);
                if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), checksum)) return false;
                verified[path] = stamp;
            }
            token.ThrowIfCancellationRequested();
            stream.Position = 0;
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
            if (reader.ReadInt32() != Magic) return false;
            int length = reader.ReadInt32();
            if (length <= 0 || length > 1024 * 1024 || length > stream.Length - 40) return false;
            info = JsonSerializer.Deserialize<FrameInfo>(reader.ReadBytes(length), jsonOptions)!;
            if (info is null || info.Version != Version) return false;
            if (!readPicture || !info.HasPicture) return true;
            if (!PictureExtensions.SharedVfdPictureDecoder.TryLoad(stream, out picture) || picture is null) return false;
            if (picture.Width == info.Width && picture.Height == info.Height && (int)picture.BitPerPixel == info.Bits
                && (picture is IHDRPicture<ushort>) == info.Hdr) return true;
            picture.Dispose();
            picture = null;
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or OverflowException or NotSupportedException)
        {
            picture?.Dispose();
            picture = null;
            Logger.Log(ex, $"Read deferred render cache {path}", nameof(DeferredRenderCache));
            return false;
        }
    }

    public void Write(string path, FrameInfo info, IPicture? picture, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.SequentialScan))
            {
                using var hash = SHA256.Create();
                byte[] checksum;
                using (var output = new CryptoStream(stream, hash, CryptoStreamMode.Write, leaveOpen: true))
                {
                    byte[] header = JsonSerializer.SerializeToUtf8Bytes(info, jsonOptions);
                    Span<byte> prefix = stackalloc byte[8];
                    BinaryPrimitives.WriteInt32LittleEndian(prefix, Magic);
                    BinaryPrimitives.WriteInt32LittleEndian(prefix[4..], header.Length);
                    output.Write(prefix);
                    output.Write(header);
                    token.ThrowIfCancellationRequested();
                    switch (picture)
                    {
                        case IHDRPicture<ushort> hdr: encoder.Save(hdr, output); break;
                        case IPicture<ushort> p16: encoder.Save(p16, output); break;
                        case IPicture<byte> p8: encoder.Save(p8, output); break;
                        case null: break;
                        default: throw new NotSupportedException($"Cannot cache picture {picture.GetType().Name}.");
                    }
                    token.ThrowIfCancellationRequested();
                    output.FlushFinalBlock();
                    checksum = hash.Hash!;
                }
                stream.Write(checksum);
                stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            Publish(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private void Publish(string temporary, string path)
    {
        var info = new FileInfo(temporary);
        var stamp = (info.Length, info.LastWriteTimeUtc);
        try
        {
            File.Move(temporary, path);
            createdFiles[path] = stamp;
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Move(temporary, path, true);
            if (createdFiles.ContainsKey(path)) createdFiles[path] = stamp;
        }
        verified.TryRemove(path, out _);
    }

    public void CleanupCreatedFiles()
    {
        if (!disposed || stage != "completed" || createdFiles.IsEmpty) return;
        int removed = 0;
        try
        {
            using var handle = OpenLock(DirectoryPath);
            foreach (var entry in createdFiles)
            {
                try
                {
                    var info = new FileInfo(entry.Key);
                    if (info.Exists && (info.Length, info.LastWriteTimeUtc) == entry.Value)
                    {
                        File.Delete(entry.Key);
                        removed++;
                    }
                    createdFiles.TryRemove(entry.Key, out _);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger.Log(ex, $"Clean deferred cache {entry.Key}", nameof(DeferredRenderCache));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Log(ex, "Lock deferred cache for cleanup", nameof(DeferredRenderCache));
        }
        if (removed > 0) Logger.Log($"[Render] Removed {removed} newly created deferred cache files: {DirectoryPath}");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (stage != "completed") SetStage("interrupted");
            foreach (string path in Directory.EnumerateFiles(DirectoryPath, "*.tmp", SearchOption.AllDirectories))
                File.Delete(path);
        }
        catch (Exception ex) { Logger.Log(ex, "Finalize deferred render state", nameof(DeferredRenderCache)); }
        finally
        {
            jobLock.Dispose();
            verified.Clear();
        }
    }
}
