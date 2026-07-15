using Microsoft.Extensions.AI;

namespace ExpandOpenAI.AgentFramework;

/// <summary>
/// <see cref="DefaultTokenCompressor"/> 的通用压缩参数。
/// </summary>
public sealed class DefaultTokenCompressorOptions
{
    /// <summary>
    /// 最近原样保留的完整轮次数。默认为 1。
    /// </summary>
    public int RecentVerbatimTurnCount { get; init; } = 1;

    /// <summary>
    /// 原样轮次之前继续保留在上下文中的逐轮摘要数。默认为 10。
    /// </summary>
    public int RecentSummaryTurnCount { get; init; } = 10;

    /// <summary>
    /// 活动历史的最大 Token 估算值。默认为 16000。
    /// </summary>
    public int MaximumHistoryTokenEstimate { get; init; } = 16_000;

    /// <summary>
    /// 单个轮次允许原样保留的最大 Token 估算值。超过后，即使是最近轮次也会被摘要。
    /// </summary>
    public int MaximumVerbatimTurnTokenEstimate { get; init; } = 4_000;

    /// <summary>
    /// 每个轮次摘要允许使用的最大模型输出 Token 数。
    /// </summary>
    public int SummaryMaxOutputTokens { get; init; } = 512;

    /// <summary>
    /// 自定义 Token 估算器。未配置时使用内置的保守字符估算。
    /// </summary>
    public Func<IReadOnlyList<ChatMessage>, int>? TokenEstimator { get; init; }

    /// <summary>
    /// 逐轮摘要提示词。该提示只定义通用信息保真要求，不判断领域重要性。
    /// </summary>
    public string SummaryPrompt { get; init; } =
        "请压缩下面一个完整对话轮次。保留用户意图、明确事实、已经做出的决定、未完成事项，以及工具调用和结果；不要添加原文没有的信息。只返回摘要正文。";
}
