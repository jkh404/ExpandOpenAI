using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI;

public class OpenAICompatibleRerankerOptions
{
    public const string ApiKeyEnvironmentVariable = "OPENAI_API_KEY";
    public const string ModelEnvironmentVariable = "OPENAI_RERANKING_MODEL";
    public const string ModelFallbackEnvironmentVariable = "OPENAI_MODEL";
    public const string EndpointEnvironmentVariable = "OPENAI_ENDPOINT";
    public const string RequestPathEnvironmentVariable = "OPENAI_RERANKING_REQUEST_PATH";

    public required Uri Endpoint { get; init; }

    public string RequestPath { get; init; } = "reranks";

    public required string ModelId { get; init; }

    public string? ApiKey { get; init; }

    public string ApiKeyHeaderName { get; init; } = "Authorization";

    public string? ApiKeyScheme { get; init; } = "Bearer";

    public int? DefaultTopN { get; init; }

    public string? DefaultInstruct { get; init; }

    public JsonSerializerOptions? SerializerOptions { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, object?>? RequestBody { get; init; }

    public Action<HttpRequestMessage, string, IReadOnlyList<string>, RerankingOptions?>? ConfigureRequest { get; init; }

    public Action<JsonObject, string, IReadOnlyList<string>, RerankingOptions?>? ConfigureRequestBody { get; init; }

    public static OpenAICompatibleRerankerOptions FromEnvironment()
    {
        var endpointValue = GetRequiredEnvironmentVariable(EndpointEnvironmentVariable);
        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException($"环境变量 {EndpointEnvironmentVariable} 不是有效的绝对 URI: {endpointValue}");
        }

        return new OpenAICompatibleRerankerOptions
        {
            ModelId = GetRequiredEnvironmentVariable(ModelEnvironmentVariable, ModelFallbackEnvironmentVariable),
            ApiKey = GetRequiredEnvironmentVariable(ApiKeyEnvironmentVariable),
            Endpoint = endpoint,
            RequestPath = Environment.GetEnvironmentVariable(RequestPathEnvironmentVariable) ?? "reranks",
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
