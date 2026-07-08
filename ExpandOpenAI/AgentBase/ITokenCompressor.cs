using Microsoft.Extensions.AI;

namespace ExpandOpenAI.AgentBase;

/// <summary>
/// 上下文Token压缩器接口，用于在对话中压缩消息。
/// </summary>
public interface ITokenCompressor
{
    Task<IList<ChatMessage>> CompressAsync(IList<ChatMessage> messages, IChatClient? chatClient = null);
}
