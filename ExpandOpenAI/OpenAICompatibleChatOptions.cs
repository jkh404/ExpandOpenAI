using System.Text.Json;
using System.Text.Json.Nodes;
using ExpandOpenAI.Internal;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI;

/// <summary>
/// OpenAI Compatible 聊天客户端的公共配置。
/// </summary>
public abstract class OpenAICompatibleChatOptions : ChatOptions
{
    protected OpenAICompatibleChatOptions(string defaultRequestPath)
    {
        RequestPath = defaultRequestPath;
    }

    protected OpenAICompatibleChatOptions(OpenAICompatibleChatOptions other)
        : base(other)
    {
        ArgumentGuard.ThrowIfNull(other, nameof(other));

        Endpoint = other.Endpoint;
        RequestPath = other.RequestPath;
        ApiKey = other.ApiKey;
        ApiKeyHeaderName = other.ApiKeyHeaderName;
        ApiKeyScheme = other.ApiKeyScheme;
        SerializerOptions = other.SerializerOptions;
        Headers = other.Headers is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : other.Headers.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
        RequestBody = other.RequestBody?.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value);
        ConfigureRequest = other.ConfigureRequest;
        ConfigureRequestBody = other.ConfigureRequestBody;
        RetryOptions = other.RetryOptions;
    }

    public Uri Endpoint { get; set; } = null!;

    public string RequestPath { get; set; }

    public string? ApiKey { get; set; }

    public string ApiKeyHeaderName { get; set; } = "Authorization";

    public string? ApiKeyScheme { get; set; } = "Bearer";

    public JsonSerializerOptions? SerializerOptions { get; set; }

    public IReadOnlyDictionary<string, string> Headers { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 透传到请求体的全局扩展字段。与强类型字段冲突时，扩展字段最后写入并覆盖默认值。
    /// </summary>
    public IReadOnlyDictionary<string, object?>? RequestBody { get; set; }

    public Action<HttpRequestMessage, IReadOnlyList<ChatMessage>, ChatOptions?, bool>? ConfigureRequest { get; set; }

    public Action<JsonObject, IReadOnlyList<ChatMessage>, ChatOptions?, bool>? ConfigureRequestBody { get; set; }

    public OpenAICompatibleHttpRetryOptions RetryOptions { get; set; } = new OpenAICompatibleHttpRetryOptions();
}
