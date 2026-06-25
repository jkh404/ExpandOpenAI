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

        var preparedMessages = PrepareMessages(messages, options);
        using var request = _requestBuilder.CreateRequestMessage(
            preparedMessages,
            options,
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

        var preparedMessages = PrepareMessages(messages, options);
        using var request = _requestBuilder.CreateRequestMessage(
            preparedMessages,
            options,
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
}
