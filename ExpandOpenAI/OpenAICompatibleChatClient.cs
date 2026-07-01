using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ExpandOpenAI.Internal;

namespace ExpandOpenAI;

/// <summary>
/// 默认通用的 ChatClient，目标兼容 OpenAI，可扩展，灵活。
/// </summary>
public class OpenAICompatibleChatClient : IChatClient
{
    public const string ApiKeyEnvironmentVariable = OpenAICompatibleChatClientOptions.ApiKeyEnvironmentVariable;
    public const string ModelEnvironmentVariable = OpenAICompatibleChatClientOptions.ModelEnvironmentVariable;
    public const string EndpointEnvironmentVariable = OpenAICompatibleChatClientOptions.EndpointEnvironmentVariable;
    public const string RequestPathEnvironmentVariable = OpenAICompatibleChatClientOptions.RequestPathEnvironmentVariable;

    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly OpenAICompatibleChatClientOptions _options;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly OpenAICompatibleRequestBuilder _requestBuilder;
    private readonly OpenAICompatibleResponseParser _responseParser;
    private bool _disposed;

    public HttpClient HttpClient => _httpClient;
    public OpenAICompatibleChatClient()
        : this(OpenAICompatibleChatClientOptions.FromEnvironment())
    {
    }

    public OpenAICompatibleChatClient(OpenAICompatibleChatClientOptions options)
        : this(new HttpClient(), options, disposeHttpClient: true)
    {
    }

    public OpenAICompatibleChatClient(HttpMessageHandler httpMessageHandler, OpenAICompatibleChatClientOptions options, bool disposeHandler = true,TimeSpan? timeout = null)
        : this(CreateHttpClient(httpMessageHandler, disposeHandler, timeout), options, disposeHttpClient: true)
    {
    }

    public OpenAICompatibleChatClient(string modelId, string apiKey, Uri endpoint, string requestPath = "chat/completions")
        : this(new OpenAICompatibleChatClientOptions
        {
            ModelId = modelId,
            ApiKey = apiKey,
            Endpoint = endpoint,
            RequestPath = requestPath,
        })
    {
    }

    public OpenAICompatibleChatClient(HttpClient httpClient, OpenAICompatibleChatClientOptions options, bool disposeHttpClient)
    {
        ArgumentGuard.ThrowIfNull(httpClient, nameof(httpClient));
        ArgumentGuard.ThrowIfNull(options, nameof(options));
        ArgumentGuard.ThrowIfNull(options.Endpoint, nameof(options.Endpoint));
        ArgumentGuard.ThrowIfNullOrWhiteSpace(options.ModelId, nameof(options.ModelId));

        _httpClient = httpClient;
        _disposeHttpClient = disposeHttpClient;
        _options = options;
        _serializerOptions = options.SerializerOptions ?? new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);
        _requestBuilder = new OpenAICompatibleRequestBuilder(_options, _serializerOptions);
        _responseParser = new OpenAICompatibleResponseParser(_serializerOptions);
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler httpMessageHandler, bool disposeHandler,TimeSpan? timeout=null)
    {
        
        ArgumentGuard.ThrowIfNull(httpMessageHandler, nameof(httpMessageHandler));
        var httpClient= new HttpClient(httpMessageHandler, disposeHandler: disposeHandler);
        httpClient.Timeout = timeout?? httpClient.Timeout;
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

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentGuard.ThrowIfDisposed(_disposed, this);

        var effectiveOptions = CreateEffectiveOptions(options);
        var preparedMessages = PrepareMessages(messages, effectiveOptions);
        using var request = _requestBuilder.CreateRequestMessage(
            preparedMessages,
            effectiveOptions,
            stream: false,
            ConfigureRequestBody,
            ConfigureRequest);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
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
        using var request = _requestBuilder.CreateRequestMessage(
            preparedMessages,
            effectiveOptions,
            stream: true,
            ConfigureRequestBody,
            ConfigureRequest);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var streamState = _responseParser.CreateStreamingState();
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
                foreach (var update in _responseParser.ParseStreamingEvent(eventLines, streamState))
                {
                    yield return update;
                }

                eventLines.Clear();
                continue;
            }

            eventLines.Add(line);
        }

        foreach (var update in _responseParser.ParseStreamingEvent(eventLines, streamState))
        {
            yield return update;
        }

        foreach (var update in _responseParser.FlushPendingToolCalls(streamState))
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

    protected virtual IReadOnlyList<ChatMessage> PrepareMessages(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        ArgumentGuard.ThrowIfNull(messages, nameof(messages));

        var list = messages.ToList();
        if (!string.IsNullOrWhiteSpace(options?.Instructions))
        {
            list.Insert(0, new ChatMessage(ChatRole.System, options.Instructions!));
        }

        return list;
    }

    protected virtual OpenAICompatibleChatClientOptions CreateEffectiveOptions(ChatOptions? options)
    {
        if (options is null || ReferenceEquals(options, _options))
        {
            return _options;
        }

        var merged = (OpenAICompatibleChatClientOptions)_options.Clone();
        ApplyChatOptionsOverrides(merged, options);

        if (options is OpenAICompatibleChatClientOptions compatibleOptions)
        {
            ApplyExtendedOptionsOverrides(merged, compatibleOptions);
        }

        return merged;
    }

    protected virtual void ConfigureRequestBody(
        System.Text.Json.Nodes.JsonObject body,
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

    private static void ApplyChatOptionsOverrides(OpenAICompatibleChatClientOptions target, ChatOptions source)
    {
        if (source.ConversationId is not null)
        {
            target.ConversationId = source.ConversationId;
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
        OpenAICompatibleChatClientOptions target,
        OpenAICompatibleChatClientOptions source)
    {
        if (source.ApiKey is not null)
        {
            target.ApiKey = source.ApiKey;
        }

        if (source.Headers.Count > 0)
        {
            var headers = target.Headers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
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
                : target.RequestBody.ToDictionary(pair => pair.Key, pair => pair.Value);

            foreach (var pair in source.RequestBody)
            {
                requestBody[pair.Key] = pair.Value;
            }

            target.RequestBody = requestBody;
        }

        if (!ReferenceEquals(target.ConfigureRequest, source.ConfigureRequest) && source.ConfigureRequest is not null)
        {
            target.ConfigureRequest = target.ConfigureRequest is null
                ? source.ConfigureRequest
                : target.ConfigureRequest + source.ConfigureRequest;
        }

        if (!ReferenceEquals(target.ConfigureRequestBody, source.ConfigureRequestBody) && source.ConfigureRequestBody is not null)
        {
            target.ConfigureRequestBody = target.ConfigureRequestBody is null
                ? source.ConfigureRequestBody
                : target.ConfigureRequestBody + source.ConfigureRequestBody;
        }
    }
}
