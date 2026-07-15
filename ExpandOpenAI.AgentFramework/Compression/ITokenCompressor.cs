using Microsoft.Extensions.AI;

namespace ExpandOpenAI.AgentFramework;

/// <summary>
/// 压缩不含系统提示的历史消息。
/// </summary>
public interface ITokenCompressor
{
    /// <summary>
    /// 判断已提交历史是否需要主动压缩。上下文超限后的强制压缩不调用此方法。
    /// </summary>
    bool ShouldCompress(IReadOnlyList<ChatMessage> messages);

    /// <summary>
    /// 返回压缩后的历史和待写入记忆体的内容。结果消息不能包含 <see cref="ChatRole.System"/>。
    /// </summary>
    ValueTask<TokenCompressionResult> CompressAsync(
        TokenCompressionContext context,
        IChatClient chatClient,
        CancellationToken cancellationToken = default);
}
