using ExpandOpenAI.AgentFramework;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.AgentFramework.Demo;

internal sealed class RecordingTokenCompressor(
    ITokenCompressor inner,
    Func<TokenCompressionContext, TokenCompressionResult, CancellationToken, ValueTask> onCompressed)
    : ITokenCompressor
{
    public bool ShouldCompress(IReadOnlyList<ChatMessage> messages) => inner.ShouldCompress(messages);

    public async ValueTask<TokenCompressionResult> CompressAsync(
        TokenCompressionContext context,
        IChatClient chatClient,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.CompressAsync(context, chatClient, cancellationToken).ConfigureAwait(false);
        await onCompressed(context, result, cancellationToken).ConfigureAwait(false);
        return result;
    }
}

internal static class NovelTokenEstimator
{
    public static int Estimate(IReadOnlyList<ChatMessage> messages)
    {
        long estimate = 0;
        foreach (var message in messages)
        {
            estimate += 4;
            foreach (var content in message.Contents)
            {
                estimate += content switch
                {
                    TextContent text => EstimateText(text.Text),
                    TextReasoningContent reasoning => EstimateText(reasoning.Text),
                    FunctionCallContent call => EstimateText(call.Name)
                        + EstimateText(System.Text.Json.JsonSerializer.Serialize(call.Arguments)),
                    FunctionResultContent result => EstimateText(result.Result?.ToString() ?? string.Empty),
                    _ => EstimateText(content.ToString() ?? string.Empty),
                };
            }
        }

        return (int)Math.Min(int.MaxValue, Math.Max(0, estimate));
    }

    private static int EstimateText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var ascii = 0;
        var nonAscii = 0;
        foreach (var character in text)
        {
            if (character <= 0x7f)
            {
                ascii++;
            }
            else
            {
                nonAscii++;
            }
        }

        return Math.Max(1, (int)Math.Ceiling(ascii / 4d + nonAscii * 0.8d));
    }
}

internal sealed record NovelCompressionRecord(
    string Id,
    DateTimeOffset OccurredAt,
    string Reason,
    int BeforeMessageCount,
    int AfterMessageCount,
    int BeforeTokenEstimate,
    int AfterTokenEstimate,
    int SessionMemoriesCreated,
    int GlobalMemoriesCreated);

internal sealed record NovelContextDiagnostics(
    string SessionId,
    int ActiveHistoryMessageCount,
    int ActiveHistoryTokenEstimate,
    int CompressionTokenThreshold,
    int MaximumOutputTokens,
    int SystemPromptVersion,
    string SessionInstructions,
    int SessionMemoryCount,
    int GlobalMemoryCount,
    IReadOnlyList<NovelCompressionRecord> CompressionHistory);

internal sealed record NovelContextInspectorResponse(
    NovelContextDiagnostics Context,
    IReadOnlyList<NovelRunSummary> RecentRuns);
