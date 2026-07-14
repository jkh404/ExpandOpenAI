using System.Text.Json;
using ExpandOpenAI.Internal;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI;

/// <summary>
/// OpenAI-compatible reranker.
/// </summary>
public class OpenAICompatibleReranker : IDisposable
{
    public const string ApiKeyEnvironmentVariable = OpenAICompatibleRerankerOptions.ApiKeyEnvironmentVariable;
    public const string ModelEnvironmentVariable = OpenAICompatibleRerankerOptions.ModelEnvironmentVariable;
    public const string ModelFallbackEnvironmentVariable = OpenAICompatibleRerankerOptions.ModelFallbackEnvironmentVariable;
    public const string EndpointEnvironmentVariable = OpenAICompatibleRerankerOptions.EndpointEnvironmentVariable;
    public const string RequestPathEnvironmentVariable = OpenAICompatibleRerankerOptions.RequestPathEnvironmentVariable;

    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly OpenAICompatibleRerankerOptions _options;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly OpenAICompatibleRerankRequestBuilder _requestBuilder;
    private readonly OpenAICompatibleRerankResponseParser _responseParser;
    private bool _disposed;

    public HttpClient HttpClient => _httpClient;

    public OpenAICompatibleReranker()
        : this(OpenAICompatibleRerankerOptions.FromEnvironment())
    {
    }

    public OpenAICompatibleReranker(OpenAICompatibleRerankerOptions options)
        : this(new HttpClient(), options, disposeHttpClient: true)
    {
    }

    public OpenAICompatibleReranker(
        HttpMessageHandler httpMessageHandler,
        OpenAICompatibleRerankerOptions options,
        bool disposeHandler = true,
        TimeSpan? timeout = null)
        : this(CreateHttpClient(httpMessageHandler, disposeHandler, timeout), options, disposeHttpClient: true)
    {
    }

    public OpenAICompatibleReranker(
        string modelId,
        string apiKey,
        Uri endpoint,
        string requestPath = "reranks",
        int? defaultTopN = null)
        : this(new OpenAICompatibleRerankerOptions
        {
            ModelId = modelId,
            ApiKey = apiKey,
            Endpoint = endpoint,
            RequestPath = requestPath,
            DefaultTopN = defaultTopN,
        })
    {
    }

    public OpenAICompatibleReranker(
        HttpClient httpClient,
        OpenAICompatibleRerankerOptions options,
        bool disposeHttpClient)
    {
        ArgumentGuard.ThrowIfNull(httpClient, nameof(httpClient));
        ArgumentGuard.ThrowIfNull(options, nameof(options));
        ArgumentGuard.ThrowIfNull(options.Endpoint, nameof(options.Endpoint));
        ArgumentGuard.ThrowIfNullOrWhiteSpace(options.ModelId, nameof(options.ModelId));
        ArgumentGuard.ThrowIfNull(options.RetryOptions, nameof(options.RetryOptions));
        options.RetryOptions.Validate(nameof(options.RetryOptions));

        if (options.DefaultTopN is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "DefaultTopN must be greater than zero.");
        }

        _httpClient = httpClient;
        _disposeHttpClient = disposeHttpClient;
        _options = options;
        _serializerOptions = options.SerializerOptions ?? new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);
        _requestBuilder = new OpenAICompatibleRerankRequestBuilder(_options, _serializerOptions);
        _responseParser = new OpenAICompatibleRerankResponseParser();
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

    public async Task<RerankingResponse> RerankAsync(
        string query,
        IEnumerable<string> documents,
        RerankingOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentGuard.ThrowIfDisposed(_disposed, this);

        var preparedDocuments = PrepareDocuments(query, documents, options);
        using var response = await HttpRetryPolicy.SendAsync(
            _httpClient,
            () => _requestBuilder.CreateRequestMessage(
                query,
                preparedDocuments,
                options,
                ConfigureRequestBody,
                ConfigureRequest),
            HttpCompletionOption.ResponseContentRead,
            _options.RetryOptions,
            cancellationToken).ConfigureAwait(false);
        var payload = await ReadSuccessfulResponseAsync(response, cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(payload);
        return _responseParser.ParseResponse(document.RootElement, preparedDocuments);
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

    protected virtual IReadOnlyList<string> PrepareDocuments(
        string query,
        IEnumerable<string> documents,
        RerankingOptions? options)
    {
        ArgumentGuard.ThrowIfNullOrWhiteSpace(query, nameof(query));
        ArgumentGuard.ThrowIfNull(documents, nameof(documents));

        if (options?.TopN is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "TopN must be greater than zero.");
        }

        var list = documents.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("At least one document is required.", nameof(documents));
        }

        if (list.Any(static document => document is null))
        {
            throw new ArgumentException("Documents cannot contain null.", nameof(documents));
        }

        return list;
    }

    protected virtual void ConfigureRequestBody(
        System.Text.Json.Nodes.JsonObject body,
        string query,
        IReadOnlyList<string> documents,
        RerankingOptions? options)
    {
    }

    protected virtual void ConfigureRequest(
        HttpRequestMessage request,
        string query,
        IReadOnlyList<string> documents,
        RerankingOptions? options)
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
