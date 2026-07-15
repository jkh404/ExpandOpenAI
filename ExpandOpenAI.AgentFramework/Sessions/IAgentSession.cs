using Microsoft.Extensions.AI;

namespace ExpandOpenAI.AgentFramework;

/// <summary>
/// Agent 对话会话的抽象。调用方应优先依赖此接口，而不是具体会话实现。
/// </summary>
public interface IAgentSession
{
    /// <summary>
    /// 获取当前历史的只读快照。
    /// </summary>
    IReadOnlyList<ChatMessage> History { get; }

    /// <summary>
    /// 清空会话历史。
    /// </summary>
    void ClearHistory();

    /// <summary>
    /// 清空当前会话层记忆，不影响全局记忆。
    /// </summary>
    ValueTask ClearMemoryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 销毁当前会话，清空历史和会话层记忆。销毁后的会话不能再次运行。
    /// </summary>
    ValueTask DestroyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行一次非流式对话。
    /// </summary>
    Task<ChatResponse> RunAsync(
        string message,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行一次非流式对话。
    /// </summary>
    Task<ChatResponse> RunAsync(
        ChatMessage userMessage,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行一次流式对话。
    /// </summary>
    IAsyncEnumerable<ChatResponseUpdate> RunStreamAsync(
        string message,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行一次流式对话。
    /// </summary>
    IAsyncEnumerable<ChatResponseUpdate> RunStreamAsync(
        ChatMessage userMessage,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default);
}
