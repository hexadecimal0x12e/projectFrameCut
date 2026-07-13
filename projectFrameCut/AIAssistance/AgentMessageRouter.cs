namespace projectFrameCut.AIAssistance;

using System.Collections.Concurrent;

/// <summary>
/// 已关闭的子 Agent 会话快照，保存在父 Agent 的聊天历史中。
/// </summary>
internal sealed class ClosedSubAgentSnapshot
{
    public string AgentId { get; init; } = "";
    public string Title { get; init; } = "";
    public string SubAgentRole { get; init; } = "";
    public Guid SourceSessionId { get; init; }
    public List<AssistanceChatMessageSnapshot> Messages { get; init; } = [];
    public DateTime ClosedAt { get; init; }
}

/// <summary>
/// Agent 注册信息。
/// </summary>
public sealed class AgentInfo
{
    public required string AgentId { get; init; }

    public string? ParentAgentId { get; init; }

    public required AssistanceChatView View { get; init; }
}

/// <summary>
/// 单例消息路由器，负责 Agent 注册、注销和跨 Agent 消息传递。
/// </summary>
public sealed class AgentMessageRouter
{
    private static readonly Lazy<AgentMessageRouter> _instance = new(() => new AgentMessageRouter());
    public static AgentMessageRouter Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, AgentInfo> _agents = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<AgentInfo>? AgentUnregistered;

    private AgentMessageRouter() { }

    /// <summary>
    /// 注册一个 Agent，返回分配的 AgentId。
    /// </summary>
    public string RegisterAgent(AssistanceChatView view, string? parentAgentId = null)
    {
        var info = new AgentInfo
        {
            AgentId = view.AgentId,
            ParentAgentId = parentAgentId,
            View = view,
        };

        _agents[view.AgentId] = info;
        return view.AgentId;
    }

    /// <summary>
    /// 注销一个 Agent。
    /// </summary>
    public void UnregisterAgent(string agentId)
    {
        if (_agents.TryRemove(agentId, out var info))
        {
            AgentUnregistered?.Invoke(this, info);
        }
    }

    /// <summary>
    /// 向目标 Agent 发送消息并等待响应。
    /// 直接调用目标 View 的方法，无需中间 Channel 或事件。
    /// </summary>
    public async Task<string> SendMessageAsync(string fromAgentId, string toAgentId, string content)
    {
        if (!_agents.TryGetValue(toAgentId, out var targetInfo))
            throw new InvalidOperationException($"Agent '{toAgentId}' is not registered.");

        if (!_agents.TryGetValue(fromAgentId, out _))
            throw new InvalidOperationException($"Agent '{fromAgentId}' is not registered.");

        // 直接调用目标 View 的方法，等待处理完成
        return await targetInfo.View.ReceiveMessageAsync(fromAgentId, content);
    }

    /// <summary>
    /// 获取指定父 Agent 的所有直接子 Agent 信息列表。
    /// </summary>
    public IReadOnlyList<(string AgentId, string Title)> GetChildAgents(string parentAgentId)
    {
        return _agents.Values
            .Where(a => string.Equals(a.ParentAgentId, parentAgentId, StringComparison.OrdinalIgnoreCase))
            .Select(a => (a.AgentId, a.View.AgentTitle ?? "Untitled"))
            .ToList();
    }

    /// <summary>
    /// 获取指定 Agent 的信息（如果已注册）。
    /// </summary>
    public AgentInfo? GetAgentInfo(string agentId)
    {
        return _agents.TryGetValue(agentId, out var info) ? info : null;
    }

    /// <summary>
    /// 获取指定父 Agent 的所有子 Agent（递归），包括直接和间接子 Agent。
    /// </summary>
    public IReadOnlyList<string> GetAllDescendantIds(string parentAgentId)
    {
        var results = new List<string>();
        CollectDescendants(parentAgentId, results, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return results;
    }

    private void CollectDescendants(string parentId, List<string> results, HashSet<string> visited)
    {
        if (!visited.Add(parentId))
            return;

        foreach (var info in _agents.Values
            .Where(a => string.Equals(a.ParentAgentId, parentId, StringComparison.OrdinalIgnoreCase)))
        {
            results.Add(info.AgentId);
            CollectDescendants(info.AgentId, results, visited);
        }
    }
}
