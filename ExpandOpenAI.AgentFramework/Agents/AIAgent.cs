using Microsoft.Extensions.AI;

namespace ExpandOpenAI.AgentFramework;

/// <summary>
/// 可复用的 Agent 抽象。对话历史由 <see cref="IAgentSession"/> 独立持有。
/// </summary>
public abstract class AIAgent
{
    private readonly AgentOptions _options;

    /// <summary>
    /// 初始化 Agent 基础定义。
    /// </summary>
    protected AIAgent(IChatClient chatClient, AgentOptions? options = null)
    {
        ChatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _options = (options ?? new AgentOptions()).Clone()
            ?? throw new InvalidOperationException("AgentOptions.Clone() 不能返回 null。");
        _options.Validate();
    }

    /// <summary>
    /// 获取此 Agent 使用的聊天客户端。
    /// </summary>
    public IChatClient ChatClient { get; }

    /// <summary>
    /// 获取创建 Agent 时生成的稳定配置快照。
    /// </summary>
    protected internal AgentOptions Options => _options;

    /// <summary>
    /// 创建一段独立会话。
    /// </summary>
    public abstract IAgentSession CreateSession(IEnumerable<ChatMessage>? initialHistory = null);
}
