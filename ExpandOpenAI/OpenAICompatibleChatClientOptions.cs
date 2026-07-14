using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExpandOpenAI.Internal;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI;

public class OpenAICompatibleChatClientOptions : ChatOptions
{
    public const string ApiKeyEnvironmentVariable = "OPENAI_API_KEY";
    public const string ModelEnvironmentVariable = "OPENAI_MODEL";
    public const string EndpointEnvironmentVariable = "OPENAI_ENDPOINT";
    public const string RequestPathEnvironmentVariable = "OPENAI_REQUEST_PATH";

    public OpenAICompatibleChatClientOptions()
    {
    }

    protected OpenAICompatibleChatClientOptions(OpenAICompatibleChatClientOptions other)
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
            : other.Headers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        RequestBody = other.RequestBody is null
            ? null
            : other.RequestBody.ToDictionary(pair => pair.Key, pair => pair.Value);
        ConfigureRequest = other.ConfigureRequest;
        ConfigureRequestBody = other.ConfigureRequestBody;
        RetryOptions = other.RetryOptions;
    }

    public Uri Endpoint { get; set; } = null!;

    public string RequestPath { get; set; } = "chat/completions";

    public string? ApiKey { get; set; }

    public string ApiKeyHeaderName { get; set; } = "Authorization";

    public string? ApiKeyScheme { get; set; } = "Bearer";

    public JsonSerializerOptions? SerializerOptions { get; set; }

    public IReadOnlyDictionary<string, string> Headers { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, object?>? RequestBody { get; set; }

    public Action<HttpRequestMessage, IReadOnlyList<ChatMessage>, ChatOptions?, bool>? ConfigureRequest { get; set; }

    public Action<JsonObject, IReadOnlyList<ChatMessage>, ChatOptions?, bool>? ConfigureRequestBody { get; set; }

    public OpenAICompatibleHttpRetryOptions RetryOptions { get; set; } = new OpenAICompatibleHttpRetryOptions();

    public override ChatOptions Clone() => new OpenAICompatibleChatClientOptions(this);

    public static OpenAICompatibleChatClientOptions FromEnvironment()
    {
        var endpointValue = GetRequiredEnvironmentVariable(EndpointEnvironmentVariable);
        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException($"环境变量 {EndpointEnvironmentVariable} 不是有效的绝对 URI: {endpointValue}");
        }

        return new OpenAICompatibleChatClientOptions
        {
            ModelId = GetRequiredEnvironmentVariable(ModelEnvironmentVariable),
            ApiKey = GetRequiredEnvironmentVariable(ApiKeyEnvironmentVariable),
            Endpoint = endpoint,
            RequestPath = Environment.GetEnvironmentVariable(RequestPathEnvironmentVariable) ?? "chat/completions",
        };
    }

    private static string GetRequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        throw new InvalidOperationException($"环境变量 {name} 未设置。");
    }
}
