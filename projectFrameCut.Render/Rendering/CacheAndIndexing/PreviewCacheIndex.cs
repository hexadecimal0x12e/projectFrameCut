namespace projectFrameCut.Render.Rendering;

internal sealed class PreviewCacheIndex
{
    private readonly CacheIndexDatabase database;
    private readonly object gate = new(), flushGate = new();
    private readonly Dictionary<string, Access> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> sourcePaths = new(StringComparer.Ordinal);
    private DateTime lastCleanup;

    private sealed record Access(string Path, Guid? Clip, uint Frame, string? Configuration, long Ticks);

    public PreviewCacheIndex(string directory)
        => database = new(directory, """
            CREATE TABLE IF NOT EXISTS preview_files (
                path TEXT PRIMARY KEY, clip TEXT, frame INTEGER NOT NULL, configuration TEXT NOT NULL,
                created_at INTEGER NOT NULL, last_access INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS preview_last_access ON preview_files(last_access);
            """);

    public void Track(string path, Guid? clip, uint frame, string configuration)
    {
        lock (gate)
        {
            if (path.EndsWith(".vfd2m", StringComparison.OrdinalIgnoreCase))
            {
                string key = PreviewFrameContainer.GetSourceKey(path);
                if (sourcePaths.TryGetValue(key, out var old) && old != path) pending.Remove(old);
                sourcePaths[key] = path;
            }
            pending[path] = new(path, clip, frame, configuration, DateTime.UtcNow.Ticks);
        }
    }

    public string? FindSource(string sourceKey)
    {
        lock (gate)
            if (sourcePaths.TryGetValue(sourceKey, out var path)) return path;
        string prefix = PreviewFrameContainer.GetSourcePrefix(sourceKey);
        using var connection = database.Open();
        using var query = CacheIndexDatabase.Command(connection, """
            SELECT path FROM preview_files WHERE path >= $prefix AND path < $end
            ORDER BY last_access DESC LIMIT 1;
            """, ("$prefix", prefix), ("$end", prefix + '\uffff'));
        return query.ExecuteScalar() as string;
    }

    public void Touch(string path)
    {
        lock (gate)
        {
            pending[path] = pending.TryGetValue(path, out var entry)
                ? entry with { Ticks = DateTime.UtcNow.Ticks }
                : new(path, null, 0, null, DateTime.UtcNow.Ticks);
        }
    }

    public void Flush()
    {
        lock (flushGate)
        {
            Access[] entries;
            lock (gate)
            {
                if (pending.Count == 0) return;
                entries = pending.Values.ToArray();
                pending.Clear();
            }
            try
            {
                using var connection = database.Open();
                using var transaction = connection.BeginTransaction();
                foreach (var entry in entries)
                {
                    if (entry.Configuration is not null && entry.Path.EndsWith(".vfd2m", StringComparison.OrdinalIgnoreCase))
                    {
                        string key = PreviewFrameContainer.GetSourceKey(entry.Path), prefix = PreviewFrameContainer.GetSourcePrefix(entry.Path);
                        using var remove = CacheIndexDatabase.Command(connection, """
                            DELETE FROM preview_files WHERE path!=$path
                                AND (path=$key OR (path >= $prefix AND path < $end));
                            """, ("$path", entry.Path), ("$key", key), ("$prefix", prefix), ("$end", prefix + '\uffff'));
                        remove.Transaction = transaction;
                        remove.ExecuteNonQuery();
                    }
                    using var command = entry.Configuration is null
                        ? CacheIndexDatabase.Command(connection,
                            "UPDATE preview_files SET last_access=MAX(last_access, $time) WHERE path=$path;",
                            ("$path", entry.Path), ("$time", entry.Ticks))
                        : CacheIndexDatabase.Command(connection, """
                            INSERT INTO preview_files(path, clip, frame, configuration, created_at, last_access)
                            VALUES ($path, $clip, $frame, $config, $time, $time)
                            ON CONFLICT(path) DO UPDATE SET clip=excluded.clip, frame=excluded.frame,
                                configuration=excluded.configuration, last_access=MAX(last_access, excluded.last_access);
                            """, ("$path", entry.Path), ("$clip", entry.Clip?.ToString("N")), ("$frame", (long)entry.Frame),
                            ("$config", entry.Configuration), ("$time", entry.Ticks));
                    command.Transaction = transaction;
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            catch
            {
                lock (gate)
                {
                    foreach (var entry in entries)
                    {
                        if (entry.Path.EndsWith(".vfd2m", StringComparison.OrdinalIgnoreCase)
                            && sourcePaths.TryGetValue(PreviewFrameContainer.GetSourceKey(entry.Path), out var current)
                            && current != entry.Path) continue;
                        if (!pending.TryGetValue(entry.Path, out var newer)) pending[entry.Path] = entry;
                        else if (newer.Configuration is null && entry.Configuration is not null)
                            pending[entry.Path] = entry with { Ticks = Math.Max(entry.Ticks, newer.Ticks) };
                    }
                }
                throw;
            }
        }
    }

    public void Maintain(TimeSpan retention, Func<string, string?> resolvePath)
    {
        lock (flushGate)
        {
            Flush();
            var now = DateTime.UtcNow;
            if (now - lastCleanup < TimeSpan.FromMinutes(1)) return;
            long cutoff = (now - retention).Ticks;
            using var connection = database.Open();
            var paths = new List<string>();
            using (var query = CacheIndexDatabase.Command(connection,
                "SELECT path FROM preview_files WHERE last_access <= $cutoff;", ("$cutoff", cutoff)))
            using (var reader = query.ExecuteReader())
                while (reader.Read()) paths.Add(reader.GetString(0));
            int deleted = 0;
            foreach (string relativePath in paths)
            {
                string? fullPath = resolvePath(relativePath);
                lock (PreviewFrameContainer.Gate(fullPath ?? relativePath))
                lock (gate)
                {
                    if (pending.ContainsKey(relativePath)) continue;
                    try
                    {
                        using var transaction = connection.BeginTransaction();
                        using (var query = CacheIndexDatabase.Command(connection,
                            "SELECT 1 FROM preview_files WHERE path=$path AND last_access <= $cutoff;",
                            ("$path", relativePath), ("$cutoff", cutoff)))
                        {
                            query.Transaction = transaction;
                            if (query.ExecuteScalar() is null) continue;
                        }
                        if (fullPath is not null && File.Exists(fullPath) && File.GetLastWriteTimeUtc(fullPath).Ticks > cutoff)
                        {
                            pending[relativePath] = new(relativePath, null, 0, null, DateTime.UtcNow.Ticks);
                            continue;
                        }
                        if (fullPath is not null) File.Delete(fullPath);
                        using var command = CacheIndexDatabase.Command(connection,
                            "DELETE FROM preview_files WHERE path=$path;", ("$path", relativePath));
                        command.Transaction = transaction;
                        command.ExecuteNonQuery();
                        transaction.Commit();
                        string key = PreviewFrameContainer.GetSourceKey(relativePath);
                        if (sourcePaths.TryGetValue(key, out var source) && source == relativePath) sourcePaths.Remove(key);
                        deleted++;
                    }
                    catch (Exception ex) { Log(ex, $"Delete expired preview cache '{relativePath}'", this); }
                }
            }
            lastCleanup = now;
            if (deleted > 0) Log($"[RenderRPC] Removed {deleted} expired preview cache entries.");
        }
    }
}
