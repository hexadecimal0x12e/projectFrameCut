using Microsoft.Data.Sqlite;

namespace projectFrameCut.Render.Rendering;

internal sealed class CacheIndexDatabase : IDisposable
{
    internal const string FileName = "cache-index.sqlite";
    private readonly string connectionString;
    private readonly bool readOnly;
    private readonly SqliteConnection? keepAlive;

    public CacheIndexDatabase(string directory, string schema, bool readOnly = false, bool keepOpen = false)
    {
        this.readOnly = readOnly;
        string path = Path.Combine(directory, FileName);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false, DefaultTimeout = 5,
        }.ToString();
        if (readOnly) return;
        try
        {
            Directory.CreateDirectory(directory);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder(connectionString)
                { Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
            connection.Open();
            using var command = Command(connection, "PRAGMA journal_mode=WAL;" + schema);
            command.ExecuteNonQuery();
            if (keepOpen) keepAlive = Open();
        }
        catch (Exception ex)
        {
            Log(ex, $"Open cache index {path}", nameof(CacheIndexDatabase));
            throw;
        }
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            if (!readOnly)
            {
                using var command = Command(connection, "PRAGMA synchronous=NORMAL;");
                command.ExecuteNonQuery();
            }
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    public static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public static bool IsIndexFile(string path)
        => string.Equals(Path.GetFileName(path), FileName, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).StartsWith(FileName + "-", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => keepAlive?.Dispose();

    public static string ResolvePath(string directory, string relativePath)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (Path.IsPathRooted(relativePath) || IsIndexFile(path) || !path.StartsWith(root,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Cache path escapes its directory.");
        return path;
    }
}
