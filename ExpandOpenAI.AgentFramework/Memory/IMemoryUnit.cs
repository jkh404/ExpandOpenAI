namespace ExpandOpenAI.AgentFramework;

/// <summary>
/// 可替换的记忆体。实现必须按 <see cref="MemoryEntry.Id"/> 幂等写入，并支持并发调用。
/// </summary>
public interface IMemoryUnit
{
    /// <summary>
    /// 保存或更新记忆。相同 ID 的记忆应被覆盖，而不是重复追加。
    /// </summary>
    ValueTask RememberAsync(
        IReadOnlyList<MemoryEntry> memories,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按相关性顺序召回记忆。
    /// </summary>
    ValueTask<IReadOnlyList<MemoryEntry>> RecallAsync(
        MemoryRecallRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 清空当前记忆体。
    /// </summary>
    ValueTask ClearAsync(CancellationToken cancellationToken = default);
}
