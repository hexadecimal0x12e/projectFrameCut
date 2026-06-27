namespace projectFrameCut.AIAssistance;

using Microsoft.Extensions.AI;
using System.Diagnostics;
using System.Text.Json;

internal sealed class AssistanceChatSession
{
    public Guid SessionId { get; init; }

    public string Title { get; set; } = Localized.AIAssistant_NewChatDefaultTitle;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public List<AssistanceChatMessageSnapshot> Messages { get; } = [];

    public List<AssistanceChatHistorySnapshot> History { get; } = [];

    public string LastPreview
    {
        get
        {
            AssistanceChatMessageSnapshot? last = Messages.LastOrDefault();
            if (last is null)
            {
                return string.Empty;
            }

            // 如果消息有附件但没有文字，用附件文件名作为预览
            if (string.IsNullOrWhiteSpace(last.Message) && last.Attachments?.Count > 0)
            {
                return string.Join(", ", last.Attachments.Select(a => a.FileName));
            }

            string? message = last.Message;
            if (string.IsNullOrWhiteSpace(message))
            {
                return string.Empty;
            }

            const int maxLines = 2;
            const int maxCharsPerLine = 150;

            string[] lines = message.Split('\n');
            int lineCount = Math.Min(lines.Length, maxLines);
            string preview = string.Join("\n",
                lines.Take(lineCount).Select(l => l.TrimEnd('\r').Length > maxCharsPerLine
                    ? l.TrimEnd('\r')[..maxCharsPerLine] + "…"
                    : l.TrimEnd('\r')));

            return preview;
        }
    }
}

internal sealed class AssistanceChatMessageSnapshot
{
    public required string Sender { get; init; }

    public required string Message { get; init; }

    public required bool IsUser { get; init; }

    public string ReasoningText { get; init; } = string.Empty;

    public string ToolCallsText { get; init; } = string.Empty;

    public bool HasFeedbackSubmitted { get; init; }

    /// <summary>
    /// 附件列表（图片/文件），仅在用户消息中有效。
    /// 文件存储在 chats/{sessionId:N}/media/ 目录下。
    /// </summary>
    public List<ChatAttachmentSnapshot>? Attachments { get; init; }
}

public sealed class ChatAttachmentSnapshot
{
    public required string FileName { get; init; }

    public required string MimeType { get; init; }

    public long FileSize { get; init; }

    /// <summary>
    /// 相对路径，相对于 chats/{sessionId:N}/ 目录。例如 "media/{guid}.jpg"。
    /// </summary>
    public required string StoredRelativePath { get; init; }
}

internal sealed class AssistanceChatHistorySnapshot
{
    public required ChatRole Role { get; init; }

    public required string Text { get; init; }
}

