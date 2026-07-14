using ExpandOpenAI.AgentBase;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.Tests.AgentBase;

public sealed class AgentSessionTests
{
    [Fact]
    public async Task RunAsync_CommitsConversationAfterSuccessfulResponse()
    {
        IReadOnlyList<ChatMessage>? receivedMessages = null;
        using var client = new TestChatClient
        {
            ResponseHandler = (messages, _, _) =>
            {
                receivedMessages = messages;
                return Task.FromResult(Response("hello"));
            },
        };
        var agent = new AIAgent(client, new AgentOptions
        {
            SystemPromptTemplate = "You are helpful.",
        });
        var session = agent.CreateSession();

        var response = await session.RunAsync("hi");

        Assert.Equal("hello", response.Text);
        Assert.Collection(
            receivedMessages!,
            message => AssertMessage(message, ChatRole.System, "You are helpful."),
            message => AssertMessage(message, ChatRole.User, "hi"));
        Assert.Collection(
            session.History,
            message => AssertMessage(message, ChatRole.System, "You are helpful."),
            message => AssertMessage(message, ChatRole.User, "hi"),
            message => AssertMessage(message, ChatRole.Assistant, "hello"));
    }

    [Fact]
    public async Task RunAsync_DoesNotChangeHistoryWhenRequestFails()
    {
        using var client = new TestChatClient
        {
            ResponseHandler = (_, _, _) => throw new InvalidOperationException("request failed"),
        };
        var session = new AIAgent(client, new AgentOptions
        {
            SystemPromptTemplate = "system",
        }).CreateSession();

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunAsync("hi"));

