namespace ExpandOpenAI.AgentFramework;

/// <summary>
/// 历史压缩原因。
/// </summary>
public enum TokenCompressionReason
{
    /// <summary>
    /// 压缩器或调用方配置主动触发了压缩。
    /// </summary>
    Configured,

    /// <summary>
    /// 模型明确拒绝了超出上下文长度的请求。
    /// </summary>
    ContextLengthExceeded,
}
