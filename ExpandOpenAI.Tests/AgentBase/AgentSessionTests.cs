using ExpandOpenAI.AgentFramework;
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
        AIAgent agent = new DefaultAIAgent(client, new AgentOptions
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
        IAgentSession session = new DefaultAIAgent(client, new AgentOptions
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
        AIAgent agent = new DefaultAIAgent(client, new AgentOptions
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
        Assert.DoesNotContain(
            compressor.LastContext!.Messages,
            message => message.Role == ChatRole.System);
        Assert.Collection(
            session.History,
            message => AssertMessage(message, ChatRole.System, "system"),
            message => AssertMessage(message, ChatRole.System, "old system"),
            message => AssertMessage(message, ChatRole.Assistant, "summary"),
            message => AssertMessage(message, ChatRole.User, "new question"),
            message => AssertMessage(message, ChatRole.Assistant, "done"));
    }

    [Fact]
    public async Task RunAsync_DoesNotDuplicateConfiguredSystemPromptAcrossRuns()
    {
        using var client = new TestChatClient
        {
            ResponseHandler = (_, _, _) => Task.FromResult(Response("done")),
        };
        IAgentSession session = new DefaultAIAgent(client, new AgentOptions
        {
            SystemPromptTemplate = "system",
        }).CreateSession();

        await session.RunAsync("first");
        await session.RunAsync("second");

        Assert.Single(session.History, message => message.Role == ChatRole.System);
        AssertMessage(session.History[0], ChatRole.System, "system");
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
        IAgentSession session = new DefaultAIAgent(client, new AgentOptions
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
        IAgentSession session = new DefaultAIAgent(client, new AgentOptions()).CreateSession();

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
        IAgentSession session = new DefaultAIAgent(client, new AgentOptions()).CreateSession();
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
        IAgentSession session = new DefaultAIAgent(client).CreateSession();

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
        IAgentSession session = new DefaultAIAgent(client, new AgentOptions
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
        IAgentSession session = new DefaultAIAgent(client, new AgentOptions
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
        IAgentSession session = new DefaultAIAgent(client, new AgentOptions
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
    public async Task RunAsync_RecoversRawJsonAndNormalizesToolArgumentNames()
    {
        string? receivedPath = null;
        var function = AIFunctionFactory.Create(
            (string relativePath) =>
            {
                receivedPath = relativePath;
                return "written";
            },
            "write_workspace_file");
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
                        "call-raw",
                        "write_workspace_file",
                        new Dictionary<string, object?>
                        {
                            ["$raw"] = "{\"relative_path\":\"chapters/chapter-01.md\"}",
                        })])));
            },
        };
        var session = new DefaultAIAgent(client, new AgentOptions
        {
            DefaultChatOptions = new ChatOptions { Tools = [function] },
            ToolApprovalAsync = static (_, _) => new ValueTask<bool>(true),
        }).CreateSession();

        var response = await session.RunAsync("write chapter");

        Assert.Equal("tool complete", response.Text);
        Assert.Equal("chapters/chapter-01.md", receivedPath);
    }

    [Fact]
    public async Task RunAsync_ReturnsActionableResultForTruncatedRawToolArguments()
    {
        var toolExecutions = 0;
        var toolResult = string.Empty;
        var function = AIFunctionFactory.Create(
            (string relativePath) =>
            {
                toolExecutions++;
                return relativePath;
            },
            "write_workspace_file");
        using var client = new TestChatClient
        {
            ResponseHandler = (messages, _, _) =>
            {
                var result = messages.SelectMany(static message => message.Contents)
                    .OfType<FunctionResultContent>()
                    .LastOrDefault();
                if (result is not null)
                {
                    toolResult = result.Result?.ToString() ?? result.Exception?.Message ?? string.Empty;
                    return Task.FromResult(Response("tool arguments rejected"));
                }

                return Task.FromResult(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent(
                        "call-truncated",
                        "write_workspace_file",
                        new Dictionary<string, object?>
                        {
                            ["$raw"] = "{\"relativePath\":\"chapter.md\",\"content\":\"unfinished",
                        })])));
            },
        };
        var session = new DefaultAIAgent(client, new AgentOptions
        {
            DefaultChatOptions = new ChatOptions { Tools = [function] },
            ToolApprovalAsync = static (_, _) => new ValueTask<bool>(true),
        }).CreateSession();

        await session.RunAsync("write chapter");

        Assert.Equal(0, toolExecutions);
        Assert.Contains("JSON", toolResult, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("重试", toolResult, StringComparison.Ordinal);
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
        IAgentSession session = new DefaultAIAgent(client, new AgentOptions
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

    [Fact]
    public async Task ContextCompactionTool_IsEnabledByDefaultAndCanBeDisabled()
    {
        IReadOnlyList<string>? defaultToolNames = null;
        using (var client = new TestChatClient
        {
            ResponseHandler = (_, options, _) =>
            {
                defaultToolNames = options!.Tools!.Select(static tool => tool.Name).ToList();
                return Task.FromResult(Response("done"));
            },
        })
        {
            var session = new DefaultAIAgent(client, new AgentOptions()).CreateSession();
            await session.RunAsync("hello");
        }

        IReadOnlyList<string>? disabledToolNames = null;
        using (var client = new TestChatClient
        {
            ResponseHandler = (_, options, _) =>
            {
                disabledToolNames = options!.Tools!.Select(static tool => tool.Name).ToList();
                return Task.FromResult(Response("done"));
            },
        })
        {
            var session = new DefaultAIAgent(client, new AgentOptions
            {
                EnableContextCompactionTool = false,
            }).CreateSession();
            await session.RunAsync("hello");
        }

        Assert.Contains("request_context_compaction", defaultToolNames!);
        Assert.DoesNotContain("request_context_compaction", disabledToolNames!);
    }

    [Fact]
    public async Task RunAsync_ModelCanCompactCurrentToolChainAndContinueSameRun()
    {
        const string modelSummary = "目标：完成工具任务；echo 已返回 HELLO；下一步给出最终答案。";
        var approvalCalls = 0;
        var echo = AIFunctionFactory.Create((string value) => value.ToUpperInvariant(), "echo");
        var compressor = CreateUserPreservingCompressor("compressed assistant and tool history");
        IReadOnlyList<ChatMessage>? messagesAfterCompaction = null;
        using var client = new TestChatClient
        {
            ResponseHandler = (messages, options, _) =>
            {
                var contents = messages.SelectMany(static message => message.Contents).ToList();
                if (contents.OfType<FunctionResultContent>()
                    .Any(static result => result.CallId == "compact-1"))
                {
                    messagesAfterCompaction = messages;
                    Assert.DoesNotContain(
                        options!.Tools!,
                        static tool => tool.Name == "request_context_compaction");
                    return Task.FromResult(Response("finished"));
                }

                if (contents.OfType<FunctionResultContent>()
                    .Any(static result => result.CallId == "echo-1"))
                {
                    return Task.FromResult(new ChatResponse(new ChatMessage(
                        ChatRole.Assistant,
                        [new FunctionCallContent(
                            "compact-1",
                            "request_context_compaction",
                            new Dictionary<string, object?>
                            {
                                ["summary"] = modelSummary,
                                ["reason"] = "工具链已经较长",
                            })])));
                }

                Assert.Contains(
                    options!.Tools!,
                    static tool => tool.Name == "request_context_compaction");
                return Task.FromResult(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent(
                        "echo-1",
                        "echo",
                        new Dictionary<string, object?> { ["value"] = "hello" })])));
            },
        };
        var session = new DefaultAIAgent(client, new AgentOptions
        {
            SystemPromptTemplate = "system",
            TokenCompressor = compressor,
            DefaultChatOptions = new ChatOptions { Tools = [echo] },
            ToolApprovalAsync = (context, _) =>
            {
                approvalCalls++;
                Assert.Equal("echo", context.Function.Name);
                return new ValueTask<bool>(true);
            },
        }).CreateSession(
        [
            new ChatMessage(ChatRole.User, "old question"),
            new ChatMessage(ChatRole.Assistant, "old answer"),
        ]);

        var response = await session.RunAsync("use tool");

        Assert.Equal("finished", response.Text);
        Assert.Equal(1, approvalCalls);
        Assert.Equal(1, compressor.CallCount);
        Assert.Equal(TokenCompressionReason.ModelRequested, compressor.LastContext!.Reason);
        Assert.DoesNotContain(
            compressor.LastContext.Messages,
            static message => message.Role == ChatRole.System);
        Assert.Contains(
            compressor.LastContext.Messages.SelectMany(static message => message.Contents),
            static content => content is FunctionCallContent { Name: "echo" });
        Assert.Contains(
            compressor.LastContext.Messages.SelectMany(static message => message.Contents),
            static content => content is FunctionResultContent { CallId: "echo-1" });
        Assert.DoesNotContain(
            compressor.LastContext.Messages.SelectMany(static message => message.Contents),
            static content => content is FunctionCallContent { Name: "request_context_compaction" });

        Assert.NotNull(messagesAfterCompaction);
        Assert.Contains(messagesAfterCompaction!, message =>
            message.Role == ChatRole.System && message.Text == "system");
        Assert.Contains(messagesAfterCompaction!, message =>
            message.Role == ChatRole.User && message.Text == "old question");
        Assert.Contains(messagesAfterCompaction!, message =>
            message.Role == ChatRole.User && message.Text == "use tool");
        Assert.Contains(messagesAfterCompaction!, message =>
            message.Role == ChatRole.Assistant && message.Text == "compressed assistant and tool history");
        var checkpointCall = Assert.Single(messagesAfterCompaction!
            .SelectMany(static message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal("compact-1", checkpointCall.CallId);
        Assert.Equal(modelSummary, checkpointCall.Arguments!["summary"]);

        Assert.Contains(session.History, message =>
            message.Role == ChatRole.Tool
            && message.Contents.OfType<FunctionResultContent>()
                .Any(static result => result.CallId == "compact-1"));
        AssertMessage(session.History[^1], ChatRole.Assistant, "finished");
    }

    [Fact]
    public async Task RunAsync_DoesNotCommitModelRequestedMemoryWhenContinuationFails()
    {
        var sessionUnits = new List<InMemoryMemoryUnit>();
        var compressor = CreateUserPreservingCompressor(
            "compressed",
            [new MemoryEntry("pending", "must not be committed")]);
        using var client = new TestChatClient
        {
            ResponseHandler = (messages, _, _) =>
            {
                if (messages.SelectMany(static message => message.Contents)
                    .OfType<FunctionResultContent>()
                    .Any(static result => result.CallId == "compact-fail"))
                {
                    throw new InvalidOperationException("continuation failed");
                }

                return Task.FromResult(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent(
                        "compact-fail",
                        "request_context_compaction",
                        new Dictionary<string, object?> { ["summary"] = "task checkpoint" })])));
            },
        };
        var session = new DefaultAIAgent(client, new AgentOptions
        {
            TokenCompressor = compressor,
            SessionMemoryUnitFactory = () =>
            {
                var unit = new InMemoryMemoryUnit();
                sessionUnits.Add(unit);
                return unit;
            },
        }).CreateSession(
        [
            new ChatMessage(ChatRole.User, "old question"),
            new ChatMessage(ChatRole.Assistant, "old answer"),
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunAsync("continue"));

        Assert.Collection(
            session.History,
            message => AssertMessage(message, ChatRole.User, "old question"),
            message => AssertMessage(message, ChatRole.Assistant, "old answer"));
        Assert.Empty(await sessionUnits[0].RecallAsync(new MemoryRecallRequest(string.Empty)));
    }

    [Fact]
    public async Task RunStreamAsync_HidesCompactionControlMessagesAndContinuesStreaming()
    {
        var compressor = CreateUserPreservingCompressor("stream compressed");
        using var client = new TestChatClient
        {
            StreamingHandler = StreamResponses,
        };
        var session = new DefaultAIAgent(client, new AgentOptions
        {
            TokenCompressor = compressor,
        }).CreateSession(
        [
            new ChatMessage(ChatRole.User, "old question"),
            new ChatMessage(ChatRole.Assistant, "old answer"),
        ]);
        var updates = new List<ChatResponseUpdate>();

        await foreach (var update in session.RunStreamAsync("continue"))
        {
            updates.Add(update);
        }

        Assert.Equal(2, client.StreamingResponseCallCount);
        Assert.Single(updates);
        Assert.Equal("stream finished", updates[0].Text);
        Assert.Equal(1, compressor.CallCount);
        Assert.Contains(session.History, message =>
            message.Role == ChatRole.Tool
            && message.Contents.OfType<FunctionResultContent>()
                .Any(static result => result.CallId == "compact-stream"));
        AssertMessage(session.History[^1], ChatRole.Assistant, "stream finished");

        async IAsyncEnumerable<ChatResponseUpdate> StreamResponses(
            IReadOnlyList<ChatMessage> messages,
            ChatOptions? options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (messages.SelectMany(static message => message.Contents)
                .OfType<FunctionResultContent>()
                .Any(static result => result.CallId == "compact-stream"))
            {
                Assert.DoesNotContain(
                    options!.Tools!,
                    static tool => tool.Name == "request_context_compaction");
                yield return new ChatResponseUpdate(ChatRole.Assistant, "stream finished");
                yield break;
            }

            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent(
                    "compact-stream",
                    "request_context_compaction",
                    new Dictionary<string, object?>
                    {
                        ["summary"] = "stream task checkpoint",
                    })]);
        }
    }

    [Fact]
    public async Task RunStreamAsync_RejectsCompactionAfterAssistantContentWasPublished()
    {
        var compressor = CreateUserPreservingCompressor("must not run");
        using var client = new TestChatClient
        {
            StreamingHandler = StreamResponses,
        };
        var session = new DefaultAIAgent(client, new AgentOptions
        {
            TokenCompressor = compressor,
        }).CreateSession();
        var visibleTexts = new List<string>();

        await foreach (var update in session.RunStreamAsync("continue"))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                visibleTexts.Add(update.Text);
            }
        }

        Assert.Equal(["partial answer", "continued without compaction"], visibleTexts);
        Assert.Equal(0, compressor.CallCount);

        async IAsyncEnumerable<ChatResponseUpdate> StreamResponses(
            IReadOnlyList<ChatMessage> messages,
            ChatOptions? _,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (messages.SelectMany(static message => message.Contents)
                .OfType<FunctionResultContent>()
                .Any(static result => result.CallId == "compact-late"))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "continued without compaction");
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial answer");
            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent(
                    "compact-late",
                    "request_context_compaction",
                    new Dictionary<string, object?> { ["summary"] = "too late" })]);
        }
    }

    [Fact]
    public async Task AbstractAgent_CanReturnCustomSessionImplementation()
    {
        using var client = new TestChatClient();
        AIAgent agent = new CustomAgent(client);

        IAgentSession session = agent.CreateSession();
        var response = await session.RunAsync("hello");

        Assert.IsType<CustomAgentSession>(session);
        Assert.Equal("custom: hello", response.Text);
    }

    [Fact]
    public void AgentOptionsClone_PreservesDerivedOptions()
    {
        AgentOptions options = new CustomAgentOptions
        {
            Marker = "custom",
            SystemPromptTemplate = "system",
            EnableContextCompactionTool = false,
        };

        var clone = Assert.IsType<CustomAgentOptions>(options.Clone());

        Assert.NotSame(options, clone);
        Assert.Equal("custom", clone.Marker);
        Assert.Equal("system", clone.SystemPromptTemplate);
        Assert.False(clone.EnableContextCompactionTool);
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

    private static TestTokenCompressor CreateUserPreservingCompressor(
        string summary,
        IReadOnlyList<MemoryEntry>? sessionMemories = null)
    {
        return new TestTokenCompressor(
            [],
            resultFactory: context =>
            {
                var messages = context.Messages
                    .Where(static message => message.Role == ChatRole.User)
                    .Select(static message => new ChatMessage(ChatRole.User, message.Contents.ToList()))
                    .ToList();
                messages.Add(new ChatMessage(ChatRole.Assistant, summary));
                return new TokenCompressionResult(messages, sessionMemories);
            });
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

    private sealed class CustomAgent : AIAgent
    {
        public CustomAgent(IChatClient chatClient)
            : base(chatClient)
        {
        }

        public override IAgentSession CreateSession(IEnumerable<ChatMessage>? initialHistory = null)
        {
            return new CustomAgentSession(initialHistory);
        }
    }

    private sealed class CustomAgentSession : IAgentSession
    {
        private readonly List<ChatMessage> _history;

        public CustomAgentSession(IEnumerable<ChatMessage>? initialHistory)
        {
            _history = initialHistory?.ToList() ?? [];
        }

        public IReadOnlyList<ChatMessage> History => _history.AsReadOnly();

        public void ClearHistory() => _history.Clear();

        public ValueTask ClearMemoryAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return default;
        }

        public ValueTask DestroyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _history.Clear();
            return default;
        }

        public Task<ChatResponse> RunAsync(
            string message,
            ChatOptions? chatOptions = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Response($"custom: {message}"));
        }

        public Task<ChatResponse> RunAsync(
            ChatMessage userMessage,
            ChatOptions? chatOptions = null,
            CancellationToken cancellationToken = default)
        {
            return RunAsync(userMessage.Text, chatOptions, cancellationToken);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> RunStreamAsync(
            string message,
            ChatOptions? chatOptions = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, $"custom: {message}");
        }

        public IAsyncEnumerable<ChatResponseUpdate> RunStreamAsync(
            ChatMessage userMessage,
            ChatOptions? chatOptions = null,
            CancellationToken cancellationToken = default)
        {
            return RunStreamAsync(userMessage.Text, chatOptions, cancellationToken);
        }
    }

    private sealed class CustomAgentOptions : AgentOptions
    {
        public CustomAgentOptions()
        {
        }

        private CustomAgentOptions(CustomAgentOptions other)
            : base(other)
        {
            Marker = other.Marker;
        }

        public string Marker { get; init; } = string.Empty;

        public override AgentOptions Clone() => new CustomAgentOptions(this);
    }
}
