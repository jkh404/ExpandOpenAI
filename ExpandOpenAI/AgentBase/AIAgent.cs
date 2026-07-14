using Microsoft.Extensions.AI;

namespace ExpandOpenAI.AgentBase;

/// <summary>
/// 可复用的 Agent 定义。对话历史由 <see cref="AgentSession"/> 独立持有。
/// </summary>
public sealed class AIAgent
{
    private readonly AgentOptions _options;

    /// <summary>
    /// 创建 Agent 定义。
    /// </summary>
    public AIAgent(IChatClient chatClient, AgentOptions? options = null)
    {
        ChatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _options = (options ?? new AgentOptions()).CreateSnapshot();
    }

    internal IChatClient ChatClient { get; }

    internal AgentOptions Options => _options;

    /// <summary>
    /// 创建一段独立会话。初始历史会被复制，调用方后续修改原集合不会影响会话。
    /// </summary>
    public AgentSession CreateSession(IEnumerable<ChatMessage>? initialHistory = null)
    {
        return new AgentSession(this, initialHistory);
    }
}
