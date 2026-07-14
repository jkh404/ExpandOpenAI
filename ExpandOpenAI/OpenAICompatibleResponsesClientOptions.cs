using System.Text.Json;
using System.Text.Json.Nodes;
using ExpandOpenAI.Internal;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI;

/// <summary>
/// OpenAI Compatible Responses API 客户端配置。
/// </summary>
public class OpenAICompatibleResponsesClientOptions : ChatOptions
{
    public const string ApiKeyEnvironmentVariable = "OPENAI_API_KEY";
    public const string ModelEnvironmentVariable = "OPENAI_RESPONSES_MODEL";
    public const string ModelFallbackEnvironmentVariable = "OPENAI_MODEL";
    public const string EndpointEnvironmentVariable = "OPENAI_ENDPOINT";
    public const string RequestPathEnvironmentVariable = "OPENAI_RESPONSES_REQUEST_PATH";

    public OpenAICompatibleResponsesClientOptions()
    {
    }

    protected OpenAICompatibleResponsesClientOptions(OpenAICompatibleResponsesClientOptions other)
        : base(other)
    {
        ArgumentGuard.ThrowIfNull(other, nameof(other));

        Endpoint = other.Endpoint;
        RequestPath = other.RequestPath;
        ApiKey = other.ApiKey;
        ApiKeyHeaderName = other.ApiKeyHeaderName;
        ApiKeyScheme = other.ApiKeyScheme;
        SerializerOptions = other.SerializerOptions;
        Headers = other.Headers.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        RequestBody = other.RequestBody?.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        ConfigureRequest = other.ConfigureRequest;
        ConfigureRequestBody = other.ConfigureRequestBody;
        RetryOptions = other.RetryOptions;
        Store = other.Store;
        PreviousResponseId = other.PreviousResponseId;
        Conversation = other.Conversation;
        Include = other.Include?.ToList();
        Truncation = other.Truncation;
        Metadata = other.Metadata?.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        MaxToolCalls = other.MaxToolCalls;
    }

    public Uri Endpoint { get; set; } = null!;

    public string RequestPath { get; set; } = "responses";

    public string? ApiKey { get; set; }

    public string ApiKeyHeaderName { get; set; } = "Authorization";

    public string? ApiKeyScheme { get; set; } = "Bearer";

    public JsonSerializerOptions? SerializerOptions { get; set; }

    public IReadOnlyDictionary<string, string> Headers { get; set; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 透传到请求体的全局扩展字段。与强类型字段冲突时，扩展字段最后写入并覆盖默认值。
    /// </summary>
    public IReadOnlyDictionary<string, object?>? RequestBody { get; set; }

    public Action<HttpRequestMessage, IReadOnlyList<ChatMessage>, ChatOptions?, bool>? ConfigureRequest { get; set; }

    public Action<JsonObject, IReadOnlyList<ChatMessage>, ChatOptions?, bool>? ConfigureRequestBody { get; set; }

    public OpenAICompatibleHttpRetryOptions RetryOptions { get; set; } = new OpenAICompatibleHttpRetryOptions();

    public bool? Store { get; set; }

    public string? PreviousResponseId { get; set; }

    /// <summary>
    /// Responses API 的 conversation 字段，可传字符串或供应商支持的对象。
    /// 未设置时会使用 <see cref="ChatOptions.ConversationId"/>。
    /// </summary>
    public object? Conversation { get; set; }

    public IReadOnlyList<string>? Include { get; set; }

    public string? Truncation { get; set; }

    public IReadOnlyDictionary<string, object?>? Metadata { get; set; }

    public int? MaxToolCalls { get; set; }

    public override ChatOptions Clone() => new OpenAICompatibleResponsesClientOptions(this);

    public static OpenAICompatibleResponsesClientOptions FromEnvironment()
    {
        var endpointValue = GetRequiredEnvironmentVariable(EndpointEnvironmentVariable);
        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException($"环境变量 {EndpointEnvironmentVariable} 不是有效的绝对 URI: {endpointValue}");
        }

        return new OpenAICompatibleResponsesClientOptions
        {
            ModelId = GetRequiredEnvironmentVariable(ModelEnvironmentVariable, ModelFallbackEnvironmentVariable),
            ApiKey = GetRequiredEnvironmentVariable(ApiKeyEnvironmentVariable),
            Endpoint = endpoint,
            RequestPath = Environment.GetEnvironmentVariable(RequestPathEnvironmentVariable) ?? "responses",
        };
    }

    private static string GetRequiredEnvironmentVariable(string name, string? fallbackName = null)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (!string.IsNullOrWhiteSpace(fallbackName))
        {
            value = Environment.GetEnvironmentVariable(fallbackName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        throw fallbackName is null
            ? new InvalidOperationException($"环境变量 {name} 未设置。")
            : new InvalidOperationException($"环境变量 {name} 或 {fallbackName} 未设置。");
    }
}
