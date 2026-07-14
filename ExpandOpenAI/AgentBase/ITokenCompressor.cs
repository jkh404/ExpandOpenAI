using Microsoft.Extensions.AI;

namespace ExpandOpenAI.AgentBase;

/// <summary>
/// 压缩不含系统提示的历史消息。
/// </summary>
public interface ITokenCompressor
{
    /// <summary>
    /// 返回压缩后的历史。结果不能包含 <see cref="ChatRole.System"/> 消息。
    /// </summary>
    ValueTask<IReadOnlyList<ChatMessage>> CompressAsync(
        IReadOnlyList<ChatMessage> messages,
        IChatClient chatClient,
        CancellationToken cancellationToken = default);
}
