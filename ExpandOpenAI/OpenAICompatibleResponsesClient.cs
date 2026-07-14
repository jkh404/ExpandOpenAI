using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExpandOpenAI.Internal;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI;

/// <summary>
/// 以 OpenAI Responses API 协议实现的 <see cref="IChatClient"/>。
/// </summary>
public class OpenAICompatibleResponsesClient : IChatClient
{
    public const string ApiKeyEnvironmentVariable = OpenAICompatibleResponsesClientOptions.ApiKeyEnvironmentVariable;
    public const string ModelEnvironmentVariable = OpenAICompatibleResponsesClientOptions.ModelEnvironmentVariable;
    public const string ModelFallbackEnvironmentVariable = OpenAICompatibleResponsesClientOptions.ModelFallbackEnvironmentVariable;
    public const string EndpointEnvironmentVariable = OpenAICompatibleResponsesClientOptions.EndpointEnvironmentVariable;
    public const string RequestPathEnvironmentVariable = OpenAICompatibleResponsesClientOptions.RequestPathEnvironmentVariable;

    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly OpenAICompatibleResponsesClientOptions _options;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly OpenAICompatibleResponsesRequestBuilder _requestBuilder;
    private readonly OpenAICompatibleResponsesResponseParser _responseParser;
    private bool _disposed;

    public OpenAICompatibleResponsesClient()
        : this(OpenAICompatibleResponsesClientOptions.FromEnvironment())
    {
    }

    public OpenAICompatibleResponsesClient(OpenAICompatibleResponsesClientOptions options)
        : this(new HttpClient(), options, disposeHttpClient: true)
    {
    }

    public OpenAICompatibleResponsesClient(
        HttpMessageHandler httpMessageHandler,
        OpenAICompatibleResponsesClientOptions options,
        bool disposeHandler = true,
        TimeSpan? timeout = null)
        : this(CreateHttpClient(httpMessageHandler, disposeHandler, timeout), options, disposeHttpClient: true)
    {
    }

    public OpenAICompatibleResponsesClient(
        string modelId,
        string apiKey,
        Uri endpoint,
        string requestPath = "responses")
        : this(new OpenAICompatibleResponsesClientOptions
        {
            ModelId = modelId,
            ApiKey = apiKey,
            Endpoint = endpoint,
            RequestPath = requestPath,
        })
    {
    }

    public OpenAICompatibleResponsesClient(
        HttpClient httpClient,
        OpenAICompatibleResponsesClientOptions options,
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
        _requestBuilder = new OpenAICompatibleResponsesRequestBuilder(_options, _serializerOptions);
        _responseParser = new OpenAICompatibleResponsesResponseParser(_serializerOptions);
    }

    public HttpClient HttpClient => _httpClient;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentGuard.ThrowIfDisposed(_disposed, this);

        var effectiveOptions = CreateEffectiveOptions(options);
        var preparedMessages = PrepareMessages(messages, effectiveOptions);
        using var response = await HttpRetryPolicy.SendAsync(
            _httpClient,
            () => _requestBuilder.CreateRequestMessage(
                preparedMessages,
                effectiveOptions,
                stream: false,
                ConfigureRequestBody,
                ConfigureRequest),
            HttpCompletionOption.ResponseContentRead,
            _options.RetryOptions,
            cancellationToken).ConfigureAwait(false);

        var payload = await ReadSuccessfulResponseAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        return _responseParser.ParseResponse(document.RootElement);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentGuard.ThrowIfDisposed(_disposed, this);

