using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI;

public sealed class DefaultChatClientOptions
{
    public const string ApiKeyEnvironmentVariable = "OPENAI_API_KEY";
    public const string ModelEnvironmentVariable = "OPENAI_MODEL";
    public const string EndpointEnvironmentVariable = "OPENAI_ENDPOINT";
    public const string RequestPathEnvironmentVariable = "OPENAI_REQUEST_PATH";

    public required Uri Endpoint { get; init; }

    public string RequestPath { get; init; } = "chat/completions";

    public required string ModelId { get; init; }

    public string? ApiKey { get; init; }

    public string ApiKeyHeaderName { get; init; } = "Authorization";

    public string? ApiKeyScheme { get; init; } = "Bearer";

    public JsonSerializerOptions? SerializerOptions { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, object?>? RequestBody { get; init; }

    public Action<HttpRequestMessage, IReadOnlyList<ChatMessage>, ChatOptions?, bool>? ConfigureRequest { get; init; }

    public Action<JsonObject, IReadOnlyList<ChatMessage>, ChatOptions?, bool>? ConfigureRequestBody { get; init; }

    public static DefaultChatClientOptions FromEnvironment()
    {
        var endpointValue = GetRequiredEnvironmentVariable(EndpointEnvironmentVariable);
        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException($"环境变量 {EndpointEnvironmentVariable} 不是有效的绝对 URI: {endpointValue}");
        }

        return new DefaultChatClientOptions
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
