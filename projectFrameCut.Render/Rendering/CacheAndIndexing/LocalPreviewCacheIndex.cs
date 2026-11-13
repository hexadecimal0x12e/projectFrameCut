namespace projectFrameCut.Render.Rendering;

public sealed class LocalPreviewCacheIndex
{
    private readonly CacheIndexDatabase database;
    public string DirectoryPath { get; }

    public LocalPreviewCacheIndex(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
        database = new(DirectoryPath, """
            CREATE TABLE IF NOT EXISTS local_frames (
                path TEXT PRIMARY KEY, kind INTEGER NOT NULL, clip TEXT NOT NULL, frame INTEGER NOT NULL,
                width INTEGER NOT NULL, height INTEGER NOT NULL, source_fingerprint INTEGER,
                last_access INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS local_frame_key ON local_frames(clip, source_fingerprint, frame, width, height);
            CREATE INDEX IF NOT EXISTS local_frame_access ON local_frames(kind, last_access);
            """);
    }

    public void Track(string path, Guid clip, uint frame, int width, int height, long? sourceFingerprint = null)
    {
        string relativePath = Path.GetRelativePath(DirectoryPath, path);
        CacheIndexDatabase.ResolvePath(DirectoryPath, relativePath);
        lock (PreviewFrameContainer.Gate(path))
        {
            if (!File.Exists(path)) return;
            using var connection = database.Open();
            using var transaction = connection.BeginTransaction();
            if (!sourceFingerprint.HasValue && path.EndsWith(".vfd2m", StringComparison.OrdinalIgnoreCase))
            {
                string key = PreviewFrameContainer.GetSourceKey(relativePath), prefix = PreviewFrameContainer.GetSourcePrefix(relativePath);
                using var remove = CacheIndexDatabase.Command(connection, """
                    DELETE FROM local_frames WHERE kind=1 AND path!=$path
                        AND (path=$key OR (path >= $prefix AND path < $end));
                    """, ("$path", relativePath), ("$key", key), ("$prefix", prefix), ("$end", prefix + '\uffff'));
                remove.Transaction = transaction;
                remove.ExecuteNonQuery();
            }
            using var command = CacheIndexDatabase.Command(connection, """
                INSERT INTO local_frames(path, kind, clip, frame, width, height, source_fingerprint, last_access)
                VALUES ($path, $kind, $clip, $frame, $width, $height, $source, $time)
                ON CONFLICT(path) DO UPDATE SET kind=excluded.kind, clip=excluded.clip, frame=excluded.frame,
                    width=excluded.width, height=excluded.height, source_fingerprint=excluded.source_fingerprint,
                    last_access=MAX(last_access, excluded.last_access);
                """, ("$path", relativePath), ("$kind", sourceFingerprint.HasValue ? 0 : 1), ("$clip", clip.ToString("N")),
                ("$frame", (long)frame), ("$width", width), ("$height", height), ("$source", sourceFingerprint), ("$time", DateTime.UtcNow.Ticks));
            command.Transaction = transaction;
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    public string? FindPreviewSource(string sourceKey)
    {
        string prefix = Path.GetRelativePath(DirectoryPath, PreviewFrameContainer.GetSourcePrefix(sourceKey));
        using var connection = database.Open();
        using var command = CacheIndexDatabase.Command(connection, """
            SELECT path FROM local_frames WHERE kind=1 AND path >= $prefix AND path < $end
            ORDER BY last_access DESC LIMIT 1;
            """, ("$prefix", prefix), ("$end", prefix + '\uffff'));
        return command.ExecuteScalar() is string path ? CacheIndexDatabase.ResolvePath(DirectoryPath, path) : null;
    }

    public string? FindFallback(Guid clip, long fingerprint, uint frame, int width, int height)
    {
        using var connection = database.Open();
        using var query = CacheIndexDatabase.Command(connection, """
            SELECT path FROM local_frames WHERE kind=0 AND clip=$clip AND source_fingerprint=$source
                AND frame=$frame AND width=$width AND height=$height;
            """, ("$clip", clip.ToString("N")), ("$source", fingerprint), ("$frame", (long)frame), ("$width", width), ("$height", height));
        return query.ExecuteScalar() is string path ? CacheIndexDatabase.ResolvePath(DirectoryPath, path) : null;
    }

    public IReadOnlyList<(int Width, int Height)> ReadFallbackSizes(Guid clip, long fingerprint, uint frame)
    {
        using var connection = database.Open();
        using var query = CacheIndexDatabase.Command(connection, """
            SELECT DISTINCT width, height FROM local_frames
            WHERE kind=0 AND clip=$clip AND source_fingerprint=$source AND frame=$frame;
            """, ("$clip", clip.ToString("N")), ("$source", fingerprint), ("$frame", (long)frame));
        using var reader = query.ExecuteReader();
        var sizes = new List<(int, int)>();
        while (reader.Read()) sizes.Add((reader.GetInt32(0), reader.GetInt32(1)));
        return sizes;
    }

    public void Touch(string path)
    {
        using var connection = database.Open();
        using var command = CacheIndexDatabase.Command(connection,
            "UPDATE local_frames SET last_access=$time WHERE path=$path;",
            ("$time", DateTime.UtcNow.Ticks), ("$path", Path.GetRelativePath(DirectoryPath, path)));
        command.ExecuteNonQuery();
    }

    public void TrimFallback(int maxFiles) => Trim(0, maxFiles, 0);
    public void TrimPreviews(int maxFiles, TimeSpan retention) => Trim(1, maxFiles, (DateTime.UtcNow - retention).Ticks);

    private void Trim(int kind, int maxFiles, long cutoff)
    {
        using var connection = database.Open();
        var paths = new List<(string Path, long Access)>();
        using (var query = CacheIndexDatabase.Command(connection, """
            SELECT path, last_access FROM local_frames WHERE kind=$kind AND (last_access < $cutoff OR path IN (
                SELECT path FROM local_frames WHERE kind=$kind ORDER BY last_access DESC, path LIMIT -1 OFFSET $limit
            ));
            """, ("$kind", kind), ("$cutoff", cutoff), ("$limit", maxFiles)))
        using (var reader = query.ExecuteReader())
            while (reader.Read()) paths.Add((reader.GetString(0), reader.GetInt64(1)));
        foreach (var entry in paths)
        {
            try
            {
                string path = CacheIndexDatabase.ResolvePath(DirectoryPath, entry.Path);
                lock (PreviewFrameContainer.Gate(path))
                {
                    using var transaction = connection.BeginTransaction();
                    using (var query = CacheIndexDatabase.Command(connection,
                        "SELECT 1 FROM local_frames WHERE path=$path AND last_access=$time;",
                        ("$path", entry.Path), ("$time", entry.Access)))
                    {
                        query.Transaction = transaction;
                        if (query.ExecuteScalar() is null) continue;
                    }
                    if (File.Exists(path) && File.GetLastWriteTimeUtc(path).Ticks > entry.Access)
                    {
                        using var touch = CacheIndexDatabase.Command(connection,
                            "UPDATE local_frames SET last_access=$time WHERE path=$path;",
                            ("$time", DateTime.UtcNow.Ticks), ("$path", entry.Path));
                        touch.Transaction = transaction;
                        touch.ExecuteNonQuery();
                        transaction.Commit();
                        continue;
                    }
                    File.Delete(path);
                    using var command = CacheIndexDatabase.Command(connection,
                        "DELETE FROM local_frames WHERE path=$path;", ("$path", entry.Path));
                    command.Transaction = transaction;
                    command.ExecuteNonQuery();
                    transaction.Commit();
                }
            }
            catch (Exception ex) { Log(ex, $"Delete local preview cache {entry.Path}", this); }
        }
    }
}
