using Microsoft.Extensions.AI;

namespace ExpandOpenAI.AgentBase;

/// <summary>
/// 定义用户消息和 AI 消息在进入模型或进入历史记录前的处理钩子。
/// </summary>
public interface IAIMessageHandler
{
    /// <summary>
    /// 用户消息进入 AI 前的处理。返回 null 时会回退到原始用户消息。
    /// </summary>
    Task<ChatMessage?> UserMessageEntryAIHandle(ChatMessage userMessage);

    /// <summary>
    /// 用户消息进入历史记录前的处理。
    /// 可根据需要保留原始用户消息，或保留经过 <see cref="UserMessageEntryAIHandle"/> 处理后的消息。
    /// 返回 null 时不会写入历史记录。
    /// </summary>
    Task<ChatMessage?> UserMessageEntryHistoryMessagesHandle(
        ChatMessage userMessage,
        ChatMessage? aiHandledUserMessage = null);

    /// <summary>
    /// AI 返回消息进入历史记录前的处理。返回 null 时不会写入历史记录。
    /// </summary>
    Task<ChatMessage?> AssistantMessageEntryHistoryMessagesHandle(ChatMessage assistantMessage);
}
