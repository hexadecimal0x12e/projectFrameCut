namespace projectFrameCut.AIAssistance;

using Microsoft.Extensions.AI;
using System.Collections.ObjectModel;

internal sealed class AssistanceChatSession
{
    public Guid SessionId { get; init; }

    public string Title { get; set; } = "新会话";

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public List<AssistanceChatMessageSnapshot> Messages { get; } = [];

    public List<AssistanceChatHistorySnapshot> History { get; } = [];

    public string LastPreview => Messages.LastOrDefault()?.Message ?? "（暂无消息）";
}

internal sealed class AssistanceChatMessageSnapshot
{
    public required string Sender { get; init; }

    public required string Message { get; init; }

    public required bool IsUser { get; init; }

    public string ReasoningText { get; init; } = string.Empty;

    public string ToolCallsText { get; init; } = string.Empty;
}

internal sealed class AssistanceChatHistorySnapshot
{
    public required ChatRole Role { get; init; }

    public required string Text { get; init; }
}

internal static class AssistanceChatSessionStore
{
    private static readonly object Gate = new();
    private static readonly List<AssistanceChatSession> SessionsInner = [];

    public static event EventHandler? SessionsChanged;

    public static IReadOnlyList<AssistanceChatSession> GetSessions()
    {
        lock (Gate)
        {
            return SessionsInner
                .OrderByDescending(x => x.UpdatedAt)
                .ToList();
        }
    }

    public static AssistanceChatSession CreateSession(string? title = null)
    {
        lock (Gate)
        {
            AssistanceChatSession session = new()
            {
                SessionId = Guid.NewGuid(),
                Title = string.IsNullOrWhiteSpace(title) ? "新会话" : title.Trim(),
                UpdatedAt = DateTime.Now,
            };
            SessionsInner.Add(session);
            RaiseChanged();
            return session;
        }
    }

    public static AssistanceChatSession GetOrCreate(Guid? sessionId)
    {
        lock (Gate)
        {
            if (sessionId.HasValue)
            {
                AssistanceChatSession? existing = SessionsInner.FirstOrDefault(x => x.SessionId == sessionId.Value);
                if (existing is not null)
                {
                    return existing;
                }
            }

            AssistanceChatSession created = new()
            {
                SessionId = sessionId ?? Guid.NewGuid(),
                Title = "新会话",
                UpdatedAt = DateTime.Now,
            };
            SessionsInner.Add(created);
            RaiseChanged();
            return created;
        }
    }

    public static void UpdateSession(Guid sessionId, string title, IEnumerable<AssistanceChatMessageSnapshot> messages, IEnumerable<AssistanceChatHistorySnapshot> history)
    {
        lock (Gate)
        {
            AssistanceChatSession session = GetOrCreate(sessionId);
            session.Title = string.IsNullOrWhiteSpace(title) ? session.Title : title.Trim();
            session.UpdatedAt = DateTime.Now;
            session.Messages.Clear();
            session.Messages.AddRange(messages);
            session.History.Clear();
            session.History.AddRange(history);
            RaiseChanged();
        }
    }

    public static void Touch(Guid sessionId)
    {
        lock (Gate)
        {
            AssistanceChatSession session = GetOrCreate(sessionId);
            session.UpdatedAt = DateTime.Now;
            RaiseChanged();
        }
    }

    public static void RenameSession(Guid sessionId, string newTitle)
    {
        lock (Gate)
        {
            AssistanceChatSession? session = SessionsInner.FirstOrDefault(x => x.SessionId == sessionId);
            if (session is not null)
            {
                session.Title = newTitle.Trim();
                RaiseChanged();
            }
        }
    }

    public static void DeleteSession(Guid sessionId)
    {
        lock (Gate)
        {
            SessionsInner.RemoveAll(x => x.SessionId == sessionId);
            RaiseChanged();
        }
    }

    private static void RaiseChanged()
    {
        SessionsChanged?.Invoke(null, EventArgs.Empty);
    }
}
