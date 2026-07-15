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
            MaximumVerbatimTurnTokenEstimate = 100_000,
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
            message => Assert.Contains("summary-2", message.Text),
            message => Assert.Contains("summary-3", message.Text),
            message => Assert.Equal("question-4", message.Text),
            message => Assert.Equal("answer-4", message.Text));
        Assert.Collection(
            result.SessionMemoriesToStore,
            memory => Assert.Equal("summary-1", memory.Content));
        Assert.Empty(result.GlobalMemoriesToStore);

        var secondResult = await compressor.CompressAsync(
            new TokenCompressionContext(result.Messages, TokenCompressionReason.ContextLengthExceeded),
            client);

        Assert.Equal(3, summaryPrompts.Count);
        Assert.Equal(4, secondResult.Messages.Count);
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
            MaximumVerbatimTurnTokenEstimate = 10,
            TokenEstimator = static messages => messages.Sum(message => message.Text.Length),
        });
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, new string('x', 30)),
            new ChatMessage(ChatRole.Assistant, new string('y', 30)),
        };

        var result = await compressor.CompressAsync(
            new TokenCompressionContext(messages, TokenCompressionReason.Configured),
            client);

        Assert.Single(result.Messages);
        Assert.Contains("short summary", result.Messages[0].Text);
    }

    private static ChatResponse Response(string text)
    {
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
    }
}
