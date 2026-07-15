using System.Collections.ObjectModel;

namespace ExpandOpenAI.AgentFramework;

/// <summary>
/// 一条可持久化和召回的记忆。
/// </summary>
public sealed class MemoryEntry
{
    /// <summary>
    /// 创建记忆。
    /// </summary>
    public MemoryEntry(
        string id,
        string content,
        DateTimeOffset? createdAt = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("记忆 ID 不能为空。", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("记忆内容不能为空。", nameof(content));
        }

        Id = id;
        Content = content;
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
        Metadata = metadata is null
            ? null
            : new ReadOnlyDictionary<string, string>(
                metadata.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value,
                    StringComparer.Ordinal));
    }

    /// <summary>
    /// 获取用于幂等写入的唯一 ID。
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// 获取可供模型召回的记忆内容。
    /// </summary>
    public string Content { get; }

    /// <summary>
    /// 获取记忆创建时间。
    /// </summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// 获取与存储实现无关的附加元数据。
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; }
}
