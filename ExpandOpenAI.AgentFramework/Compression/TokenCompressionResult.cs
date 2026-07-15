using Microsoft.Extensions.AI;

namespace ExpandOpenAI.AgentFramework;

/// <summary>
/// 历史压缩结果及压缩过程中产生的待持久化记忆。
/// </summary>
public sealed class TokenCompressionResult
{
    /// <summary>
    /// 创建压缩结果。
    /// </summary>
    public TokenCompressionResult(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<MemoryEntry>? sessionMemoriesToStore = null,
        IReadOnlyList<MemoryEntry>? globalMemoriesToStore = null)
    {
        Messages = messages ?? throw new ArgumentNullException(nameof(messages));
        SessionMemoriesToStore = sessionMemoriesToStore ?? Array.Empty<MemoryEntry>();
        GlobalMemoriesToStore = globalMemoriesToStore ?? Array.Empty<MemoryEntry>();
    }

    /// <summary>
    /// 获取压缩后仍保留在活动上下文中的历史消息。
    /// </summary>
    public IReadOnlyList<ChatMessage> Messages { get; }

    /// <summary>
    /// 获取应写入当前会话记忆体的记忆。
    /// </summary>
    public IReadOnlyList<MemoryEntry> SessionMemoriesToStore { get; }

    /// <summary>
    /// 获取应写入跨会话全局记忆体的记忆。
    /// </summary>
    public IReadOnlyList<MemoryEntry> GlobalMemoriesToStore { get; }
}
