using Microsoft.Extensions.AI;

namespace ExpandOpenAI.AgentFramework;

/// <summary>
/// <see cref="AIAgent"/> 的默认基础实现。
/// </summary>
public class DefaultAIAgent : AIAgent
{
    /// <summary>
    /// 创建默认 Agent 实现。
    /// </summary>
    public DefaultAIAgent(IChatClient chatClient, AgentOptions? options = null)
        : base(chatClient, options)
    {
    }

    /// <inheritdoc />
    public override IAgentSession CreateSession(IEnumerable<ChatMessage>? initialHistory = null)
    {
        return new AgentSession(this, initialHistory);
    }
}
