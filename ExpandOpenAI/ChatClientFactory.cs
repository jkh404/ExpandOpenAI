using Microsoft.Extensions.AI;

namespace ExpandOpenAI;

/// <summary>
/// 根据 OpenAI Compatible 协议创建聊天客户端。
/// </summary>
public static class ChatClientFactory
{
    /// <summary>
    /// 创建 OpenAI Compatible 聊天客户端。
    /// </summary>
    public static IChatClient Create(
        string modelId,
        string apiKey,
        Uri endpoint,
        OpenAICompatibleChatProtocol protocol = OpenAICompatibleChatProtocol.ChatCompletions)
    {
        return protocol switch
        {
            OpenAICompatibleChatProtocol.Auto =>
                CreateFromEndpoint(modelId, apiKey, endpoint),
            OpenAICompatibleChatProtocol.ChatCompletions =>
                new OpenAICompatibleChatClient(modelId, apiKey, endpoint),
            OpenAICompatibleChatProtocol.Responses =>
                new OpenAICompatibleResponsesClient(modelId, apiKey, endpoint),
            _ => throw new ArgumentOutOfRangeException(
                nameof(protocol),
                protocol,
                "不支持的 OpenAI Compatible 聊天协议。"),
        };
    }

    private static IChatClient CreateFromEndpoint(string modelId, string apiKey, Uri endpoint)
    {
        if (endpoint is null)
        {
            throw new ArgumentNullException(nameof(endpoint));
        }

        if (!endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("使用 Auto 协议时，Endpoint 必须是绝对 URI。", nameof(endpoint));
        }

        var path = endpoint.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
        {
            return new OpenAICompatibleResponsesClient(modelId, apiKey, endpoint, requestPath: string.Empty);
        }

        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return new OpenAICompatibleChatClient(modelId, apiKey, endpoint, requestPath: string.Empty);
        }

        throw new ArgumentException(
            "使用 Auto 协议时，Endpoint 必须是以 /responses 或 /chat/completions 结尾的完整请求地址。",
            nameof(endpoint));
    }
}