internal static class AssistanceChatSessionStore
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ProjectSessionStore> StoresByProjectPath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions PersistOptions = new()
    {
        WriteIndented = true,
    };

    public static event EventHandler? SessionsChanged;

    public static IReadOnlyList<AssistanceChatSession> GetSessions(string? projectPath = null)
    {
        lock (Gate)
        {
            (_, ProjectSessionStore store) = GetProjectStoreLocked(projectPath);
            return store.Sessions
                .OrderByDescending(x => x.UpdatedAt)
                .ToList();
        }
    }

    public static AssistanceChatSession CreateSession(string? projectPath = null, string? title = null)
    {
        lock (Gate)
        {
            (string normalizedProjectPath, ProjectSessionStore store) = GetProjectStoreLocked(projectPath);
            AssistanceChatSession session = new()
            {
                SessionId = Guid.NewGuid(),
                Title = string.IsNullOrWhiteSpace(title) ? Localized.AIAssistant_NewChatDefaultTitle : title.Trim(),
                UpdatedAt = DateTime.Now,
            };
            store.Sessions.Add(session);
            SaveSessionLocked(normalizedProjectPath, session);
            RaiseChanged();
            return session;
        }
    }

    public static AssistanceChatSession GetOrCreate(string? projectPath, Guid? sessionId)
    {
        lock (Gate)
        {
            (string normalizedProjectPath, ProjectSessionStore store) = GetProjectStoreLocked(projectPath);
            if (sessionId.HasValue)
            {
                AssistanceChatSession? existing = store.Sessions.FirstOrDefault(x => x.SessionId == sessionId.Value);
                if (existing is not null)
                {
                    return existing;
                }
            }

            AssistanceChatSession created = new()
            {
                SessionId = sessionId ?? Guid.NewGuid(),
                Title = Localized.AIAssistant_NewChatDefaultTitle,
                UpdatedAt = DateTime.Now,
            };
            store.Sessions.Add(created);
            SaveSessionLocked(normalizedProjectPath, created);
            RaiseChanged();
            return created;
        }
    }

    public static AssistanceChatSession? GetSession(string? projectPath, Guid sessionId)
    {
        lock (Gate)
        {
            (_, ProjectSessionStore store) = GetProjectStoreLocked(projectPath);
            return store.Sessions.FirstOrDefault(x => x.SessionId == sessionId);
        }
    }

    public static AssistanceChatSession ForkSession(
        string? projectPath,
        Guid sourceSessionId,
        IEnumerable<AssistanceChatMessageSnapshot> messages,
        IEnumerable<AssistanceChatHistorySnapshot> history,
        string? newTitle = null)
    {
        lock (Gate)
        {
            (string normalizedProjectPath, ProjectSessionStore store) = GetProjectStoreLocked(projectPath);
            AssistanceChatSession? source = store.Sessions.FirstOrDefault(x => x.SessionId == sourceSessionId);
            string title = !string.IsNullOrWhiteSpace(newTitle)
                ? newTitle.Trim()
                : source is not null
                    ? Localized.AIAssistant_ChatView_BranchTitle(source.Title)
                    : Localized.AIAssistant_NewChatDefaultTitle;

            AssistanceChatSession session = new()
            {
                SessionId = Guid.NewGuid(),
                Title = title,
                UpdatedAt = DateTime.Now,
            };
            session.Messages.AddRange(messages);
            session.History.AddRange(history);
            store.Sessions.Add(session);
            SaveSessionLocked(normalizedProjectPath, session);
            RaiseChanged();
            return session;
        }
    }

    public static void UpdateSession(string? projectPath, Guid sessionId, string title, IEnumerable<AssistanceChatMessageSnapshot> messages, IEnumerable<AssistanceChatHistorySnapshot> history)
    {
        lock (Gate)
        {
            (string normalizedProjectPath, ProjectSessionStore store) = GetProjectStoreLocked(projectPath);
            AssistanceChatSession session = GetOrCreateLocked(normalizedProjectPath, store, sessionId);
            session.Title = string.IsNullOrWhiteSpace(title) ? session.Title : title.Trim();
            session.UpdatedAt = DateTime.Now;
            session.Messages.Clear();
            session.Messages.AddRange(messages);
            session.History.Clear();
            session.History.AddRange(history);
            SaveSessionLocked(normalizedProjectPath, session);
            RaiseChanged();
        }
    }

    public static void Touch(string? projectPath, Guid sessionId)
    {
        lock (Gate)
        {
            (string normalizedProjectPath, ProjectSessionStore store) = GetProjectStoreLocked(projectPath);
            AssistanceChatSession session = GetOrCreateLocked(normalizedProjectPath, store, sessionId);
            session.UpdatedAt = DateTime.Now;
            SaveSessionLocked(normalizedProjectPath, session);
            RaiseChanged();
        }
    }

    public static void RenameSession(string? projectPath, Guid sessionId, string newTitle)
    {
        lock (Gate)
        {
            (string normalizedProjectPath, ProjectSessionStore store) = GetProjectStoreLocked(projectPath);
            AssistanceChatSession? session = store.Sessions.FirstOrDefault(x => x.SessionId == sessionId);
            if (session is not null)
            {
                session.Title = newTitle.Trim();
                SaveSessionLocked(normalizedProjectPath, session);
                RaiseChanged();
            }
        }
    }

    public static void DeleteSession(string? projectPath, Guid sessionId)
    {
        lock (Gate)
        {
            (string normalizedProjectPath, ProjectSessionStore store) = GetProjectStoreLocked(projectPath);
            store.Sessions.RemoveAll(x => x.SessionId == sessionId);
            DeleteSessionFileLocked(normalizedProjectPath, sessionId);
            RaiseChanged();
        }
    }

    private static (string ProjectPath, ProjectSessionStore Store) GetProjectStoreLocked(string? projectPath)
    {
        string normalizedProjectPath = NormalizeProjectPath(projectPath);
        if (!StoresByProjectPath.TryGetValue(normalizedProjectPath, out ProjectSessionStore? store))
        {
            store = new ProjectSessionStore();
            StoresByProjectPath[normalizedProjectPath] = store;
        }

        if (!store.IsLoaded)
        {
            LoadSessionsLocked(normalizedProjectPath, store);
            store.IsLoaded = true;
        }

        return (normalizedProjectPath, store);
    }

    private static AssistanceChatSession GetOrCreateLocked(string projectPath, ProjectSessionStore store, Guid sessionId)
    {
        AssistanceChatSession? existing = store.Sessions.FirstOrDefault(x => x.SessionId == sessionId);
        if (existing is not null)
        {
            return existing;
        }

        AssistanceChatSession created = new()
        {
            SessionId = sessionId,
            Title = Localized.AIAssistant_NewChatDefaultTitle,
            UpdatedAt = DateTime.Now,
        };
        store.Sessions.Add(created);
        SaveSessionLocked(projectPath, created);
        return created;
    }

    private static string NormalizeProjectPath(string? projectPath)
    {
        string targetPath = string.IsNullOrWhiteSpace(projectPath) ? Environment.CurrentDirectory : projectPath;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            targetPath = AppContext.BaseDirectory;
        }

        return Path.GetFullPath(targetPath);
    }

    private static void LoadSessionsLocked(string projectPath, ProjectSessionStore store)
    {
        string chatsDirectory = GetChatsDirectory(projectPath);
        store.Sessions.Clear();
        if (!Directory.Exists(chatsDirectory))
        {
            return;
        }

        foreach (string filePath in Directory.EnumerateFiles(chatsDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                string json = File.ReadAllText(filePath);
                AssistanceChatSessionDiskSnapshot? snapshot = JsonSerializer.Deserialize<AssistanceChatSessionDiskSnapshot>(json, PersistOptions);
                AssistanceChatSession? session = FromDiskSnapshot(snapshot);
                if (session is not null)
                {
                    store.Sessions.Add(session);
                }
            }
            catch (IOException ex)
            {
                Debug.WriteLine($"[AssistanceChatSessionStore] Failed to read '{filePath}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Debug.WriteLine($"[AssistanceChatSessionStore] Access denied when reading '{filePath}': {ex.Message}");
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"[AssistanceChatSessionStore] Invalid chat JSON '{filePath}': {ex.Message}");
            }
        }
    }

    private static AssistanceChatSession? FromDiskSnapshot(AssistanceChatSessionDiskSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.SessionId == Guid.Empty)
        {
            return null;
        }

        AssistanceChatSession session = new()
        {
            SessionId = snapshot.SessionId,
            Title = string.IsNullOrWhiteSpace(snapshot.Title) ? Localized.AIAssistant_NewChatDefaultTitle : snapshot.Title.Trim(),
            UpdatedAt = snapshot.UpdatedAt == default ? DateTime.Now : snapshot.UpdatedAt,
        };

        if (snapshot.Messages is not null)
        {
            foreach (AssistanceChatMessageSnapshot message in snapshot.Messages)
            {
                session.Messages.Add(new AssistanceChatMessageSnapshot
                {
                    Sender = message.Sender,
                    Message = message.Message,
                    IsUser = message.IsUser,
                    ReasoningText = message.ReasoningText,
                    ToolCallsText = message.ToolCallsText,
                    HasFeedbackSubmitted = message.HasFeedbackSubmitted,
                    Attachments = message.Attachments?.Select(a => new ChatAttachmentSnapshot
                    {
                        FileName = a.FileName,
                        MimeType = a.MimeType,
                        FileSize = a.FileSize,
                        StoredRelativePath = a.StoredRelativePath,
                    }).ToList(),
                });
            }
        }

        if (snapshot.History is not null)
        {
            foreach (AssistanceChatHistoryDiskSnapshot history in snapshot.History)
            {
                session.History.Add(new AssistanceChatHistorySnapshot
                {
                    Role = ParseRole(history.Role),
                    Text = history.Text ?? string.Empty,
                });
            }
        }

        return session;
    }

    private static AssistanceChatSessionDiskSnapshot ToDiskSnapshot(AssistanceChatSession session)
    {
        return new AssistanceChatSessionDiskSnapshot
        {
            SessionId = session.SessionId,
            Title = session.Title,
            UpdatedAt = session.UpdatedAt,
            Messages = session.Messages.Select(x => new AssistanceChatMessageSnapshot
            {
                Sender = x.Sender,
                Message = x.Message,
                IsUser = x.IsUser,
                ReasoningText = x.ReasoningText,
                ToolCallsText = x.ToolCallsText,
                HasFeedbackSubmitted = x.HasFeedbackSubmitted,
                Attachments = x.Attachments?.Select(a => new ChatAttachmentSnapshot
                {
                    FileName = a.FileName,
                    MimeType = a.MimeType,
                    FileSize = a.FileSize,
                    StoredRelativePath = a.StoredRelativePath,
                }).ToList(),
            }).ToList(),
            History = session.History.Select(x => new AssistanceChatHistoryDiskSnapshot
            {
                Role = ToRoleText(x.Role),
                Text = x.Text,
            }).ToList(),
        };
    }

    private static ChatRole ParseRole(string? roleText)
    {
        if (string.IsNullOrWhiteSpace(roleText))
        {
            return ChatRole.User;
        }

        if (string.Equals(roleText, "system", StringComparison.OrdinalIgnoreCase))
        {
            return ChatRole.System;
        }

        if (string.Equals(roleText, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            return ChatRole.Assistant;
        }

        if (string.Equals(roleText, "tool", StringComparison.OrdinalIgnoreCase))
        {
            return ChatRole.Tool;
        }

        return ChatRole.User;
    }

    private static string ToRoleText(ChatRole role)
    {
        if (role == ChatRole.System)
        {
            return "system";
        }

        if (role == ChatRole.Assistant)
        {
            return "assistant";
        }

        if (role == ChatRole.Tool)
        {
            return "tool";
        }

        return "user";
    }

    private static void SaveSessionLocked(string projectPath, AssistanceChatSession session)
    {
        string chatsDirectory = GetChatsDirectory(projectPath);
        Directory.CreateDirectory(chatsDirectory);
        string filePath = GetSessionFilePath(projectPath, session.SessionId);
        string payload = JsonSerializer.Serialize(ToDiskSnapshot(session), PersistOptions);
        File.WriteAllText(filePath, payload);
    }

    private static void DeleteSessionFileLocked(string projectPath, Guid sessionId)
    {
        string filePath = GetSessionFilePath(projectPath, sessionId);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    private static string GetChatsDirectory(string projectPath)
    {
        return Path.Combine(projectPath, "chats");
    }

    private static string GetSessionFilePath(string projectPath, Guid sessionId)
    {
        return Path.Combine(GetChatsDirectory(projectPath), $"{sessionId:N}.json");
    }

    private static void RaiseChanged()
    {
        SessionsChanged?.Invoke(null, EventArgs.Empty);
    }

    private sealed class ProjectSessionStore
    {
        public bool IsLoaded { get; set; }

        public List<AssistanceChatSession> Sessions { get; } = [];
    }

    private sealed class AssistanceChatSessionDiskSnapshot
    {
        public Guid SessionId { get; init; }

        public string Title { get; init; } = Localized.AIAssistant_NewChatDefaultTitle;

        public DateTime UpdatedAt { get; init; } = DateTime.Now;

        public List<AssistanceChatMessageSnapshot>? Messages { get; init; }

        public List<AssistanceChatHistoryDiskSnapshot>? History { get; init; }
    }

    private sealed class AssistanceChatHistoryDiskSnapshot
    {
        public string Role { get; init; } = "user";

        public string? Text { get; init; }
    }
}
