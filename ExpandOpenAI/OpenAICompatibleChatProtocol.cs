namespace ExpandOpenAI;

/// <summary>
/// OpenAI Compatible 聊天客户端使用的请求协议。
/// </summary>
public enum OpenAICompatibleChatProtocol
{
    /// <summary>
    /// 根据完整 Endpoint 的路径自动识别协议。
    /// </summary>
    Auto,

    /// <summary>
    /// Chat Completions API。
    /// </summary>
    ChatCompletions,

    /// <summary>
    /// Responses API。
    /// </summary>
    Responses,
}
