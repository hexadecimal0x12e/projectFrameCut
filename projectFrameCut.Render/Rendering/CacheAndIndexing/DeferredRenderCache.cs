using projectFrameCut.Drawing.Base.ReadWriteConvert;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace projectFrameCut.Render.Rendering;

internal sealed class DeferredRenderCache : IDisposable
{
    internal const int Version = 2;
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
            length INTEGER NOT NULL, checksum BLOB NOT NULL, hash TEXT
        );
        CREATE TABLE IF NOT EXISTS hashes (
            clip TEXT NOT NULL, frame INTEGER NOT NULL, hash TEXT NOT NULL, reusable INTEGER NOT NULL,
            PRIMARY KEY(clip, frame)
        );
        """;
    private readonly CacheIndexDatabase database;
    private readonly IPictureEncoder encoder;
    private readonly ConcurrentDictionary<string, (long Length, DateTime Modified)> verified = new();
    private readonly ConcurrentDictionary<string, (long Length, DateTime Modified)> createdFiles = new();
    private readonly FileStream jobLock;
    private readonly string fingerprint;
    private readonly Dictionary<(string Hash, bool Composed), List<(CacheIndexDatabase Index, string Directory, string Path)>> reusableFrames = new();
    private readonly List<CacheIndexDatabase> previousIndexes = new();
    private bool disposed;
    private string stage = "rendering";

    public string DirectoryPath { get; }
    public bool Restoring { get; }
    public bool KeepFiles { get; init; }

    internal sealed record FrameInfo
    {
        public FrameInfo() { }
        public int Version { get; init; } = DeferredRenderCache.Version;
        public Guid Clip { get; init; }
        public uint Frame { get; init; }
        public bool Composed { get; init; }
        public bool HasPicture { get; init; }
        public bool Reusable { get; init; } = true;
        public string? Hash { get; init; }
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
            : Path.Combine(Path.GetFullPath(projectRoot), "incrementalRendering");
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
                        "SELECT 1 FROM task WHERE id=1 AND version=$version AND fingerprint=$fingerprint AND stage!='completed';",
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

    public string ClipPath(Guid clip, string hash) => Path.Combine(DirectoryPath, "clips", clip.ToString("N"), $"{hash}.vfd");
    public string ComposedPath(uint frame) => Path.Combine(DirectoryPath, "composed", $"{frame}.vfd");

    public void WriteHashes(IEnumerable<(Guid Clip, uint Frame, FrameHashResult Identity)> hashes, CancellationToken token)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = CacheIndexDatabase.Command(connection, """
            INSERT OR REPLACE INTO hashes(clip, frame, hash, reusable) VALUES ($clip, $frame, $hash, $reusable);
            """, ("$clip", ""), ("$frame", 0L), ("$hash", ""), ("$reusable", false));
        command.Transaction = transaction;
        foreach (var (clip, frame, identity) in hashes)
        {
            token.ThrowIfCancellationRequested();
            command.Parameters["$clip"].Value = clip.ToString("N");
            command.Parameters["$frame"].Value = (long)frame;
            command.Parameters["$hash"].Value = identity.Hash;
            command.Parameters["$reusable"].Value = identity.CanReuse;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void IndexReusableFrames(IReadOnlySet<string> hashes, CancellationToken token)
    {
        string root = Path.GetDirectoryName(Path.GetDirectoryName(DirectoryPath))!;
        foreach (string directory in Directory.EnumerateDirectories(root).SelectMany(p => Directory.EnumerateDirectories(p))
            .OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(Path.Combine(directory, CacheIndexDatabase.FileName))) continue;
            var index = new CacheIndexDatabase(directory, Schema, readOnly: true);
            previousIndexes.Add(index);
            try
            {
                using var connection = index.Open();
                using (var task = CacheIndexDatabase.Command(connection, "SELECT version FROM task WHERE id=1;"))
                    if (task.ExecuteScalar() is not long version || version != Version) continue;
                using var query = CacheIndexDatabase.Command(connection,
                    "SELECT path, hash, composed FROM frames WHERE version=$version AND reusable=1 AND hash IS NOT NULL;", ("$version", Version));
                using var reader = query.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    string hash = reader.GetString(1);
                    if (!hashes.Contains(hash)) continue;
                    var key = (hash, reader.GetBoolean(2));
                    if (!reusableFrames.TryGetValue(key, out var entries)) reusableFrames[key] = entries = new();
                    entries.Add((index, directory, CacheIndexDatabase.ResolvePath(directory, reader.GetString(0))));
                }
            }
            catch (Exception ex) when (ex is IOException or SqliteException or ArgumentException)
            {
                Logger.Log(ex, $"Index deferred render cache {directory}", nameof(DeferredRenderCache));
            }
        }
    }

    public bool TryReadReusable(string path, string hash, bool composed, CancellationToken token, out FrameInfo info,
        out IPicture? picture, bool readPicture = true)
    {
        if (TryRead(database, DirectoryPath, path, token, out info, out picture, readPicture, hash)
            && info.Reusable && info.Composed == composed) return true;
        picture?.Dispose();
        picture = null;
        if (reusableFrames.TryGetValue((hash, composed), out var entries))
            foreach (var entry in entries)
            {
                if (TryRead(entry.Index, entry.Directory, entry.Path, token, out info, out picture, readPicture, hash)
                    && info.Reusable && info.Composed == composed) return true;
                picture?.Dispose();
                picture = null;
            }
        info = null!;
        return false;
    }

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
        => TryRead(database, DirectoryPath, path, token, out info, out picture, readPicture);

    private bool TryRead(CacheIndexDatabase index, string directory, string path, CancellationToken token,
        out FrameInfo info, out IPicture? picture, bool readPicture, string? hash = null)
    {
        info = null!;
        picture = null;
        token.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return false;
        try
        {
            byte[] checksum;
            long length;
            using (var connection = index.Open())
            using (var query = CacheIndexDatabase.Command(connection, """
                SELECT version, clip, frame, composed, has_picture, reusable, x, y, target_width, target_height,
                    is_delta, rotation, preserve_aspect, is_canvas, center_implicit, width, height, bits, hdr, length, checksum, hash
                FROM frames WHERE path=$path AND ($hash IS NULL OR hash=$hash);
                """, ("$path", Path.GetRelativePath(directory, path)), ("$hash", hash)))
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
                    Hash = reader.IsDBNull(21) ? null : reader.GetString(21),
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
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[65536];
                long remaining = stream.Length;
                while (remaining > 0)
                {
                    token.ThrowIfCancellationRequested();
                    int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (count == 0) return false;
                    sha.AppendData(buffer.AsSpan(0, count));
                    remaining -= count;
                }
                if (!CryptographicOperations.FixedTimeEquals(sha.GetHashAndReset(), checksum)) return false;
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
                    width, height, bits, hdr, length, checksum, hash)
                VALUES ($path, $version, $clip, $frame, $composed, $picture, $reusable,
                    $x, $y, $tw, $th, $delta, $rotation, $aspect, $canvas, $center,
                    $width, $height, $bits, $hdr, $length, $checksum, $hash);
                """, ("$path", Path.GetRelativePath(DirectoryPath, path)), ("$version", info.Version), ("$clip", info.Clip.ToString("N")),
                ("$frame", (long)info.Frame), ("$composed", info.Composed), ("$picture", info.HasPicture), ("$reusable", info.Reusable),
                ("$x", info.Position.TargetX), ("$y", info.Position.TargetY), ("$tw", info.Position.TargetWidth), ("$th", info.Position.TargetHeight),
                ("$delta", info.Position.IsDelta), ("$rotation", info.Position.Rotation), ("$aspect", info.PreserveAspect),
                ("$canvas", info.IsCanvas), ("$center", info.CenterImplicit), ("$width", info.Width), ("$height", info.Height),
                ("$bits", info.Bits), ("$hdr", info.Hdr), ("$length", new FileInfo(path).Length), ("$checksum", checksum), ("$hash", info.Hash));
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
        if (KeepFiles || !disposed || stage != "completed" || createdFiles.IsEmpty) return;
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
            foreach (var index in previousIndexes) index.Dispose();
            previousIndexes.Clear();
            reusableFrames.Clear();
            jobLock.Dispose();
            verified.Clear();
        }
    }
}
