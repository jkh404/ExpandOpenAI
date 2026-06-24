using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExpandOpenAI.Internal;

internal sealed class OpenAICompatibleRerankRequestBuilder
{
    private readonly OpenAICompatibleRerankerOptions _options;
    private readonly JsonSerializerOptions _serializerOptions;

    public OpenAICompatibleRerankRequestBuilder(OpenAICompatibleRerankerOptions options, JsonSerializerOptions serializerOptions)
    {
        _options = options;
        _serializerOptions = serializerOptions;
    }

    public HttpRequestMessage CreateRequestMessage(
        string query,
        IReadOnlyList<string> documents,
        RerankingOptions? options,
        Action<JsonObject, string, IReadOnlyList<string>, RerankingOptions?>? configureRequestBody,
        Action<HttpRequestMessage, string, IReadOnlyList<string>, RerankingOptions?>? configureRequest)
    {
        var requestUri = BuildRequestUri();
        var body = CreateRequestBody(query, documents, options, configureRequestBody);
        var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(body.ToJsonString(_serializerOptions), Encoding.UTF8, "application/json"),
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        AddAuthenticationHeader(request);
        AddDefaultHeaders(request);

        _options.ConfigureRequest?.Invoke(request, query, documents, options);
        configureRequest?.Invoke(request, query, documents, options);
        return request;
    }

    private Uri BuildRequestUri()
    {
        if (string.IsNullOrWhiteSpace(_options.RequestPath))
        {
            return _options.Endpoint;
        }

        if (Uri.TryCreate(_options.RequestPath, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri;
        }

        var baseUri = _options.Endpoint;
        if (!baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal))
        {
            baseUri = new Uri($"{baseUri.AbsoluteUri}/", UriKind.Absolute);
        }

        return new Uri(baseUri, _options.RequestPath);
    }

    private JsonObject CreateRequestBody(
        string query,
        IReadOnlyList<string> documents,
        RerankingOptions? options,
        Action<JsonObject, string, IReadOnlyList<string>, RerankingOptions?>? configureRequestBody)
    {
        var body = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(options?.ModelId) ? _options.ModelId : options!.ModelId,
            ["query"] = query,
            ["documents"] = new JsonArray(documents.Select(static value => JsonValue.Create(value)).ToArray()),
        };

        var topN = options?.TopN ?? _options.DefaultTopN;
        if (topN is not null)
        {
            body["top_n"] = JsonSerializer.SerializeToNode(topN, _serializerOptions);
        }

        var instruct = string.IsNullOrWhiteSpace(options?.Instruct) ? _options.DefaultInstruct : options!.Instruct;
        if (!string.IsNullOrWhiteSpace(instruct))
        {
            body["instruct"] = instruct;
        }

        MergeRequestProperties(body, _options.RequestBody);
        MergeRequestProperties(body, options?.AdditionalProperties);

        _options.ConfigureRequestBody?.Invoke(body, query, documents, options);
        configureRequestBody?.Invoke(body, query, documents, options);

        return body;
    }

    private void AddAuthenticationHeader(HttpRequestMessage request)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return;
        }

        var headerName = _options.ApiKeyHeaderName;
        var value = string.IsNullOrWhiteSpace(_options.ApiKeyScheme)
            ? _options.ApiKey
            : $"{_options.ApiKeyScheme} {_options.ApiKey}";

        request.Headers.Remove(headerName);
        request.Headers.TryAddWithoutValidation(headerName, value);
    }

    private void AddDefaultHeaders(HttpRequestMessage request)
    {
        foreach (var pair in _options.Headers)
        {
            request.Headers.Remove(pair.Key);
            request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        }
    }

    private void MergeRequestProperties(JsonObject body, IReadOnlyDictionary<string, object?>? properties)
    {
        if (properties is null)
        {
            return;
        }

        foreach (var pair in properties)
        {
            body[pair.Key] = JsonSerializer.SerializeToNode(pair.Value, _serializerOptions);
        }
    }
}
