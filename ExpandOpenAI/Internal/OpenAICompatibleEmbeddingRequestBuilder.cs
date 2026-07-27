using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.Internal;

internal sealed class OpenAICompatibleEmbeddingRequestBuilder
{
    private readonly OpenAICompatibleEmbeddingGeneratorOptions _options;
    private readonly JsonSerializerOptions _serializerOptions;

    public OpenAICompatibleEmbeddingRequestBuilder(OpenAICompatibleEmbeddingGeneratorOptions options, JsonSerializerOptions serializerOptions)
    {
        _options = options;
        _serializerOptions = serializerOptions;
    }

    public HttpRequestMessage CreateRequestMessage(
        IReadOnlyList<string> values,
        EmbeddingGenerationOptions? options,
        Action<JsonObject, IReadOnlyList<string>, EmbeddingGenerationOptions?>? configureRequestBody,
        Action<HttpRequestMessage, IReadOnlyList<string>, EmbeddingGenerationOptions?>? configureRequest)
    {
        var requestUri = BuildRequestUri();
        var body = CreateRequestBody(values, options, configureRequestBody);
        var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(body.ToJsonString(_serializerOptions), Encoding.UTF8, "application/json"),
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        AddAuthenticationHeader(request);
        AddDefaultHeaders(request);

        _options.ConfigureRequest?.Invoke(request, values, options);
        configureRequest?.Invoke(request, values, options);
        return request;
    }

    public HttpRequestMessage CreateMultimodalRequestMessage(
        IReadOnlyList<AIContent> contents,
        EmbeddingGenerationOptions? options,
        Action<JsonObject, IReadOnlyList<AIContent>, EmbeddingGenerationOptions?>? configureRequestBody,
        Action<HttpRequestMessage, IReadOnlyList<AIContent>, EmbeddingGenerationOptions?>? configureRequest)
    {
        var requestUri = BuildRequestUri();
        var body = CreateMultimodalRequestBody(contents, options, configureRequestBody);
        var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(body.ToJsonString(_serializerOptions), Encoding.UTF8, "application/json"),
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        AddAuthenticationHeader(request);
        AddDefaultHeaders(request);

        _options.ConfigureMultimodalRequest?.Invoke(request, contents, options);
        configureRequest?.Invoke(request, contents, options);
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
        IReadOnlyList<string> values,
        EmbeddingGenerationOptions? options,
        Action<JsonObject, IReadOnlyList<string>, EmbeddingGenerationOptions?>? configureRequestBody)
    {
        var body = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(options?.ModelId) ? _options.ModelId : options!.ModelId,
            ["input"] = new JsonArray(values.Select(static value => JsonValue.Create(value)).ToArray()),
        };

        var dimensions = options?.Dimensions ?? _options.DefaultModelDimensions;
        if (dimensions is not null)
        {
            body["dimensions"] = JsonSerializer.SerializeToNode(dimensions, _serializerOptions);
        }

        if (!string.IsNullOrWhiteSpace(_options.EncodingFormat))
        {
            body["encoding_format"] = _options.EncodingFormat;
        }

        MergeRequestProperties(body, _options.RequestBody);
        MergeRequestProperties(body, options?.AdditionalProperties);

        _options.ConfigureRequestBody?.Invoke(body, values, options);
        configureRequestBody?.Invoke(body, values, options);

        return body;
    }

    private JsonObject CreateMultimodalRequestBody(
        IReadOnlyList<AIContent> contents,
        EmbeddingGenerationOptions? options,
        Action<JsonObject, IReadOnlyList<AIContent>, EmbeddingGenerationOptions?>? configureRequestBody)
    {
        var parameters = new JsonObject();
        var dimensions = options?.Dimensions ?? _options.DefaultModelDimensions;
        if (dimensions is not null)
        {
            parameters["dimension"] = JsonSerializer.SerializeToNode(dimensions, _serializerOptions);
        }

        var body = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(options?.ModelId) ? _options.ModelId : options!.ModelId,
            ["parameters"] = parameters,
            ["input"] = new JsonObject
            {
                ["contents"] = SerializeMultimodalContents(contents),
            },
        };

        MergeRequestProperties(body, _options.RequestBody);
        MergeRequestProperties(body, options?.AdditionalProperties);

        _options.ConfigureMultimodalRequestBody?.Invoke(body, contents, options);
        configureRequestBody?.Invoke(body, contents, options);
        return body;
    }

    private static JsonArray SerializeMultimodalContents(IReadOnlyList<AIContent> contents)
    {
        var serializedContents = new JsonArray();
        foreach (var content in contents)
        {
            serializedContents.Add(content switch
            {
                TextContent text => new JsonObject { ["text"] = text.Text },
                DataContent imageData when imageData.HasTopLevelMediaType("image") =>
                    new JsonObject { ["image"] = imageData.Uri },
                UriContent imageUri when imageUri.HasTopLevelMediaType("image") =>
                    new JsonObject { ["image"] = imageUri.Uri.ToString() },
                UriContent videoUri when videoUri.HasTopLevelMediaType("video") =>
                    new JsonObject { ["video"] = videoUri.Uri.ToString() },
                _ => throw new NotSupportedException(
                    $"DashScope 多模态向量只支持 TextContent、图片 DataContent / UriContent 和视频 UriContent，当前类型为 {content.GetType().FullName}。"),
            });
        }

        return serializedContents;
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
