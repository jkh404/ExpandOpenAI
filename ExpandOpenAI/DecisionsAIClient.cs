using System.Text.Json;
using ExpandOpenAI.Internal;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI;

/// <summary>
/// HTTP client for a Decisions-compatible HTTP endpoint.
/// </summary>
public sealed class DecisionsAIClient : IDisposable
{
    public const string ApiKeyEnvironmentVariable = DecisionsAIClientOptions.ApiKeyEnvironmentVariable;
    public const string ModelEnvironmentVariable = DecisionsAIClientOptions.ModelEnvironmentVariable;
    public const string EndpointEnvironmentVariable = DecisionsAIClientOptions.EndpointEnvironmentVariable;
    public const string RequestPathEnvironmentVariable = DecisionsAIClientOptions.RequestPathEnvironmentVariable;

    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly DecisionsAIClientOptions _options;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly DecisionsAIRequestBuilder _requestBuilder;
    private readonly DecisionsAIResponseParser _responseParser = new();
    private bool _disposed;

    public DecisionsAIClient()
        : this(DecisionsAIClientOptions.FromEnvironment())
    {
    }

    public DecisionsAIClient(DecisionsAIClientOptions options)
        : this(new HttpClient(), options, disposeHttpClient: true)
    {
    }

    public DecisionsAIClient(
        HttpMessageHandler httpMessageHandler,
        DecisionsAIClientOptions options,
        bool disposeHandler = true,
        TimeSpan? timeout = null)
        : this(CreateHttpClient(httpMessageHandler, disposeHandler, timeout), options, disposeHttpClient: true)
    {
    }

    public DecisionsAIClient(
        string modelId,
        string apiKey,
        Uri endpoint,
        string requestPath = "v1/systemone")
        : this(new DecisionsAIClientOptions
        {
            ModelId = modelId,
            ApiKey = apiKey,
            Endpoint = endpoint,
            RequestPath = requestPath,
        })
    {
    }

    public DecisionsAIClient(
        HttpClient httpClient,
        DecisionsAIClientOptions options,
        bool disposeHttpClient = false)
    {
        ArgumentGuard.ThrowIfNull(httpClient, nameof(httpClient));
        ArgumentGuard.ThrowIfNull(options, nameof(options));
        ArgumentGuard.ThrowIfNull(options.Endpoint, nameof(options.Endpoint));
        if (!options.Endpoint.IsAbsoluteUri
            || options.Endpoint.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Endpoint must be an absolute HTTP or HTTPS URI.", nameof(options));
        }
        ArgumentGuard.ThrowIfNullOrWhiteSpace(options.ModelId, nameof(options.ModelId));
        ArgumentGuard.ThrowIfNull(options.RetryOptions, nameof(options.RetryOptions));
        options.RetryOptions.Validate(nameof(options.RetryOptions));

        _httpClient = httpClient;
        _disposeHttpClient = disposeHttpClient;
        _options = options;
        _serializerOptions = options.SerializerOptions ?? new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);
        _requestBuilder = new DecisionsAIRequestBuilder(options, _serializerOptions);
    }

    public HttpClient HttpClient => _httpClient;

    /// <summary>
    /// Sends a typed Decisions request and parses its typed answers.
    /// </summary>
    public async Task<DecisionsResponse> GetResponseAsync(
        DecisionsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentGuard.ThrowIfDisposed(_disposed, this);
        ArgumentGuard.ThrowIfNull(request, nameof(request));
        cancellationToken.ThrowIfCancellationRequested();

        using var response = await HttpRetryPolicy.SendAsync(
            _httpClient,
            () => _requestBuilder.CreateRequestMessage(request),
            HttpCompletionOption.ResponseContentRead,
            _options.RetryOptions,
            cancellationToken).ConfigureAwait(false);

        var payload = await ReadSuccessfulResponseAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        return _responseParser.ParseResponse(document.RootElement);
    }

    /// <summary>
    /// Convenience overload for evaluating one state against several questions.
    /// </summary>
    public Task<DecisionsResponse> GetResponseAsync(
        object state,
        IReadOnlyDictionary<string, DecisionsQuestion> questions,
        CancellationToken cancellationToken = default)
    {
        ArgumentGuard.ThrowIfNull(state, nameof(state));
        ArgumentGuard.ThrowIfNull(questions, nameof(questions));

        return GetResponseAsync(
            new DecisionsRequest
            {
                State = state,
                Questions = questions,
            },
            cancellationToken);
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

    private static HttpClient CreateHttpClient(
        HttpMessageHandler httpMessageHandler,
        bool disposeHandler,
        TimeSpan? timeout)
    {
        ArgumentGuard.ThrowIfNull(httpMessageHandler, nameof(httpMessageHandler));
        var httpClient = new HttpClient(httpMessageHandler, disposeHandler);
        if (timeout is not null)
        {
            httpClient.Timeout = timeout.Value;
        }

        return httpClient;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsyncCompat(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException(
            $"请求失败，状态码 {(int)response.StatusCode} ({response.ReasonPhrase})。响应内容: {body}");
    }

    private static async Task<string> ReadSuccessfulResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsyncCompat(cancellationToken).ConfigureAwait(false);
    }
}
