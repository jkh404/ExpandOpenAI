using Microsoft.Extensions.AI;

namespace ExpandOpenAI;

/// <summary>
/// OpenAI Compatible Responses API 客户端配置。
/// </summary>
public class OpenAICompatibleResponsesClientOptions : OpenAICompatibleChatOptions
{
    public const string ApiKeyEnvironmentVariable = "OPENAI_API_KEY";
    public const string ModelEnvironmentVariable = "OPENAI_RESPONSES_MODEL";
    public const string ModelFallbackEnvironmentVariable = "OPENAI_MODEL";
    public const string EndpointEnvironmentVariable = "OPENAI_ENDPOINT";
    public const string RequestPathEnvironmentVariable = "OPENAI_RESPONSES_REQUEST_PATH";

    public OpenAICompatibleResponsesClientOptions()
        : base("responses")
    {
    }

    protected OpenAICompatibleResponsesClientOptions(OpenAICompatibleResponsesClientOptions other)
        : base(other)
    {
        Store = other.Store;
        PreviousResponseId = other.PreviousResponseId;
        Conversation = other.Conversation;
        Include = other.Include?.ToList();
        Truncation = other.Truncation;
        Metadata = other.Metadata?.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        MaxToolCalls = other.MaxToolCalls;
    }

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
