namespace projectFrameCut.Render.Rendering;

internal sealed class PreviewCacheIndex
{
    private readonly CacheIndexDatabase database;
    private readonly object gate = new(), flushGate = new();
    private readonly Dictionary<string, Access> pending = new(StringComparer.Ordinal);
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
        lock (gate) pending[path] = new(path, clip, frame, configuration, DateTime.UtcNow.Ticks);
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
                        if (resolvePath(relativePath) is { } path) File.Delete(path);
                        using var command = CacheIndexDatabase.Command(connection,
                            "DELETE FROM preview_files WHERE path=$path;", ("$path", relativePath));
                        command.Transaction = transaction;
                        command.ExecuteNonQuery();
                        transaction.Commit();
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
