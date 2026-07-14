using Microsoft.Extensions.AI;

namespace ExpandOpenAI.AgentBase;

/// <summary>
/// 处理消息进入模型和会话历史前的转换。
/// </summary>
public interface IAgentMessageHandler
{
    /// <summary>
    /// 生成发送给模型的用户消息。返回值不能为 <see langword="null"/>。
    /// </summary>
    ValueTask<ChatMessage> PrepareUserForModelAsync(
        ChatMessage userMessage,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 生成写入历史的用户消息。返回 <see langword="null"/> 时不记录该用户消息。
    /// </summary>
    ValueTask<ChatMessage?> PrepareUserForHistoryAsync(
        ChatMessage originalUserMessage,
        ChatMessage modelUserMessage,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 生成写入历史的助手消息。返回 <see langword="null"/> 时不记录该助手消息。
    /// </summary>
    ValueTask<ChatMessage?> PrepareAssistantForHistoryAsync(
        ChatMessage assistantMessage,
        CancellationToken cancellationToken = default);
}
