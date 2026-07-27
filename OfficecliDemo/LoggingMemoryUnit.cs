using ExpandOpenAI.AgentFramework;
using Microsoft.Extensions.Logging;

namespace OfficecliDemo;

internal sealed class LoggingMemoryUnit(
    IMemoryUnit inner,
    ILogger<LoggingMemoryUnit> logger) : IMemoryUnit
{
    public async ValueTask RememberAsync(
        IReadOnlyList<MemoryEntry> memories,
        CancellationToken cancellationToken = default)
    {
        var ids = string.Join(", ", memories.Select(static memory => memory.Id));
        var characters = memories.Sum(static memory => memory.Content.Length);
        logger.LogInformation(
            "[Memory] 开始写入 {Count} 条长期记忆，字符数 {Characters}，ID: {Ids}",
            memories.Count,
            characters,
            ids);

        try
        {
            await inner.RememberAsync(memories, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("[Memory] 长期记忆写入完成，ID: {Ids}", ids);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[Memory] 长期记忆写入失败，ID: {Ids}", ids);
            throw;
        }
    }

    public async ValueTask<IReadOnlyList<MemoryEntry>> RecallAsync(
        MemoryRecallRequest request,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "[Memory] 开始召回，Query: {Query}，MaxResults: {MaxResults}",
            DemoLogFormatter.Limit(request.Query, 1_000),
            request.MaxResults);

        try
        {
            var results = await inner.RecallAsync(request, cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "[Memory] 召回完成，共 {Count} 条，ID: {Ids}",
                results.Count,
                string.Join(", ", results.Select(static memory => memory.Id)));
            return results;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[Memory] 召回失败，Query: {Query}", request.Query);
            throw;
        }
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("[Memory] 开始清空长期记忆");
        try
        {
            await inner.ClearAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("[Memory] 长期记忆已清空");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[Memory] 清空长期记忆失败");
            throw;
        }
    }
}
