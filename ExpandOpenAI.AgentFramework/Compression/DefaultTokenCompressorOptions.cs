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
    /// 单条 Assistant 文本或 Tool Result 消息允许使用的最大 Token 估算值。
    /// 超过后先执行消息级压缩；设置为 0 时关闭消息级压缩。默认为 0。
    /// User、System 和 FunctionCall 消息不会进行消息级压缩。
    /// </summary>
    public int MaximumMessageTokenEstimate { get; init; }

    /// <summary>
    /// 单消息摘要和轮次摘要允许使用的最大模型输出 Token 数。
    /// </summary>
    public int SummaryMaxOutputTokens { get; init; } = 512;

    /// <summary>
    /// 自定义 Token 估算器。未配置时使用内置的保守字符估算。
    /// </summary>
    public Func<IReadOnlyList<ChatMessage>, int>? TokenEstimator { get; init; }

    /// <summary>
    /// 单消息摘要提示词。
    /// </summary>
    public string MessageSummaryPrompt { get; init; } =
        "请压缩下面一条 Assistant 文本或 Tool Result 消息，提炼任务相关关键信息。"
        + "保留明确事实、已经做出的决定、约束、未完成事项、工具结果和错误；不要添加原文没有的信息。只返回摘要正文。";

    /// <summary>
    /// 逐轮摘要提示词。该提示只定义通用信息保真要求，不判断领域重要性。
    /// </summary>
    public string SummaryPrompt { get; init; } =
        "请压缩下面一个完整对话轮次中的 Assistant 和 Tool 内容。User 消息会由系统原样保留，摘要中不要改写或替代用户原文。"
        + "提炼任务相关关键信息，保留明确事实、已经做出的决定、约束、未完成事项，以及工具调用、结果和错误；"
        + "不要添加原文没有的信息。只返回非用户内容的摘要正文。";
}
