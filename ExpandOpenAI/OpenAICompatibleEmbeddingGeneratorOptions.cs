using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI;

public class OpenAICompatibleEmbeddingGeneratorOptions
{
    public const string ApiKeyEnvironmentVariable = "OPENAI_API_KEY";
    public const string ModelEnvironmentVariable = "OPENAI_EMBEDDING_MODEL";
    public const string ModelFallbackEnvironmentVariable = "OPENAI_MODEL";
    public const string EndpointEnvironmentVariable = "OPENAI_ENDPOINT";
    public const string RequestPathEnvironmentVariable = "OPENAI_EMBEDDING_REQUEST_PATH";

    public required Uri Endpoint { get; init; }

    public string RequestPath { get; init; } = "embeddings";

    public required string ModelId { get; init; }

    public string? ApiKey { get; init; }

    public string ApiKeyHeaderName { get; init; } = "Authorization";

    public string? ApiKeyScheme { get; init; } = "Bearer";

    public string? EncodingFormat { get; init; } = "float";

    public int? DefaultModelDimensions { get; init; }

    public JsonSerializerOptions? SerializerOptions { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, object?>? RequestBody { get; init; }

    public Action<HttpRequestMessage, IReadOnlyList<string>, EmbeddingGenerationOptions?>? ConfigureRequest { get; init; }

    public Action<JsonObject, IReadOnlyList<string>, EmbeddingGenerationOptions?>? ConfigureRequestBody { get; init; }

    public static OpenAICompatibleEmbeddingGeneratorOptions FromEnvironment()
    {
        var endpointValue = GetRequiredEnvironmentVariable(EndpointEnvironmentVariable);
        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException($"环境变量 {EndpointEnvironmentVariable} 不是有效的绝对 URI: {endpointValue}");
        }

        return new OpenAICompatibleEmbeddingGeneratorOptions
        {
            ModelId = GetRequiredEnvironmentVariable(ModelEnvironmentVariable, ModelFallbackEnvironmentVariable),
            ApiKey = GetRequiredEnvironmentVariable(ApiKeyEnvironmentVariable),
            Endpoint = endpoint,
            RequestPath = Environment.GetEnvironmentVariable(RequestPathEnvironmentVariable) ?? "embeddings",
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
