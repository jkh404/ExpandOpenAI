using ExpandOpenAI.AgentFramework;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.Tests.AgentBase;

public sealed class DefaultTokenCompressorTests
{
    [Fact]
    public async Task CompressAsync_KeepsLatestTurnSummarizesRecentTurnsAndArchivesOlderTurns()
    {
        var summaryPrompts = new List<string>();
        using var client = new TestChatClient
        {
            ResponseHandler = (messages, _, _) =>
            {
                summaryPrompts.Add(messages.Last().Text);
                return Task.FromResult(Response($"summary-{summaryPrompts.Count}"));
            },
        };
        var compressor = new DefaultTokenCompressor(new DefaultTokenCompressorOptions
        {
            RecentVerbatimTurnCount = 1,
            RecentSummaryTurnCount = 2,
            MaximumHistoryTokenEstimate = 100_000,
        });
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, "question-1"),
            new ChatMessage(ChatRole.Assistant, "answer-1"),
            new ChatMessage(ChatRole.User, "question-2"),
            new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "call-1",
                    "lookup",
                    new Dictionary<string, object?> { ["id"] = 42 }),
            ]),
            new ChatMessage(ChatRole.Tool,
            [
                new FunctionResultContent("call-1", "lookup-result"),
            ]),
            new ChatMessage(ChatRole.Assistant, "answer-2"),
            new ChatMessage(ChatRole.User, "question-3"),
            new ChatMessage(ChatRole.Assistant, "answer-3"),
            new ChatMessage(ChatRole.User, "question-4"),
            new ChatMessage(ChatRole.Assistant, "answer-4"),
        };

        Assert.True(compressor.ShouldCompress(messages));

        var result = await compressor.CompressAsync(
            new TokenCompressionContext(messages, TokenCompressionReason.Configured),
            client);

        Assert.Equal(3, summaryPrompts.Count);
        Assert.Contains("Function call: lookup", summaryPrompts[1]);
        Assert.Contains("Function result", summaryPrompts[1]);
        Assert.Collection(
            result.Messages,
            message => Assert.Equal("question-2", message.Text),
            message =>
            {
                Assert.Equal(ChatRole.Assistant, message.Role);
                Assert.Contains("summary-2", message.Text);
            },
            message => Assert.Equal("question-3", message.Text),
            message =>
            {
                Assert.Equal(ChatRole.Assistant, message.Role);
                Assert.Contains("summary-3", message.Text);
            },
            message => Assert.Equal("question-4", message.Text),
            message => Assert.Equal("answer-4", message.Text));
        Assert.Collection(
            result.SessionMemoriesToStore,
            memory =>
            {
                Assert.Contains("question-1", memory.Content);
                Assert.Contains("summary-1", memory.Content);
            });
        Assert.Empty(result.GlobalMemoriesToStore);

        var secondResult = await compressor.CompressAsync(
            new TokenCompressionContext(result.Messages, TokenCompressionReason.ContextLengthExceeded),
            client);

        Assert.Equal(3, summaryPrompts.Count);
        Assert.Equal(6, secondResult.Messages.Count);
    }

    [Fact]
    public async Task CompressAsync_SummarizesOversizedLatestTurn()
    {
        using var client = new TestChatClient
        {
            ResponseHandler = (_, _, _) => Task.FromResult(Response("short summary")),
        };
        var compressor = new DefaultTokenCompressor(new DefaultTokenCompressorOptions
        {
            MaximumHistoryTokenEstimate = 100,
            TokenEstimator = static messages => messages.Sum(message => message.Text.Length),
        });
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, new string('x', 34)),
            new ChatMessage(ChatRole.Assistant, new string('y', 34)),
        };

        Assert.True(compressor.ShouldCompress(messages));

        var result = await compressor.CompressAsync(
            new TokenCompressionContext(messages, TokenCompressionReason.Configured),
            client);

        Assert.Collection(
            result.Messages,
            message => Assert.Equal(new string('x', 34), message.Text),
            message =>
            {
                Assert.Equal(ChatRole.Assistant, message.Role);
                Assert.Contains("short summary", message.Text);
            });
    }

    [Fact]
    public void ShouldCompress_KeepsLatestTurnAtOrBelowTwoThirdsOfHistoryLimit()
    {
        var compressor = new DefaultTokenCompressor(new DefaultTokenCompressorOptions
        {
            MaximumHistoryTokenEstimate = 90,
            TokenEstimator = static messages => messages.Sum(message => message.Text.Length),
        });

        Assert.False(compressor.ShouldCompress(
        [
            new ChatMessage(ChatRole.User, new string('x', 30)),
            new ChatMessage(ChatRole.Assistant, new string('y', 30)),
        ]));
        Assert.True(compressor.ShouldCompress(
        [
            new ChatMessage(ChatRole.User, new string('x', 30)),
            new ChatMessage(ChatRole.Assistant, new string('y', 31)),
        ]));
    }

    [Fact]
    public async Task CompressAsync_ArchivesUserOnlyTurnWithoutChangingUserText()
    {
        var modelCallCount = 0;
        using var client = new TestChatClient
        {
            ResponseHandler = (_, _, _) =>
            {
                modelCallCount++;
                return Task.FromResult(Response("unexpected"));
            },
        };
        var compressor = new DefaultTokenCompressor(new DefaultTokenCompressorOptions
        {
            RecentSummaryTurnCount = 0,
            MaximumHistoryTokenEstimate = 100_000,
        });

        var result = await compressor.CompressAsync(
            new TokenCompressionContext(
            [
                new ChatMessage(ChatRole.User, "verbatim user-only turn"),
                new ChatMessage(ChatRole.User, "latest question"),
                new ChatMessage(ChatRole.Assistant, "latest answer"),
            ],
            TokenCompressionReason.Configured),
            client);

        Assert.Equal(0, modelCallCount);
        Assert.Collection(
            result.Messages,
            message => Assert.Equal("latest question", message.Text),
            message => Assert.Equal("latest answer", message.Text));
        Assert.Collection(
            result.SessionMemoriesToStore,
            memory => Assert.Contains("verbatim user-only turn", memory.Content));
    }

    [Fact]
    public async Task CompressAsync_CompressesOversizedAssistantMessageBeforeTurnCompressionOnlyOnce()
    {
        var modelCallCount = 0;
        string? summaryPrompt = null;
        using var client = new TestChatClient
        {
            ResponseHandler = (messages, _, _) =>
            {
                modelCallCount++;
                summaryPrompt = messages[0].Text;
                return Task.FromResult(Response("task-relevant assistant summary"));
            },
        };
        var compressor = new DefaultTokenCompressor(new DefaultTokenCompressorOptions
        {
            MaximumMessageTokenEstimate = 10,
            MaximumHistoryTokenEstimate = 1_000,
            TokenEstimator = EstimateContentLength,
        });
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, new string('u', 100)),
            new ChatMessage(ChatRole.Assistant, new string('a', 40)),
        };

        Assert.True(compressor.ShouldCompress(messages));
        var first = await compressor.CompressAsync(
            new TokenCompressionContext(messages, TokenCompressionReason.Configured),
            client);

        Assert.Equal(1, modelCallCount);
        Assert.Contains("提炼任务相关关键信息", summaryPrompt);
        Assert.Collection(
            first.Messages,
            message => Assert.Equal(new string('u', 100), message.Text),
            message => Assert.Contains("task-relevant assistant summary", message.Text));
        Assert.False(compressor.ShouldCompress(first.Messages));

        var second = await compressor.CompressAsync(
            new TokenCompressionContext(first.Messages, TokenCompressionReason.Configured),
            client);

        Assert.Equal(1, modelCallCount);
        Assert.Equal(first.Messages.Select(message => message.Text), second.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task CompressAsync_MessageThresholdZeroDisablesMessageCompression()
    {
        var modelCallCount = 0;
        using var client = new TestChatClient
        {
            ResponseHandler = (_, _, _) =>
            {
                modelCallCount++;
                return Task.FromResult(Response("unexpected"));
            },
        };
        var compressor = new DefaultTokenCompressor(new DefaultTokenCompressorOptions
        {
            MaximumMessageTokenEstimate = 0,
            MaximumHistoryTokenEstimate = 1_000,
            TokenEstimator = EstimateContentLength,
        });
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, "question"),
            new ChatMessage(ChatRole.Assistant, new string('a', 100)),
        };

        Assert.False(compressor.ShouldCompress(messages));
        var result = await compressor.CompressAsync(
            new TokenCompressionContext(messages, TokenCompressionReason.Configured),
            client);

        Assert.Equal(0, modelCallCount);
        Assert.Equal(new string('a', 100), result.Messages[1].Text);
    }

    [Fact]
    public async Task CompressAsync_CompressesToolResultWithoutCompressingFunctionCall()
    {
        using var client = new TestChatClient
        {
            ResponseHandler = (_, _, _) => Task.FromResult(Response("condensed tool result")),
        };
        var compressor = new DefaultTokenCompressor(new DefaultTokenCompressorOptions
        {
            MaximumMessageTokenEstimate = 20,
            MaximumHistoryTokenEstimate = 10_000,
            TokenEstimator = EstimateContentLength,
        });
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, "run tool"),
            new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "call-large",
                    "lookup",
                    new Dictionary<string, object?> { ["query"] = new string('q', 100) }),
            ]),
            new ChatMessage(ChatRole.Tool,
            [
                new FunctionResultContent("call-large", new string('r', 100)),
            ]),
            new ChatMessage(ChatRole.Assistant, "done"),
        };

        var result = await compressor.CompressAsync(
            new TokenCompressionContext(messages, TokenCompressionReason.Configured),
            client);

        var functionCall = Assert.Single(result.Messages[1].Contents.OfType<FunctionCallContent>());
        Assert.Equal(new string('q', 100), Assert.IsType<string>(functionCall.Arguments!["query"]));
        var functionResult = Assert.Single(result.Messages[2].Contents.OfType<FunctionResultContent>());
        Assert.Equal("call-large", functionResult.CallId);
        Assert.Contains("condensed tool result", functionResult.Result?.ToString());
    }

    [Fact]
    public void ShouldCompress_DoesNotApplyMessageCompressionToUserOrFunctionCall()
    {
        var compressor = new DefaultTokenCompressor(new DefaultTokenCompressorOptions
        {
            MaximumMessageTokenEstimate = 10,
            MaximumHistoryTokenEstimate = 10_000,
            TokenEstimator = EstimateContentLength,
        });

        Assert.False(compressor.ShouldCompress(
        [
            new ChatMessage(ChatRole.User, new string('u', 100)),
            new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "call-large",
                    "lookup",
                    new Dictionary<string, object?> { ["query"] = new string('q', 100) }),
            ]),
        ]));
    }

    [Fact]
    public void DefaultSummaryPrompts_RequestTaskRelevantInformation()
    {
        var options = new DefaultTokenCompressorOptions();

        Assert.Contains("提炼任务相关关键信息", options.MessageSummaryPrompt);
        Assert.Contains("提炼任务相关关键信息", options.SummaryPrompt);
    }

    [Fact]
    public async Task CompressAsync_HierarchicallySummarizesVeryLongSingleTurn()
    {
        var summaryInputs = new List<string>();
        var summaryPrompts = new List<string>();
        using var client = new TestChatClient
        {
            ResponseHandler = (messages, _, _) =>
            {
                summaryPrompts.Add(messages[0].Text);
                summaryInputs.Add(messages[1].Text);
                return Task.FromResult(Response($"partial-summary-{summaryInputs.Count}"));
            },
        };
        var compressor = new DefaultTokenCompressor(new DefaultTokenCompressorOptions
        {
            MaximumHistoryTokenEstimate = 300,
            SummaryMaxOutputTokens = 40,
            TokenEstimator = EstimateContentLength,
        });
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, "task"),
        };
        for (var index = 0; index < 20; index++)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant, new string((char)('a' + (index % 20)), 20)));
        }

        var result = await compressor.CompressAsync(
            new TokenCompressionContext(messages, TokenCompressionReason.Configured),
            client);

        Assert.True(summaryInputs.Count > 2);
        Assert.All(summaryInputs, input => Assert.True(EstimateContentLength(
            [new ChatMessage(ChatRole.User, input)]) <= 200));
        Assert.Contains(summaryPrompts, prompt => prompt.Contains("有序片段", StringComparison.Ordinal));
        Assert.Contains(summaryPrompts, prompt => prompt.Contains("局部摘要", StringComparison.Ordinal));
        Assert.Collection(
            result.Messages,
            message => Assert.Equal("task", message.Text),
            message => Assert.Contains("partial-summary-", message.Text));
    }

    [Fact]
    public async Task CompressAsync_KeepsFunctionCallAndResultInSameSummaryFragment()
    {
        var requests = new List<(string Prompt, string Input)>();
        using var client = new TestChatClient
        {
            ResponseHandler = (messages, _, _) =>
            {
                requests.Add((messages[0].Text, messages[1].Text));
                return Task.FromResult(Response($"tool-summary-{requests.Count}"));
            },
        };
        var compressor = new DefaultTokenCompressor(new DefaultTokenCompressorOptions
        {
            MaximumHistoryTokenEstimate = 450,
            SummaryMaxOutputTokens = 40,
            TokenEstimator = static messages => EstimateContentLength(messages) + (messages.Count * 50),
        });
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, "task"),
        };
        for (var index = 1; index <= 4; index++)
        {
            var callId = $"call-{index}";
            messages.Add(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent(
                    callId,
                    "lookup",
                    new Dictionary<string, object?> { ["query"] = $"query-{index}" }),
            ]));
            messages.Add(new ChatMessage(ChatRole.Tool,
            [
                new FunctionResultContent(callId, $"result-{index}"),
            ]));
        }

        await compressor.CompressAsync(
            new TokenCompressionContext(messages, TokenCompressionReason.Configured),
            client);

        var initialInputs = requests
            .Where(request => request.Prompt.Contains("有序片段", StringComparison.Ordinal))
            .Select(static request => request.Input)
            .ToList();
        Assert.NotEmpty(initialInputs);
        for (var index = 1; index <= 4; index++)
        {
            var callId = $"call-{index}";
            Assert.Contains(initialInputs, input =>
                input.Contains($"callId={callId}", StringComparison.Ordinal)
                && input.Contains($"result-{index}", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task CompressAsync_ModelRequestedAlwaysSummarizesLatestTurnAndKeepsUserVerbatim()
    {
        var summaryCalls = 0;
        using var client = new TestChatClient
        {
            ResponseHandler = (_, _, _) =>
            {
                summaryCalls++;
                return Task.FromResult(Response("task-focused checkpoint"));
            },
        };
        var compressor = new DefaultTokenCompressor(new DefaultTokenCompressorOptions
        {
            MaximumHistoryTokenEstimate = 100_000,
        });
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, "keep this user message exactly"),
            new ChatMessage(ChatRole.Assistant, "short assistant message"),
        };

        Assert.False(compressor.ShouldCompress(messages));

        var result = await compressor.CompressAsync(
            new TokenCompressionContext(messages, TokenCompressionReason.ModelRequested),
            client);

        Assert.Equal(1, summaryCalls);
        Assert.Collection(
            result.Messages,
            message => Assert.Equal("keep this user message exactly", message.Text),
            message => Assert.Contains("task-focused checkpoint", message.Text));
    }

    private static int EstimateContentLength(IReadOnlyList<ChatMessage> messages)
    {
        return messages.Sum(message => message.Contents.Sum(content => content switch
        {
            TextContent text => text.Text.Length,
            TextReasoningContent reasoning => reasoning.Text.Length,
            FunctionCallContent call => call.Arguments?.Values.Sum(value => value?.ToString()?.Length ?? 0) ?? 0,
            FunctionResultContent result => result.Result?.ToString()?.Length ?? 0,
            _ => 0,
        }));
    }

    private static ChatResponse Response(string text)
    {
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
    }
}