        Assert.Empty(session.History);
    }

    [Fact]
    public async Task RunAsync_ForceCompressesOnceAndCommitsCompressedHistoryAfterSuccess()
    {
        var callCount = 0;
        var compressor = new TestTokenCompressor(
            [new ChatMessage(ChatRole.Assistant, "summary")]);
        using var client = new TestChatClient
        {
            ResponseHandler = (_, _, _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    throw new InvalidOperationException("context_length_exceeded");
                }

                return Task.FromResult(Response("done"));
            },
        };
        var agent = new AIAgent(client, new AgentOptions
        {
            SystemPromptTemplate = "system",
            TokenCompressor = compressor,
        });
        var session = agent.CreateSession(
        [
            new ChatMessage(ChatRole.System, "old system"),
            new ChatMessage(ChatRole.User, "old question"),
            new ChatMessage(ChatRole.Assistant, "old answer"),
        ]);

        await session.RunAsync("new question");

        Assert.Equal(2, callCount);
        Assert.Equal(1, compressor.CallCount);
        Assert.Collection(
            session.History,
            message => AssertMessage(message, ChatRole.System, "system"),
            message => AssertMessage(message, ChatRole.Assistant, "summary"),
            message => AssertMessage(message, ChatRole.User, "new question"),
            message => AssertMessage(message, ChatRole.Assistant, "done"));
    }

    [Fact]
    public async Task RunAsync_DoesNotCommitCompressedHistoryWhenRetryFails()
    {
        var compressor = new TestTokenCompressor(
            [new ChatMessage(ChatRole.Assistant, "summary")]);
        using var client = new TestChatClient
        {
            ResponseHandler = (_, _, _) => throw new InvalidOperationException("context_length_exceeded"),
        };
        var session = new AIAgent(client, new AgentOptions
        {
            SystemPromptTemplate = "system",
            TokenCompressor = compressor,
        }).CreateSession(
        [
            new ChatMessage(ChatRole.System, "system"),
            new ChatMessage(ChatRole.User, "old question"),
            new ChatMessage(ChatRole.Assistant, "old answer"),
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunAsync("new question"));

        Assert.Collection(
            session.History,
            message => AssertMessage(message, ChatRole.System, "system"),
            message => AssertMessage(message, ChatRole.User, "old question"),
            message => AssertMessage(message, ChatRole.Assistant, "old answer"));
    }

    [Fact]
    public async Task RunStreamAsync_DoesNotCommitPartialResponseWhenConsumerStopsEarly()
    {
        using var client = new TestChatClient
        {
            ResponseHandler = (_, _, _) => Task.FromResult(Response("after stream")),
            StreamingHandler = StreamTwoUpdates,
        };
        var session = new AIAgent(client, new AgentOptions()).CreateSession();

        await using (var enumerator = session.RunStreamAsync("hi").GetAsyncEnumerator())
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("first", enumerator.Current.Text);
        }

        Assert.Empty(session.History);

        await session.RunAsync("next");
        Assert.Collection(
            session.History,
            message => AssertMessage(message, ChatRole.User, "next"),
            message => AssertMessage(message, ChatRole.Assistant, "after stream"));
    }

    [Fact]
    public async Task RunAsync_PropagatesCancellationToken()
    {
        using var client = new TestChatClient
        {
            ResponseHandler = async (_, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Response("unreachable");
            },
        };
        var session = new AIAgent(client, new AgentOptions()).CreateSession();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.RunAsync("hi", cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task RunAsync_RejectsConcurrentUseOfSameSession()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new TestChatClient
        {
            ResponseHandler = async (_, _, cancellationToken) =>
            {
                requestStarted.TrySetResult();
                await releaseRequest.Task.WaitAsync(cancellationToken);
                return Response("done");
            },
        };
        var session = new AIAgent(client).CreateSession();

        var firstRun = session.RunAsync("first");
        await requestStarted.Task;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunAsync("second"));

        releaseRequest.TrySetResult();
        await firstRun;
    }

    [Fact]
    public async Task RunAsync_UsesMessageHandlerWithoutExposingMutableHistory()
    {
        IReadOnlyList<ChatMessage>? receivedMessages = null;
        using var client = new TestChatClient
        {
            ResponseHandler = (messages, _, _) =>
            {
                receivedMessages = messages;
                return Task.FromResult(Response("private answer"));
            },
        };
        var session = new AIAgent(client, new AgentOptions
        {
            MessageHandler = new RedactingMessageHandler(),
        }).CreateSession();

        await session.RunAsync("private question");

        AssertMessage(receivedMessages!.Single(), ChatRole.User, "model question");
        AssertMessage(session.History.Single(), ChatRole.Assistant, "history answer");
    }

    [Fact]
    public async Task RunAsync_ClonesDefaultChatOptionsForEveryAttempt()
    {
        var receivedTemperatures = new List<float?>();
        using var client = new TestChatClient
        {
            ResponseHandler = (_, options, _) =>
            {
                receivedTemperatures.Add(options?.Temperature);
                options!.Temperature = 0.9f;
                return Task.FromResult(Response("done"));
            },
        };
        var session = new AIAgent(client, new AgentOptions
        {
            DefaultChatOptions = new ChatOptions
            {
                Temperature = 0.2f,
            },
        }).CreateSession();

        await session.RunAsync("first");
        await session.RunAsync("second");

        Assert.Equal([0.2f, 0.2f], receivedTemperatures);
    }

    [Fact]
    public async Task RunAsync_ApprovesToolWithInvocationContext()
    {
        var toolExecutions = 0;
        var function = AIFunctionFactory.Create(
            (string value) =>
            {
                toolExecutions++;
                return value.ToUpperInvariant();
            },
            "echo");
        using var client = new TestChatClient
        {
            ResponseHandler = (messages, _, _) =>
            {
                if (messages.SelectMany(static message => message.Contents).OfType<FunctionResultContent>().Any())
                {
                    return Task.FromResult(Response("tool complete"));
                }

                return Task.FromResult(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent(
                        "call-1",
                        "echo",
                        new Dictionary<string, object?> { ["value"] = "hello" })])));
            },
        };
        var session = new AIAgent(client, new AgentOptions
        {
            DefaultChatOptions = new ChatOptions
            {
                Tools = [function],
            },
            ToolApprovalAsync = (context, _) =>
            {
                Assert.Equal("echo", context.Function.Name);
                Assert.Equal("hello", context.Arguments["value"]);
                return new ValueTask<bool>(true);
            },
        }).CreateSession();

        var response = await session.RunAsync("use tool");

        Assert.Equal("tool complete", response.Text);
        Assert.Equal(1, toolExecutions);
    }

    [Fact]
    public async Task RunAsync_DoesNotRetryAfterToolExecutionHasStarted()
    {
        var toolExecutions = 0;
        var compressor = new TestTokenCompressor(
            [new ChatMessage(ChatRole.Assistant, "summary")]);
        var function = AIFunctionFactory.Create(
            () =>
            {
                toolExecutions++;
                return "done";
            },
            "side_effect");
        var innerCallCount = 0;
        using var client = new TestChatClient
        {
            ResponseHandler = (_, _, _) =>
            {
                innerCallCount++;
                if (innerCallCount == 1)
                {
                    return Task.FromResult(new ChatResponse(new ChatMessage(
                        ChatRole.Assistant,
                        [new FunctionCallContent(
                            "call-1",
                            "side_effect",
                            new Dictionary<string, object?>())])));
                }

                throw new InvalidOperationException("context_length_exceeded");
            },
        };
        var session = new AIAgent(client, new AgentOptions
        {
            TokenCompressor = compressor,
            DefaultChatOptions = new ChatOptions
            {
                Tools = [function],
            },
            ToolApprovalAsync = static (_, _) => new ValueTask<bool>(true),
        }).CreateSession(
        [
            new ChatMessage(ChatRole.User, "old question"),
            new ChatMessage(ChatRole.Assistant, "old answer"),
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunAsync("use tool"));

        Assert.Equal(2, client.ResponseCallCount);
        Assert.Equal(1, toolExecutions);
        Assert.Equal(0, compressor.CallCount);
        Assert.Collection(
            session.History,
            message => AssertMessage(message, ChatRole.User, "old question"),
            message => AssertMessage(message, ChatRole.Assistant, "old answer"));
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamTwoUpdates(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "first");
        yield return new ChatResponseUpdate(ChatRole.Assistant, "second");
    }

    private static ChatResponse Response(string text)
    {
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
    }

    private static void AssertMessage(ChatMessage message, ChatRole role, string text)
    {
        Assert.Equal(role, message.Role);
        Assert.Equal(text, message.Text);
    }

    private sealed class RedactingMessageHandler : IAgentMessageHandler
    {
        public ValueTask<ChatMessage> PrepareUserForModelAsync(
            ChatMessage userMessage,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<ChatMessage>(new ChatMessage(ChatRole.User, "model question"));
        }

        public ValueTask<ChatMessage?> PrepareUserForHistoryAsync(
            ChatMessage originalUserMessage,
            ChatMessage modelUserMessage,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<ChatMessage?>((ChatMessage?)null);
        }

        public ValueTask<ChatMessage?> PrepareAssistantForHistoryAsync(
            ChatMessage assistantMessage,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<ChatMessage?>(new ChatMessage(ChatRole.Assistant, "history answer"));
        }
    }
}
