using System.Diagnostics;
using System.Text;
using System.Text.Json;
using projectFrameCut.Shared;

namespace projectFrameCut.AIAssistance;

/// <summary>
/// Represents a single memory entry stored by the AI.
/// </summary>
public sealed class MemoryEntry
{
    /// <summary>
    /// Unique identifier for this memory (e.g., "user-name", "preferred-language").
    /// </summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>
    /// The actual memory content written by the AI.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when this memory was first created.
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Timestamp when this memory was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Disk snapshot DTO wrapping the memories array.
/// </summary>
internal sealed class MemoryStoreSnapshot
{
    public List<MemoryEntry> Memories { get; init; } = [];
}

/// <summary>
/// Thread-safe manager for persisting AI memories to disk.
/// Follows the same pattern as <see cref="AssistanceChatSessionStore"/>.
/// </summary>
public static class MemoryManager
{
    private static readonly object Gate = new();
    private static List<MemoryEntry>? _cachedMemories;
    private static bool _isLoaded;
    private static long _revision;

    internal static event Action? Changed;

    private static readonly JsonSerializerOptions PersistOptions = new()
    {
        WriteIndented = true,
    };

    private static string GetStorageFilePath() =>
        Path.Combine(MauiProgram.BasicDataPath, "ai_memories.json");

    /// <summary>
    /// Monotonically increasing version used to detect memory changes while a
    /// model response is in progress.
    /// </summary>
    public static long Revision => Interlocked.Read(ref _revision);

    /// <summary>
    /// Write (upsert) a memory entry. If a memory with the same key
    /// (case-insensitive) already exists, its content is updated;
    /// otherwise a new entry is created.
    /// </summary>
    public static void WriteMemory(string key, string content)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        lock (Gate)
        {
            EnsureLoadedLocked();

            var existing = _cachedMemories!.FirstOrDefault(m =>
                string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.Content = content;
                existing.UpdatedAt = DateTime.Now;
            }
            else
            {
                _cachedMemories.Add(new MemoryEntry
                {
                    Key = key,
                    Content = content,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now,
                });
            }

            SaveLocked();
            Interlocked.Increment(ref _revision);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Read a specific memory by key, or return all memories if key is null/empty.
    /// Returns a formatted string suitable for returning to the AI.
    /// </summary>
    public static string ReadMemory(string? key = null)
    {
        lock (Gate)
        {
            EnsureLoadedLocked();

            if (!string.IsNullOrWhiteSpace(key))
            {
                var entry = _cachedMemories!.FirstOrDefault(m =>
                    string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
                return entry is not null
                    ? $"{entry.Key}: {entry.Content} (Created: {entry.CreatedAt:g}, Updated: {entry.UpdatedAt:g})"
                    : $"Memory '{key}' not found.";
            }

            if (_cachedMemories!.Count == 0)
                return "No memories stored.";

            var sb = new StringBuilder();
            foreach (var mem in _cachedMemories)
            {
                sb.AppendLine($"- **{mem.Key}**: {mem.Content} (Updated: {mem.UpdatedAt:g})");
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Returns all memories as a list (for programmatic use).
    /// </summary>
    public static List<MemoryEntry> GetAllMemories()
    {
        lock (Gate)
        {
            EnsureLoadedLocked();
            return [.. _cachedMemories!];
        }
    }

    /// <summary>
    /// Returns a formatted markdown string suitable for injecting into the
    /// "User additional prompts and memory" section of the system prompt.
    /// Returns null when there are no memories.
    /// </summary>
    public static string? GetFormattedMemoryText()
    {
        lock (Gate)
        {
            EnsureLoadedLocked();

            if (_cachedMemories!.Count == 0)
                return null;

            var sb = new StringBuilder();
            sb.AppendLine("以下是你已经知道的关于用户的信息：");
            sb.AppendLine();
            foreach (var mem in _cachedMemories)
            {
                sb.AppendLine($"- **{mem.Key}**: {mem.Content}");
            }
            sb.AppendLine();
            sb.AppendLine("你可以使用工具 `write_memory` 来添加新的记忆，使用工具 `read_memory` 来读取它们。");

            return sb.ToString();
        }
    }

    /// <summary>
    /// Clears all stored memories. Intended for debugging/testing.
    /// </summary>
    internal static void Clear()
    {
        lock (Gate)
        {
            _cachedMemories = [];
            _isLoaded = true;
            SaveLocked();
            Interlocked.Increment(ref _revision);
        }

        Changed?.Invoke();
    }

    private static void EnsureLoadedLocked()
    {
        if (_isLoaded)
            return;

        string filePath = GetStorageFilePath();
        if (File.Exists(filePath))
        {
            try
            {
                string json = File.ReadAllText(filePath);
                var snapshot = JsonSerializer.Deserialize<MemoryStoreSnapshot>(json, PersistOptions);
                _cachedMemories = snapshot?.Memories ?? [];
            }
            catch (IOException ex)
            {
                Debug.WriteLine($"[MemoryManager] Failed to read '{filePath}': {ex.Message}");
                _cachedMemories = [];
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"[MemoryManager] Invalid JSON '{filePath}': {ex.Message}");
                _cachedMemories = [];
            }
        }
        else
        {
            _cachedMemories = [];
        }

        _isLoaded = true;
    }

    private static void SaveLocked()
    {
        string filePath = GetStorageFilePath();
        var snapshot = new MemoryStoreSnapshot { Memories = _cachedMemories ?? [] };
        string payload = JsonSerializer.Serialize(snapshot, PersistOptions);
        File.WriteAllText(filePath, payload);
    }
}
