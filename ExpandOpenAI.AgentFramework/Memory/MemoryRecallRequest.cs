namespace ExpandOpenAI.AgentFramework;

/// <summary>
/// 记忆召回请求。
/// </summary>
public sealed class MemoryRecallRequest
{
    /// <summary>
    /// 创建召回请求。
    /// </summary>
    public MemoryRecallRequest(string query, int maxResults = 5)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        if (maxResults <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResults), "最大结果数必须大于 0。");
        }

        Query = query;
        MaxResults = maxResults;
    }

    /// <summary>
    /// 获取召回查询。
    /// </summary>
    public string Query { get; }

    /// <summary>
    /// 获取最大结果数。
    /// </summary>
    public int MaxResults { get; }
}
