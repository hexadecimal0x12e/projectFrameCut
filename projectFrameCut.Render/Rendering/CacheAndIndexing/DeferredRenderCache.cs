using projectFrameCut.Drawing.Base.ReadWriteConvert;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace projectFrameCut.Render.Rendering;

internal sealed class DeferredRenderCache : IDisposable
{
    internal const int Version = 1;
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS task (
            id INTEGER PRIMARY KEY CHECK(id=1), version INTEGER NOT NULL, fingerprint TEXT NOT NULL,
            stage TEXT NOT NULL, updated_at INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS frames (
            path TEXT PRIMARY KEY, version INTEGER NOT NULL, clip TEXT NOT NULL, frame INTEGER NOT NULL,
            composed INTEGER NOT NULL, has_picture INTEGER NOT NULL, reusable INTEGER NOT NULL,
            x INTEGER NOT NULL, y INTEGER NOT NULL, target_width INTEGER NOT NULL, target_height INTEGER NOT NULL,
            is_delta INTEGER NOT NULL, rotation REAL NOT NULL, preserve_aspect INTEGER NOT NULL,
            is_canvas INTEGER NOT NULL, center_implicit INTEGER NOT NULL,
            width INTEGER NOT NULL, height INTEGER NOT NULL, bits INTEGER NOT NULL, hdr INTEGER NOT NULL,
            length INTEGER NOT NULL, checksum BLOB NOT NULL
        );
        """;
    private readonly CacheIndexDatabase database;
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
                if (!File.Exists(Path.Combine(candidate, CacheIndexDatabase.FileName))) continue;
                FileStream? handle = null;
                CacheIndexDatabase? index = null;
                try
                {
                    handle = OpenLock(candidate);
                    index = new(candidate, Schema, keepOpen: true);
                    using var connection = index.Open();
                    using var query = CacheIndexDatabase.Command(connection,
                        "SELECT 1 FROM task WHERE id=1 AND version=$version AND fingerprint=$fingerprint;",
                        ("$version", Version), ("$fingerprint", fingerprint));
                    if (query.ExecuteScalar() is not null)
                    {
                        DirectoryPath = candidate;
                        jobLock = handle;
                        database = index;
                        Restoring = true;
                        return;
                    }
                }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { break; }
                catch (IOException) { }
                catch (SqliteException ex) { Log(ex, $"Read deferred render index {candidate}", this); }
                finally
                {
                    if (!Restoring)
                    {
                        index?.Dispose();
                        handle?.Dispose();
                    }
                }
            }
        }
        DirectoryPath = Path.Combine(jobs, $"{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(DirectoryPath);
        jobLock = OpenLock(DirectoryPath);
        try
        {
            database = new(DirectoryPath, Schema, keepOpen: true);
            SetStage("rendering");
        }
        catch { database?.Dispose(); jobLock.Dispose(); throw; }
    }

    private static FileStream OpenLock(string path) => new(Path.Combine(path, "job.lock"), FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.None);

    public string ClipPath(Guid clip, uint frame) => Path.Combine(DirectoryPath, "clips", clip.ToString("N"), $"{frame}.vfd");
    public string ComposedPath(uint frame) => Path.Combine(DirectoryPath, "composed", $"{frame}.vfd");

    public void SetStage(string value)
    {
        using var connection = database.Open();
        using var command = CacheIndexDatabase.Command(connection, """
            INSERT INTO task(id, version, fingerprint, stage, updated_at) VALUES (1, $version, $fingerprint, $stage, $time)
            ON CONFLICT(id) DO UPDATE SET stage=excluded.stage, updated_at=excluded.updated_at;
            """, ("$version", Version), ("$fingerprint", fingerprint), ("$stage", value), ("$time", DateTime.UtcNow.Ticks));
        command.ExecuteNonQuery();
        stage = value;
    }

    public bool TryRead(string path, CancellationToken token, out FrameInfo info, out IPicture? picture, bool readPicture = true)
    {
        info = null!;
        picture = null;
        try
        {
            byte[] checksum;
            long length;
            using (var connection = database.Open())
            using (var query = CacheIndexDatabase.Command(connection, """
                SELECT version, clip, frame, composed, has_picture, reusable, x, y, target_width, target_height,
                    is_delta, rotation, preserve_aspect, is_canvas, center_implicit, width, height, bits, hdr, length, checksum
                FROM frames WHERE path=$path;
                """, ("$path", Path.GetRelativePath(DirectoryPath, path))))
            using (var reader = query.ExecuteReader())
            {
                if (!reader.Read() || reader.GetInt32(0) != Version) return false;
                info = new()
                {
                    Version = reader.GetInt32(0), Clip = Guid.ParseExact(reader.GetString(1), "N"),
                    Frame = checked((uint)reader.GetInt64(2)), Composed = reader.GetBoolean(3),
                    HasPicture = reader.GetBoolean(4), Reusable = reader.GetBoolean(5),
                    Position = new(reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9),
                        reader.GetBoolean(10), reader.GetFloat(11)),
                    PreserveAspect = reader.GetBoolean(12), IsCanvas = reader.GetBoolean(13), CenterImplicit = reader.GetBoolean(14),
                    Width = reader.GetInt32(15), Height = reader.GetInt32(16), Bits = reader.GetInt32(17), Hdr = reader.GetBoolean(18),
                };
                length = reader.GetInt64(19);
                checksum = (byte[])reader.GetValue(20);
            }
            if (!File.Exists(path)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                65536, FileOptions.SequentialScan);
            var stamp = (stream.Length, File.GetLastWriteTimeUtc(path));
            if (stream.Length != length || checksum.Length != 32) return false;
            if (!verified.TryGetValue(path, out var saved) || saved != stamp)
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[65536];
                long remaining = stream.Length;
                while (remaining > 0)
                {
                    token.ThrowIfCancellationRequested();
                    int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (count == 0) return false;
                    hash.AppendData(buffer.AsSpan(0, count));
                    remaining -= count;
                }
                if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), checksum)) return false;
                verified[path] = stamp;
            }
            token.ThrowIfCancellationRequested();
            stream.Position = 0;
            if (!readPicture || !info.HasPicture) return true;
            if (!PictureExtensions.SharedVfdPictureDecoder.TryLoad(stream, out picture) || picture is null) return false;
            if (picture.Width == info.Width && picture.Height == info.Height && (int)picture.BitPerPixel == info.Bits
                && (picture is IHDRPicture<ushort>) == info.Hdr) return true;
            picture.Dispose();
            picture = null;
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SqliteException or ArgumentException or FormatException or OverflowException or NotSupportedException)
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
            byte[] checksum;
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.SequentialScan))
            {
                using var hash = SHA256.Create();
                using (var output = new CryptoStream(stream, hash, CryptoStreamMode.Write, leaveOpen: true))
                {
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
                stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            Publish(temporary, path);
            using var connection = database.Open();
            using var command = CacheIndexDatabase.Command(connection, """
                INSERT OR REPLACE INTO frames(path, version, clip, frame, composed, has_picture, reusable,
                    x, y, target_width, target_height, is_delta, rotation, preserve_aspect, is_canvas, center_implicit,
                    width, height, bits, hdr, length, checksum)
                VALUES ($path, $version, $clip, $frame, $composed, $picture, $reusable,
                    $x, $y, $tw, $th, $delta, $rotation, $aspect, $canvas, $center,
                    $width, $height, $bits, $hdr, $length, $checksum);
                """, ("$path", Path.GetRelativePath(DirectoryPath, path)), ("$version", info.Version), ("$clip", info.Clip.ToString("N")),
                ("$frame", (long)info.Frame), ("$composed", info.Composed), ("$picture", info.HasPicture), ("$reusable", info.Reusable),
                ("$x", info.Position.TargetX), ("$y", info.Position.TargetY), ("$tw", info.Position.TargetWidth), ("$th", info.Position.TargetHeight),
                ("$delta", info.Position.IsDelta), ("$rotation", info.Position.Rotation), ("$aspect", info.PreserveAspect),
                ("$canvas", info.IsCanvas), ("$center", info.CenterImplicit), ("$width", info.Width), ("$height", info.Height),
                ("$bits", info.Bits), ("$hdr", info.Hdr), ("$length", new FileInfo(path).Length), ("$checksum", checksum));
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) { Log(ex, $"Save deferred render index for {path}", this); throw; }
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
            using var index = new CacheIndexDatabase(DirectoryPath, Schema);
            foreach (var entry in createdFiles)
            {
                try
                {
                    var info = new FileInfo(entry.Key);
                    if (info.Exists && (info.Length, info.LastWriteTimeUtc) == entry.Value)
                    {
                        File.Delete(entry.Key);
                        using var connection = index.Open();
                        using var command = CacheIndexDatabase.Command(connection,
                            "DELETE FROM frames WHERE path=$path;", ("$path", Path.GetRelativePath(DirectoryPath, entry.Key)));
                        command.ExecuteNonQuery();
                        removed++;
                    }
                    createdFiles.TryRemove(entry.Key, out _);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
                {
                    Logger.Log(ex, $"Clean deferred cache {entry.Key}", nameof(DeferredRenderCache));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
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
            database.Dispose();
            jobLock.Dispose();
            verified.Clear();
        }
    }
}
