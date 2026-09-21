using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExpandOpenAI;

/// <summary>
/// Configuration for <see cref="DecisionsAIClient"/>.
/// </summary>
public class DecisionsAIClientOptions
{
    public const string ApiKeyEnvironmentVariable = "OPENROUTER_API_KEY";
    public const string ModelEnvironmentVariable = "OPENROUTER_DECISIONS_MODEL";
    public const string ModelFallbackEnvironmentVariable = "OPENROUTER_MODEL";
    public const string EndpointEnvironmentVariable = "OPENROUTER_ENDPOINT";
    public const string RequestPathEnvironmentVariable = "OPENROUTER_DECISIONS_REQUEST_PATH";
    public const string TypeSafeApiKeyEnvironmentVariable = "TYPESAFE_API_KEY";
    public const string TypeSafeBaseUrlEnvironmentVariable = "TYPESAFE_BASE_URL";

    public Uri Endpoint { get; set; } = new("https://openrouter.ai/api/");

    /// <summary>
    /// Relative to <see cref="Endpoint"/>. The OpenRouter Alpha.Decisions endpoint is the default.
    /// TypeSafe's direct endpoint can be selected with <c>v1/systemone</c>.
    /// </summary>
    public string RequestPath { get; set; } = "alpha/decisions";

    public string ModelId { get; set; } = "~typesafe/jev-latest";

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
        var typeSafeBaseUrl = Environment.GetEnvironmentVariable(TypeSafeBaseUrlEnvironmentVariable);
        var endpointValue = Environment.GetEnvironmentVariable(EndpointEnvironmentVariable);
        var useTypeSafeDefaults = string.IsNullOrWhiteSpace(endpointValue)
            && !string.IsNullOrWhiteSpace(typeSafeBaseUrl);
        if (useTypeSafeDefaults)
        {
            endpointValue = typeSafeBaseUrl;
        }
        var endpoint = string.IsNullOrWhiteSpace(endpointValue)
            ? new Uri("https://openrouter.ai/api/")
            : CreateEndpoint(endpointValue!, useTypeSafeDefaults
                ? TypeSafeBaseUrlEnvironmentVariable : EndpointEnvironmentVariable);

        var model = Environment.GetEnvironmentVariable(ModelEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(model))
        {
            model = Environment.GetEnvironmentVariable(ModelFallbackEnvironmentVariable);
        }

        var requestPath = Environment.GetEnvironmentVariable(RequestPathEnvironmentVariable);
        requestPath ??= useTypeSafeDefaults ? "v1/systemone" : "alpha/decisions";

        var defaultModel = useTypeSafeDefaults ? "jev-latest" : "~typesafe/jev-latest";

        return new DecisionsAIClientOptions
        {
            Endpoint = endpoint,
            RequestPath = requestPath,
            ModelId = string.IsNullOrWhiteSpace(model) ? defaultModel : model!,
            ApiKey = Environment.GetEnvironmentVariable(useTypeSafeDefaults
                ? TypeSafeApiKeyEnvironmentVariable : ApiKeyEnvironmentVariable),
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
