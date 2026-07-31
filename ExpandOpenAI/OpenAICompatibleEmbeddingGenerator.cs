using System.Text.Json;
using ExpandOpenAI.Internal;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI;

/// <summary>
/// OpenAI-compatible embedding generator for Microsoft.Extensions.AI.
/// </summary>
public class OpenAICompatibleEmbeddingGenerator :
    IEmbeddingGenerator<string, Embedding<float>>,
    IEmbeddingGenerator<AIContent, Embedding<float>>,
    IMultimodalEmbeddingGenerator
{
    public const string ApiKeyEnvironmentVariable = OpenAICompatibleEmbeddingGeneratorOptions.ApiKeyEnvironmentVariable;
    public const string ModelEnvironmentVariable = OpenAICompatibleEmbeddingGeneratorOptions.ModelEnvironmentVariable;
    public const string ModelFallbackEnvironmentVariable = OpenAICompatibleEmbeddingGeneratorOptions.ModelFallbackEnvironmentVariable;
    public const string EndpointEnvironmentVariable = OpenAICompatibleEmbeddingGeneratorOptions.EndpointEnvironmentVariable;
    public const string RequestPathEnvironmentVariable = OpenAICompatibleEmbeddingGeneratorOptions.RequestPathEnvironmentVariable;
    public const string MultimodalApiKeyEnvironmentVariable = OpenAICompatibleEmbeddingGeneratorOptions.MultimodalApiKeyEnvironmentVariable;
    public const string MultimodalModelEnvironmentVariable = OpenAICompatibleEmbeddingGeneratorOptions.MultimodalModelEnvironmentVariable;
    public const string MultimodalEndpointEnvironmentVariable = OpenAICompatibleEmbeddingGeneratorOptions.MultimodalEndpointEnvironmentVariable;
    public const string MultimodalRequestPathEnvironmentVariable = OpenAICompatibleEmbeddingGeneratorOptions.MultimodalRequestPathEnvironmentVariable;
    public const string MultimodalDimensionsEnvironmentVariable = OpenAICompatibleEmbeddingGeneratorOptions.MultimodalDimensionsEnvironmentVariable;

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

    public OpenAICompatibleEmbeddingGenerator(
        string modelId,
        string apiKey,
        Uri endpoint,
        string requestPath = "embeddings",
        int? defaultModelDimensions = null)
        : this(new OpenAICompatibleEmbeddingGeneratorOptions
        {
            ModelId = modelId,
            ApiKey = apiKey,
            Endpoint = endpoint,
            RequestPath = requestPath,
            DefaultModelDimensions = defaultModelDimensions,
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
        ArgumentGuard.ThrowIfNull(options.RetryOptions, nameof(options.RetryOptions));
        options.RetryOptions.Validate(nameof(options.RetryOptions));

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

    public async Task<Embedding<float>> GenerateAsync(
        string value,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        GeneratedEmbeddings<Embedding<float>> embeddings =
            await GenerateAsync([value], options, cancellationToken).ConfigureAwait(false);
        return embeddings[0];
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentGuard.ThrowIfDisposed(_disposed, this);

        var preparedValues = PrepareValues(values, options);
        using var response = await HttpRetryPolicy.SendAsync(
            _httpClient,
            () => _requestBuilder.CreateRequestMessage(
                preparedValues,
                options,
                ConfigureRequestBody,
                ConfigureRequest),
            HttpCompletionOption.ResponseContentRead,
            _options.RetryOptions,
            cancellationToken).ConfigureAwait(false);
        var payload = await ReadSuccessfulResponseAsync(response, cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(payload);
        return _responseParser.ParseResponse(document.RootElement);
    }

    public async Task<Embedding<float>> GenerateAsync(
        AIContent content,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        GeneratedEmbeddings<Embedding<float>> embeddings =
            await GenerateMultimodalAsync([content], options, cancellationToken).ConfigureAwait(false);
        return embeddings[0];
    }

    /// <summary>
    /// 调用 DashScope 多模态向量接口生成文本、图片或视频的向量。
    /// </summary>
    /// <remarks>
    /// 此方法使用 DashScope 的 <c>input.contents</c> 请求格式，不影响标准 OpenAI-compatible
    /// <c>/embeddings</c> 接口使用的 <see cref="GenerateAsync(IEnumerable{string}, EmbeddingGenerationOptions?, CancellationToken)"/>。
    /// </remarks>
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateMultimodalAsync(
        IEnumerable<AIContent> contents,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentGuard.ThrowIfDisposed(_disposed, this);

        var preparedContents = PrepareMultimodalContents(contents);
        using var response = await HttpRetryPolicy.SendAsync(
            _httpClient,
            () => _requestBuilder.CreateMultimodalRequestMessage(
                preparedContents,
                options,
                ConfigureMultimodalRequestBody,
                ConfigureMultimodalRequest),
            HttpCompletionOption.ResponseContentRead,
            _options.RetryOptions,
            cancellationToken).ConfigureAwait(false);
        var payload = await ReadSuccessfulResponseAsync(response, cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(payload);
        var modelId = string.IsNullOrWhiteSpace(options?.ModelId) ? _options.ModelId : options!.ModelId;
        return _responseParser.ParseDashScopeMultimodalResponse(document.RootElement, modelId);
    }

    async Task<GeneratedEmbeddings<Embedding<float>>> IEmbeddingGenerator<AIContent, Embedding<float>>.GenerateAsync(
        IEnumerable<AIContent> values,
        EmbeddingGenerationOptions? options,
        CancellationToken cancellationToken)
    {
        return await GenerateMultimodalAsync(values, options, cancellationToken).ConfigureAwait(false);
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

    protected virtual IReadOnlyList<AIContent> PrepareMultimodalContents(IEnumerable<AIContent> contents)
    {
        ArgumentGuard.ThrowIfNull(contents, nameof(contents));

        var list = contents.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("At least one multimodal input content is required.", nameof(contents));
        }

        if (list.Any(static content => content is null))
        {
            throw new ArgumentException("Multimodal input contents cannot contain null.", nameof(contents));
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

    protected virtual void ConfigureMultimodalRequestBody(
        System.Text.Json.Nodes.JsonObject body,
        IReadOnlyList<AIContent> contents,
        EmbeddingGenerationOptions? options)
    {
    }

    protected virtual void ConfigureMultimodalRequest(
        HttpRequestMessage request,
        IReadOnlyList<AIContent> contents,
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
