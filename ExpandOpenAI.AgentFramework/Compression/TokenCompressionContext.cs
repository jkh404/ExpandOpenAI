using Microsoft.Extensions.AI;

namespace ExpandOpenAI.AgentFramework;

/// <summary>
/// 一次历史压缩的稳定输入。
/// </summary>
public sealed class TokenCompressionContext
{
    /// <summary>
    /// 创建压缩上下文。消息不包含系统提示和当前尚未提交的用户消息。
    /// </summary>
    public TokenCompressionContext(
        IReadOnlyList<ChatMessage> messages,
        TokenCompressionReason reason)
    {
        Messages = messages ?? throw new ArgumentNullException(nameof(messages));
        Reason = reason;
    }

    /// <summary>
    /// 获取待压缩的已提交历史。
    /// </summary>
    public IReadOnlyList<ChatMessage> Messages { get; }

    /// <summary>
    /// 获取本次压缩原因。
    /// </summary>
    public TokenCompressionReason Reason { get; }
}
