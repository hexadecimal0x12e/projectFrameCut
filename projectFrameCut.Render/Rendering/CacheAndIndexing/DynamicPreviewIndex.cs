using Microsoft.Data.Sqlite;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.Render.Rendering;

public sealed class DynamicPreviewIndex : IDisposable
{
    private readonly CacheIndexDatabase database;
    private readonly SqliteConnection connection;
    private readonly SqliteCommand? write;
    private readonly object gate = new();

    public DynamicPreviewIndex(string directory, bool readOnly = false)
    {
        database = new(directory, """
            CREATE TABLE IF NOT EXISTS playback_frames (
                frame INTEGER NOT NULL, clip TEXT NOT NULL, path TEXT NOT NULL,
                width INTEGER NOT NULL, height INTEGER NOT NULL, container TEXT NOT NULL,
                effect_hash TEXT NOT NULL, positioned INTEGER NOT NULL,
                PRIMARY KEY (frame, clip)
            );
            """, readOnly);
        connection = database.Open();
        if (readOnly) return;
        try
        {
            write = CacheIndexDatabase.Command(connection, """
                INSERT OR REPLACE INTO playback_frames
                    (frame, clip, path, width, height, container, effect_hash, positioned)
                VALUES ($frame, $clip, $path, $width, $height, $container, $hash, $positioned);
                """, ("$frame", 0L), ("$clip", ""), ("$path", ""), ("$width", 0), ("$height", 0),
                ("$container", ""), ("$hash", ""), ("$positioned", false));
            write.Prepare();
        }
        catch
        {
            write?.Dispose();
            connection.Dispose();
            database.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            write?.Dispose();
            connection.Dispose();
            database.Dispose();
        }
    }

    public void Write(DynamicPreviewCachedClip entry) => Write([entry]);

    public void Write(IReadOnlyList<DynamicPreviewCachedClip> entries)
    {
        if (entries.Count == 0) return;
        lock (gate)
        {
            if (write is null) throw new InvalidOperationException("The playback index is read-only.");
            using var transaction = connection.BeginTransaction();
            write.Transaction = transaction;
            try
            {
                foreach (var entry in entries)
                {
                    write.Parameters["$frame"].Value = (long)entry.FrameIndex;
                    write.Parameters["$clip"].Value = entry.ClipId.ToString("N");
                    write.Parameters["$path"].Value = entry.ProjectRelativePath;
                    write.Parameters["$width"].Value = entry.Width;
                    write.Parameters["$height"].Value = entry.Height;
                    write.Parameters["$container"].Value = entry.PreviewContainer;
                    write.Parameters["$hash"].Value = entry.PreviewEffectHash;
                    write.Parameters["$positioned"].Value = entry.IsPositionedClipPreview;
                    write.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            finally { write.Transaction = null; }
        }
    }

    public DynamicPreviewCachedClip? Read(uint frame, Guid clip)
    {
        lock (gate)
        {
            using var command = CacheIndexDatabase.Command(connection,
                "SELECT frame, clip, path, width, height, container, effect_hash, positioned FROM playback_frames WHERE frame=$frame AND clip=$clip;",
                ("$frame", (long)frame), ("$clip", clip.ToString("N")));
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadEntry(reader) : null;
        }
    }

    public IReadOnlyList<DynamicPreviewCachedClip> ReadRange(uint start, uint end)
    {
        lock (gate)
        {
            using var command = CacheIndexDatabase.Command(connection, """
                SELECT frame, clip, path, width, height, container, effect_hash, positioned FROM playback_frames
                WHERE frame BETWEEN $start AND $end ORDER BY frame, clip;
                """, ("$start", (long)start), ("$end", (long)end));
            using var reader = command.ExecuteReader();
            var entries = new List<DynamicPreviewCachedClip>();
            while (reader.Read()) entries.Add(ReadEntry(reader));
            return entries;
        }
    }

    public void RemoveBefore(long frame)
    {
        lock (gate)
        {
            using var command = CacheIndexDatabase.Command(connection,
                "DELETE FROM playback_frames WHERE frame < $frame;", ("$frame", frame));
            command.ExecuteNonQuery();
        }
    }

    private static DynamicPreviewCachedClip ReadEntry(SqliteDataReader reader) => new()
    {
        FrameIndex = checked((uint)reader.GetInt64(0)), ClipId = Guid.ParseExact(reader.GetString(1), "N"),
        ProjectRelativePath = reader.GetString(2), Width = reader.GetInt32(3), Height = reader.GetInt32(4),
        PreviewContainer = reader.GetString(5), PreviewEffectHash = reader.GetString(6), IsPositionedClipPreview = reader.GetBoolean(7),
    };
}
