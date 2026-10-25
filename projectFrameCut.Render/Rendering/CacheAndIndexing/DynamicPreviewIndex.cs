using Microsoft.Data.Sqlite;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.Render.Rendering;

public sealed class DynamicPreviewIndex : IDisposable
{
    private readonly CacheIndexDatabase database;

    public DynamicPreviewIndex(string directory, bool readOnly = false)
        => database = new(directory, """
            CREATE TABLE IF NOT EXISTS playback_frames (
                frame INTEGER NOT NULL, clip TEXT NOT NULL, path TEXT NOT NULL,
                width INTEGER NOT NULL, height INTEGER NOT NULL, container TEXT NOT NULL,
                effect_hash TEXT NOT NULL, positioned INTEGER NOT NULL,
                PRIMARY KEY (frame, clip)
            );
            """, readOnly, keepOpen: !readOnly);

    public void Dispose() => database.Dispose();

    public void Write(DynamicPreviewCachedClip entry)
    {
        using var connection = database.Open();
        using var command = CacheIndexDatabase.Command(connection, """
            INSERT OR REPLACE INTO playback_frames
                (frame, clip, path, width, height, container, effect_hash, positioned)
            VALUES ($frame, $clip, $path, $width, $height, $container, $hash, $positioned);
            """, ("$frame", (long)entry.FrameIndex), ("$clip", entry.ClipId.ToString("N")),
            ("$path", entry.ProjectRelativePath), ("$width", entry.Width), ("$height", entry.Height),
            ("$container", entry.PreviewContainer), ("$hash", entry.PreviewEffectHash), ("$positioned", entry.IsPositionedClipPreview));
        command.ExecuteNonQuery();
    }

    public DynamicPreviewCachedClip? Read(uint frame, Guid clip)
    {
        using var connection = database.Open();
        using var command = CacheIndexDatabase.Command(connection,
            "SELECT frame, clip, path, width, height, container, effect_hash, positioned FROM playback_frames WHERE frame=$frame AND clip=$clip;",
            ("$frame", (long)frame), ("$clip", clip.ToString("N")));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    public IReadOnlyList<DynamicPreviewCachedClip> ReadRange(uint start, uint end)
    {
        using var connection = database.Open();
        using var command = CacheIndexDatabase.Command(connection, """
            SELECT frame, clip, path, width, height, container, effect_hash, positioned FROM playback_frames
            WHERE frame BETWEEN $start AND $end ORDER BY frame, clip;
            """, ("$start", (long)start), ("$end", (long)end));
        using var reader = command.ExecuteReader();
        var entries = new List<DynamicPreviewCachedClip>();
        while (reader.Read()) entries.Add(ReadEntry(reader));
        return entries;
    }

    public void RemoveBefore(long frame)
    {
        using var connection = database.Open();
        using var command = CacheIndexDatabase.Command(connection,
            "DELETE FROM playback_frames WHERE frame < $frame;", ("$frame", frame));
        command.ExecuteNonQuery();
    }

    private static DynamicPreviewCachedClip ReadEntry(SqliteDataReader reader) => new()
    {
        FrameIndex = checked((uint)reader.GetInt64(0)), ClipId = Guid.ParseExact(reader.GetString(1), "N"),
        ProjectRelativePath = reader.GetString(2), Width = reader.GetInt32(3), Height = reader.GetInt32(4),
        PreviewContainer = reader.GetString(5), PreviewEffectHash = reader.GetString(6), IsPositionedClipPreview = reader.GetBoolean(7),
    };
}
