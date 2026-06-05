using System.Text.Json;
using ExpandOpenAI.Internal;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI;

/// <summary>
/// OpenAI-compatible embedding generator for Microsoft.Extensions.AI.
/// </summary>
public class OpenAICompatibleEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public const string ApiKeyEnvironmentVariable = OpenAICompatibleEmbeddingGeneratorOptions.ApiKeyEnvironmentVariable;
    public const string ModelEnvironmentVariable = OpenAICompatibleEmbeddingGeneratorOptions.ModelEnvironmentVariable;
    public const string ModelFallbackEnvironmentVariable = OpenAICompatibleEmbeddingGeneratorOptions.ModelFallbackEnvironmentVariable;
    public const string EndpointEnvironmentVariable = OpenAICompatibleEmbeddingGeneratorOptions.EndpointEnvironmentVariable;
    public const string RequestPathEnvironmentVariable = OpenAICompatibleEmbeddingGeneratorOptions.RequestPathEnvironmentVariable;

    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly OpenAICompatibleEmbeddingGeneratorOptions _options;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly OpenAICompatibleEmbeddingRequestBuilder _requestBuilder;
    private readonly OpenAICompatibleEmbeddingResponseParser _responseParser;
    private bool _disposed;

    public HttpClient HttpClient => _httpClient;

    public OpenAICompatibleEmbeddingGenerator()
        : this(OpenAICompatibleEmbeddingGeneratorOptions.FromEnvironment())
    {
    }

    public OpenAICompatibleEmbeddingGenerator(OpenAICompatibleEmbeddingGeneratorOptions options)
        : this(new HttpClient(), options, disposeHttpClient: true)
    {
    }

    public OpenAICompatibleEmbeddingGenerator(
        HttpMessageHandler httpMessageHandler,
        OpenAICompatibleEmbeddingGeneratorOptions options,
        bool disposeHandler = true,
        TimeSpan? timeout = null)
        : this(CreateHttpClient(httpMessageHandler, disposeHandler, timeout), options, disposeHttpClient: true)
    {
    }

    public OpenAICompatibleEmbeddingGenerator(string modelId, string apiKey, Uri endpoint, string requestPath = "embeddings")
        : this(new OpenAICompatibleEmbeddingGeneratorOptions
        {
            ModelId = modelId,
            ApiKey = apiKey,
            Endpoint = endpoint,
            RequestPath = requestPath,
        })
    {
    }

    public OpenAICompatibleEmbeddingGenerator(
        HttpClient httpClient,
        OpenAICompatibleEmbeddingGeneratorOptions options,
        bool disposeHttpClient)
    {
        ArgumentGuard.ThrowIfNull(httpClient, nameof(httpClient));
        ArgumentGuard.ThrowIfNull(options, nameof(options));
        ArgumentGuard.ThrowIfNull(options.Endpoint, nameof(options.Endpoint));
        ArgumentGuard.ThrowIfNullOrWhiteSpace(options.ModelId, nameof(options.ModelId));

        _httpClient = httpClient;
        _disposeHttpClient = disposeHttpClient;
        _options = options;
        _serializerOptions = options.SerializerOptions ?? new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);
        _requestBuilder = new OpenAICompatibleEmbeddingRequestBuilder(_options, _serializerOptions);
        _responseParser = new OpenAICompatibleEmbeddingResponseParser(_serializerOptions);
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler httpMessageHandler, bool disposeHandler, TimeSpan? timeout = null)
    {
        ArgumentGuard.ThrowIfNull(httpMessageHandler, nameof(httpMessageHandler));
        var httpClient = new HttpClient(httpMessageHandler, disposeHandler: disposeHandler);
        httpClient.Timeout = timeout ?? httpClient.Timeout;
        return httpClient;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentGuard.ThrowIfDisposed(_disposed, this);

        var preparedValues = PrepareValues(values, options);
        using var request = _requestBuilder.CreateRequestMessage(
            preparedValues,
            options,
            ConfigureRequestBody,
            ConfigureRequest);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var payload = await ReadSuccessfulResponseAsync(response, cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(payload);
        return _responseParser.ParseResponse(document.RootElement);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentGuard.ThrowIfNull(serviceType, nameof(serviceType));

        if (serviceType != typeof(object) && serviceType.IsAssignableFrom(GetType()))
        {
            return this;
        }

        if (serviceType.IsAssignableFrom(typeof(HttpClient)))
        {
            return _httpClient;
        }

        if (serviceType != typeof(object) && serviceType.IsAssignableFrom(_options.GetType()))
        {
            return _options;
        }

        if (serviceType.IsAssignableFrom(typeof(JsonSerializerOptions)))
        {
            return _serializerOptions;
        }

        return null;
    }

    protected virtual IReadOnlyList<string> PrepareValues(IEnumerable<string> values, EmbeddingGenerationOptions? options)
    {
        ArgumentGuard.ThrowIfNull(values, nameof(values));

        var list = values.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("At least one input value is required.", nameof(values));
        }

        if (list.Any(static value => value is null))
        {
            throw new ArgumentException("Input values cannot contain null.", nameof(values));
        }

        return list;
    }

    protected virtual void ConfigureRequestBody(
        System.Text.Json.Nodes.JsonObject body,
        IReadOnlyList<string> values,
        EmbeddingGenerationOptions? options)
    {
    }

    protected virtual void ConfigureRequest(
        HttpRequestMessage request,
        IReadOnlyList<string> values,
        EmbeddingGenerationOptions? options)
    {
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsyncCompat(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException(
            $"请求失败，状态码 {(int)response.StatusCode} ({response.ReasonPhrase})。响应内容: {body}");
    }

    private static async Task<string> ReadSuccessfulResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsyncCompat(cancellationToken).ConfigureAwait(false);
    }
}
