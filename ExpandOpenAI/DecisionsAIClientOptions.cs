using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExpandOpenAI;

/// <summary>
/// Configuration for <see cref="DecisionsAIClient"/>.
/// </summary>
public class DecisionsAIClientOptions
{
    public const string ApiKeyEnvironmentVariable = "DECISIONS_API_KEY";
    public const string ModelEnvironmentVariable = "DECISIONS_MODEL";
    public const string EndpointEnvironmentVariable = "DECISIONS_ENDPOINT";
    public const string RequestPathEnvironmentVariable = "DECISIONS_REQUEST_PATH";

    /// <summary>
    /// API base address. It is intentionally unset for manually constructed options;
    /// configure it explicitly or use <see cref="FromEnvironment"/>.
    /// </summary>
    public Uri Endpoint { get; set; } = null!;

    /// <summary>
    /// Relative to <see cref="Endpoint"/>; defaults to <c>v1/systemone</c>.
    /// Use an empty string when Endpoint is the complete request URL.
    /// </summary>
    public string RequestPath { get; set; } = "v1/systemone";

    public string ModelId { get; set; } = null!;

    public string? ApiKey { get; set; }

    public string ApiKeyHeaderName { get; set; } = "Authorization";

    public string? ApiKeyScheme { get; set; } = "Bearer";

    public JsonSerializerOptions? SerializerOptions { get; set; }

    public IReadOnlyDictionary<string, string> Headers { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Additional request fields. Required fields from <see cref="DecisionsRequest"/> always win.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? RequestBody { get; set; }

    public Action<HttpRequestMessage, DecisionsRequest>? ConfigureRequest { get; set; }

    public Action<JsonObject, DecisionsRequest>? ConfigureRequestBody { get; set; }

    public OpenAICompatibleHttpRetryOptions RetryOptions { get; set; } = new();

    public static DecisionsAIClientOptions FromEnvironment()
    {
        var endpointValue = Environment.GetEnvironmentVariable(EndpointEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(endpointValue))
        {
            throw new InvalidOperationException($"环境变量 {EndpointEnvironmentVariable} 未设置。");
        }

        var endpoint = CreateEndpoint(endpointValue!, EndpointEnvironmentVariable);

        var model = Environment.GetEnvironmentVariable(ModelEnvironmentVariable);
        var requestPath = Environment.GetEnvironmentVariable(RequestPathEnvironmentVariable) ?? "v1/systemone";
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException($"环境变量 {ModelEnvironmentVariable} 未设置。");
        }

        return new DecisionsAIClientOptions
        {
            Endpoint = endpoint,
            RequestPath = requestPath,
            ModelId = model!,
            ApiKey = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable),
        };
    }

    private static Uri CreateEndpoint(string value, string variableName)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
            && endpoint.Scheme is "http" or "https")
        {
            return endpoint;
        }

        throw new InvalidOperationException(
            $"环境变量 {variableName} 不是有效的 HTTP/HTTPS 绝对 URI: {value}");
    }
}