        var effectiveOptions = CreateEffectiveOptions(options);
        var preparedMessages = PrepareMessages(messages, effectiveOptions);
        using var response = await HttpRetryPolicy.SendAsync(
            _httpClient,
            () => _requestBuilder.CreateRequestMessage(
                preparedMessages,
                effectiveOptions,
                stream: true,
                ConfigureRequestBody,
                ConfigureRequest),
            HttpCompletionOption.ResponseHeadersRead,
            _options.RetryOptions,
            cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var state = _responseParser.CreateStreamingState();
        using var stream = await response.Content.ReadAsStreamAsyncCompat(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var eventLines = new List<string>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsyncCompat(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                foreach (var update in _responseParser.ParseStreamingEvent(eventLines, state))
                {
                    yield return update;
                }

                eventLines.Clear();
                continue;
            }

            eventLines.Add(line);
        }

        foreach (var update in _responseParser.ParseStreamingEvent(eventLines, state))
        {
            yield return update;
        }

        foreach (var update in _responseParser.FlushPendingItems(state))
        {
            yield return update;
        }
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

    protected virtual IReadOnlyList<ChatMessage> PrepareMessages(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options)
    {
        ArgumentGuard.ThrowIfNull(messages, nameof(messages));
        return messages.ToList();
    }

    protected virtual OpenAICompatibleResponsesClientOptions CreateEffectiveOptions(ChatOptions? options)
    {
        if (options is null || ReferenceEquals(options, _options))
        {
            return _options;
        }

        var merged = (OpenAICompatibleResponsesClientOptions)_options.Clone();
        ApplyChatOptionsOverrides(merged, options);

        if (options is OpenAICompatibleResponsesClientOptions compatibleOptions)
        {
            ApplyExtendedOptionsOverrides(merged, compatibleOptions);
        }

        return merged;
    }

    protected virtual void ConfigureRequestBody(
        JsonObject body,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        bool stream)
    {
    }

    protected virtual void ConfigureRequest(
        HttpRequestMessage request,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        bool stream)
    {
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

    private static void ApplyChatOptionsOverrides(
        OpenAICompatibleResponsesClientOptions target,
        ChatOptions source)
    {
        if (source.ConversationId is not null)
        {
            target.ConversationId = source.ConversationId;
            target.Conversation = null;
        }

        if (source.Instructions is not null)
        {
            target.Instructions = source.Instructions;
        }

        if (source.Temperature is not null)
        {
            target.Temperature = source.Temperature;
        }

        if (source.MaxOutputTokens is not null)
        {
            target.MaxOutputTokens = source.MaxOutputTokens;
        }

        if (source.TopP is not null)
        {
            target.TopP = source.TopP;
        }

        if (source.TopK is not null)
        {
            target.TopK = source.TopK;
        }

        if (source.FrequencyPenalty is not null)
        {
            target.FrequencyPenalty = source.FrequencyPenalty;
        }

        if (source.PresencePenalty is not null)
        {
            target.PresencePenalty = source.PresencePenalty;
        }

        if (source.Seed is not null)
        {
            target.Seed = source.Seed;
        }

        if (source.Reasoning is not null)
        {
            target.Reasoning = source.Reasoning;
        }

        if (source.ResponseFormat is not null)
        {
            target.ResponseFormat = source.ResponseFormat;
        }

        if (source.ModelId is not null)
        {
            target.ModelId = source.ModelId;
        }

        if (source.StopSequences is not null)
        {
            target.StopSequences = source.StopSequences;
        }

        if (source.AllowMultipleToolCalls is not null)
        {
            target.AllowMultipleToolCalls = source.AllowMultipleToolCalls;
        }

        if (source.ToolMode is not null)
        {
            target.ToolMode = source.ToolMode;
        }

        if (source.Tools is not null)
        {
            target.Tools = source.Tools;
        }

        if (source.AllowBackgroundResponses is not null)
        {
            target.AllowBackgroundResponses = source.AllowBackgroundResponses;
        }

        if (source.ContinuationToken is not null)
        {
            target.ContinuationToken = source.ContinuationToken;
        }

        if (source.RawRepresentationFactory is not null)
        {
            target.RawRepresentationFactory = source.RawRepresentationFactory;
        }

        if (source.AdditionalProperties is not null)
        {
            var additionalProperties = target.AdditionalProperties is null
                ? new AdditionalPropertiesDictionary()
                : new AdditionalPropertiesDictionary(target.AdditionalProperties);

            foreach (var pair in source.AdditionalProperties)
            {
                additionalProperties[pair.Key] = pair.Value;
            }

            target.AdditionalProperties = additionalProperties;
        }
    }

    private static void ApplyExtendedOptionsOverrides(
        OpenAICompatibleResponsesClientOptions target,
        OpenAICompatibleResponsesClientOptions source)
    {
        if (source.ApiKey is not null)
        {
            target.ApiKey = source.ApiKey;
        }

        if (source.Headers.Count > 0)
        {
            var headers = target.Headers.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
            foreach (var pair in source.Headers)
            {
                headers[pair.Key] = pair.Value;
            }

            target.Headers = headers;
        }

        if (source.RequestBody is not null)
        {
            var requestBody = target.RequestBody is null
                ? new Dictionary<string, object?>()
                : target.RequestBody.ToDictionary(static pair => pair.Key, static pair => pair.Value);
            foreach (var pair in source.RequestBody)
            {
                requestBody[pair.Key] = pair.Value;
            }

            target.RequestBody = requestBody;
        }

        target.Store = source.Store ?? target.Store;
        target.PreviousResponseId = source.PreviousResponseId ?? target.PreviousResponseId;
        target.Conversation = source.Conversation ?? target.Conversation;
        target.Include = source.Include ?? target.Include;
        target.Truncation = source.Truncation ?? target.Truncation;
        target.Metadata = source.Metadata ?? target.Metadata;
        target.MaxToolCalls = source.MaxToolCalls ?? target.MaxToolCalls;

        if (!ReferenceEquals(target.ConfigureRequest, source.ConfigureRequest) && source.ConfigureRequest is not null)
        {
            target.ConfigureRequest = target.ConfigureRequest is null
                ? source.ConfigureRequest
                : target.ConfigureRequest + source.ConfigureRequest;
        }

        if (!ReferenceEquals(target.ConfigureRequestBody, source.ConfigureRequestBody)
            && source.ConfigureRequestBody is not null)
        {
            target.ConfigureRequestBody = target.ConfigureRequestBody is null
                ? source.ConfigureRequestBody
                : target.ConfigureRequestBody + source.ConfigureRequestBody;
        }
    }
}
