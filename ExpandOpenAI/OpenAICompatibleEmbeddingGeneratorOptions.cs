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
    public const string MultimodalApiKeyEnvironmentVariable = "OPENAI_MULTIMODAL_EMBEDDING_API_KEY";
    public const string MultimodalModelEnvironmentVariable = "OPENAI_MULTIMODAL_EMBEDDING_MODEL";
    public const string MultimodalEndpointEnvironmentVariable = "OPENAI_MULTIMODAL_EMBEDDING_ENDPOINT";
    public const string MultimodalRequestPathEnvironmentVariable = "OPENAI_MULTIMODAL_EMBEDDING_REQUEST_PATH";
    public const string MultimodalDimensionsEnvironmentVariable = "OPENAI_MULTIMODAL_EMBEDDING_DIMENSIONS";

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

    /// <summary>
    /// 配置 DashScope 多模态向量请求体。
    /// </summary>
    public Action<JsonObject, IReadOnlyList<AIContent>, EmbeddingGenerationOptions?>? ConfigureMultimodalRequestBody { get; init; }

    /// <summary>
    /// 配置 DashScope 多模态向量请求。
    /// </summary>
    public Action<HttpRequestMessage, IReadOnlyList<AIContent>, EmbeddingGenerationOptions?>? ConfigureMultimodalRequest { get; init; }

    public OpenAICompatibleHttpRetryOptions RetryOptions { get; init; } = new OpenAICompatibleHttpRetryOptions();

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

    public static OpenAICompatibleEmbeddingGeneratorOptions FromMultimodalEnvironment()
    {
        string endpointValue = GetRequiredEnvironmentVariable(
            MultimodalEndpointEnvironmentVariable,
            EndpointEnvironmentVariable);
        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException(
                $"环境变量 {MultimodalEndpointEnvironmentVariable} 或 {EndpointEnvironmentVariable} 不是有效的绝对 URI: {endpointValue}");
        }

        return new OpenAICompatibleEmbeddingGeneratorOptions
        {
            ModelId = GetRequiredEnvironmentVariable(
                MultimodalModelEnvironmentVariable,
                ModelEnvironmentVariable,
                ModelFallbackEnvironmentVariable),
            ApiKey = GetRequiredEnvironmentVariable(
                MultimodalApiKeyEnvironmentVariable,
                ApiKeyEnvironmentVariable),
            Endpoint = endpoint,
            RequestPath = Environment.GetEnvironmentVariable(MultimodalRequestPathEnvironmentVariable) ?? string.Empty,
            DefaultModelDimensions = GetOptionalPositiveInt32(MultimodalDimensionsEnvironmentVariable),
        };
    }

    private static string GetRequiredEnvironmentVariable(string name, params string[] fallbackNames)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        foreach (string fallbackName in fallbackNames)
        {
            value = Environment.GetEnvironmentVariable(fallbackName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return fallbackNames.Length == 0
            ? throw new InvalidOperationException($"环境变量 {name} 未设置。")
            : throw new InvalidOperationException($"环境变量 {name} 或 {string.Join(" 或 ", fallbackNames)} 未设置。");
    }

    private static int? GetOptionalPositiveInt32(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value, out int parsed) && parsed > 0)
        {
            return parsed;
        }

        throw new InvalidOperationException($"环境变量 {name} 必须是大于 0 的整数。");
    }
}
