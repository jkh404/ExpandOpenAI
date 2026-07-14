using System.Net;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.Tests;

public sealed class HttpRetryTests
{
    [Fact]
    public async Task ChatClient_RetriesTransientStatusWithNewRequest()
    {
        HttpRequestMessage? previousRequest = null;
        using var handler = new DelegateHttpMessageHandler((attempt, request, _) =>
        {
            if (previousRequest is not null)
            {
                Assert.NotSame(previousRequest, request);
            }

            previousRequest = request;
            return Task.FromResult(attempt == 1
                ? Response(HttpStatusCode.ServiceUnavailable, "busy")
                : JsonResponse(ChatResponseJson));
        });
        using var client = new OpenAICompatibleChatClient(handler, ChatOptions());

        var response = await client.GetResponseAsync("hello");

        Assert.Equal("ok", response.Text);
        Assert.Equal(2, handler.AttemptCount);
    }

    [Fact]
    public async Task StreamingChatClient_RetriesBeforeSuccessfulResponseHeaders()
    {
        using var handler = new DelegateHttpMessageHandler((attempt, _, _) => Task.FromResult(
            attempt == 1
                ? Response(HttpStatusCode.BadGateway, "gateway")
                : EventStreamResponse(
                    "data: {\"id\":\"stream-1\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}\n\n" +
                    "data: [DONE]\n\n")));
        using var client = new OpenAICompatibleChatClient(handler, ChatOptions());
        var updates = new List<ChatResponseUpdate>();

        await foreach (var update in client.GetStreamingResponseAsync("hello"))
        {
            updates.Add(update);
        }

        Assert.Equal("ok", Assert.Single(updates).Text);
        Assert.Equal(2, handler.AttemptCount);
    }

    [Fact]
    public async Task EmbeddingGenerator_RetriesTransientNetworkException()
    {
        using var handler = new DelegateHttpMessageHandler((attempt, _, _) =>
        {
            if (attempt == 1)
            {
                throw new HttpRequestException("connection reset");
            }

            return Task.FromResult(JsonResponse(EmbeddingResponseJson));
        });
        using var generator = new OpenAICompatibleEmbeddingGenerator(handler, EmbeddingOptions());

        var result = await generator.GenerateAsync("hello");

        Assert.Equal(2, result.Vector.Length);
        Assert.Equal(2, handler.AttemptCount);
    }

    [Fact]
    public async Task Reranker_RetriesTooManyRequests()
    {
        using var handler = new DelegateHttpMessageHandler((attempt, _, _) => Task.FromResult(
            attempt == 1
                ? Response(HttpStatusCode.TooManyRequests, "slow down")
                : JsonResponse(RerankResponseJson)));
        using var reranker = new OpenAICompatibleReranker(handler, RerankerOptions());

        var result = await reranker.RerankAsync("query", ["document"]);

        Assert.Single(result);
        Assert.Equal(2, handler.AttemptCount);
    }

    [Fact]
    public async Task ChatClient_DoesNotRetryNonTransientStatus()
    {
        using var handler = new DelegateHttpMessageHandler((_, _, _) => Task.FromResult(
            Response(HttpStatusCode.BadRequest, "invalid request")));
        using var client = new OpenAICompatibleChatClient(handler, ChatOptions());

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync("hello"));

        Assert.Equal(1, handler.AttemptCount);
    }

    [Fact]
    public async Task ChatClient_DoesNotRetryCallerCancellation()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new DelegateHttpMessageHandler(async (_, _, cancellationToken) =>
        {
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse(ChatResponseJson);
        });
        using var client = new OpenAICompatibleChatClient(handler, ChatOptions());
        using var cancellation = new CancellationTokenSource();

        var responseTask = client.GetResponseAsync("hello", cancellationToken: cancellation.Token);
        await requestStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => responseTask);
        Assert.Equal(1, handler.AttemptCount);
    }

    [Fact]
    public async Task ChatClient_RetriesTimeoutWhenCallerDidNotCancel()
    {
        using var handler = new DelegateHttpMessageHandler((attempt, _, _) =>
        {
            if (attempt == 1)
            {
                throw new TaskCanceledException("simulated timeout");
            }

            return Task.FromResult(JsonResponse(ChatResponseJson));
        });
        using var client = new OpenAICompatibleChatClient(handler, ChatOptions());

        var response = await client.GetResponseAsync("hello");

        Assert.Equal("ok", response.Text);
        Assert.Equal(2, handler.AttemptCount);
    }

    [Fact]
    public async Task ChatClient_StopsAfterConfiguredRetryCount()
    {
        using var handler = new DelegateHttpMessageHandler((_, _, _) => Task.FromResult(
            Response(HttpStatusCode.ServiceUnavailable, "still busy")));
        var options = ChatOptions();
        options.RetryOptions = new OpenAICompatibleHttpRetryOptions
        {
            MaxRetryAttempts = 2,
            InitialDelay = TimeSpan.Zero,
            MaxDelay = TimeSpan.Zero,
        };
        using var client = new OpenAICompatibleChatClient(handler, options);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync("hello"));

        Assert.Equal(3, handler.AttemptCount);
    }

    private static OpenAICompatibleChatClientOptions ChatOptions()
    {
        return new OpenAICompatibleChatClientOptions
        {
            Endpoint = new Uri("https://example.test/v1"),
            ModelId = "test-model",
            RetryOptions = NoDelayRetryOptions(),
        };
    }

    private static OpenAICompatibleEmbeddingGeneratorOptions EmbeddingOptions()
    {
        return new OpenAICompatibleEmbeddingGeneratorOptions
        {
            Endpoint = new Uri("https://example.test/v1"),
            ModelId = "test-embedding-model",
            RetryOptions = NoDelayRetryOptions(),
        };
    }

    private static OpenAICompatibleRerankerOptions RerankerOptions()
    {
        return new OpenAICompatibleRerankerOptions
        {
            Endpoint = new Uri("https://example.test/v1"),
            ModelId = "test-reranker-model",
            RetryOptions = NoDelayRetryOptions(),
        };
    }

    private static OpenAICompatibleHttpRetryOptions NoDelayRetryOptions()
    {
        return new OpenAICompatibleHttpRetryOptions
        {
            MaxRetryAttempts = 2,
            InitialDelay = TimeSpan.Zero,
            MaxDelay = TimeSpan.Zero,
        };
    }

    private static HttpResponseMessage Response(HttpStatusCode statusCode, string content)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content),
        };
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
    }

    private static HttpResponseMessage EventStreamResponse(string content)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, System.Text.Encoding.UTF8, "text/event-stream"),
        };
    }

    private const string ChatResponseJson =
        "{\"id\":\"response-1\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}";

    private const string EmbeddingResponseJson =
        "{\"data\":[{\"index\":0,\"embedding\":[0.1,0.2]}],\"model\":\"test-embedding-model\"}";

    private const string RerankResponseJson =
        "{\"results\":[{\"index\":0,\"relevance_score\":0.9}]}";

    private sealed class DelegateHttpMessageHandler(
        Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        private int _attemptCount;

        public int AttemptCount => Volatile.Read(ref _attemptCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attemptCount);
            return handler(attempt, request, cancellationToken);
        }
    }
}
